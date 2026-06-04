namespace AnalystAgent.Execution;

using Microsoft.Extensions.Logging;
using AnalystAgent.Abstractions;
using AnalystAgent.Models;
using AnalystAgent.Schema;
using AnalystAgent.Semantic;

/// <summary>
/// Outermost executor wrapper that masks PII columns in result rows. Defence in depth: the
/// compiler already refuses to project columns declared sensitive in <see cref="EntityDefinition.SensitiveColumns"/>,
/// but if a future code path (raw SQL, SELECT *, joins selecting nested objects) slips a sensitive
/// column through, this wrapper masks it before the row reaches the explainer / trace / chat UI.
///
/// <para>Strategy: walk every result row, check each column key against the semantic layer's
/// per-table sensitive-column set, and replace matching values with <c>"[REDACTED]"</c>. The set
/// is keyed by table name; the executor doesn't know which table each result column came from
/// (the rows arrive as <c>Dictionary&lt;string, object?&gt;</c> with no table-attribution), so we
/// take the union of sensitive column NAMES across all entities — any column whose name matches
/// any entity's sensitive list is redacted regardless of source table. False-positive cost is
/// trivial (we mask a non-sensitive column with the same name); false-negative cost is data leak.</para>
///
/// <para>Wraps <see cref="CostGateExecutor"/> in the registered chain (CostGate → Cache →
/// ReadOnly → Pii). Off the hot path when there are zero declared sensitive columns.</para>
/// </summary>
internal sealed class PiiRedactingExecutor : IExecutor
{
    private const string RedactionPlaceholder = "[REDACTED]";

    private readonly IExecutor _inner;
    private readonly ISemanticLayer _semanticLayer;
    private readonly IAnalystSchemaAccessPolicy _schemaPolicy;
    private readonly ILogger<PiiRedactingExecutor> _logger;

    /// <summary>Thread-safe lazy union of every entity's SensitiveColumns — case-insensitive name set.
    /// Uses Lazy&lt;T&gt; to guarantee exactly one initialization even under concurrent first requests.</summary>
    private readonly Lazy<HashSet<string>> _sensitiveColumns;

    public PiiRedactingExecutor(
        IExecutor inner,
        ISemanticLayer semanticLayer,
        IAnalystSchemaAccessPolicy schemaPolicy,
        ILogger<PiiRedactingExecutor> logger)
    {
        _inner = inner;
        _semanticLayer = semanticLayer;
        _schemaPolicy = schemaPolicy;
        _logger = logger;
        _sensitiveColumns = new Lazy<HashSet<string>>(BuildSensitiveColumnSet);
    }

    public async Task<ExecutionResult> ExecuteAsync(CompiledSql compiled, CancellationToken cancellationToken = default)
    {
        var result = await _inner.ExecuteAsync(compiled, cancellationToken);
        var sensitive = GetSensitiveColumnSet();
        if (sensitive.Count == 0 || result.Rows is null || result.RowCount == 0) return result;

        // Quick check: any of this result's columns sensitive? If not, skip entirely.
        var firstKeys = result.Rows[0].Keys;
        var redactKeys = firstKeys.Where(k => sensitive.Contains(StripTablePrefix(k))).ToList();
        if (redactKeys.Count == 0) return result;

        // Some sensitive columns slipped through — mask them in every row. Allocate fresh
        // dictionaries (the executor's rows are usually IReadOnlyDictionary so we can't mutate).
        var redactedRows = new List<IReadOnlyDictionary<string, object?>>(result.RowCount);
        var redactionCount = 0;
        foreach (var row in result.Rows)
        {
            var copy = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
            foreach (var key in redactKeys)
            {
                if (copy.ContainsKey(key)) { copy[key] = RedactionPlaceholder; redactionCount++; }
            }
            redactedRows.Add(copy);
        }
        _logger.LogWarning(
            "[PiiRedactor] Masked {Count} cell(s) across {Rows} row(s). Sensitive keys: {Keys}. " +
            "Compiler should have rejected these in SELECT — investigate the planner output.",
            redactionCount, redactedRows.Count, string.Join(", ", redactKeys));

        // Stamp PiiRedactionCount on the result so the trace's sql-execution typed payload
        // surfaces this as a security-relevant breadcrumb (count > 0 indicates an attempted
        // exposure that the redactor caught — investigate the planner / semantic layer).
        return result with { Rows = redactedRows, PiiRedactionCount = redactionCount };
    }

    /// <summary>Strip "Table_" alias prefix produced by the compiler's collision-aliasing path
    /// (e.g. "TicketAiAnalyses_DiagnosticMetadata" → "DiagnosticMetadata").</summary>
    private static string StripTablePrefix(string columnName)
    {
        var idx = columnName.LastIndexOf('_');
        return idx > 0 && idx < columnName.Length - 1 ? columnName[(idx + 1)..] : columnName;
    }

    private HashSet<string> GetSensitiveColumnSet() => _sensitiveColumns.Value;

    /// <summary>Builds the union of sensitive column names — called exactly once by the Lazy wrapper.</summary>
    private HashSet<string> BuildSensitiveColumnSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _semanticLayer.Config?.Entities ?? Enumerable.Empty<EntityDefinition>())
        {
            if (e.SensitiveColumns is null) continue;
            foreach (var col in e.SensitiveColumns)
                if (!string.IsNullOrEmpty(col)) set.Add(col);
        }
        foreach (var pattern in _schemaPolicy is null ? Enumerable.Empty<string>() : GetConfiguredSensitiveColumnNames())
            set.Add(pattern);
        return set;
    }

    private IEnumerable<string> GetConfiguredSensitiveColumnNames()
    {
        foreach (var e in _semanticLayer.Config?.Entities ?? Enumerable.Empty<EntityDefinition>())
        {
            foreach (var col in e.DisplayColumns.Concat(e.SensitiveColumns ?? Enumerable.Empty<string>()))
            {
                if (!string.IsNullOrWhiteSpace(col) && _schemaPolicy.IsColumnSensitive(e.Table, col))
                    yield return col;
            }
        }
    }
}
