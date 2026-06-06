namespace AnalystAgent.Pipeline;

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Abstractions;
using AnalystAgent.Configuration;
using AnalystAgent.Grounding;
using AnalystAgent.Models;
using AnalystAgent.Retrieval;
using AnalystAgent.Schema;
using AnalystAgent.Validation;

/// <summary>
/// The minimal "direct analyst" path — the lean answer to copilot weakness (NOT model weakness):
/// <c>retrieve → ground → generate SQL directly (grounded) → validate → execute → explain</c>, in
/// ~2 LLM calls, BEFORE the heavy form-filling QuerySpec pipeline.
///
/// <para>It reuses the proven pieces wholesale — the schema-semantic retriever, the deterministic
/// <see cref="IQuestionGrounder"/> (the moat), the grounding-aware <see cref="Stages.ILlmDirectSqlEmitter"/>,
/// and the same <see cref="IValidator"/> / read-only <see cref="IExecutor"/> safety net. On ANY miss
/// (no tables, no SQL, invalid SQL, execution error) it returns <c>null</c> and the caller falls
/// through to the existing pipeline unchanged — so enabling it can never regress today's behavior.</para>
///
/// <para>Validated live 2026-06-02 against qwen2.5-coder:7b on the real schema: the grounded prompt
/// produced schema-correct T-SQL for 12/12 shapes (count/filter/group/rank, above-average subquery,
/// TOP-N, HAVING, multi-join, window RANK + running-total, JOIN+filter, and Arabic), where the same
/// model without grounding silently joined the wrong column.</para>
/// </summary>
internal interface IDirectAnalystPath
{
    Task<AnalystResponse?> TryAnswerAsync(
        AnalystRequest request, string question,
        Stopwatch totalSw, BroadcastingStepList steps, CancellationToken cancellationToken);
}

internal sealed class DirectAnalystPath : IDirectAnalystPath
{
    private readonly Schema.ISchemaLinker _linker;
    private readonly ISchemaKnowledge _knowledge;
    private readonly IEntityCatalog _catalog;
    private readonly IQuestionGrounder _grounder;
    private readonly Stages.ILlmDirectSqlEmitter _emitter;
    private readonly IValidator _validator;
    private readonly IExecutor _executor;
    private readonly IExplainer _explainer;
    private readonly IResponsePersister _persister;
    private readonly IRetryBudget _retryBudget;
    private readonly IOptions<AnalystOptions> _options;
    private readonly ILogger<DirectAnalystPath> _logger;

    public DirectAnalystPath(
        Schema.ISchemaLinker linker,
        ISchemaKnowledge knowledge,
        IEntityCatalog catalog,
        IQuestionGrounder grounder,
        Stages.ILlmDirectSqlEmitter emitter,
        IValidator validator,
        IExecutor executor,
        IExplainer explainer,
        IResponsePersister persister,
        IRetryBudget retryBudget,
        IOptions<AnalystOptions> options,
        ILogger<DirectAnalystPath> logger)
    {
        _linker = linker;
        _knowledge = knowledge;
        _catalog = catalog;
        _grounder = grounder;
        _emitter = emitter;
        _validator = validator;
        _executor = executor;
        _explainer = explainer;
        _persister = persister;
        _retryBudget = retryBudget;
        _options = options;
        _logger = logger;
    }

    public async Task<AnalystResponse?> TryAnswerAsync(
        AnalystRequest request, string question,
        Stopwatch totalSw, BroadcastingStepList steps, CancellationToken cancellationToken)
    {
        try
        {
            if (!_knowledge.IsAvailable) return null;

            // 1) Schema-link by SIMILARITY (name/synonym + trigram + embedding) → matched-set FK closure.
            //    One clean component (ISchemaLinker); see its doc-comment. Tight slice for the 7B.
            var linkSw = Stopwatch.StartNew();
            var candidateNames = await _linker.LinkAsync(question, cancellationToken);
            var infTables = candidateNames
                .Select(_knowledge.GetTable)
                .Where(t => t is not null)
                .Cast<InferredTable>()
                .ToList();
            linkSw.Stop();
            if (infTables.Count == 0) return null;
            steps.RecordSchemaLink(question, candidateNames, linkSw.ElapsedMilliseconds);

            // 2) Ground (deterministic, no LLM) — resolves real values, natural keys, dates. The moat.
            QuestionGroundingContext grounding;
            var groundSw = Stopwatch.StartNew();
            try { grounding = await _grounder.GroundAsync(question, infTables, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[DirectAnalystPath] grounding failed; using empty grounding.");
                grounding = QuestionGroundingContext.Empty;
            }
            groundSw.Stop();
            steps.RecordGrounding(question, grounding, groundSw.ElapsedMilliseconds);

            // Pull in any table the grounder matched a value/natural-key in (it may be a lookup the
            // retriever missed but is needed for the filter/join).
            var names = new HashSet<string>(candidateNames, StringComparer.OrdinalIgnoreCase);
            foreach (var lv in grounding.LinkedValues) if (_catalog.TableExists(lv.Table)) names.Add(lv.Table);
            foreach (var nk in grounding.LinkedNaturalKeys) if (_catalog.TableExists(nk.Table)) names.Add(nk.Table);
            foreach (var lt in grounding.LinkedTables) if (_catalog.TableExists(lt)) names.Add(lt);

            // 3-5) Generate (grounded) → verify → execute, with ONE self-correct retry that feeds the
            //      real validator/SQL-Server error back to the model. (This is what fixes the derived-
            //      table-alias-in-subquery class the live run surfaced — the model rewrites it as a
            //      CTE.) If the corrected attempt still fails, return null and fall through to the
            //      heavy pipeline. Invented columns fail validation/execution and land here too.
            var hints = BuildGroundingHints(grounding, _options.Value.AllowMultiPeriodHints);
            var tableNames = names.ToList();

            // KEYSTONE — repair provenance. Set true when a LOSSY repair fires (a load-bearing predicate dropped
            // because of an invalid column). Closure-captured so PersistAsync, defined before the loop, can read
            // the per-answer outcome and floor the confidence accordingly.
            bool lossyRepairFired = false;

            // Explain + persist a successful execution. Local fn so both the happy path and the
            // empty-fallback path below share one implementation.
            async Task<AnalystResponse> PersistAsync(CompiledSql compiled, ExecutionResult exec, int attempt)
            {
                var explainSw = Stopwatch.StartNew();
                var explanation = await _explainer.ExplainAsync(question, exec, compiled, cancellationToken);
                explainSw.Stop();
                steps.RecordExplainer(question, compiled, exec, explanation, explainSw.ElapsedMilliseconds);
                var grounded = grounding.LinkedValues.Count > 0 || grounding.LinkedNaturalKeys.Count > 0;
                var conf = attempt > 0 ? 0.6 : (grounded ? 0.8 : 0.72);
                // A lossy strip can ship an over-broad answer (the load-bearing filter was dropped). Floor the
                // confidence so it is distinguishable from a clean grounded answer and a downstream confidence
                // gate can act on it. Telemetry-only today — no abstain gate keys off it yet (measure first).
                if (lossyRepairFired) conf = Math.Min(conf, 0.5);
                var provenance = (attempt > 0 ? "direct-analyst:self-corrected" : "direct-analyst")
                    + (lossyRepairFired ? ":lossy-strip" : "");
                return (await _persister.PersistAsync(request, totalSw, steps,
                    reply: explanation.Reply, sql: compiled.Sql, rowCount: exec.RowCount, rows: exec.Rows,
                    cancellationToken: cancellationToken))
                    with { Provenance = provenance, Confidence = conf };
            }

            // CONTROL LOOP. Generate → validate → execute → VERIFY → resample. Three levers feed a fresh
            // attempt back to the model: (1) a SQL error, (2) an invalid column we couldn't auto-strip,
            // and crucially (3) a query that RUNS BUT RETURNS 0 ROWS — the dominant *semantic* miss
            // (an over-restrictive filter: an active-flag, a status value that doesn't exist, a stray
            // WHERE). An empty result throws no error, so without this it would silently ship a wrong
            // "no rows" answer. We keep the first valid empty result as an honest fallback in case the
            // data genuinely is empty.
            string? lastError = null;
            string? emptyHint = null;
            (CompiledSql Compiled, ExecutionResult Exec)? emptyFallback = null;
            const int maxAttempts = 3;
            // Holds the greedy attempt's outcome so the self-consistency fallback can REUSE it as
            // candidate 0 (it is NOT re-called). Captured from the last error-free greedy attempt.
            AttemptOutcome? greedyCandidate = null;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var attemptHints = hints;
                if (lastError is not null)
                    attemptHints = hints.Append($"Your previous T-SQL failed with this SQL Server error — FIX it and re-output valid T-SQL (use a WITH CTE if a derived-table alias was referenced out of scope): {lastError}").ToList();
                else if (emptyHint is not null)
                    attemptHints = hints.Append(emptyHint).ToList();

                // One full attempt: emit → deterministic repairs → validate → execute → invalid-column
                // repairs. Pure mechanical extraction (sampling=null here ⇒ identical to the inline block
                // before the refactor), so the greedy loop's behavior is unchanged.
                var outcome = await EmitRepairValidateExecuteOnce(
                    question, tableNames, attemptHints, grounding, sampling: null, attemptLabel: attempt, steps, cancellationToken);

                // KEYSTONE — surface the shipping attempt's repair provenance to PersistAsync.
                lossyRepairFired = outcome.LossyStripFired;

                if (outcome.EmitProducedNoSql)
                {
                    _logger.LogInformation("[DirectAnalystPath] emit produced no SQL (attempt {Attempt}) for q='{Q}': {Reason}", attempt, question, outcome.EmitError ?? "(no reason)");
                    break;
                }
                if (outcome.ValidationFailed)
                {
                    lastError = outcome.Error; emptyHint = null;
                    _logger.LogInformation("[DirectAnalystPath] validation rejected (attempt {Attempt}) for q='{Q}': {Errors}\n  SQL: {Sql}", attempt, question, lastError, outcome.EmitSql);
                    continue;
                }

                CompiledSql compiled = outcome.Compiled!;
                ExecutionResult exec = outcome.Exec!;

                if (exec.Error is not null)
                {
                    lastError = exec.Error; emptyHint = null;
                    _logger.LogInformation("[DirectAnalystPath] execution error (attempt {Attempt}) for q='{Q}': {Error}\n  SQL: {Sql}", attempt, question, exec.Error, outcome.EmitSql);
                    continue;
                }

                // First error-free greedy attempt becomes self-consistency candidate 0 (reused, not re-run).
                greedyCandidate ??= outcome;

                // Verify: resample a suspicious empty result, keeping it as the honest fallback.
                // ENH-2: but TRUST a grounded empty — when the question produced explicit filters
                // (linked values / natural keys), 0 rows is very likely the honest answer ("tickets
                // from 2010"), and loosening would answer a DIFFERENT question. Only ungrounded empties
                // (the over-restrictive active-flag/status case) are resampled.
                var groundedEmpty = _options.Value.TrustGroundedEmptyResult
                    && (grounding.LinkedValues.Count > 0 || grounding.LinkedNaturalKeys.Count > 0);
                if (exec.RowCount == 0 && attempt < maxAttempts - 1 && !groundedEmpty)
                {
                    emptyFallback ??= (compiled, exec);
                    lastError = null;
                    emptyHint = "Your query executed but returned 0 ROWS. That is almost always an OVER-RESTRICTIVE filter: re-check every WHERE clause — do NOT filter an active/enabled flag, do NOT invent a status/value that may not exist in the data, and drop any filter the question did not explicitly ask for. Re-output the corrected SQL.";
                    _logger.LogInformation("[DirectAnalystPath] 0 rows (attempt {Attempt}) for q='{Q}' — resampling.\n  SQL: {Sql}", attempt, question, compiled.Sql);
                    continue;
                }

                // Layer-2 confident-hallucination backstop (default-OFF). The world-cup class:
                // zero grounded evidence + a generic label-only projection with no aggregate.
                if (ShouldAbstainUngroundedProjection(grounding, compiled.Sql, _options.Value))
                {
                    _logger.LogInformation(
                        "[DirectAnalystPath] ABSTAIN — zero grounding + no aggregate (generic projection) for q='{Q}'\n  SQL: {Sql}",
                        question, compiled.Sql);
                    return await AbstainOrSelfConsistentAsync(
                        "ungrounded-projection", request, question, tableNames, hints, grounding,
                        greedyCandidate, totalSw, steps, PersistAsync, cancellationToken);
                }
                // Load-bearing-lossy-strip backstop: a value filter the question wanted was dropped and nothing
                // grounded replaced it → the answer is over-broad. Abstain rather than ship a confident wrong
                // number (keeps the zero-confident-wrong posture on data the agent wasn't tuned on).
                if (exec.RowCount > 0
                    && ShouldAbstainAfterLoadBearingLossyStrip(outcome.StrippedValueLiteral, grounding, _options.Value))
                {
                    _logger.LogInformation(
                        "[DirectAnalystPath] ABSTAIN — a load-bearing value filter was lossily stripped with no grounded replacement (over-broad answer) for q='{Q}'\n  SQL: {Sql}",
                        question, compiled.Sql);
                    return await AbstainOrSelfConsistentAsync(
                        "load-bearing-lossy-strip", request, question, tableNames, hints, grounding,
                        greedyCandidate, totalSw, steps, PersistAsync, cancellationToken);
                }
                return await PersistAsync(compiled, exec, attempt);
            }

            // All attempts errored or stayed empty — return the honest empty result if we got one.
            if (emptyFallback is { } ef)
                return await PersistAsync(ef.Compiled, ef.Exec, maxAttempts);
            // genuinely couldn't answer → honest abstain (or self-consistency fallback when enabled).
            return await AbstainOrSelfConsistentAsync(
                "loop-fallthrough", request, question, tableNames, hints, grounding,
                greedyCandidate, totalSw, steps, PersistAsync, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Never let the front-path break a question — fall through to the heavy pipeline.
            _logger.LogWarning(ex, "[DirectAnalystPath] failed; falling through to the form-filling pipeline.");
            return null;
        }
    }

    /// <summary>Layer-2 confident-hallucination guard (default-OFF, gated by
    /// <see cref="AnalystOptions.EnableUngroundedProjectionAbstain"/>). Returns true only when
    /// (a) the guard is enabled, (b) grounding resolved NOTHING real (no values, natural keys, temporal,
    /// or derived metrics), and (c) the emitted SQL contains no aggregate-function CALL — i.e. a generic
    /// label-only projection whose every literal is model-invented (the "who won the world cup →
    /// SELECT TOP 1 NameEn" shape). Aggregate presence (COUNT/SUM/…) exempts legitimate zero-value
    /// analytics like "count tickets by region". Word-boundary + open-paren match so a column named
    /// "AccountCount" never reads as COUNT(...).</summary>
    internal static bool ShouldAbstainUngroundedProjection(
        QuestionGroundingContext g, string? sql, AnalystOptions opts)
    {
        if (!opts.EnableUngroundedProjectionAbstain) return false;
        var groundedAnything =
            g.LinkedValues.Count > 0 || g.LinkedNaturalKeys.Count > 0 ||
            g.LinkedTemporal.Count > 0 || g.DerivedMetricHints.Count > 0;
        if (groundedAnything) return false;          // any real evidence → never abstain here
        if (string.IsNullOrWhiteSpace(sql)) return false;
        foreach (var fn in opts.AggregateSqlFunctions ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(fn)) continue;
            if (Regex.IsMatch(sql, $@"\b{Regex.Escape(fn)}\s*\(", RegexOptions.IgnoreCase))
                return false;                        // analytic intent → keep the answer
        }
        return true;                                 // zero grounding + no aggregate = abstain
    }

