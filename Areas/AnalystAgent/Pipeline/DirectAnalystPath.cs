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
            var hints = BuildGroundingHints(grounding);
            var tableNames = names.ToList();

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
                return (await _persister.PersistAsync(request, totalSw, steps,
                    reply: explanation.Reply, sql: compiled.Sql, rowCount: exec.RowCount, rows: exec.Rows,
                    cancellationToken: cancellationToken))
                    with { Provenance = attempt > 0 ? "direct-analyst:self-corrected" : "direct-analyst", Confidence = conf };
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
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var attemptHints = hints;
                if (lastError is not null)
                    attemptHints = hints.Append($"Your previous T-SQL failed with this SQL Server error — FIX it and re-output valid T-SQL (use a WITH CTE if a derived-table alias was referenced out of scope): {lastError}").ToList();
                else if (emptyHint is not null)
                    attemptHints = hints.Append(emptyHint).ToList();

                var emitSw = Stopwatch.StartNew();
                var emit = await _emitter.EmitAsync(question, tableNames, attemptHints, cancellationToken);
                emitSw.Stop();
                steps.RecordSqlEmit(question, emit, attempt, emitSw.ElapsedMilliseconds);
                if (string.IsNullOrWhiteSpace(emit.Sql))
                {
                    _logger.LogInformation("[DirectAnalystPath] emit produced no SQL (attempt {Attempt}) for q='{Q}': {Reason}", attempt, question, emit.Error ?? "(no reason)");
                    break;
                }

                var compiled = new CompiledSql(emit.Sql!, new Dictionary<string, object?>());
                var validation = _validator.Validate(compiled);
                if (!validation.IsValid)
                {
                    steps.RecordValidatorFailed(compiled, validation.Errors, attempt);
                    lastError = string.Join("; ", validation.Errors); emptyHint = null;
                    _logger.LogInformation("[DirectAnalystPath] validation rejected (attempt {Attempt}) for q='{Q}': {Errors}\n  SQL: {Sql}", attempt, question, lastError, emit.Sql);
                    continue;
                }
                steps.RecordValidatorOk(compiled, attempt);

                var execSw = Stopwatch.StartNew();
                var exec = await _executor.ExecuteAsync(compiled, cancellationToken);

                // Deterministic repair for the dominant 7B failure: it bolts `IsDeleted = 0` (or another
                // non-existent column) onto a table that lacks it, and re-emits the SAME filter when the
                // error is fed back. So when SQL Server reports an invalid column, strip that predicate
                // and re-run ONCE here — no LLM call, no wasted retry.
                if (exec.Error is not null && TryStripInvalidColumnPredicate(emit.Sql!, exec.Error, out var repaired))
                {
                    var recompiled = new CompiledSql(repaired, new Dictionary<string, object?>());
                    if (_validator.Validate(recompiled).IsValid)
                    {
                        var reexec = await _executor.ExecuteAsync(recompiled, cancellationToken);
                        if (reexec.Error is null)
                        {
                            _logger.LogInformation("[DirectAnalystPath] deterministic invalid-column repair succeeded for q='{Q}'", question);
                            compiled = recompiled;
                            exec = reexec;
                        }
                    }
                }

                // Second deterministic repair: the model projected a column the table lacks (its strong
                // `Name` prior over the real bilingual NameEn/NameAr). Resolve it from the schema — no
                // LLM call — then re-validate + re-execute ONCE. The strip-predicate above repairs WHERE;
                // this repairs the SELECT/ORDER-BY/GROUP-BY projection it cannot touch. Operates on
                // compiled.Sql so it chains on any prior strip.
                if (exec.Error is not null &&
                    TryResolveInvalidProjectionColumn(
                        compiled.Sql, exec.Error, tableNames, _knowledge.GetTable,
                        _options.Value.BilingualLocaleSuffixes,
                        Internal.QuestionLanguageDetector.Detect(question), out var colRepaired))
                {
                    var recompiledCol = new CompiledSql(colRepaired, new Dictionary<string, object?>());
                    if (_validator.Validate(recompiledCol).IsValid)
                    {
                        var reexecCol = await _executor.ExecuteAsync(recompiledCol, cancellationToken);
                        if (reexecCol.Error is null)
                        {
                            _logger.LogInformation("[DirectAnalystPath] deterministic bilingual-column repair succeeded for q='{Q}'", question);
                            compiled = recompiledCol;
                            exec = reexecCol;
                        }
                    }
                }
                execSw.Stop();
                steps.RecordExecutor(compiled, exec, execSw.ElapsedMilliseconds, attempt);

                if (exec.Error is not null)
                {
                    lastError = exec.Error; emptyHint = null;
                    _logger.LogInformation("[DirectAnalystPath] execution error (attempt {Attempt}) for q='{Q}': {Error}\n  SQL: {Sql}", attempt, question, exec.Error, emit.Sql);
                    continue;
                }

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
                    return null; // honest abstain → caller surfaces "could not answer"
                }
                return await PersistAsync(compiled, exec, attempt);
            }

            // All attempts errored or stayed empty — return the honest empty result if we got one.
            if (emptyFallback is { } ef)
                return await PersistAsync(ef.Compiled, ef.Exec, maxAttempts);
            return null; // genuinely couldn't answer → honest abstain
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
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        var before = sql.Trim();
        var s = sql;
        s = Regex.Replace(s, $@"\bWHERE\s+(?:{pred})\s+AND\b", "WHERE", O);                                   // WHERE <bad> AND ... -> WHERE ...
        s = Regex.Replace(s, $@"\s+AND\s+(?:{pred})", " ", O);                                                 // ... AND <bad> ... -> ...
        s = Regex.Replace(s, $@"\bWHERE\s+(?:{pred})\s*(?=(GROUP\s+BY|ORDER\s+BY|HAVING|\)|;|$))", "", O);     // WHERE <bad> (only pred) -> drop WHERE
        repaired = s.Trim();
        return !string.Equals(repaired, before, StringComparison.Ordinal);
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

    private static readonly Regex FromJoinAlias = new(
        @"\b(?:FROM|JOIN)\s+(?:\[?\w+\]?\.)?\[?(\w+)\]?(?:\s+(?:AS\s+)?(\w+))?",
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
        return null;
    }


    /// <summary>
    /// Render the grounded facts as verbatim hint lines for the generator. Pure (no catalog/LLM) so
    /// the exact wording is unit-testable. Mirrors what the live smoke proved is load-bearing:
    /// real values, natural keys, resolved dates, derived metrics, distinctness/all-time intent.
    /// </summary>
    internal static List<string> BuildGroundingHints(QuestionGroundingContext g)
    {
        var hints = new List<string>();
        foreach (var lv in g.LinkedValues)
        {
            // SQL-escape the literal the model is told to copy: double the single-quote so a value like
            // O'Brien yields valid T-SQL ('O''Brien'), not a syntax error.
            var v = lv.Value.Replace("'", "''");
            hints.Add($"'{v}' is the value {lv.Table}.{lv.Column} — filter {lv.Table}.{lv.Column} = '{v}' (join {lv.Table} to the fact table on the matching foreign-key column if needed).");
        }
        foreach (var nk in g.LinkedNaturalKeys)
        {
            var v = nk.Value.Replace("'", "''");
            hints.Add($"'{v}' is {nk.Entity} — filter {nk.Table}.{nk.Column} = '{v}'.");
        }
        // Name the period only — NOT the compiler's @-tokens (@month_start, @months:1). The direct
        // path has no token expander, so emitting raw tokens made the 7B copy undefined SQL or invent
        // broken date math. The system prompt defines the concrete T-SQL for each named period.
        foreach (var t in g.LinkedTemporal)
            hints.Add($"the question covers the period '{t.Label}' — apply the matching date range on the relevant date column (per the Dates rules) and filter NO other period.");
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
}
