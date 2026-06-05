namespace AnalystAgent.Tests.Pipeline;

using System;
using System.Collections.Generic;
using AnalystAgent.Models;
using AnalystAgent.Pipeline;
using Xunit;

/// <summary>
/// Pure-function tests for the execution-guided self-consistency vote (Slice 1). Pins the
/// fingerprint + majority-vote contract the abstain-fallback relies on: agreement → winner,
/// total disagreement → abstain, ties → candidate-0 bucket, and float tolerance merging.
/// No LLM/DB — the vote logic is deliberately I/O-free and unit-testable in isolation.
/// </summary>
public class SelfConsistencyVoteTests
{
    // ── Pick: agreement, disagreement, ties ────────────────────────────────────────────

    [Fact]
    public void Pick_TwoOfThreeAgree_ReturnsWinnerFromMajorityBucket()
    {
        // A, A, B → bucket "A" wins (size 2 ≥ minAgreement 2). Winner index is a member of "A" (index 0).
        var vote = SelfConsistencyVote.Pick(new[] { "A", "A", "B" }, minAgreement: 2);
        Assert.NotNull(vote.WinnerIndex);
        Assert.Equal(2, vote.Agreement);
        Assert.Contains(vote.WinnerIndex!.Value, new[] { 0, 1 });   // a member of the "A" bucket
    }

    [Fact]
    public void Pick_AllDistinct_Abstains()
    {
        // 1-1-1 → no bucket reaches minAgreement 2 → abstain (null winner).
        var vote = SelfConsistencyVote.Pick(new[] { "A", "B", "C" }, minAgreement: 2);
        Assert.Null(vote.WinnerIndex);
        Assert.Equal(1, vote.Agreement);   // strongest bucket was still only 1
    }

    [Fact]
    public void Pick_MinAgreementOne_AllDistinct_PicksFirst()
    {
        // With minAgreement 1 even a singleton bucket qualifies; first-seen wins deterministically.
        var vote = SelfConsistencyVote.Pick(new[] { "A", "B", "C" }, minAgreement: 1);
        Assert.Equal(0, vote.WinnerIndex);
        Assert.Equal(1, vote.Agreement);
    }

    [Fact]
    public void Pick_Tie_PrefersBucketWithCandidate0()
    {
        // Two buckets of size 2: {A:idx0,1} and {B:idx2,3}. Candidate 0 is in the "A" bucket → it wins,
        // even though "B" is equally large. Without the candidate-0 tiebreak this would be ambiguous.
        var vote = SelfConsistencyVote.Pick(
            new[] { "A", "A", "B", "B" }, minAgreement: 2, candidate0Index: 2);
        Assert.NotNull(vote.WinnerIndex);
        // candidate0Index=2 lives in the "B" bucket, so "B" must win.
        Assert.Contains(vote.WinnerIndex!.Value, new[] { 2, 3 });
    }

    [Fact]
    public void Pick_Tie_NoCandidate0_FewerLossyRepairsWins()
    {
        // Two equal buckets, no candidate-0 hint. Tiebreak (b): the bucket with the fewest lossy repairs.
        // Bucket "A" (idx 0,1) has lossy counts {1,1}; bucket "B" (idx 2,3) has {0,0} → "B" wins.
        var vote = SelfConsistencyVote.Pick(
            new[] { "A", "A", "B", "B" }, minAgreement: 2,
            candidate0Index: null,
            lossyRepairCounts: new[] { 1, 1, 0, 0 });
        Assert.NotNull(vote.WinnerIndex);
        Assert.Contains(vote.WinnerIndex!.Value, new[] { 2, 3 });
    }

    [Fact]
    public void Pick_Tie_NonEmptyBeatsEmpty()
    {
        // Equal buckets, no candidate-0, equal lossy → tiebreak (c): non-empty over empty (via rowCounts).
        // Bucket "A" (idx 0,1) rowCounts {0,0}; bucket "B" (idx 2,3) rowCounts {5,5} → non-empty "B" wins.
        var vote = SelfConsistencyVote.Pick(
            new[] { "A", "A", "B", "B" }, minAgreement: 2,
            candidate0Index: null,
            lossyRepairCounts: new[] { 0, 0, 0, 0 },
            rowCounts: new[] { 0, 0, 5, 5 });
        Assert.NotNull(vote.WinnerIndex);
        Assert.Contains(vote.WinnerIndex!.Value, new[] { 2, 3 });
    }

    [Fact]
    public void Pick_Empty_Abstains()
    {
        var vote = SelfConsistencyVote.Pick(Array.Empty<string>(), minAgreement: 1);
        Assert.Null(vote.WinnerIndex);
        Assert.Equal(0, vote.Agreement);
    }

    // ── Fingerprint: scalar identity + numeric tolerance ────────────────────────────────

    [Fact]
    public void Fingerprint_ScalarCells_AgreeRegardlessOfColumnAlias()
    {
        // Same scalar value under different column aliases must produce the SAME fingerprint (so two
        // candidates that both COUNT to 42 agree even when one aliases it "Ct" and the other "Total").
        var a = Scalar("Ct", 42);
        var b = Scalar("Total", 42);
        Assert.Equal(
            SelfConsistencyVote.Fingerprint(a, numericTolerance: 4),
            SelfConsistencyVote.Fingerprint(b, numericTolerance: 4));
    }

    [Fact]
    public void Fingerprint_NumericTolerance_Merges_4_0000_And_4_00004()
    {
        // Float noise within the tolerance collapses: 4.0000 and 4.00004 must share a bucket at tol=4.
        var a = Scalar("Avg", 4.0000d);
        var b = Scalar("Avg", 4.00004d);
        Assert.Equal(
            SelfConsistencyVote.Fingerprint(a, numericTolerance: 4),
            SelfConsistencyVote.Fingerprint(b, numericTolerance: 4));

        // And the whole pipeline agrees: two candidates with those near-equal scalars VOTE together.
        var fps = new[]
        {
            SelfConsistencyVote.Fingerprint(a, 4),
            SelfConsistencyVote.Fingerprint(b, 4),
        };
        var vote = SelfConsistencyVote.Pick(fps, minAgreement: 2);
        Assert.NotNull(vote.WinnerIndex);
        Assert.Equal(2, vote.Agreement);
    }

    [Fact]
    public void Fingerprint_DifferentScalars_DoNotAgree()
    {
        var a = Scalar("Ct", 42);
        var b = Scalar("Ct", 43);
        Assert.NotEqual(
            SelfConsistencyVote.Fingerprint(a, numericTolerance: 4),
            SelfConsistencyVote.Fingerprint(b, numericTolerance: 4));
    }

    [Fact]
    public void Fingerprint_EmptyResult_IsItsOwnBucket_AndLosesToNonEmpty()
    {
        var empty = new ExecutionResult(Array.Empty<IReadOnlyDictionary<string, object?>>(), 0, TimeSpan.Zero);
        var nonEmpty = Scalar("Ct", 7);
        Assert.NotEqual(
            SelfConsistencyVote.Fingerprint(empty, 4),
            SelfConsistencyVote.Fingerprint(nonEmpty, 4));
    }

    private static ExecutionResult Scalar(string column, object value)
    {
        var row = new Dictionary<string, object?> { [column] = value };
        return new ExecutionResult(new IReadOnlyDictionary<string, object?>[] { row }, RowCount: 1, Elapsed: TimeSpan.Zero);
    }
}