    /// <summary>Load-bearing-lossy-strip abstain guard — makes the repair-provenance keystone act. Returns
    /// true (abstain) only when (a) the gate is enabled, (b) a lossy invalid-column strip dropped a predicate
    /// carrying a VALUE LITERAL (a filter the question wanted, not a flag), and (c) grounding resolved NO real
    /// value/key to replace it — so the shipped answer is over-broad. A grounded query is exempt: the injector
    /// already enforced the right filter, so the strip of the model's wrong-column attempt is harmless. Pure
    /// SQL-structure + grounding state — no schema/business vocab.</summary>
    internal static bool ShouldAbstainAfterLoadBearingLossyStrip(
        bool strippedValueLiteral, QuestionGroundingContext g, AnalystOptions opts)
    {
        if (!opts.AbstainOnLoadBearingLossyStrip) return false;
        if (!strippedValueLiteral) return false;                       // a flag strip is not load-bearing
        return g.LinkedValues.Count == 0 && g.LinkedNaturalKeys.Count == 0;  // nothing grounded replaced it
    }

    // A string literal sitting in a PREDICATE / comparison context — i.e. immediately preceded (ignoring
    // whitespace) by a comparison operator or `IN (` / `LIKE`. This is the ONLY kind of literal that signals
    // a value FILTER. It deliberately does NOT match a literal that is an argument to a function (a
    // FORMAT(d,'yyyy-MM') time bucket, a CONVERT/DATENAME style mask) or a CASE label, because those are
    // never preceded by a comparison operator — they sit after `(` or `,` or a keyword. Used to detect
    // whether a lossy strip dropped a value-literal predicate (load-bearing) so the count no longer moves
    // when an unrelated FORMAT/CASE literal is present.
    private static readonly Regex PredicateValueLiteralRx = new(
        @"(?:=|<>|!=|>=|<=|>|<|\bIN\s*\(|\bLIKE)\s*N?'[^']*'",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Counts single-quoted string literals that appear in a PREDICATE / comparison context (after a
    /// comparison operator or <c>IN (</c> / <c>LIKE</c>) — NOT literals that are function arguments
    /// (<c>FORMAT(d,'yyyy-MM')</c>) or CASE labels. Used to decide whether a lossy strip dropped a real
    /// value-FILTER predicate (load-bearing) vs a flag predicate (not), without the abstain decision riding on
    /// the total apostrophe count (a FORMAT bucket no longer moves the number).</summary>
    internal static int CountPredicateValueLiterals(string? sql) =>
        string.IsNullOrEmpty(sql) ? 0 : PredicateValueLiteralRx.Matches(sql).Count;

    // The clause keyword / terminator set that ENDS a WHERE clause — the boundary a sole-predicate WHERE is
    // dropped before. Beyond GROUP BY / ORDER BY / HAVING / ) / ; / end, this now also covers OFFSET, FETCH,
    // and the set operators (UNION / EXCEPT / INTERSECT) so a `WHERE <pred> OFFSET 10 ROWS` or
    // `WHERE <pred> UNION SELECT ...` has its now-empty WHERE removed cleanly instead of leaving a dangling
    // `WHERE` glued to the next clause (a parse error). Used by the shared StripPredicate helper below.
    private const string WhereClauseBoundary =
        @"(?:GROUP\s+BY|ORDER\s+BY|HAVING|OFFSET|FETCH|UNION|EXCEPT|INTERSECT|\)|;|$)";

    /// <summary>The shared WHERE/AND predicate-removal triplet used by every deterministic strip. Removes a
    /// matched predicate in all three positions it can occupy: <c>WHERE &lt;pred&gt; AND …</c> (leading) →
    /// keep WHERE; <c>… AND &lt;pred&gt; …</c> (trailing) → drop the AND; and a sole <c>WHERE &lt;pred&gt;</c>
    /// right before a clause boundary (<see cref="WhereClauseBoundary"/>) → drop the whole WHERE. The caller
    /// passes <paramref name="predicateRegex"/> already regex-ready (e.g. <see cref="Regex.Escape"/> of a
    /// literal match, or a small alternation); it is wrapped in a non-capturing group here so a top-level
    /// alternation is safe. Pure string→string (no allocation of intent); returns the SQL unchanged when the
    /// predicate isn't present. NOT trimmed — the caller trims/compares.</summary>
    private static string StripPredicate(string sql, string predicateRegex)
    {
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        var p = "(?:" + predicateRegex + ")";
        var s = Regex.Replace(sql, $@"\bWHERE\s+{p}\s+AND\b", "WHERE", O);                 // WHERE <bad> AND ... -> WHERE ...
        s = Regex.Replace(s, $@"\s+AND\s+{p}", " ", O);                                    // ... AND <bad> ... -> ...
        s = Regex.Replace(s, $@"\bWHERE\s+{p}\s*(?={WhereClauseBoundary})", "", O);        // WHERE <bad> (only pred) -> drop WHERE
        return s;
    }

    /// <summary>Deterministically remove an equality / IS-NULL predicate that references a column SQL
    /// Server reported as non-existent ("Invalid column name 'X'"). Targets the 7B model's habit of
    /// bolting <c>WHERE IsDeleted = 0</c> onto a table that has no such column (and re-emitting it when
    /// the error is fed back). Conservative: only strips a single simple <c>[t.]Col &lt;op&gt; literal</c>
    /// or <c>[t.]Col IS [NOT] NULL</c> predicate joined by WHERE/AND; returns false if nothing changed,
    /// so a clean query is never altered.</summary>
    internal static bool TryStripInvalidColumnPredicate(string sql, string? error, out string repaired)
    {
        repaired = sql;
        var m = Regex.Match(error ?? string.Empty, @"Invalid column name '([^']+)'", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        var col = Regex.Escape(m.Groups[1].Value);
        var pred = $@"(?:\[?\w+\]?\.)?\[?{col}\]?\s*(?:=|<>|>=|<=|>|<)\s*[^()\s]+"
                 + $@"|(?:\[?\w+\]?\.)?\[?{col}\]?\s+IS(?:\s+NOT)?\s+NULL";
        var before = sql.Trim();
        repaired = StripPredicate(sql, pred).Trim();
        return !string.Equals(repaired, before, StringComparison.Ordinal);
    }

    /// <summary>Deterministically strip an UNREQUESTED status/lifecycle equality filter — the 7B's habit of
    /// inventing <c>WHERE Status='Paid'</c> on "total of all bills" even when the prompt forbids it (a model
    /// prior prose can't reach). A status predicate is unrequested when its literal appears NEITHER in the
    /// question NOR among the grounded values (so the model made it up). Schema-agnostic: keys off the column
    /// NAME shape — a column whose name ENDS in "Status" or "State" (end-anchored, so a real lifecycle column
    /// like <c>Status</c>/<c>OrderStatus</c> matches but an unrelated <c>StatusReport</c>/<c>UserStatusFlag</c>
    /// — Status not at the end — is NEVER stripped), not a hardcoded value list. Quoted-literal required, so an
    /// int FK like <c>StatusId = 5</c> is never matched. Returns false (no change) when nothing was stripped, so
    /// a legitimately-named status filter ("paid bills" / a synonym-grounded value) is never touched.</summary>
    internal static bool TryStripUnrequestedStatusFilter(
        string sql, string? question, IEnumerable<string>? groundedValues, out string repaired,
        bool trustGroundingOnly = false)
    {
        repaired = sql;
        if (string.IsNullOrWhiteSpace(sql)) return false;
        var qLower = (question ?? string.Empty).ToLowerInvariant();
        var grounded = new HashSet<string>(groundedValues ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        // Match a status comparison to a quoted literal — EQUALITY (=) OR INEQUALITY (!= / <>). The 7B
        // over-filters in both directions: "bills issued this year" -> Status='Issued', and "how many customers
        // in total" -> Status != 'Churned' (200 -> 191). Both are ungrounded; both should go.
        // The column must END in Status/State (end-anchored `\b`), so OrderStatus/Status match while
        // StatusReport / UserStatusFlag (Status NOT at the end) are excluded — a legitimate ungrounded filter
        // on a *Status*-containing column that isn't actually a lifecycle column is no longer wrongly stripped.
        var rx = new Regex(@"(?:\[?\w+\]?\.)?\[?\w*(?:Status|State)\b\]?\s*(?:=|!=|<>)\s*N?'([^']+)'", O);
        var s = sql;
        foreach (Match m in rx.Matches(sql))
        {
            var literal = m.Groups[1].Value;
            // KEEP a status comparison only when GROUNDING bound this literal (grounding = the moat / single
            // authority on what to filter). The legacy fallback "the question contains the word" is verb-blind:
            // it kept Status='Issued' for "bills ISSUED so far this year" (a date verb, not a status). The value-
            // linker now does proper attributive-vs-verb grounding, so trusting grounding alone strips the 7B's
            // self-added status filter while keeping every adjective case ("overdue bills"->'Overdue' grounded).
            // Gated: trustGroundingOnly=false preserves the old question-contains fallback.
            var keep = grounded.Contains(literal) || (!trustGroundingOnly && qLower.Contains(literal.ToLowerInvariant()));
            if (keep) continue; // requested → keep
            s = StripPredicate(s, Regex.Escape(m.Value));
        }
        repaired = s.Trim();
        return !string.Equals(repaired, sql.Trim(), StringComparison.Ordinal);
    }

    /// <summary>Deterministically strip an UNREQUESTED boolean-flag filter — the 7B's habit of bolting an
    /// invented <c>IsPlanned = 0</c> / <c>IsActive = 1</c> onto a query whose question names no such concept
    /// (e.g. "critical outages" came back filtered to UNPLANNED ones, 6 vs 7). The symmetric partner of
    /// <see cref="TryStripUnrequestedStatusFilter"/> for boolean flags, and routed through the SAME authority:
    /// a flag predicate <c>Is&lt;Concept&gt; = 0|1</c> is KEPT only when the concept was GROUNDED, or appears in
    /// the question as a WHOLE WORD (<c>\bConcept</c> — so "active" matches "active"/"actively"/"actives" but
    /// NOT "activities", which only shares a truncated prefix). This replaces the old brittle
    /// <c>Substring(0,len-2)</c> stem inference, which was morphologically blind ("activities" kept an unrelated
    /// IsActive filter) and verb-blind. EXCLUDES the soft-delete invariant (any <c>Is*Delete*</c> flag is
    /// structural and always kept, so a Tickets <c>IsDeleted = 0</c> is never stripped). Schema-agnostic (keys
    /// off the <c>Is&lt;word&gt;</c> shape + integer literal, so a quoted status value or an int FK is never
    /// matched) and portable. Returns false when nothing was stripped.</summary>
    internal static bool TryStripUnrequestedFlagFilter(
        string sql, string? question, out string repaired, IEnumerable<string>? groundedConcepts = null)
    {
        repaired = sql;
        if (string.IsNullOrWhiteSpace(sql)) return false;
        var qText = question ?? string.Empty;
        var grounded = new HashSet<string>(groundedConcepts ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        // [qual.]Is<Concept> = 0|1 — a boolean flag set to an INTEGER literal (no quotes).
        var rx = new Regex(@"(?:\[?\w+\]?\.)?\[?Is(?<c>[A-Za-z]+)\]?\s*=\s*[01]\b", O);
        var s = sql;
        foreach (Match m in rx.Matches(sql))
        {
            var concept = m.Groups["c"].Value;                                          // "Planned","Active","Deleted"
            if (concept.IndexOf("Delete", StringComparison.OrdinalIgnoreCase) >= 0) continue;  // soft-delete invariant → keep
            if (grounded.Contains(concept)) continue;                                   // grounding bound the concept → keep
            // KEEP only when the concept is present in the question as a WHOLE WORD — a leading word boundary
            // anchored at the concept start, so morphological tails (active→actively/actives) still count but a
            // word that merely shares a truncated prefix (activities, planning) does NOT. No ad-hoc Substring.
            if (Regex.IsMatch(qText, $@"\b{Regex.Escape(concept)}", RegexOptions.IgnoreCase)) continue;
            s = StripPredicate(s, Regex.Escape(m.Value));
        }
        repaired = s.Trim();
        return !string.Equals(repaired, sql.Trim(), StringComparison.Ordinal);
    }

    /// <summary>Deterministically strip an UNREQUESTED foreign-key equality filter — the 7B's habit of bolting
    /// an invented <c>WHERE ServiceTypeId = 1</c> onto "how many blackouts in total" because it associates a
    /// word ("blackout") with a category (electricity) the user never asked to filter by. The integer-FK twin of
    /// <see cref="TryStripUnrequestedStatusFilter"/> / <see cref="TryStripUnrequestedFlagFilter"/>: those require
    /// a QUOTED literal and so deliberately skip a bare-int FK like this. Routed through the SAME authority —
    /// GROUNDING is the moat: a <c>[qual.]Col = &lt;bare-int&gt;</c> predicate is KEPT only when (a) grounding
    /// bound a value to this FK COLUMN, or (b) grounding bound a value to the FK's REFERENCED TABLE (so a
    /// genuine "outages for service type X" whose 'X' the value-linker resolved survives), or (c) the question
    /// itself NAMES the integer as a standalone token ("outages in region 5" keeps RegionId=5 — the user typed
    /// the id). Schema-driven, NO hardcoded table/column/value: "is this an FK?" and "what does it reference?"
    /// come solely from the InferredColumn (<see cref="Models.SpecConstants.ColumnRoles.ForeignKey"/> role or a
    /// non-null <c>References</c>); a non-FK int column and the PRIMARY KEY are never stripped. Returns false when
    /// nothing was stripped, so a legitimately-grounded or user-named FK filter is never touched.</summary>
    internal static bool TryStripUnrequestedForeignKeyFilter(
        string sql, string? question, Func<string, Schema.InferredTable?> getTable,
        IEnumerable<string> tableNames,
        IEnumerable<(string Table, string Column)> groundedColumns,
        IEnumerable<string> groundedTables, out string repaired)
    {
        repaired = sql;
        if (string.IsNullOrWhiteSpace(sql)) return false;
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline;

        // The tables linked into this query (alias map values + the caller's candidate set) — the universe we
        // resolve an unqualified FK column against. We can't trust a SQL alias ("o") to name a table, so for a
        // qualified column we still resolve the FK by COLUMN NAME across these tables (the task's stated approach).
        var aliasMap = BuildAliasToTableMap(sql);
        var linkedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in aliasMap.Values) linkedTables.Add(t);
        foreach (var t in tableNames ?? Enumerable.Empty<string>())
            if (!string.IsNullOrWhiteSpace(t)) linkedTables.Add(t);

        var groundedCols = new HashSet<string>(
            (groundedColumns ?? Enumerable.Empty<(string, string)>()).Select(c => c.Column),
            StringComparer.OrdinalIgnoreCase);
        var groundedTabs = new HashSet<string>(
            groundedTables ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var qText = question ?? string.Empty;

        // [qual.]Col = <bare integer literal> — INTEGER only, NO quotes (a quoted value is a status/label,
        // handled by the status strip; a quoted int is a string compare, not an FK id). The qualifier is
        // optional and discarded; we resolve the owning table by the column NAME across the linked tables.
        var rx = new Regex(@"(?:\[?\w+\]?\.)?\[?(?<col>\w+)\]?\s*=\s*(?<int>\d+)\b", O);
        var s = sql;
        foreach (Match m in rx.Matches(sql))
        {
            var col = m.Groups["col"].Value;
            var intLit = m.Groups["int"].Value;

            // (c) KEEP — the user NAMED this integer as a standalone token ("region 5" → RegionId = 5). The id
            // is in the question, so it's a requested filter, not an invented association.
            if (Regex.IsMatch(qText, $@"\b{Regex.Escape(intLit)}\b")) continue;

            // (a) KEEP — grounding bound a value to this FK column (the value-linker resolved the filter).
            if (groundedCols.Contains(col)) continue;

            // Resolve the column to an FK on one of the linked tables: the InferredColumn must carry the
            // foreign_key role OR a non-null References. The PRIMARY KEY is never treated as a strippable FK.
            string? referencedTable = null;
            var isForeignKey = false;
            var isPrimaryKey = false;
            foreach (var tn in linkedTables)
            {
                var t = getTable(tn);
                var ic = t?.Columns.FirstOrDefault(c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase));
                if (ic is null) continue;
                // PK guard — either the explicit primary_key role or membership in the table's PrimaryKey set.
                if (string.Equals(ic.Role, SpecConstants.ColumnRoles.PrimaryKey, StringComparison.OrdinalIgnoreCase)
                    || (t!.PrimaryKey?.Any(pk => string.Equals(pk, col, StringComparison.OrdinalIgnoreCase)) ?? false))
                    isPrimaryKey = true;
                var fk = string.Equals(ic.Role, SpecConstants.ColumnRoles.ForeignKey, StringComparison.OrdinalIgnoreCase)
                         || !string.IsNullOrWhiteSpace(ic.References);
                if (fk)
                {
                    isForeignKey = true;
                    // References is "RefTable.RefColumn" — the referenced table is the part before '.'.
                    if (!string.IsNullOrWhiteSpace(ic.References))
                    {
                        var dot = ic.References!.IndexOf('.');
                        referencedTable = dot > 0 ? ic.References[..dot] : ic.References;
                    }
                    break;
                }
            }

            // KEEP — column is the PK, or is NOT actually a foreign key (so it's a real int filter, not an
            // invented category association we have license to drop).
            if (isPrimaryKey || !isForeignKey) continue;

            // (b) KEEP — grounding bound a value to the FK's REFERENCED table (the lookup the FK points at).
            if (referencedTable is not null && groundedTabs.Contains(referencedTable)) continue;

            // None of the KEEP conditions held → the model invented this FK equality. Strip it.
            s = StripPredicate(s, Regex.Escape(m.Value));
        }
        repaired = s.Trim();
        return !string.Equals(repaired, sql.Trim(), StringComparison.Ordinal);
    }

    /// <summary>Strip an UNGROUNDED relative-date predicate the 7B invents to approximate a status concept —
    /// e.g. "overdue bills" came back as <c>WHERE DueDate &lt; GETDATE()</c> instead of the <c>Status='Overdue'</c>
    /// that the inline-enum value-link grounded. Fires ONLY when (a) a value was grounded (so the real filter is
    /// enforced separately by the injector) AND (b) the question carries NO temporal cue — so a genuine "bills due
    /// before March" (which grounds a temporal range) is never touched. Conservative: removes only a single
    /// comparison of a column against the SERVER CLOCK (GETDATE/SYSDATETIME/…/DATEADD), never a literal-date
    /// filter. Returns false when nothing matched.</summary>
    internal static bool TryStripUngroundedDatePredicate(string sql, bool hasGroundedValue, bool hasTemporalCue, out string repaired)
    {
        repaired = sql;
        if (string.IsNullOrWhiteSpace(sql) || !hasGroundedValue || hasTemporalCue) return false;
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        // [qual.]Col <op> <server-clock expr> — a "relative now" comparison (the invented kind).
        var rx = new Regex(
            @"(?:\[?\w+\]?\.)?\[?\w+\]?\s*(?:<=|>=|<>|<|>|=)\s*(?:GETDATE\(\)|SYSDATETIME\(\)|SYSUTCDATETIME\(\)|GETUTCDATE\(\)|CURRENT_TIMESTAMP|DATEADD\s*\([^)]*\))",
            O);
        var s = sql;
        foreach (Match m in rx.Matches(sql))
            s = StripPredicate(s, Regex.Escape(m.Value));
        repaired = s.Trim();
        return !string.Equals(repaired, sql.Trim(), StringComparison.Ordinal);
    }

    /// <summary>Fix a GROUP-BY GRAIN bug: the 7B groups an aggregate by a LABEL column (GROUP BY
    /// Customers.FullNameEn) instead of the entity KEY, silently MERGING distinct entities that share a
    /// label (two different customers named alike) — "top 5 customers by total billed" returned the wrong 5.
    /// Adds the table's single-column primary key to the GROUP BY (<c>GROUP BY Id, FullNameEn</c>), restoring
    /// per-entity grain. SAFE by construction: adding the PK only changes the result when the label actually
    /// has duplicates (the bug case); for a unique label, GROUP BY Id,Label ≡ GROUP BY Label. Conservative:
    /// only a SINGLE label-shaped GROUP-BY column, an aggregate present, a single-column PK that isn't already
    /// grouped. Returns false otherwise.</summary>
    internal static bool TryFixGroupByGrain(string sql, Func<string, Schema.InferredTable?> getTable, out string repaired)
    {
        repaired = sql;
        if (string.IsNullOrWhiteSpace(sql)) return false;
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        if (!Regex.IsMatch(sql, @"\b(?:SUM|COUNT|AVG|MIN|MAX)\s*\(", O)) return false;      // real aggregate only
        // a SINGLE GROUP BY column immediately followed by ORDER BY / HAVING / end (multi-column GROUP BY won't match).
        // The whitespace before the next clause is in the LOOKAHEAD (not consumed) so it SURVIVES the replacement:
        // consuming it (the old `\s*(?=...)`) glued the rebuilt GROUP BY to the following clause ("pm.NameEnHAVING"),
        // a parse error that rejected otherwise-valid SQL from ANY model whenever this grain repair fired.
        var m = Regex.Match(sql,
            @"\bGROUP\s+BY\s+(?<full>(?<qual>\[?\w+\]?\.)?\[?(?<col>\w+)\]?)(?=\s*(ORDER\s+BY|HAVING|;|\)|$))", O);
        if (!m.Success) return false;
        var col = m.Groups["col"].Value;
        if (!Regex.IsMatch(col, "(Name|Title|Label)", RegexOptions.IgnoreCase)) return false;  // label-shaped only
        var qual = m.Groups["qual"].Value.TrimEnd('.').Trim('[', ']');
        var aliasMap = BuildAliasToTableMap(sql);
        string? table = null;
        if (!string.IsNullOrEmpty(qual) && aliasMap.TryGetValue(qual, out var tq)) table = tq;
        else
        {
            var owners = aliasMap.Values.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(tn => { var ti = getTable(tn); return ti is not null && HasColumn(ti, col); }).ToList();
            if (owners.Count == 1) table = owners[0];
        }
        if (table is null) return false;
        var t = getTable(table);
        if (t?.PrimaryKey is not { Count: 1 } pk) return false;                              // single-column PK only
        var pkCol = pk[0];
        if (string.Equals(pkCol, col, StringComparison.OrdinalIgnoreCase)) return false;     // already grouping by the key
        if (Regex.IsMatch(m.Groups["full"].Value, $@"\b{Regex.Escape(pkCol)}\b", O)) return false;
        var qualifier = string.IsNullOrEmpty(qual) ? table : qual;
        var newGroupBy = $"GROUP BY {qualifier}.{pkCol}, {m.Groups["full"].Value}";
        repaired = sql.Substring(0, m.Index) + newGroupBy + sql.Substring(m.Index + m.Length);
        return !string.Equals(repaired, sql, StringComparison.Ordinal);
    }

    // Words that can follow a table name in FROM/JOIN but are NOT an alias (so the injector doesn't
    // mistake "JOIN TicketStatuses ON ..." for an alias named "ON").
    private static readonly HashSet<string> SqlAliasStopWords = new(StringComparer.OrdinalIgnoreCase)
    { "ON", "WHERE", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "JOIN", "GROUP", "ORDER", "HAVING", "UNION", "AS" };

    /// <summary>Deterministically INJECT a filter for a value the question NAMED (a value-link) but the model
    /// DROPPED — the symmetric partner of <see cref="TryStripUnrequestedStatusFilter"/>. The 7B is flaky about
    /// applying a requested filter ("open tickets" came back as all 83 one run, 35 the next), so the
    /// prescriptive hint alone isn't reliable; this enforces it. CONSERVATIVE: only a single simple statement
    /// (one WHERE, no GROUP BY — insertion point unambiguous), only when the value's table is already in the
    /// FROM/JOIN, and only when that literal isn't already present (no double-filter). Returns false (no change)
    /// otherwise, so a query that already filters correctly — or is too complex to touch — is left alone.</summary>
    internal static bool TryInjectGroundedValueFilters(
        string sql, IEnumerable<(string Table, string Column, string Value)>? linked, out string repaired)
    {
        repaired = sql;
        if (string.IsNullOrWhiteSpace(sql) || linked is null) return false;
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        if (Regex.Matches(sql, @"\bWHERE\b", O).Count > 1) return false;   // subquery → ambiguous, skip
        if (Regex.IsMatch(sql, @"\bGROUP\s+BY\b", O)) return false;        // aggregation → skip (lower value, riskier)
        var s = sql;
        var changed = false;
        var linkedList = linked.Where(l => !string.IsNullOrWhiteSpace(l.Column) && !string.IsNullOrWhiteSpace(l.Value)).ToList();

        // CONFLICT-REPLACE: the model put a DIFFERENT, non-grounded literal on a grounded column — most often
        // the Arabic question word copied verbatim (WHERE Name='مفتوحة' while grounding resolved the real value
        // 'Open'). Strip those predicates so the grounded value below doesn't AND into a contradiction (= 0 rows).
        // Only strips literals that are NOT themselves grounded, so a legitimate multi-value filter ("Damascus
        // AND Aleppo", both grounded) is preserved.
        var groundedByCol = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in linkedList)
        {
            if (!groundedByCol.TryGetValue(l.Column, out var set)) groundedByCol[l.Column] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(l.Value);
        }
        foreach (var (col, gvals) in groundedByCol)
        {
            var conflictRx = new Regex($@"(?:\[?\w+\]?\.)?\[?{Regex.Escape(col)}\]?\s*=\s*N?'([^']*)'", O);
            foreach (Match cm in conflictRx.Matches(s).Cast<Match>().ToList())
            {
                if (gvals.Contains(cm.Groups[1].Value)) continue;        // a grounded value → keep
                var before = s;
                s = StripPredicate(s, Regex.Escape(cm.Value));
                if (!string.Equals(before, s, StringComparison.Ordinal)) changed = true;
            }
        }

        // Group by (table, column): two values on the SAME column ("tickets in Damascus OR Aleppo" →
        // Regions.NameEn in {Damascus, Aleppo}) must become a single `col IN ('a','b')` — emitting a separate
        // `AND col = 'a' AND col = 'b'` ANDs into a contradiction (a column can't equal two values) → 0 rows.
        // A single value keeps the plain `col = 'val'` (unchanged). Distinct values per group preserve order.
        var byTableColumn = linkedList
            .Where(l => !string.IsNullOrWhiteSpace(l.Table) && !string.IsNullOrWhiteSpace(l.Column) && !string.IsNullOrWhiteSpace(l.Value))
            .GroupBy(l => (l.Table, l.Column));
        foreach (var grp in byTableColumn)
        {
            var (table, col) = grp.Key;
            // Distinct values for this column, dropping any already present in the SQL as a literal (no double-filter).
            var values = grp.Select(l => l.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(v => s.IndexOf("'" + v.Replace("'", "''") + "'", StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();
            if (values.Count == 0) continue;                                                            // all already filtered
            // Find the table in FROM/JOIN and capture its alias if any. When a table is aliased
            // (JOIN TicketStatuses s) SQL Server REQUIRES the alias, so qualify the predicate with it.
            var mt = Regex.Match(s, $@"\b(?:FROM|JOIN)\s+\[?{Regex.Escape(table)}\]?(?:\s+(?:AS\s+)?(?<a>\w+))?", O);
            if (!mt.Success) continue;                                                                  // table not in query
            var qualifier = table;
            if (mt.Groups["a"].Success && !SqlAliasStopWords.Contains(mt.Groups["a"].Value))
                qualifier = mt.Groups["a"].Value;
            // Single value → equality (unchanged); 2+ distinct values → one IN(...) so they OR, not AND-to-empty.
            var pred = values.Count == 1
                ? $"{qualifier}.{col} = '{values[0].Replace("'", "''")}'"
                : $"{qualifier}.{col} IN ({string.Join(", ", values.Select(v => "'" + v.Replace("'", "''") + "'"))})";
            var m = Regex.Match(s, @"\b(ORDER\s+BY|HAVING)\b", O);
            var at = m.Success ? m.Index : (s.LastIndexOf(';') >= 0 ? s.LastIndexOf(';') : s.TrimEnd().Length);
            var keyword = Regex.IsMatch(s.Substring(0, at), @"\bWHERE\b", O) ? " AND " : " WHERE ";
            s = s.Insert(at, keyword + pred + " ");
            changed = true;
        }
        repaired = changed ? s.Trim() : sql;
        return changed;
    }

    /// <summary>Resolve a self-contradicting filter: the SAME column equated to TWO different literals in one
    /// AND-chain (<c>Name = 'مفتوحة' AND ... AND Name = 'Open'</c>) — a guaranteed 0-row result, since a column
    /// cannot equal two distinct values. This is the bilingual failure mode where the 7B copies the question's
    /// (Arabic) word AND also writes the resolved (English) enum value. We keep the GROUNDED literal (the value
    /// the grounder resolved from the question) and strip the other(s). Schema-agnostic: keys off the grounded
    /// VALUE SET and the column's bare name (qualifier/brackets stripped), so it is robust to <c>T.[Name]</c> vs
    /// <c>Name</c> and needs no per-table knowledge. CONSERVATIVE: only strips a literal on a column that ALSO
    /// carries a grounded literal in the same statement, so a legitimate single filter is never touched.</summary>
    internal static bool TryResolveContradictoryEqualityLiterals(
        string sql, IEnumerable<string>? groundedValues, out string repaired)
    {
        repaired = sql;
        if (string.IsNullOrWhiteSpace(sql)) return false;
        var grounded = (groundedValues ?? Enumerable.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (grounded.Count == 0) return false;
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        if (Regex.Matches(sql, @"\bWHERE\b", O).Count > 1) return false;   // subquery present → ambiguous, skip

        // Every "<qualifier?>.<col> = 'literal'" equality, captured with its bare column + literal.
        var eqRx = new Regex(@"(?:\[?\w+\]?\.)?\[?(?<col>\w+)\]?\s*=\s*N?'(?<val>[^']*)'", O);
        var matches = eqRx.Matches(sql).Cast<Match>().ToList();
        if (matches.Count < 2) return false;

        // Group the equality literals by bare column name. A column with >=2 distinct literals where at least
        // one is grounded is the contradiction — strip the NON-grounded literals on that column.
        var byCol = matches
            .GroupBy(m => m.Groups["col"].Value, StringComparer.OrdinalIgnoreCase);

        var s = sql;
        var changed = false;
        foreach (var g in byCol)
        {
            var literals = g.Select(m => m.Groups["val"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (literals.Count < 2) continue;                          // not a contradiction
            if (!literals.Any(v => grounded.Contains(v))) continue;    // no grounded anchor → can't safely pick
            foreach (var m in g)
            {
                if (grounded.Contains(m.Groups["val"].Value)) continue; // keep the grounded literal
                var before = s;
                s = StripPredicate(s, Regex.Escape(m.Value));
                if (!string.Equals(before, s, StringComparison.Ordinal)) changed = true;
            }
        }
        repaired = changed ? s.Trim() : sql;
        return changed;
    }

    /// <summary>Deterministically rewrite a PROJECTED column SQL Server reported as non-existent
    /// ("Invalid column name 'X'") to the owning table's real bilingual column. Schema-driven — no
    /// hardcoded table/column names: the target is the single column named X+&lt;localeSuffix&gt; on the
    /// owning table, else that table's LabelColumn. Locale-aware (Arabic → prefer the Ar suffix).
    /// SAFE: rewrites ONLY when X genuinely does not exist on a candidate AND exactly ONE candidate
    /// yields an unambiguous real target; returns false (no change) otherwise, so a valid query is
    /// never altered and the caller falls back to today's retry.</summary>
    internal static bool TryResolveInvalidProjectionColumn(
        string sql, string? error, IReadOnlyList<string> candidateTables,
        Func<string, Schema.InferredTable?> getTable, IReadOnlyList<string> localeSuffixes,
        string language, out string repaired)
    {
        repaired = sql;
        var m = Regex.Match(error ?? string.Empty, @"Invalid column name '([^']+)'", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        var bad = m.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(bad)) return false;

        var ordered = OrderSuffixesByLocale(localeSuffixes, language);
        var aliasToTable = BuildAliasToTableMap(sql);   // alias OR bare table name → real table name

        // For UNqualified occurrences we cannot tell which table the column belongs to, so we keep the
        // original conservative rule: rewrite only when NO candidate owns the bad column AND exactly ONE
        // candidate yields an unambiguous target. (A "Name" that is valid on some candidate stays put.)
        string? unqualTarget = null;
        var unqualOwners = 0;
        var anyCandidateOwnsBad = false;
        foreach (var tn in candidateTables)
        {
            var t = getTable(tn);
            if (t is null) continue;
            if (HasColumn(t, bad)) { anyCandidateOwnsBad = true; continue; }
            var cand = ResolveTarget(t, bad, ordered);
            if (cand is null) continue;
            unqualOwners++;
            unqualTarget = cand;
        }
        var unqualSafe = !anyCandidateOwnsBad && unqualOwners == 1 && unqualTarget is not null;

        var changedAny = false;
        var c = Regex.Escape(bad);
        // Capture the qualifier (T. / Regions. / dbo.Table. / none). Word-boundary lookahead so "Name"
        // never matches inside "NameEn". QUALIFIED hits resolve against THAT one table (so Regions.Name →
        // Regions.NameEn while a real TicketStatuses.Name is left untouched); UNqualified hits use the
        // single-owner rule above.
        var rx = new Regex($@"(?<q>(?:\[?(?<qual>\w+)\]?\.)?)\[?{c}\]?(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);
        var rewritten = rx.Replace(sql, mt =>
        {
            var qual = mt.Groups["qual"].Value;
            if (!string.IsNullOrEmpty(qual))
            {
                if (!aliasToTable.TryGetValue(qual, out var tn)) return mt.Value;   // unknown qualifier → leave
                var t = getTable(tn);
                if (t is null || HasColumn(t, bad)) return mt.Value;                // valid on its owner → leave
                var target = ResolveTarget(t, bad, ordered);
                if (target is null) return mt.Value;
                changedAny = true;
                return mt.Groups["q"].Value + "[" + target + "]";
            }
            if (unqualSafe)
            {
                changedAny = true;
                return "[" + unqualTarget + "]";
            }
            return mt.Value;
        });

        if (!changedAny) return false;
        repaired = rewritten;
        return !string.Equals(repaired, sql, StringComparison.Ordinal);
    }

    // ── STAGE-2 AST repair adapters (gated by AnalystOptions.EnableAstRepairs) ──────────────────
    // Each adapter has the SAME (sql, …, out repaired) shape as its regex twin so the repair-chain call
    // sites just switch which method they call based on the flag. The adapters supply the SAME decision
    // inputs the regex versions use (label NAME-shape, grounded values, alias→table map, single-column PK)
    // to SqlAstRepairs — only the MECHANISM (tree mutation vs string surgery) differs, never the POLICY.

    // The label NAME-shape test the grain fix keys off — identical to the regex literal in TryFixGroupByGrain
    // ("(Name|Title|Label)"). Schema-agnostic: a naming convention, not a per-table list.
    private static readonly Regex LabelColumnShape = new("(Name|Title|Label)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // The status/lifecycle column NAME-shape — a column whose name ENDS in Status/State (end-anchored \b),
    // identical to the column part of TryStripUnrequestedStatusFilter's regex.
    private static readonly Regex StatusColumnShape = new(@"\w*(?:Status|State)\b$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>AST adapter for the status-strip (Pass 2). Builds the same keep/strip decision the regex
    /// version makes — keep a status literal only when GROUNDING bound it, or (unless trust-grounding-only)
    /// the question contains it — and delegates the tree mutation to <see cref="SqlAstRepairs"/>.</summary>
    private bool AstStripUnrequestedStatusFilter(
        string sql, string? question, IEnumerable<string>? groundedValues, out string repaired)
    {
        var qLower = (question ?? string.Empty).ToLowerInvariant();
        var grounded = new HashSet<string>(groundedValues ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var trustGroundingOnly = _options.Value.StripStatusFilterTrustGroundingOnly;
        bool ShouldStrip(string literal)
        {
            var keep = grounded.Contains(literal) || (!trustGroundingOnly && qLower.Contains(literal.ToLowerInvariant()));
            return !keep;
        }
        return Validation.SqlAstRepairs.TryStripUnrequestedStatusFilter(
            sql, col => StatusColumnShape.IsMatch(col), ShouldStrip, out repaired);
    }

    /// <summary>AST adapter for the multi-value injection (Pass 3). Runs the AST IN-predicate injection for
    /// every (table,column) group with 2+ grounded values FIRST (the robust mechanism), then the regex
    /// injector on the result — which now sees those literals already present and so skips them, while still
    /// applying the single-value equality + conflict-replace cases unchanged. Net predicate set is identical
    /// to the regex-only path; only the multi-value IN is built by the grammar instead of string surgery.</summary>
    private bool AstInjectGroundedValueFilters(
        string sql, IEnumerable<(string Table, string Column, string Value)>? linked, out string repaired)
    {
        repaired = sql;
        var linkedList = (linked ?? Enumerable.Empty<(string Table, string Column, string Value)>())
            .Where(l => !string.IsNullOrWhiteSpace(l.Table) && !string.IsNullOrWhiteSpace(l.Column) && !string.IsNullOrWhiteSpace(l.Value))
            .ToList();

        // The regex injector self-restricts to a single simple statement (one WHERE, no GROUP BY); mirror that
        // guard so the AST multi-value pass fires on exactly the same shapes (else the two paths diverge).
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        var simpleStatement = Regex.Matches(sql, @"\bWHERE\b", O).Count <= 1 && !Regex.IsMatch(sql, @"\bGROUP\s+BY\b", O);

        var working = sql;
        var changedByAst = false;
        if (simpleStatement)
        {
            var aliasMap = BuildAliasToTableMap(sql);
            var groups = linkedList
                .GroupBy(l => (l.Table, l.Column))
                .Select(g => (
                    g.Key.Table,
                    g.Key.Column,
                    Values: (IReadOnlyList<string>)g.Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
                .Where(g => g.Values.Count >= 2)
                .ToList();
            if (groups.Count > 0)
            {
                // Resolve the FROM/JOIN qualifier the SAME way the regex injector does: prefer the table's
                // alias when aliased, else the bare table name; null when the table isn't in the query.
                string? ResolveQualifier(string table)
                {
                    var mt = Regex.Match(working, $@"\b(?:FROM|JOIN)\s+\[?{Regex.Escape(table)}\]?(?:\s+(?:AS\s+)?(?<a>\w+))?", O);
                    if (!mt.Success) return null;
                    if (mt.Groups["a"].Success && !SqlAliasStopWords.Contains(mt.Groups["a"].Value))
                        return mt.Groups["a"].Value;
                    return table;
                }
                changedByAst = Validation.SqlAstRepairs.TryInjectGroundedValueFilters(
                    working, groups, ResolveQualifier, out var astInjected) && (working = astInjected) is not null;
            }
        }

        // Now the regex injector handles single-value equality + conflict-replace, and skips any literal the AST
        // IN already placed (it filters values already present in the SQL) — so no double-injection.
        var changedByRegex = TryInjectGroundedValueFilters(working, linkedList, out var regexInjected);
        repaired = changedByRegex ? regexInjected : working;
        return changedByAst || changedByRegex;
    }

    /// <summary>AST adapter for the GROUP-BY grain fix (Pass 1). Builds the same owner/PK resolution the
    /// regex version does — qualified column → its mapped table; unqualified → the single FROM/JOIN owner
    /// that has the column — then a single-column PK, and delegates the (gluing-proof) tree mutation.</summary>
    private bool AstFixGroupByGrain(string sql, Func<string, Schema.InferredTable?> getTable, out string repaired)
    {
        var aliasMap = BuildAliasToTableMap(sql);
        (string Qualifier, string PkColumn)? ResolvePk(Microsoft.SqlServer.TransactSql.ScriptDom.ColumnReferenceExpression colRef)
        {
            var ids = colRef.MultiPartIdentifier?.Identifiers;
            if (ids is not { Count: > 0 }) return null;
            var col = ids[^1].Value;
            var qual = ids.Count > 1 ? ids[^2].Value : null;   // <qual>.<col> → take the qualifier

            string? table = null;
            if (!string.IsNullOrEmpty(qual) && aliasMap.TryGetValue(qual, out var tq)) table = tq;
            else
            {
                var owners = aliasMap.Values.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(tn => { var ti = getTable(tn); return ti is not null && HasColumn(ti, col); }).ToList();
                if (owners.Count == 1) table = owners[0];
            }
            if (table is null) return null;
            var t = getTable(table);
            if (t?.PrimaryKey is not { Count: 1 } pk) return null;       // single-column PK only
            var qualifier = string.IsNullOrEmpty(qual) ? table : qual;
            return (qualifier!, pk[0]);
        }
        return Validation.SqlAstRepairs.TryFixGroupByGrain(
            sql, c => LabelColumnShape.IsMatch(c), ResolvePk, out repaired);
    }

    /// <summary>Maps FROM/JOIN aliases (and bare table names) to their base table, so a column
    /// qualifier can be resolved to the table it points at. Lightweight (no parser); SQL clause
    /// keywords are never captured as an alias.</summary>
    private static Dictionary<string, string> BuildAliasToTableMap(string sql)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FromJoinAlias.Matches(sql ?? string.Empty))
        {
            var table = m.Groups[1].Value;
            if (string.IsNullOrEmpty(table)) continue;
            map[table] = table;                                   // qualifier may be the table name itself
            var alias = m.Groups[2].Value;
            if (!string.IsNullOrEmpty(alias) && !SqlClauseKeywords.Contains(alias))
                map[alias] = table;
        }
        return map;
    }

    // The alias group has a negative lookahead so it never swallows a following clause keyword AS the
    // alias: for an UNaliased table immediately followed by a join (`FROM Tickets JOIN TicketStatuses s`),
    // a naive `(\w+)?` ate the `JOIN`, consuming it so the SECOND table+alias were never matched and its
    // qualifier (`s` / `TicketStatuses`) went unmapped — silently defeating any qualified-column repair.
    private static readonly Regex FromJoinAlias = new(
        @"\b(?:FROM|JOIN)\s+(?:\[?\w+\]?\.)?\[?(\w+)\]?(?:\s+(?:AS\s+)?(?!(?:INNER|LEFT|RIGHT|FULL|OUTER|CROSS|JOIN|ON|WHERE|GROUP|ORDER|HAVING|UNION|EXCEPT|INTERSECT|PIVOT|UNPIVOT)\b)(\w+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly System.Collections.Generic.HashSet<string> SqlClauseKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        { "ON", "WHERE", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "JOIN", "GROUP",
          "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "AS", "WITH", "PIVOT", "UNPIVOT" };

    private static IReadOnlyList<string> OrderSuffixesByLocale(IReadOnlyList<string> suffixes, string language)
    {
        if (suffixes is null || suffixes.Count == 0) return System.Array.Empty<string>();
        var isAr = string.Equals(language, Internal.QuestionLanguageDetector.Arabic, StringComparison.OrdinalIgnoreCase);
        return isAr
            ? suffixes.OrderByDescending(s => s.EndsWith("Ar", StringComparison.OrdinalIgnoreCase)).ToList()
            : suffixes.ToList();   // English: list order (En first by default)
    }

    private static bool HasColumn(Schema.InferredTable t, string col) =>
        t.Columns.Any(c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase));

    private static string? ResolveTarget(Schema.InferredTable t, string bad, IReadOnlyList<string> orderedSuffixes)
    {
        // 1) Exact twin: a column literally named <bad><suffix> on this table (Name → NameEn / NameAr).
        foreach (var suf in orderedSuffixes)
        {
            var twin = t.Columns.FirstOrDefault(c =>
                string.Equals(c.Name, bad + suf, StringComparison.OrdinalIgnoreCase));
            if (twin is not null) return twin.Name;       // return the exact-case real column name
        }
        // 2) Label-column fallback: covers Customers.FullNameEn where "Name" has no "NameEn" twin but the
        //    schema-annotated display column ends in a locale suffix and its base ends with the bad token.
        var label = t.Roles.LabelColumn;
        if (!string.IsNullOrWhiteSpace(label) && HasColumn(t, label!))
        {
            foreach (var suf in orderedSuffixes)
            {
                if (!label!.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) continue;
                var labelBase = label[..^suf.Length];      // "FullNameEn" → "FullName"
                if (labelBase.EndsWith(bad, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(labelBase, bad, StringComparison.OrdinalIgnoreCase))
                    return label;
            }
        }
        // 3) REVERSE twin: the model SUFFIXED a column that exists only as the BASE on this table —
        //    it wrote TicketStatuses.NameEn (its "every label is bilingual" prior) but the real
        //    column is a plain Name. Strip the locale suffix and use the base if it exists. The mirror
        //    of (1); without it the lossy strip-predicate fallback drops the whole filter and the
        //    answer goes over-broad (open tickets → ALL tickets). Only fires when <bad> itself does
        //    not exist on the table (guaranteed: the caller checked HasColumn) and the base does.
        foreach (var suf in orderedSuffixes)
        {
            if (!bad.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) continue;
            var baseName = bad[..^suf.Length];                          // "NameEn" → "Name"
            if (baseName.Length == 0) continue;
            var baseCol = t.Columns.FirstOrDefault(c =>
                string.Equals(c.Name, baseName, StringComparison.OrdinalIgnoreCase));
            if (baseCol is not null) return baseCol.Name;               // real plain column
        }
        return null;
    }


    /// <summary>
    /// Render the grounded facts as verbatim hint lines for the generator. Pure (no catalog/LLM) so
    /// the exact wording is unit-testable. Mirrors what the live smoke proved is load-bearing:
    /// real values, natural keys, resolved dates, derived metrics, distinctness/all-time intent.
    /// </summary>
    /// <param name="g">Resolved grounding facts for the question.</param>
    /// <param name="allowMultiPeriodHints">When true (default) and the question has more than one
    /// temporal binding, a SINGLE permissive multi-period hint is emitted instead of N strict
    /// "filter NO other period" lines (which jointly forbid the required OR-of-ranges). When false,
    /// the legacy per-binding strict hint is emitted for every period. Sourced from
    /// <see cref="AnalystOptions.AllowMultiPeriodHints"/> at the call site.</param>
    internal static List<string> BuildGroundingHints(QuestionGroundingContext g, bool allowMultiPeriodHints = true)
    {
        var hints = new List<string>();
        foreach (var lv in g.LinkedValues)
        {
            // SQL-escape the literal the model is told to copy: double the single-quote so a value like
            // O'Brien yields valid T-SQL ('O''Brien'), not a syntax error.
            var v = lv.Value.Replace("'", "''");
            // PRESCRIPTIVE: a value LINK means the question literally contains this value, so filtering IS
            // requested — force it (the declarative version made the 7B drop the filter, e.g. "open tickets"
            // returned all 83). The complementary over-filter GUARD strips status filters the model invents
            // WITHOUT a grounded value, so this can be a MUST without re-introducing spurious filters.
            hints.Add($"the question names '{v}' — you MUST add WHERE {lv.Table}.{lv.Column} = '{v}' (join {lv.Table} on its matching foreign key) to answer correctly.");
        }
        foreach (var nk in g.LinkedNaturalKeys)
        {
            var v = nk.Value.Replace("'", "''");
            hints.Add($"'{v}' is {nk.Entity} — filter {nk.Table}.{nk.Column} = '{v}'.");
        }
        // Name the period only — NOT the compiler's @-tokens (@month_start, @months:1). The direct
        // path has no token expander, so emitting raw tokens made the 7B copy undefined SQL or invent
        // broken date math. The system prompt defines the concrete T-SQL for each named period.
        //
        // MULTI-PERIOD: with ONE temporal binding the strict "filter NO other period" hint is correct.
        // With MORE THAN ONE (e.g. "compare Q1 and Q3 2025"), emitting that strict line PER binding
        // produces N jointly-contradictory instructions that forbid the required OR-of-ranges — a silent
        // semantic UNDER-FETCH. So when AllowMultiPeriodHints is on (default), collapse the >1 case into a
        // SINGLE permissive hint that names every period and asks for a range PER period (OR / UNION).
        // Schema-agnostic — names no table or column. Set the option false to restore per-binding behavior.
        if (allowMultiPeriodHints && g.LinkedTemporal.Count > 1)
        {
            var labels = string.Join(", ", g.LinkedTemporal.Select(t => $"'{t.Label}'"));
            hints.Add($"the question covers multiple periods: {labels} — apply a date range for EACH period (combine with OR, or UNION) on the relevant date column (per the Dates rules); do not collapse them to one period and do not drop any.");
        }
        else
        {
            foreach (var t in g.LinkedTemporal)
                hints.Add($"the question covers the period '{t.Label}' — apply the matching date range on the relevant date column (per the Dates rules) and filter NO other period.");
        }
        if (!string.IsNullOrEmpty(g.TimeBucketHint))
        {
            var b = g.TimeBucketHint.ToLowerInvariant();
            var expr = b == "day" ? "CAST(<date col> AS DATE)"
                     : b == "year" ? "YEAR(<date col>)"
                     : $"FORMAT(<date col>,'{(b == "month" ? "yyyy-MM" : b == "quarter" ? "yyyy" : "yyyy-MM-dd")}')";
            hints.Add($"the question groups over time PER {b.ToUpperInvariant()} — bucket with {expr} in BOTH the SELECT and the GROUP BY, ordered ascending (do NOT split the date into separate year/month columns).");
        }
        foreach (var dm in g.DerivedMetricHints)
            hints.Add($"for the metric '{dm.MetricKeyword}', aggregate with {dm.PreferredFunction}({dm.Expression}).");
        if (!string.IsNullOrEmpty(g.DateRoleHint))
            hints.Add($"the question's date intent is '{g.DateRoleHint}' — use the matching lifecycle date column.");
        if (g.IsDistinctCountIntent)
            hints.Add("the question asks for DISTINCT/unique — use COUNT(DISTINCT ...), not COUNT(*).");
        if (g.IsAllTimeIntent)
            hints.Add("the question is 'all time' — do NOT add a default date filter.");
        return hints;
    }

    // ── Self-consistency (Slice 1: abstain-fallback) ───────────────────────────────────────────

    /// <summary>Outcome of ONE emit→repair→validate→execute pass — the unit of work shared by the
    /// greedy loop and the self-consistency candidate draws. A pure carrier of the per-attempt result
    /// so the loop's control flow (break / continue / resample / abstain / persist) reads from it.</summary>
    private sealed class AttemptOutcome
    {
        public CompiledSql? Compiled { get; init; }
        public ExecutionResult? Exec { get; init; }
        public List<(string Name, string Before, string After)> RepairsApplied { get; init; } = new();
        public bool LossyStripFired { get; init; }
        public bool StrippedValueLiteral { get; init; }
        public string? EmitSql { get; init; }
        public string? EmitError { get; init; }
        public bool EmitProducedNoSql { get; init; }
        public bool ValidationFailed { get; init; }
        /// <summary>Validation-error string (when <see cref="ValidationFailed"/>), else null.</summary>
        public string? Error { get; init; }
    }

    /// <summary>
    /// ONE full attempt: emit (with optional <paramref name="sampling"/>) → deterministic repairs →
    /// validate → execute → invalid-column repairs. This is a PURE MECHANICAL extraction of the block
    /// that used to live inline in the greedy loop — with <paramref name="sampling"/>=null it is
    /// byte-for-byte equivalent (same repairs, same order, same trace recorders, same outcome), which is
    /// what keeps the greedy path's behavior unchanged. The self-consistency draws call it with a
    /// non-null sampling (temperature + distinct seed) to get DIVERSE candidates through the SAME path.
    /// </summary>
    private async Task<AttemptOutcome> EmitRepairValidateExecuteOnce(
        string question, IReadOnlyList<string> tableNames, IReadOnlyList<string> attemptHints,
        QuestionGroundingContext grounding, Abstractions.LlmSamplingOptions? sampling, int attemptLabel,
        BroadcastingStepList steps, CancellationToken cancellationToken)
    {
        var strippedValueLiteral = false;   // a load-bearing value filter was lossily dropped this attempt
        var lossyRepairFired = false;       // a lossy invalid-column strip fired this attempt
        var repairsApplied = new List<(string Name, string Before, string After)>();  // for the trace

        var emitSw = Stopwatch.StartNew();
        var emit = await _emitter.EmitAsync(question, tableNames, attemptHints, sampling, cancellationToken);
        emitSw.Stop();
        steps.RecordSqlEmit(question, emit, attemptLabel, emitSw.ElapsedMilliseconds);
        if (string.IsNullOrWhiteSpace(emit.Sql))
            return new AttemptOutcome { EmitProducedNoSql = true, EmitSql = emit.Sql, EmitError = emit.Error };

        // STAGE-0 PARSE GATE. Every string-mutation repair below routes its candidate through this gate
        // BEFORE it becomes the running SQL. A repair is accepted only when its rewrite still PARSES as
        // T-SQL (cheap TSql170Parser syntax check, no allowlist) — so a bad regex rewrite (e.g. a clause
        // glued to the next keyword) is DISCARDED at the source and can never poison the next repair pass
        // before the real AST validator runs. The strips preserve validity and so always pass; the one
        // clause-rebuilder (grain) is now protected here. Returns the candidate when it parses (and records
        // the repair in the trace), else keeps `current` and logs a discarded-repair warning.
        var sqlToUse = emit.Sql!;
        string AcceptIfParses(string repairName, string current, bool changed, string candidate)
        {
            if (!changed) return current;
            if (!Validation.SqlAstValidator.ParsesAsSingleSelect(candidate))
            {
                _logger.LogWarning("[repair] {Repair} produced unparseable SQL, discarded for q='{Q}'. SQL: {Sql}", repairName, question, candidate);
                return current;
            }
            _logger.LogInformation("[DirectAnalystPath] {Repair} for q='{Q}': {Before} => {After}", repairName, question, current, candidate);
            repairsApplied.Add((repairName, current, candidate));
            return candidate;
        }

        // STAGE-2 mechanism switch: when EnableAstRepairs is on, the three highest-risk repairs run as
        // gluing-proof AST mutations; when off (default), the regex twins run unchanged (byte-identical).
        var useAst = _options.Value.EnableAstRepairs;

        // OVER-FILTER GUARD: the 7B invents an unrequested status/lifecycle filter (WHERE Status='Paid'
        // on "total of all bills") regardless of the system-prompt rule. Deterministically strip a
        // status-column equality whose literal is neither in the question nor a grounded value — it was
        // made up. No prompt, no LLM; the one fix for the model prior that prose can't reach.
        var statusStripped = useAst
            ? AstStripUnrequestedStatusFilter(sqlToUse, question, grounding.LinkedValues.Select(v => v.Value), out var deScoped)
            : TryStripUnrequestedStatusFilter(sqlToUse, question, grounding.LinkedValues.Select(v => v.Value), out deScoped,
                trustGroundingOnly: _options.Value.StripStatusFilterTrustGroundingOnly);
        sqlToUse = AcceptIfParses("unrequested-status-strip", sqlToUse, statusStripped, deScoped);
        // Same model prior, boolean flavour: an invented IsPlanned=0 / IsActive=1 the question never named.
        sqlToUse = AcceptIfParses("unrequested-flag-strip", sqlToUse,
            TryStripUnrequestedFlagFilter(sqlToUse, question, out var deFlagged), deFlagged);
        // Same model prior, integer-FK flavour: an invented `ServiceTypeId = 1` the question never named and
        // grounding never bound ("how many blackouts in total" → all outages, not the electricity subset). Kept
        // when grounding bound the FK column / its referenced table, or the user named the id in the question.
        // deFk is pre-initialized so the flag short-circuit (&&) leaves it definitely assigned for AcceptIfParses.
        var deFk = sqlToUse;
        sqlToUse = AcceptIfParses("unrequested-fk-strip", sqlToUse,
            _options.Value.StripUnrequestedForeignKeyFilter &&
            TryStripUnrequestedForeignKeyFilter(sqlToUse, question, _knowledge.GetTable, tableNames,
                grounding.LinkedValues.Select(v => (v.Table, v.Column)),
                grounding.LinkedValues.Select(v => v.Table), out deFk), deFk);
        // A grounded enum value ("overdue"->Bills.Status) means an invented relative-date predicate
        // (WHERE DueDate<GETDATE()) is the model APPROXIMATING that concept — strip it so the injector's
        // grounded Status filter stands alone (else they AND to an empty intersection). No temporal cue → safe.
        sqlToUse = AcceptIfParses("ungrounded-date-strip", sqlToUse,
            TryStripUngroundedDatePredicate(sqlToUse, grounding.LinkedValues.Count > 0, grounding.LinkedTemporal.Count > 0, out var deDated), deDated);
        // Symmetric to the strip: ENFORCE a filter the question named but the 7B dropped (flaky under-filter).
        var valueInjected = useAst
            ? AstInjectGroundedValueFilters(sqlToUse, grounding.LinkedValues.Select(v => (v.Table, v.Column, v.Value)), out var injected)
            : TryInjectGroundedValueFilters(sqlToUse, grounding.LinkedValues.Select(v => (v.Table, v.Column, v.Value)), out injected);
        sqlToUse = AcceptIfParses("grounded-value-injection", sqlToUse, valueInjected, injected);
        // GRAIN: a label-only GROUP BY merges distinct entities sharing a name — add the entity key.
        var regrainedOk = useAst
            ? AstFixGroupByGrain(sqlToUse, _knowledge.GetTable, out var regrained)
            : TryFixGroupByGrain(sqlToUse, _knowledge.GetTable, out regrained);
        sqlToUse = AcceptIfParses("group-by-grain-fix", sqlToUse, regrainedOk, regrained);
        // CONTRADICTION: same column = two different literals (the bilingual "Name='مفتوحة' AND Name='Open'"
        // case → 0 rows). Keep the grounded literal, drop the other. Backstops the in-injector conflict-
        // replace, which misses some column-qualification forms. Runs last so it sees model + injected SQL.
        sqlToUse = AcceptIfParses("contradiction-resolution", sqlToUse,
            TryResolveContradictoryEqualityLiterals(sqlToUse, grounding.LinkedValues.Select(v => v.Value), out var deConflicted), deConflicted);

        var compiled = new CompiledSql(sqlToUse, new Dictionary<string, object?>());
        var validation = _validator.Validate(compiled);
        if (!validation.IsValid)
        {
            steps.RecordValidatorFailed(compiled, validation.Errors, attemptLabel);
            return new AttemptOutcome
            {
                ValidationFailed = true,
                Error = string.Join("; ", validation.Errors),
                EmitSql = emit.Sql,
                RepairsApplied = repairsApplied,
            };
        }
        steps.RecordValidatorOk(compiled, attemptLabel);

        var execSw = Stopwatch.StartNew();
        var exec = await _executor.ExecuteAsync(compiled, cancellationToken);

        // Deterministic invalid-column repairs, PRECISE before LOSSY (order is load-bearing):
        //
        // (1) Bilingual-column repair FIRST. The 7B uses the wrong locale form of a label column
        //     in EITHER direction — wrote `Name` where the real column is `NameEn`, OR wrote
        //     `TicketStatuses.NameEn` where the real column is a plain `Name`. Resolving it from
        //     the schema PRESERVES the predicate/projection (intent was right, only the column
        //     name was wrong). This MUST precede the strip below: stripping a real
        //     `TicketStatuses.NameEn = 'Open'` predicate counted ALL tickets, not the open ones.
        //     Operates on compiled.Sql (chains on any earlier status-strip/inject). No LLM call.
        if (exec.Error is not null &&
            TryResolveInvalidProjectionColumn(
                compiled.Sql, exec.Error, tableNames, _knowledge.GetTable,
                _options.Value.BilingualLocaleSuffixes,
                Internal.QuestionLanguageDetector.Detect(question), out var colRepaired))
        {
            // The bilingual rewrite can MAP the model's Arabic-literal predicate onto the SAME column as
            // a grounded English value (NameAr='مفتوحة' → Name='مفتوحة', alongside an injected/own
            // Name='Open') — a FRESH same-column contradiction that didn't exist pre-execution (the
            // columns were NameAr vs Name then). Resolve it here, on the rewritten SQL, before re-exec.
            if (TryResolveContradictoryEqualityLiterals(colRepaired, grounding.LinkedValues.Select(v => v.Value), out var colDeconflicted))
                colRepaired = colDeconflicted;
            var recompiledCol = new CompiledSql(colRepaired, new Dictionary<string, object?>());
            if (_validator.Validate(recompiledCol).IsValid)
            {
                var reexecCol = await _executor.ExecuteAsync(recompiledCol, cancellationToken);
                if (reexecCol.Error is null)
                {
                    _logger.LogInformation("[DirectAnalystPath] deterministic bilingual-column repair succeeded for q='{Q}'", question);
                    repairsApplied.Add(("bilingual-column-fix", compiled.Sql, colRepaired));
                    compiled = recompiledCol;
                    exec = reexecCol;
                }
            }
        }

        // (2) Strip-predicate FALLBACK — only when the column genuinely has no real sibling (the
        //     dominant case: the 7B bolts `IsDeleted = 0` onto a table that lacks it, and re-emits
        //     it when the error is fed back). LOSSY (drops the predicate), so it runs only after
        //     the precise bilingual repair above had its chance. No LLM call, no wasted retry.
        if (exec.Error is not null && TryStripInvalidColumnPredicate(compiled.Sql, exec.Error, out var repaired))
        {
            var recompiled = new CompiledSql(repaired, new Dictionary<string, object?>());
            if (_validator.Validate(recompiled).IsValid)
            {
                var reexec = await _executor.ExecuteAsync(recompiled, cancellationToken);
                if (reexec.Error is null)
                {
                    _logger.LogInformation("[DirectAnalystPath] deterministic invalid-column repair succeeded for q='{Q}'", question);
                    repairsApplied.Add(("lossy-invalid-column-strip", compiled.Sql, repaired));
                    // LOAD-BEARING if the dropped predicate carried a value LITERAL (WHERE BadCol='X') —
                    // a filter the question wanted — vs a flag (IsDeleted=0). Counting PREDICATE-scoped
                    // literals (only those after a comparison op / IN / LIKE) before/after isolates that:
                    // fewer such literals after ⇒ a value filter was lost. A FORMAT('yyyy-MM') bucket or a
                    // CASE label is NOT a predicate literal, so it never moves this number (the abstain
                    // decision no longer rides on the total apostrophe count).
                    strippedValueLiteral = CountPredicateValueLiterals(compiled.Sql) > CountPredicateValueLiterals(repaired);
                    compiled = recompiled;
                    exec = reexec;
                    lossyRepairFired = true;   // KEYSTONE: this strip drops a predicate → floor confidence
                }
            }
        }
        execSw.Stop();
        steps.RecordExecutor(compiled, exec, execSw.ElapsedMilliseconds, attemptLabel);
        // Surface the deterministic repairs in the trace (richer investigation detail) — one step per
        // attempt listing every no-LLM repair that fired, with before→after, so the UI shows WHY the
        // final SQL differs from the model's first emit.
        steps.RecordRepairsApplied(repairsApplied, attemptLabel);

        return new AttemptOutcome
        {
            Compiled = compiled,
            Exec = exec,
            RepairsApplied = repairsApplied,
            LossyStripFired = lossyRepairFired,
            StrippedValueLiteral = strippedValueLiteral,
            EmitSql = emit.Sql,
        };
    }

    /// <summary>
    /// The single decision point at every former <c>return null</c> (abstain) exit: when self-consistency
    /// is enabled for the abstain fallback, draw diverse candidates and vote; otherwise return <c>null</c>
    /// exactly as before. Flag OFF ⇒ identical <c>return null</c> (byte-identical abstain path).
    /// </summary>
    private async Task<AnalystResponse?> AbstainOrSelfConsistentAsync(
        string reason, AnalystRequest request, string question, IReadOnlyList<string> tableNames,
        IReadOnlyList<string> hints, QuestionGroundingContext grounding, AttemptOutcome? greedyCandidate,
        Stopwatch totalSw, BroadcastingStepList steps,
        Func<CompiledSql, ExecutionResult, int, Task<AnalystResponse>> persistAsync,
        CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        if (!(opts.EnableSelfConsistency && opts.SelfConsistencyOnAbstain))
            return null;   // flag off → honest abstain, unchanged
        return await TrySelfConsistentAnswerAsync(
            reason, request, question, tableNames, hints, grounding, greedyCandidate,
            totalSw, steps, persistAsync, cancellationToken);
    }

    /// <summary>
    /// Execution-guided self-consistency fallback (Slice 1). Candidate 0 = the already-computed greedy
    /// attempt (REUSED, not re-run). Candidates 1..k-1 are fresh diverse draws through the SAME
    /// emit→repair→validate→execute path, each with a higher temperature and a distinct seed. Each
    /// error-free result set is fingerprinted; the majority bucket wins. Returns the winner (persisted
    /// with provenance "self-consistent") when a bucket reaches <c>SelfConsistencyMinAgreement</c>;
    /// otherwise abstains (return null). Every sampled draw is gated by the per-question RetryBudget.
    /// </summary>
    private async Task<AnalystResponse?> TrySelfConsistentAnswerAsync(
        string reason, AnalystRequest request, string question, IReadOnlyList<string> tableNames,
        IReadOnlyList<string> hints, QuestionGroundingContext grounding, AttemptOutcome? greedyCandidate,
        Stopwatch totalSw, BroadcastingStepList steps,
        Func<CompiledSql, ExecutionResult, int, Task<AnalystResponse>> persistAsync,
        CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        var k = Math.Max(2, opts.SelfConsistencyK);
        var tol = Math.Max(0, opts.SelfConsistencyNumericTolerance);
        steps.RecordSelfConsistencyTriggered(question, reason);

        // Collect candidates. Candidate 0 reuses the greedy attempt when it produced an error-free
        // execution; otherwise the first index is a fresh draw (so an abstain after a validation/exec
        // failure still gets k diverse draws).
        var candidates = new List<AttemptOutcome>();
        if (greedyCandidate is { Compiled: not null, Exec: { Error: null } })
            candidates.Add(greedyCandidate);

        for (int i = candidates.Count == 0 ? 0 : 1; i < k; i++)
        {
            if (!_retryBudget.TryConsumeLlmCall("SelfConsistency"))
            {
                _logger.LogInformation("[DirectAnalystPath] self-consistency draw {I} skipped — retry budget exhausted for q='{Q}'", i, question);
                break;
            }
            var sampling = new Abstractions.LlmSamplingOptions(
                Temperature: opts.SelfConsistencyTemperature,
                Seed: opts.SelfConsistencySeedBase + i);
            var draw = await EmitRepairValidateExecuteOnce(
                question, tableNames, hints, grounding, sampling, attemptLabel: i, steps, cancellationToken);
            if (draw is { Compiled: not null, Exec: { Error: null } })
                candidates.Add(draw);
        }

        // Fingerprint each error-free candidate's result set, then vote.
        var fingerprints = candidates
            .Select(c => SelfConsistencyVote.Fingerprint(c.Exec!, tol))
            .ToList();
        var hasCandidate0 = greedyCandidate is { Compiled: not null, Exec: { Error: null } };
        var pick = SelfConsistencyVote.Pick(
            fingerprints,
            minAgreement: Math.Max(1, opts.SelfConsistencyMinAgreement),
            candidate0Index: hasCandidate0 ? 0 : (int?)null,
            lossyRepairCounts: candidates.Select(c => c.RepairsApplied.Count(r => r.Name.Contains("lossy", StringComparison.OrdinalIgnoreCase))).ToList(),
            rowCounts: candidates.Select(c => c.Exec!.RowCount).ToList());

        steps.RecordSelfConsistencyVote(question, fingerprints, pick);

        if (pick.WinnerIndex is not int winnerIdx)
        {
            _logger.LogInformation("[DirectAnalystPath] self-consistency ABSTAIN — no bucket reached agreement {Min} (candidates={N}) for q='{Q}'",
                opts.SelfConsistencyMinAgreement, candidates.Count, question);
            return null;   // genuine disagreement → honest abstain
        }

        var winner = candidates[winnerIdx];
        _logger.LogInformation("[DirectAnalystPath] self-consistency WINNER idx={Idx} agreement={Agree}/{N} for q='{Q}'",
            winnerIdx, pick.Agreement, candidates.Count, question);

        // Persist the winner via the shared explain+persist local fn, then stamp the self-consistency
        // provenance + an agreement-derived confidence over it (attempt label 0 keeps the base confidence
        // tier; provenance/confidence below are authoritative).
        var response = await persistAsync(winner.Compiled!, winner.Exec!, 0);
        var agreementRatio = candidates.Count > 0 ? (double)pick.Agreement / candidates.Count : 0.0;
        return response with
        {
            Provenance = "self-consistent",
            Confidence = Math.Round(agreementRatio, 3),
        };
    }
}
