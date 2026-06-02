namespace SuperAdminCopilot.Pipeline;

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Grounding;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Retrieval;
using SuperAdminCopilot.Schema;

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
    Task<CopilotResponse?> TryAnswerAsync(
        CopilotRequest request, string question,
        Stopwatch totalSw, BroadcastingStepList steps, CancellationToken cancellationToken);
}

internal sealed class DirectAnalystPath : IDirectAnalystPath
{
    private readonly ISchemaSemanticRetriever _retriever;
    private readonly ISchemaKnowledge _knowledge;
    private readonly IEntityCatalog _catalog;
    private readonly IQuestionGrounder _grounder;
    private readonly Stages.ILlmDirectSqlEmitter _emitter;
    private readonly IValidator _validator;
    private readonly IExecutor _executor;
    private readonly IExplainer _explainer;
    private readonly IResponsePersister _persister;
    private readonly IOptions<CopilotOptions> _options;
    private readonly ILogger<DirectAnalystPath> _logger;

    public DirectAnalystPath(
        ISchemaSemanticRetriever retriever,
        ISchemaKnowledge knowledge,
        IEntityCatalog catalog,
        IQuestionGrounder grounder,
        Stages.ILlmDirectSqlEmitter emitter,
        IValidator validator,
        IExecutor executor,
        IExplainer explainer,
        IResponsePersister persister,
        IOptions<CopilotOptions> options,
        ILogger<DirectAnalystPath> logger)
    {
        _retriever = retriever;
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

    public async Task<CopilotResponse?> TryAnswerAsync(
        CopilotRequest request, string question,
        Stopwatch totalSw, BroadcastingStepList steps, CancellationToken cancellationToken)
    {
        try
        {
            if (!_knowledge.IsAvailable) return null;

            // 1) Schema-link GENEROUSLY. Embedding retrieval alone drops the table the question needs
            //    when a hub like Tickets has ~17 FK neighbours and a hard cap evicts the right one
            //    (this starved "rank regions by tickets" — Regions never reached the prompt). Per
            //    "The Death of Schema Linking?" (arXiv 2408.07702), a capable model does BETTER with a
            //    broad slice than with aggressive filtering. So: tables the question NAMES (always) ∪
            //    embedding seeds ∪ their 1-hop FK neighbours — never silently evict a needed table.
            var retrieval = await _retriever.RetrieveAsync(question, _options.Value.RetrieverTopK, cancellationToken);
            var baseTables = new HashSet<string>(
                retrieval.Tables.Select(t => t.Table.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var n in KeywordMatchedTables(question)) baseTables.Add(n);
            if (baseTables.Count == 0) return null;
            var candidateNames = ExpandWithFkNeighbours(baseTables);

            var infTables = candidateNames
                .Select(_knowledge.GetTable)
                .Where(t => t is not null)
                .Cast<InferredTable>()
                .ToList();
            if (infTables.Count == 0) return null;
            _logger.LogInformation("[DirectAnalystPath] q='{Question}' candidate tables=[{Tables}]",
                question, string.Join(", ", candidateNames));

            // 2) Ground (deterministic, no LLM) — resolves real values, natural keys, dates. The moat.
            QuestionGroundingContext grounding;
            try { grounding = await _grounder.GroundAsync(question, infTables, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[DirectAnalystPath] grounding failed; using empty grounding.");
                grounding = QuestionGroundingContext.Empty;
            }

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
            string? lastError = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var attemptHints = lastError is null
                    ? hints
                    : hints.Append($"Your previous T-SQL failed with this SQL Server error — FIX it and re-output valid T-SQL (use a WITH CTE if a derived-table alias was referenced out of scope): {lastError}").ToList();

                var emit = await _emitter.EmitAsync(question, tableNames, attemptHints, cancellationToken);
                if (string.IsNullOrWhiteSpace(emit.Sql))
                {
                    _logger.LogInformation("[DirectAnalystPath] emit produced no SQL (attempt {Attempt}) for q='{Q}': {Reason}", attempt, question, emit.Error ?? "(no reason)");
                    return null;
                }

                var compiled = new CompiledSql(emit.Sql!, new Dictionary<string, object?>());
                var validation = _validator.Validate(compiled);
                if (!validation.IsValid)
                {
                    lastError = string.Join("; ", validation.Errors);
                    _logger.LogInformation("[DirectAnalystPath] validation rejected (attempt {Attempt}) for q='{Q}': {Errors}\n  SQL: {Sql}", attempt, question, lastError, emit.Sql);
                    continue;
                }

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

                if (exec.Error is not null)
                {
                    lastError = exec.Error;
                    _logger.LogInformation("[DirectAnalystPath] execution error (attempt {Attempt}) for q='{Q}': {Error}\n  SQL: {Sql}", attempt, question, exec.Error, emit.Sql);
                    continue;
                }

                var explanation = await _explainer.ExplainAsync(question, exec, compiled, cancellationToken);
                // Confidence: higher when the grounder pinned a concrete value / natural key, lower on a self-corrected attempt.
                var grounded = grounding.LinkedValues.Count > 0 || grounding.LinkedNaturalKeys.Count > 0;
                var conf = attempt > 0 ? 0.6 : (grounded ? 0.8 : 0.72);
                return (await _persister.PersistAsync(request, totalSw, steps,
                    reply: explanation.Reply,
                    sql: compiled.Sql,
                    rowCount: exec.RowCount,
                    rows: exec.Rows,
                    cancellationToken: cancellationToken)) with { Provenance = attempt > 0 ? "direct-analyst:self-corrected" : "direct-analyst", Confidence = conf };
            }
            return null; // both attempts failed → fall through to the heavy pipeline
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Never let the front-path break a question — fall through to the heavy pipeline.
            _logger.LogWarning(ex, "[DirectAnalystPath] failed; falling through to the form-filling pipeline.");
            return null;
        }
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

    /// <summary>Deterministic schema-link by NAME: every table whose name (or a synonym) appears in
    /// the question is included — UNCAPPED — so a question that explicitly mentions "regions",
    /// "customers", or "bills" always gets those tables regardless of embedding similarity. This is
    /// the fix for the over-abstention failure (the needed lookup was being evicted by the cap).</summary>
    private List<string> KeywordMatchedTables(string question)
    {
        var ql = " " + question.ToLowerInvariant() + " ";
        var matched = new List<string>();
        foreach (var t in _knowledge.AllTables)
        {
            var name = t.Name.ToLowerInvariant();
            bool hit = ql.Contains(name);
            if (!hit && name.Length > 3 && name.EndsWith("s")) hit = ql.Contains(name[..^1]); // singular
            if (!hit && t.Synonyms is { Count: > 0 })
                hit = t.Synonyms.Any(s => !string.IsNullOrWhiteSpace(s) && ql.Contains(s.ToLowerInvariant()));
            if (hit) matched.Add(t.Name);
        }
        return matched;
    }

    /// <summary>Add FK-connected neighbour tables (1 hop, either direction) of every base table so the
    /// model has the lookup/fact tables it needs to join. Base/named tables are ALWAYS kept (added
    /// first); neighbours are added up to a generous budget. Schema-driven via the live catalog FKs.</summary>
    private List<string> ExpandWithFkNeighbours(HashSet<string> baseTables)
    {
        var result = new List<string>(baseTables);   // base/named tables first — never dropped
        var have = new HashSet<string>(baseTables, StringComparer.OrdinalIgnoreCase);
        var fks = _catalog.Snapshot.ForeignKeys;
        int budget = 12;   // generous — a capable model handles a broad slice (Death of Schema Linking)
        foreach (var s in baseTables)
        {
            foreach (var fk in fks)
            {
                if (budget <= 0) break;
                var other = string.Equals(fk.ParentTable, s, StringComparison.OrdinalIgnoreCase) ? fk.ReferencedTable
                          : string.Equals(fk.ReferencedTable, s, StringComparison.OrdinalIgnoreCase) ? fk.ParentTable
                          : null;
                if (other is not null && _catalog.TableExists(other) && have.Add(other))
                { result.Add(other); budget--; }
            }
            if (budget <= 0) break;
        }
        return result;
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
            hints.Add($"'{lv.Value}' is the value {lv.Table}.{lv.Column} — filter {lv.Table}.{lv.Column} = '{lv.Value}' (join {lv.Table} to the fact table on the matching foreign-key column if needed).");
        foreach (var nk in g.LinkedNaturalKeys)
            hints.Add($"'{nk.Value}' is {nk.Entity} — filter {nk.Table}.{nk.Column} = '{nk.Value}'.");
        foreach (var t in g.LinkedTemporal)
            hints.Add(t.EndToken is null
                ? $"time range '{t.Label}': filter the relevant date column {t.Op} {t.StartToken}."
                : $"time range '{t.Label}': filter the relevant date column >= {t.StartToken} AND < {t.EndToken}.");
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
