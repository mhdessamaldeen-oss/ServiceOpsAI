namespace AnalystAgent.Tests.Pipeline;

using AnalystAgent.Grounding;
using AnalystAgent.Pipeline;
using Xunit;

/// <summary>
/// Pins the grounded-fact hint lines the direct-analyst path feeds the generator — the load-bearing
/// translation of <see cref="QuestionGroundingContext"/> into the verbatim instructions proven live
/// to make qwen2.5-coder:7b use the right column/value instead of guessing.
/// </summary>
public sealed class DirectAnalystPathHintsTests
{
    [Fact]
    public void BuildGroundingHints_RendersValue_NaturalKey_Temporal_Metric_AndIntent()
    {
        var g = new QuestionGroundingContext
        {
            LinkedValues = new[] { new ValueLinkBinding("Regions", "NameEn", "Malki", "malki", 1.0f) },
            LinkedNaturalKeys = new[] { new NaturalKeyBinding("Tickets", "Tickets", "TicketNumber", "TKT-00050") },
            LinkedTemporal = new[] { new TemporalBinding("this week", "@week_start", null, "gte") },
            DerivedMetricHints = new[] { new DerivedMetricHint("age", "DATEDIFF(DAY, Tickets.CreatedAt, GETDATE())", "AVG") },
            TimeBucketHint = "month",
            IsDistinctCountIntent = true,
            DateRoleHint = "created",
        };

        var hints = DirectAnalystPath.BuildGroundingHints(g);

        Assert.Contains(hints, h => h.Contains("'Malki' is the value Regions.NameEn") && h.Contains("Regions.NameEn = 'Malki'"));
        Assert.Contains(hints, h => h.Contains("TKT-00050") && h.Contains("Tickets.TicketNumber = 'TKT-00050'"));
        // Period is NAMED, never the raw compiler @-token (the direct path has no token expander).
        Assert.Contains(hints, h => h.Contains("period 'this week'"));
        Assert.DoesNotContain(hints, h => h.Contains("@week_start"));
        Assert.Contains(hints, h => h.Contains("PER MONTH") && h.Contains("FORMAT(<date col>,'yyyy-MM')"));
        Assert.Contains(hints, h => h.Contains("DATEDIFF(DAY, Tickets.CreatedAt, GETDATE())") && h.Contains("AVG("));
        Assert.Contains(hints, h => h.Contains("COUNT(DISTINCT"));
        Assert.Contains(hints, h => h.Contains("'created'"));
    }

    [Fact]
    public void BuildGroundingHints_Empty_WhenNothingGrounded()
    {
        Assert.Empty(DirectAnalystPath.BuildGroundingHints(QuestionGroundingContext.Empty));
    }

    // ── Deterministic invalid-column repair ────────────────────────────────────
    // The dominant 7B failure: it adds `IsDeleted = 0` to a table that lacks the column and re-emits
    // it when the error is fed back. The stripper removes that predicate so the query can run.
    [Theory]
    // leading predicate followed by AND … keeps the rest of the WHERE
    [InlineData(
        "SELECT COUNT(*) FROM Outages WHERE Outages.IsDeleted = 0 AND Outages.EndedAt IS NOT NULL GROUP BY x",
        "Invalid column name 'IsDeleted'.",
        "SELECT COUNT(*) FROM Outages WHERE Outages.EndedAt IS NOT NULL GROUP BY x")]
    // trailing AND predicate
    [InlineData(
        "SELECT * FROM Bills WHERE Status = 'Issued' AND Bills.IsDeleted = 0",
        "Invalid column name 'IsDeleted'.",
        "SELECT * FROM Bills WHERE Status = 'Issued'")]
    // sole predicate → the whole WHERE is dropped
    [InlineData(
        "SELECT COUNT(*) FROM Customers WHERE IsDeleted = 0",
        "Invalid column name 'IsDeleted'.",
        "SELECT COUNT(*) FROM Customers")]
    public void StripInvalidColumnPredicate_RemovesOffendingFilter(string sql, string error, string expected)
    {
        var changed = DirectAnalystPath.TryStripInvalidColumnPredicate(sql, error, out var repaired);
        Assert.True(changed);
        Assert.Equal(Norm(expected), Norm(repaired));
    }

    [Fact]
    public void StripInvalidColumnPredicate_LeavesCleanSqlUntouched()
    {
        var sql = "SELECT COUNT(*) FROM Tickets WHERE IsDeleted = 0";
        // error names a DIFFERENT column than the query filters on → no change
        var changed = DirectAnalystPath.TryStripInvalidColumnPredicate(sql, "Invalid column name 'Bogus'.", out var repaired);
        Assert.False(changed);
        Assert.Equal(sql, repaired);
    }

    private static string Norm(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
}
