namespace AnalystAgent.Tests.Pipeline;

using AnalystAgent.Configuration;
using AnalystAgent.Grounding;
using AnalystAgent.Pipeline;
using Xunit;

/// <summary>
/// Layer-2 confident-hallucination backstop (<see cref="DirectAnalystPath.ShouldAbstainUngroundedProjection"/>).
/// Proves the discriminator that lets "who won the world cup" abstain while "count tickets by region"
/// still answers: zero grounded evidence + a generic label-only projection with NO aggregate call.
/// </summary>
public class UngroundedProjectionAbstainTests
{
    private static AnalystOptions Enabled() => new() { EnableUngroundedProjectionAbstain = true };

    // The world-cup signature: nothing grounded, a bare name/label projection, no aggregate → ABSTAIN.
    [Fact]
    public void ZeroGrounding_labelOnlyProjection_abstains() =>
        Assert.True(DirectAnalystPath.ShouldAbstainUngroundedProjection(
            QuestionGroundingContext.Empty,
            "SELECT TOP 1 T1.NameEn AS WinningCountry FROM Countries T1 JOIN Regions T2 ON T1.Id=T2.CountryId ORDER BY T2.NameEn",
            Enabled()));

    // "count tickets by region": grounds no specific value either, BUT emits COUNT(...) → ANSWER.
    [Fact]
    public void ZeroGrounding_withAggregate_answers() =>
        Assert.False(DirectAnalystPath.ShouldAbstainUngroundedProjection(
            QuestionGroundingContext.Empty,
            "SELECT r.NameEn, COUNT(*) AS Ct FROM Tickets t JOIN Regions r ON t.RegionId=r.Id GROUP BY r.NameEn",
            Enabled()));

    // A column NAMED with an aggregate word but no call ("AccountCount") must NOT read as COUNT(...) → ABSTAIN.
    [Fact]
    public void AggregateWordInsideColumnName_isNotAnAggregateCall_abstains() =>
        Assert.True(DirectAnalystPath.ShouldAbstainUngroundedProjection(
            QuestionGroundingContext.Empty, "SELECT AccountCount FROM Customers", Enabled()));

    // Default-OFF: the guard never fires unless an operator opts in.
    [Fact]
    public void DisabledByDefault_neverAbstains() =>
        Assert.False(DirectAnalystPath.ShouldAbstainUngroundedProjection(
            QuestionGroundingContext.Empty,
            "SELECT TOP 1 NameEn FROM Countries ORDER BY NameEn",
            new AnalystOptions()));
}
