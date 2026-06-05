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

        // Value hint is PRESCRIPTIVE: a value LINK means the question literally names the value, so the
        // filter is required (the declarative version made the 7B drop it). Spurious filters the model
        // INVENTS without a grounded value are handled by the separate over-filter guard, not here.
        Assert.Contains(hints, h => h.Contains("the question names 'Malki'") && h.Contains("WHERE Regions.NameEn = 'Malki'"));
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

    // ── Over-filter guard: strip a status filter the model INVENTED (literal not in question, not grounded) ──
    [Fact]
    public void StripUnrequestedStatusFilter_RemovesInvented_KeepsRequested()
    {
        // invented: "total of all bills" never says 'Paid' and nothing grounded it → strip
        Assert.True(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            "SELECT SUM(TotalAmount) FROM Bills WHERE Status = 'Paid'", "what is the total amount of all bills",
            System.Array.Empty<string>(), out var r1));
        Assert.Equal(Norm("SELECT SUM(TotalAmount) FROM Bills"), Norm(r1));

        // requested via the question text → keep
        Assert.False(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            "SELECT COUNT(*) FROM Bills WHERE Status = 'Paid'", "how many paid bills",
            System.Array.Empty<string>(), out _));

        // requested via a GROUNDED value (synonym: 'unpaid' → Overdue) → keep
        Assert.False(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            "SELECT COUNT(*) FROM Bills WHERE Status = 'Overdue'", "how many unpaid bills",
            new[] { "Overdue" }, out _));

        // invented status next to a real predicate → strip only the status
        Assert.True(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            "SELECT SUM(TotalAmount) FROM Bills WHERE Status = 'Paid' AND TotalAmount > 100", "total of all bills",
            System.Array.Empty<string>(), out var r4));
        Assert.Contains("TotalAmount > 100", r4);
        Assert.DoesNotContain("'Paid'", r4);

        // int FK StatusId = 5 (no quoted literal) → never matched
        Assert.False(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            "SELECT * FROM Tickets WHERE StatusId = 5", "tickets", System.Array.Empty<string>(), out _));
    }

    // ── trustGroundingOnly: grounding is the sole authority; the verb-blind question-contains fallback is off ──
    [Fact]
    public void StripUnrequestedStatusFilter_TrustGroundingOnly_DropsVerbWordFilter()
    {
        // "bills issued so far this year" — the word "issued" IS in the question (as a date verb), but the value-
        // linker did NOT ground 'Issued' (verb context). Legacy (false) keeps it via question-contains; the new
        // grounding-only mode strips it.
        const string sql = "SELECT COUNT(*) FROM Bills WHERE Status = 'Issued' AND IssuedAt >= '2026-01-01'";
        const string q = "how many bills were issued so far this year";

        // legacy fallback ON (default false): the word is in the question → KEEP (no change) — documents old behavior
        Assert.False(DirectAnalystPath.TryStripUnrequestedStatusFilter(sql, q, System.Array.Empty<string>(), out _));

        // grounding-only (true): 'Issued' not grounded → STRIP the status, keep the real date predicate
        Assert.True(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            sql, q, System.Array.Empty<string>(), out var r, trustGroundingOnly: true));
        Assert.DoesNotContain("'Issued'", r);
        Assert.Contains("IssuedAt >= '2026-01-01'", r);

        // grounding-only but the literal IS grounded ("overdue bills" → 'Overdue') → KEEP
        Assert.False(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            "SELECT COUNT(*) FROM Bills WHERE Status = 'Overdue'", "how many overdue bills",
            new[] { "Overdue" }, out _, trustGroundingOnly: true));
    }

    // ── Over-filter guard (boolean flags): strip an invented Is<Concept>=0/1 not named in the question ──
    [Fact]
    public void StripUnrequestedFlagFilter_RemovesInvented_KeepsRequested_AndSoftDelete()
    {
        // invented IsPlanned=0 on "critical outages" (question never says planned) → strip (6 → 7)
        Assert.True(DirectAnalystPath.TryStripUnrequestedFlagFilter(
            "SELECT COUNT(*) FROM Outages WHERE Severity = 'Critical' AND IsPlanned = 0",
            "how many critical outages have we had", out var r1));
        Assert.Equal(Norm("SELECT COUNT(*) FROM Outages WHERE Severity = 'Critical'"), Norm(r1));

        // soft-delete IsDeleted=0 is a structural invariant → ALWAYS keep
        Assert.False(DirectAnalystPath.TryStripUnrequestedFlagFilter(
            "SELECT COUNT(*) FROM Tickets WHERE IsDeleted = 0", "how many tickets", out _));

        // requested flag — the question literally says "active" → keep
        Assert.False(DirectAnalystPath.TryStripUnrequestedFlagFilter(
            "SELECT * FROM Departments WHERE IsActive = 1", "list active departments", out _));

        // invented IsActive on a question that does NOT say active → strip
        Assert.True(DirectAnalystPath.TryStripUnrequestedFlagFilter(
            "SELECT * FROM Departments WHERE IsActive = 1", "list all departments", out var r4));
        Assert.DoesNotContain("IsActive", r4);

        // int FK (StatusId = 5) is not an Is<flag> → never matched
        Assert.False(DirectAnalystPath.TryStripUnrequestedFlagFilter(
            "SELECT * FROM Tickets WHERE StatusId = 5", "tickets", out _));
    }

    // ── Ungrounded relative-date strip: complete the inline-enum overdue fix ──────────────────────
    [Fact]
    public void StripUngroundedDatePredicate_RemovesInventedRelativeDate_WhenValueGrounded_NoTemporalCue()
    {
        // "overdue bills" grounded Bills.Status='Overdue'; model invented WHERE DueDate < GETDATE() → strip it
        Assert.True(DirectAnalystPath.TryStripUngroundedDatePredicate(
            "SELECT COUNT(*) FROM Bills WHERE DueDate < GETDATE()", hasGroundedValue: true, hasTemporalCue: false, out var r1));
        Assert.DoesNotContain("GETDATE", r1);
        Assert.Equal(Norm("SELECT COUNT(*) FROM Bills"), Norm(r1));

        // strips ONLY the relative-date predicate, keeps the real grounded status filter
        Assert.True(DirectAnalystPath.TryStripUngroundedDatePredicate(
            "SELECT COUNT(*) FROM Bills WHERE Status = 'Overdue' AND DueDate < GETDATE()", true, false, out var r2));
        Assert.Contains("Status = 'Overdue'", r2);
        Assert.DoesNotContain("GETDATE", r2);

        // a TEMPORAL CUE in the question → never touch a date filter (legit "bills due before March")
        Assert.False(DirectAnalystPath.TryStripUngroundedDatePredicate(
            "SELECT * FROM Bills WHERE DueDate < GETDATE()", hasGroundedValue: true, hasTemporalCue: true, out _));

        // no grounded value → don't touch (nothing enforces the real filter)
        Assert.False(DirectAnalystPath.TryStripUngroundedDatePredicate(
            "SELECT * FROM Bills WHERE DueDate < GETDATE()", hasGroundedValue: false, hasTemporalCue: false, out _));

        // a LITERAL-date filter is NEVER stripped (not a server-clock comparison)
        Assert.False(DirectAnalystPath.TryStripUngroundedDatePredicate(
            "SELECT * FROM Tickets WHERE ResolvedAt >= '2026-01-01'", true, false, out _));
    }

    // ── Grounded-value injector: enforce a named filter the flaky 7B dropped (symmetric to the strip) ──
    [Fact]
    public void InjectGroundedValueFilters_EnforcesDroppedFilter_Conservatively()
    {
        var open = new[] { ("TicketStatuses", "Name", "Open") };

        // joined, no filter → inject (unaliased table → qualify by table name)
        Assert.True(DirectAnalystPath.TryInjectGroundedValueFilters(
            "SELECT COUNT(*) FROM Tickets JOIN TicketStatuses ON Tickets.StatusId = TicketStatuses.Id WHERE Tickets.IsDeleted = 0",
            open, out var r1));
        Assert.Contains("AND TicketStatuses.Name = 'Open'", r1);

        // ALIASED table → must qualify by the alias, never the table name
        Assert.True(DirectAnalystPath.TryInjectGroundedValueFilters(
            "SELECT COUNT(*) FROM Tickets t JOIN TicketStatuses s ON t.StatusId = s.Id WHERE t.IsDeleted = 0",
            open, out var r2));
        Assert.Contains("s.Name = 'Open'", r2);
        Assert.DoesNotContain("TicketStatuses.Name", r2);

        // no WHERE → adds one
        Assert.True(DirectAnalystPath.TryInjectGroundedValueFilters(
            "SELECT COUNT(*) FROM Tickets JOIN TicketStatuses ON Tickets.StatusId = TicketStatuses.Id",
            open, out var r3));
        Assert.Contains("WHERE TicketStatuses.Name = 'Open'", r3);

        // already filtered → no-op
        Assert.False(DirectAnalystPath.TryInjectGroundedValueFilters(
            "SELECT COUNT(*) FROM Tickets JOIN TicketStatuses ON Tickets.StatusId = TicketStatuses.Id WHERE TicketStatuses.Name = 'Open'",
            open, out _));

        // table not in query → no-op (never fabricate a join)
        Assert.False(DirectAnalystPath.TryInjectGroundedValueFilters(
            "SELECT COUNT(*) FROM Tickets WHERE IsDeleted = 0", open, out _));

        // GROUP BY present → skipped (conservative)
        Assert.False(DirectAnalystPath.TryInjectGroundedValueFilters(
            "SELECT s.Name, COUNT(*) FROM Tickets t JOIN TicketStatuses s ON t.StatusId = s.Id GROUP BY s.Name",
            open, out _));

        // ORDER BY present → filter goes BEFORE it
        Assert.True(DirectAnalystPath.TryInjectGroundedValueFilters(
            "SELECT TOP 5 t.Id FROM Tickets t JOIN TicketStatuses s ON t.StatusId = s.Id WHERE t.IsDeleted = 0 ORDER BY t.CreatedAt DESC",
            open, out var r7));
        Assert.Matches(@"s\.Name = 'Open'\s+ORDER BY", r7);
    }

    // Repro of the LIVE open-tickets SQL byte-for-byte (multi-line, `AS OpenTicketCount` alias whose
    // text contains "Open", trailing ';') — the injector returned false live despite a (TicketStatuses,
    // Name, Open) binding. Isolates method-vs-wiring.
    [Fact]
    public void InjectGroundedValueFilters_LiveOpenSql_Repro()
    {
        var open = new[] { ("TicketStatuses", "Name", "Open") };
        var sql = "SELECT COUNT(*) AS OpenTicketCount\nFROM Tickets\nJOIN TicketStatuses ON Tickets.StatusId = TicketStatuses.Id\nWHERE Tickets.IsDeleted = 0;";
        var did = DirectAnalystPath.TryInjectGroundedValueFilters(sql, open, out var r);
        Assert.True(did);
        Assert.Contains("TicketStatuses.Name = 'Open'", r);
    }

    // ── Injector conflict-replace: a grounded value REPLACES the model's different non-grounded literal ──
    [Fact]
    public void InjectGroundedValueFilters_ReplacesConflictingModelLiteral_OnGroundedColumn()
    {
        // The Arabic case: model copied the question word as the literal (Name='مفتوحة'), grounding resolved
        // 'Open' via cross-lingual linking. The conflicting non-grounded literal is stripped so the grounded
        // value isn't AND-ed into a contradiction (which returned 0 rows).
        var open = new[] { ("TicketStatuses", "Name", "Open") };
        Assert.True(DirectAnalystPath.TryInjectGroundedValueFilters(
            "SELECT COUNT(*) FROM Tickets JOIN TicketStatuses ON Tickets.StatusId = TicketStatuses.Id WHERE TicketStatuses.Name = 'مفتوحة' AND Tickets.IsDeleted = 0",
            open, out var r));
        Assert.Contains("TicketStatuses.Name = 'Open'", r);
        Assert.DoesNotContain("مفتوحة", r);

        // multi-value: BOTH literals are grounded ("Damascus AND Aleppo") → neither is stripped, no change.
        var twoRegions = new[] { ("Regions", "NameEn", "Damascus"), ("Regions", "NameEn", "Aleppo") };
        Assert.False(DirectAnalystPath.TryInjectGroundedValueFilters(
            "SELECT COUNT(*) FROM Tickets t JOIN Regions r ON t.RegionId=r.Id WHERE r.NameEn = 'Damascus' AND r.NameEn = 'Aleppo'",
            twoRegions, out _));
    }

    // ── Standalone contradiction repair: same column = two different literals, keep grounded, drop other ──
    // This is the LIVE A-OpenTickets failure the in-injector conflict-replace missed: the model wrote BOTH the
    // Arabic word AND the English value, on a BRACKETED column form (TicketStatuses.[Name]), so both survived
    // and AND-ed to 0 rows.
    [Fact]
    public void ResolveContradictoryEqualityLiterals_LiveOpenTickets_DropsArabicKeepsGrounded()
    {
        var sql = "SELECT COUNT(Tickets.Id) AS NumberOfOpenTickets FROM Tickets JOIN TicketStatuses ON Tickets.StatusId = TicketStatuses.Id WHERE TicketStatuses.[Name] = 'مفتوحة' AND Tickets.IsDeleted = 0 AND TicketStatuses.Name = 'Open'";
        Assert.True(DirectAnalystPath.TryResolveContradictoryEqualityLiterals(sql, new[] { "Open" }, out var r));
        Assert.DoesNotContain("مفتوحة", r);
        Assert.Contains("TicketStatuses.Name = 'Open'", r);
        Assert.Contains("Tickets.IsDeleted = 0", r);   // unrelated predicate preserved
    }

    [Fact]
    public void ResolveContradictoryEqualityLiterals_LeavesLegitMultiValueAndCleanSql_Untouched()
    {
        // Both literals grounded (a real multi-value filter, though oddly ANDed) → no grounded-vs-ungrounded
        // split on the column → no change.
        Assert.False(DirectAnalystPath.TryResolveContradictoryEqualityLiterals(
            "SELECT COUNT(*) FROM Regions r WHERE r.NameEn = 'Damascus' AND r.NameEn = 'Aleppo'",
            new[] { "Damascus", "Aleppo" }, out _));

        // A contradiction with NO grounded anchor → can't safely pick which to keep → no change.
        Assert.False(DirectAnalystPath.TryResolveContradictoryEqualityLiterals(
            "SELECT COUNT(*) FROM T WHERE Status = 'A' AND Status = 'B'",
            new[] { "Open" }, out _));

        // A clean single-literal filter → no change.
        Assert.False(DirectAnalystPath.TryResolveContradictoryEqualityLiterals(
            "SELECT COUNT(*) FROM Bills WHERE Status = 'Paid'",
            new[] { "Paid" }, out _));

        // No grounded values at all → no change.
        Assert.False(DirectAnalystPath.TryResolveContradictoryEqualityLiterals(
            "SELECT COUNT(*) FROM T WHERE Name = 'x' AND Name = 'y'",
            System.Array.Empty<string>(), out _));
    }

    // ── Load-bearing-lossy-strip abstain gate (#5): makes the repair-provenance keystone act ──
    [Fact]
    public void AbstainAfterLoadBearingLossyStrip_OnlyWhenValueStripped_AndUngrounded()
    {
        var opts = new AnalystAgent.Configuration.AnalystOptions();   // AbstainOnLoadBearingLossyStrip = true (default)
        var ungrounded = QuestionGroundingContext.Empty;
        var grounded = new QuestionGroundingContext
        {
            LinkedValues = new[] { new ValueLinkBinding("Bills", "Status", "Paid", "paid", 1.0f) }
        };

        // A value filter was lossily dropped AND nothing grounded replaced it → over-broad → ABSTAIN.
        Assert.True(DirectAnalystPath.ShouldAbstainAfterLoadBearingLossyStrip(true, ungrounded, opts));
        // Grounding resolved a real value (the injector enforced the right filter) → strip is harmless → KEEP.
        Assert.False(DirectAnalystPath.ShouldAbstainAfterLoadBearingLossyStrip(true, grounded, opts));
        // A flag strip (no value literal) is not load-bearing → KEEP.
        Assert.False(DirectAnalystPath.ShouldAbstainAfterLoadBearingLossyStrip(false, ungrounded, opts));
        // Gate disabled → never abstain.
        opts.AbstainOnLoadBearingLossyStrip = false;
        Assert.False(DirectAnalystPath.ShouldAbstainAfterLoadBearingLossyStrip(true, ungrounded, opts));
    }

    private static string Norm(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
}
