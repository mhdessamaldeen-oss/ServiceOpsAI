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

    // ── Multi-period grounding hint: >1 temporal binding → ONE permissive hint, not N contradictory ones ──
    // Two strict "filter NO other period" lines jointly forbid the OR-of-ranges a "compare Q1 and Q3" question
    // needs (silent under-fetch). With AllowMultiPeriodHints on, the >1 case collapses to a single permissive
    // hint that names BOTH periods and asks for a range per period (OR / UNION).
    [Fact]
    public void BuildGroundingHints_TwoPeriods_EmitsSinglePermissiveHint_NamingBoth_NotForbidding()
    {
        var g = new QuestionGroundingContext
        {
            LinkedTemporal = new[]
            {
                new TemporalBinding("Q1 2025", "@quarter_start", "@quarter_end", "gte"),
                new TemporalBinding("Q3 2025", "@quarter_start", "@quarter_end", "gte"),
            },
        };

        var hints = DirectAnalystPath.BuildGroundingHints(g, allowMultiPeriodHints: true);

        // Exactly ONE temporal/period hint (no other grounding facts in this context).
        var periodHints = hints.Where(h => h.Contains("period")).ToList();
        Assert.Single(periodHints);
        var hint = periodHints[0];
        // Names BOTH period labels.
        Assert.Contains("Q1 2025", hint);
        Assert.Contains("Q3 2025", hint);
        // Does NOT carry the contradictory single-period instruction.
        Assert.DoesNotContain(hints, h => h.Contains("NO other period"));
        // Asks for a per-period range combined with OR / UNION (the load-bearing permissive wording).
        Assert.Contains("EACH period", hint);
    }

    // One binding → the strict single-period hint is UNCHANGED (the multi-period branch never fires).
    [Fact]
    public void BuildGroundingHints_OnePeriod_KeepsStrictSinglePeriodHint()
    {
        var g = new QuestionGroundingContext
        {
            LinkedTemporal = new[] { new TemporalBinding("this month", "@month_start", null, "gte") },
        };

        var hints = DirectAnalystPath.BuildGroundingHints(g, allowMultiPeriodHints: true);

        Assert.Contains(hints, h => h.Contains("the question covers the period 'this month'") && h.Contains("filter NO other period"));
        Assert.DoesNotContain(hints, h => h.Contains("multiple periods"));
    }

    // Flag OFF → legacy behavior: each of the >1 bindings emits its own strict per-period hint.
    [Fact]
    public void BuildGroundingHints_MultiPeriod_FlagOff_EmitsPerBindingStrictHints()
    {
        var g = new QuestionGroundingContext
        {
            LinkedTemporal = new[]
            {
                new TemporalBinding("Q1 2025", "@quarter_start", "@quarter_end", "gte"),
                new TemporalBinding("Q3 2025", "@quarter_start", "@quarter_end", "gte"),
            },
        };

        var hints = DirectAnalystPath.BuildGroundingHints(g, allowMultiPeriodHints: false);

        // Two strict per-binding hints (the pre-fix contradictory behavior), no collapsed multi-period line.
        Assert.Equal(2, hints.Count(h => h.Contains("filter NO other period")));
        Assert.DoesNotContain(hints, h => h.Contains("multiple periods"));
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
    // sole predicate directly before OFFSET (paging) → the now-empty WHERE is dropped cleanly (boundary-set gap fix)
    [InlineData(
        "SELECT Id FROM Customers WHERE IsDeleted = 0 OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY",
        "Invalid column name 'IsDeleted'.",
        "SELECT Id FROM Customers OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY")]
    // sole predicate before UNION (set operator) → the now-empty WHERE is dropped before UNION (boundary-set gap fix)
    [InlineData(
        "SELECT Id FROM Customers WHERE IsDeleted = 0 UNION SELECT Id FROM Suppliers",
        "Invalid column name 'IsDeleted'.",
        "SELECT Id FROM Customers UNION SELECT Id FROM Suppliers")]
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

        // INEQUALITY over-filter: "how many customers in total" → the 7B adds Status != 'Churned' (not grounded,
        // not requested) → strip it so the count is the true total. Same rule, != / <> as well as =.
        Assert.True(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            "SELECT COUNT(*) FROM Customers WHERE Status != 'Churned'", "how many customers do we have in total",
            System.Array.Empty<string>(), out var rNe, trustGroundingOnly: true));
        Assert.DoesNotContain("Churned", rNe);
        // a grounded inequality stays ("customers who are not churned" → 'Churned' grounded) → KEEP
        Assert.False(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            "SELECT COUNT(*) FROM Customers WHERE Status <> 'Churned'", "customers who are not churned",
            new[] { "Churned" }, out _, trustGroundingOnly: true));
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

    // ── BUG C: whole-word concept match, not a truncated-stem/substring (morphologically + verb aware) ──
    [Fact]
    public void StripUnrequestedFlagFilter_WholeWordConcept_NotSubstringStem()
    {
        // "active accounts" names the concept as a WHOLE WORD → keep IsActive
        Assert.False(DirectAnalystPath.TryStripUnrequestedFlagFilter(
            "SELECT COUNT(*) FROM Accounts WHERE IsActive = 1", "list active accounts", out _));

        // "list activities" shares only a truncated prefix of "active" — NOT a whole word → strip IsActive.
        // (The old Substring(0,len-2) stem 'activ' / the old Contains('active') substring both wrongly KEPT it.)
        Assert.True(DirectAnalystPath.TryStripUnrequestedFlagFilter(
            "SELECT COUNT(*) FROM Accounts WHERE IsActive = 1", "list activities", out var rAct));
        Assert.DoesNotContain("IsActive", rAct);

        // simple morphological tail still counts ("actively") — leading \b, trailing free
        Assert.False(DirectAnalystPath.TryStripUnrequestedFlagFilter(
            "SELECT COUNT(*) FROM Users WHERE IsActive = 1", "users actively logging in", out _));

        // IsDeleted is the soft-delete invariant → ALWAYS kept, even when "deleted" is nowhere in the question
        Assert.False(DirectAnalystPath.TryStripUnrequestedFlagFilter(
            "SELECT COUNT(*) FROM Tickets WHERE IsDeleted = 0", "how many tickets", out _));

        // grounding authority: the concept was grounded → keep even if the literal word isn't in the question
        Assert.False(DirectAnalystPath.TryStripUnrequestedFlagFilter(
            "SELECT COUNT(*) FROM Outages WHERE IsPlanned = 1", "scheduled maintenance", out _,
            groundedConcepts: new[] { "Planned" }));
    }

    // ── BUG B: the status-column match is END-ANCHORED on Status/State, not "contains Status" ──
    [Fact]
    public void StripUnrequestedStatusFilter_OnlyEndAnchoredStatusOrState_NotAnyStarStatusStar()
    {
        // real lifecycle columns ending in Status/State → eligible to strip an invented literal
        Assert.True(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            "SELECT COUNT(*) FROM Bills WHERE Status = 'X'", "all bills", System.Array.Empty<string>(), out _));
        Assert.True(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            "SELECT COUNT(*) FROM Orders WHERE OrderStatus = 'X'", "all orders", System.Array.Empty<string>(), out _));
        Assert.True(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            "SELECT COUNT(*) FROM Accounts WHERE AccountState = 'X'", "all accounts", System.Array.Empty<string>(), out _));

        // columns that merely CONTAIN "Status" but do NOT end in it → a legitimate ungrounded filter is left alone
        Assert.False(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            "SELECT COUNT(*) FROM Reports WHERE StatusReport = 'X'", "all reports", System.Array.Empty<string>(), out _));
        Assert.False(DirectAnalystPath.TryStripUnrequestedStatusFilter(
            "SELECT COUNT(*) FROM Users WHERE UserStatusFlag = 'X'", "all users", System.Array.Empty<string>(), out _));
    }

    // ── BUG A: load-bearing strip rides on PREDICATE literals only — a FORMAT/CASE literal never moves the count ──
    [Fact]
    public void CountPredicateValueLiterals_CountsOnlyComparisonContextLiterals_NotFunctionOrCaseLabels()
    {
        // A value FILTER literal (after '=') is counted; the FORMAT('yyyy-MM') time-bucket literal is NOT.
        const string withStatusAndBucket =
            "SELECT FORMAT(CreatedAt,'yyyy-MM') AS m, COUNT(*) FROM Tickets WHERE Status = 'X' GROUP BY FORMAT(CreatedAt,'yyyy-MM')";
        // Stripping the Status predicate must register as load-bearing: the predicate-literal count drops by 1
        // (1 → 0) while the two FORMAT literals are never counted (they stay invisible to the count).
        const string afterStrip =
            "SELECT FORMAT(CreatedAt,'yyyy-MM') AS m, COUNT(*) FROM Tickets GROUP BY FORMAT(CreatedAt,'yyyy-MM')";
        Assert.Equal(1, DirectAnalystPath.CountPredicateValueLiterals(withStatusAndBucket));
        Assert.Equal(0, DirectAnalystPath.CountPredicateValueLiterals(afterStrip));
        Assert.True(DirectAnalystPath.CountPredicateValueLiterals(withStatusAndBucket)
                  > DirectAnalystPath.CountPredicateValueLiterals(afterStrip));   // load-bearing: a value filter was lost

        // A query whose ONLY literal is the FORMAT bucket (nothing stripped) registers 0 predicate literals —
        // so a flag-only strip next to a time bucket is NOT mistaken for a load-bearing value strip.
        const string bucketOnly =
            "SELECT FORMAT(CreatedAt,'yyyy-MM') AS m, COUNT(*) FROM Tickets GROUP BY FORMAT(CreatedAt,'yyyy-MM')";
        Assert.Equal(0, DirectAnalystPath.CountPredicateValueLiterals(bucketOnly));

        // An IN(...) predicate is detected (the leading `IN (` literal counts once — enough to mark the
        // predicate present); a LIKE predicate literal is counted; a CASE THEN/ELSE label literal is NOT.
        Assert.Equal(1, DirectAnalystPath.CountPredicateValueLiterals(
            "SELECT * FROM T WHERE Region IN ('Damascus', 'Aleppo')"));
        Assert.Equal(1, DirectAnalystPath.CountPredicateValueLiterals(
            "SELECT CASE WHEN x = 1 THEN 'high' ELSE 'low' END, Name FROM T WHERE Name LIKE '%a%'"));
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

    // ── Multi-value same-column injection → IN(...) (not multiple ANDed equalities that yield 0 rows) ──
    [Fact]
    public void InjectGroundedValueFilters_TwoValuesSameColumn_EmitsSingleIn_OneValueStaysEquality()
    {
        // "tickets in Damascus or Aleppo" → both ground Regions.NameEn. A separate AND col='a' AND col='b'
        // is a contradiction (0 rows); the injector must emit a single col IN ('Damascus','Aleppo').
        var twoRegions = new[] { ("Regions", "NameEn", "Damascus"), ("Regions", "NameEn", "Aleppo") };
        Assert.True(DirectAnalystPath.TryInjectGroundedValueFilters(
            "SELECT COUNT(*) FROM Tickets t JOIN Regions r ON t.RegionId = r.Id",
            twoRegions, out var rIn));
        Assert.Contains("r.NameEn IN ('Damascus', 'Aleppo')", rIn);
        Assert.DoesNotContain("r.NameEn = 'Damascus' AND", rIn);   // never two ANDed equalities on one column

        // single value on the same column → plain equality, unchanged shape (no IN()).
        var oneRegion = new[] { ("Regions", "NameEn", "Damascus") };
        Assert.True(DirectAnalystPath.TryInjectGroundedValueFilters(
            "SELECT COUNT(*) FROM Tickets t JOIN Regions r ON t.RegionId = r.Id",
            oneRegion, out var rEq));
        Assert.Contains("r.NameEn = 'Damascus'", rEq);
        Assert.DoesNotContain("IN (", rEq);
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
