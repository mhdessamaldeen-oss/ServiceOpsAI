namespace AnalystAgent.Tests.Grounding;

using System.Collections.Generic;
using AnalystAgent.Grounding;
using Xunit;

/// <summary>
/// FIX A — STATUS-VERB OVER-FILTER. The inline-enum value pass must SKIP binding an enum word as a status
/// filter when the word is used as a past-tense VERB introducing a clause ("bills <b>issued so far this
/// year</b>" is a date filter, NOT <c>Status='Issued'</c>) while STILL binding it as an ADJECTIVE modifying
/// the entity ("<b>overdue bills</b>", "<b>open tickets</b>").
///
/// <para>These tests pin the pure <see cref="ValueLinker.IsVerbContext"/> decision directly, on the FOLDED,
/// space-padded corpus (the form the production caller passes). The disambiguation is value-list-free and
/// schema-driven — the entity-noun set is the table Name + auto-generated Synonyms — so it is portable to any
/// schema; no table/column/value vocabulary appears here beyond the placeholders the test itself supplies.</para>
/// </summary>
public class ValueLinkerVerbContextTests
{
    // Entity-noun set for a Bills table (Name + singular/plural synonyms), as BuildEntityNouns would produce.
    private static readonly IReadOnlyCollection<string> BillNouns =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "bills", "bill" };

    private static readonly IReadOnlyCollection<string> TicketNouns =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "tickets", "ticket" };

    private static readonly IReadOnlyCollection<string> WorkOrderNouns =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "work orders", "work order", "orders", "order" };

    // The English closed-class verb-context cue sets, passed IN to IsVerbContext (now a parameter, sourced
    // per-locale from ILinguisticRegistry in production). Byte-identical to the English fallback so these
    // assertions pin the same behaviour the externalisation preserves. INCLUDES to/for/with — the 2026-06
    // "assigned to"/"allocated for"/"with" over-filter fix (see DeptsWithNoWorkOrderAssignedTo tests).
    private static readonly IReadOnlySet<string> EnPrepositions =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        { "in", "by", "on", "during", "over", "since", "between", "within", "before", "after", "from", "to", "for", "with" };

    private static readonly IReadOnlySet<string> EnTimeCues =
        new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        { "this", "last", "next", "so", "today", "yesterday", "tomorrow", "ago", "ytd", "now", "currently",
          "recently", "lately", "yet", "already", "still", "when", "while", "until", "till" };

    /// <summary>Build the FOLDED, space-padded corpus the production caller (LinkAsync) feeds IsVerbContext.</summary>
    private static string Folded(string question) => " " + ValueLinker.FoldForMatch(question.ToLowerInvariant()) + " ";

    // ── Time-cue arm ON (the fix) ────────────────────────────────────────────────────────────

    [Fact]
    public void Verb_before_time_cue_so_is_verb_context_skip()
    {
        // "bills issued so far this year" — "issued" before "so" → VERB (date clause) → SKIP binding.
        var q = Folded("how many bills were issued so far this year");
        Assert.True(ValueLinker.IsVerbContext(q, "issued", BillNouns, EnPrepositions, EnTimeCues, enableTimeCueArm: true));
    }

    [Fact]
    public void Verb_before_time_cue_last_is_verb_context_skip()
    {
        // "tickets closed last month" — "closed" before "last" → VERB → SKIP.
        var q = Folded("how many tickets were closed last month");
        Assert.True(ValueLinker.IsVerbContext(q, "closed", TicketNouns, EnPrepositions, EnTimeCues, enableTimeCueArm: true));
    }

    [Fact]
    public void Verb_before_bare_year_is_verb_context_skip()
    {
        // "bills issued 2024" — "issued" before a bare number → VERB → SKIP.
        var q = Folded("bills issued 2024");
        Assert.True(ValueLinker.IsVerbContext(q, "issued", BillNouns, EnPrepositions, EnTimeCues, enableTimeCueArm: true));
    }

    // ── Attributive override (wins) — real status filters still bind ──────────────────────────

    [Fact]
    public void Adjective_before_entity_noun_overdue_bills_binds()
    {
        // "overdue bills" — "overdue" before the entity noun "bills" → ADJECTIVE → BIND (not verb context).
        var q = Folded("how many overdue bills do we have");
        Assert.False(ValueLinker.IsVerbContext(q, "overdue", BillNouns, EnPrepositions, EnTimeCues, enableTimeCueArm: true));
    }

    [Fact]
    public void Adjective_before_entity_noun_open_tickets_binds()
    {
        // "open tickets" — "open" before the entity noun "tickets" → ADJECTIVE → BIND.
        var q = Folded("list the open tickets");
        Assert.False(ValueLinker.IsVerbContext(q, "open", TicketNouns, EnPrepositions, EnTimeCues, enableTimeCueArm: true));
    }

    [Fact]
    public void Verb_before_preposition_is_verb_context_skip_independent_of_arm()
    {
        // "bills issued in the last 30 days" — "issued" before "in" → VERB → SKIP, even with the arm OFF
        // (the preposition test predates and is independent of the time-cue arm).
        var q = Folded("bills issued in the last 30 days");
        Assert.True(ValueLinker.IsVerbContext(q, "issued", BillNouns, EnPrepositions, EnTimeCues, enableTimeCueArm: false));
    }

    // ── "assigned to" / "allocated for" / "... with" — the 2026-06 verb-preposition gap (to/for/with) ──────
    // BUG: "how many departments have no work order ASSIGNED TO them" → the model adds Status='Assigned';
    // because 'to'/'for'/'with' were NOT in the preposition set, 'Assigned' was BOUND and force-filtered →
    // a confidently-wrong narrower count. With to/for/with added, the enum word before them reads as a VERB.

    [Fact]
    public void Verb_before_to_is_verb_context_skip()
    {
        // "work order assigned to them" — "assigned" before "to" → VERB → SKIP (even arm OFF: it's a preposition).
        var q = Folded("how many departments have no work order assigned to them");
        Assert.True(ValueLinker.IsVerbContext(q, "assigned", WorkOrderNouns, EnPrepositions, EnTimeCues, enableTimeCueArm: false));
    }

    [Fact]
    public void Verb_before_for_is_verb_context_skip()
    {
        // "work orders allocated for the region" — "allocated" before "for" → VERB → SKIP.
        var q = Folded("work orders allocated for the northern region");
        Assert.True(ValueLinker.IsVerbContext(q, "allocated", WorkOrderNouns, EnPrepositions, EnTimeCues, enableTimeCueArm: false));
    }

    [Fact]
    public void Verb_before_with_is_verb_context_skip()
    {
        // "tickets resolved with a workaround" — "resolved" before "with" → VERB → SKIP.
        var q = Folded("tickets resolved with a workaround");
        Assert.True(ValueLinker.IsVerbContext(q, "resolved", TicketNouns, EnPrepositions, EnTimeCues, enableTimeCueArm: false));
    }

    [Fact]
    public void Attributive_value_before_entity_noun_still_binds_with_to_for_with_in_set()
    {
        // The CAUTION: adding to/for/with must NOT break the attributive case. "assigned tickets" — the
        // value is BEFORE the entity noun → the attributive override (rule 1) fires and returns BIND, proving
        // the new prepositions only catch the post-value VERB usage, never the adjective-before-noun case.
        var q = Folded("how many assigned tickets are there");
        Assert.False(ValueLinker.IsVerbContext(q, "assigned", TicketNouns, EnPrepositions, EnTimeCues, enableTimeCueArm: false));
    }

    // ── Time-cue arm OFF — time-cue / number cases fall back to BIND ───────────────────────────

    [Fact]
    public void With_arm_off_time_cue_falls_back_to_bind()
    {
        // Arm OFF: "issued" before "so" no longer reads as verb context → falls back to BIND (false).
        var q = Folded("how many bills were issued so far this year");
        Assert.False(ValueLinker.IsVerbContext(q, "issued", BillNouns, EnPrepositions, EnTimeCues, enableTimeCueArm: false));
    }

    [Fact]
    public void With_arm_off_bare_number_falls_back_to_bind()
    {
        // Arm OFF: "issued" before a bare number no longer reads as verb context → BIND (false).
        var q = Folded("bills issued 2024");
        Assert.False(ValueLinker.IsVerbContext(q, "issued", BillNouns, EnPrepositions, EnTimeCues, enableTimeCueArm: false));
    }

    // ── Bare usage — value ends the question → BIND (original behaviour) ───────────────────────

    [Fact]
    public void Value_at_end_of_question_binds()
    {
        // No token after the value ("on hold") → adjective/bare usage → BIND, regardless of the arm.
        var q = Folded("list the work orders that are on hold");
        Assert.False(ValueLinker.IsVerbContext(q, "on hold", null!, EnPrepositions, EnTimeCues, enableTimeCueArm: true));
    }

    [Fact]
    public void Value_absent_from_question_returns_false()
    {
        // Value not present in the corpus at all → false (no binding decision to skip).
        var q = Folded("how many bills do we have");
        Assert.False(ValueLinker.IsVerbContext(q, "issued", BillNouns, EnPrepositions, EnTimeCues, enableTimeCueArm: true));
    }
}
