namespace AnalystAgent.Execution;

using System.Diagnostics;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Abstractions;
using AnalystAgent.Configuration;
using AnalystAgent.Models;
using AnalystAgent.Schema;

/// <summary>
/// §8 cost gate from the abstraction guide. Wraps the inner executor with a SHOWPLAN_XML
/// pre-flight: parse SQL Server's estimated query cost, reject queries above
/// <see cref="AnalystOptions.MaxEstimatedQueryCost"/> before they touch the actual data.
///
/// Cheap protection against pathological joins, missing WHERE clauses, and Cartesian
/// products. Adds ~10-50ms per query when enabled — fine for an interactive copilot, off by
/// default so existing benchmarks don't change underneath us.
/// </summary>
internal sealed class CostGateExecutor : IExecutor
{
    private readonly CachingExecutor _inner;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly AnalystOptions _options;
    private readonly ILogger<CostGateExecutor> _logger;

    public CostGateExecutor(
        CachingExecutor inner,
        IDbConnectionFactory connectionFactory,
        IOptions<AnalystOptions> options,
        ILogger<CostGateExecutor> logger)
    {
        _inner = inner;
        _connectionFactory = connectionFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ExecutionResult> ExecuteAsync(CompiledSql compiled, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableCostGate || _options.MaxEstimatedQueryCost <= 0)
            return await _inner.ExecuteAsync(compiled, cancellationToken);

        var (cost, planError) = TryEstimateCost(compiled);
        if (planError is not null)
        {
            // SHOWPLAN failure shouldn't block the query — log and pass through. The query
            // might still be fine; we just couldn't size it.
            _logger.LogDebug("[AnalystAgent] Cost gate: SHOWPLAN_XML failed ({Error}); passing through.", planError);
            return await _inner.ExecuteAsync(compiled, cancellationToken);
        }

        if (cost > _options.MaxEstimatedQueryCost)
        {
            _logger.LogInformation(
                "[AnalystAgent] Cost gate REJECTED query: estimated cost {Cost:F2} > limit {Limit:F2}.",
                cost, _options.MaxEstimatedQueryCost);
            return new ExecutionResult(
                Array.Empty<IReadOnlyDictionary<string, object?>>(), 0, TimeSpan.Zero,
                Error: $"Query rejected by cost gate: estimated cost {cost:F2} exceeds the limit ({_options.MaxEstimatedQueryCost:F2}). Please narrow the question (add filters, reduce joins, or set a smaller limit).",
                CostGated: true,
                EstimatedCost: cost);
        }
        // Pass-through: stamp the estimated cost on the result so the trace shows what we measured.
        var inner = await _inner.ExecuteAsync(compiled, cancellationToken);
        return inner with { CostGated = false, EstimatedCost = cost };
    }

    /// <summary>
    /// Runs SET SHOWPLAN_XML ON + the original SQL, parses the returned XML, and pulls the
    /// statement subtree cost. Returns (cost, null) on success or (0, errorMessage) on failure
    /// (caller decides whether to gate or pass through).
    /// </summary>
    private (double cost, string? error) TryEstimateCost(CompiledSql compiled)
    {
        try
        {
            using var conn = _connectionFactory.Open();

            // SHOWPLAN_XML must be set in its own batch — can't share a batch with the SELECT.
            using (var setCmd = conn.CreateCommand())
            {
                setCmd.CommandText = "SET SHOWPLAN_XML ON;";
                setCmd.ExecuteNonQuery();
            }

            string planXml;
            try
            {
                using var planCmd = conn.CreateCommand();
                planCmd.CommandText = compiled.Sql;
                planCmd.CommandTimeout = Math.Max(5, _options.CommandTimeoutSeconds / 4);
                foreach (var (name, value) in compiled.Parameters)
                    planCmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

                using var reader = planCmd.ExecuteReader();
                if (!reader.Read()) return (0, "SHOWPLAN_XML returned no row");
                planXml = reader.GetString(0);
            }
            finally
            {
                using var unsetCmd = conn.CreateCommand();
                unsetCmd.CommandText = "SET SHOWPLAN_XML OFF;";
                try { unsetCmd.ExecuteNonQuery(); } catch { /* best-effort */ }
            }

            return (ExtractSubtreeCost(planXml), null);
        }
        catch (Exception ex)
        {
            return (0, ex.Message);
        }
    }

    /// <summary>
    /// Pulls the highest StatementSubTreeCost from the SHOWPLAN_XML payload. SQL Server emits
    /// one StmtSimple per statement; we take the max so a multi-statement plan (rare here, but
    /// possible via metadata-handler queries) still gets bounded.
    /// </summary>
    private static double ExtractSubtreeCost(string planXml)
    {
        if (string.IsNullOrWhiteSpace(planXml)) return 0;
        try
        {
            using var sr = new StringReader(planXml);
            using var xr = XmlReader.Create(sr, new XmlReaderSettings { IgnoreWhitespace = true });
            double max = 0;
            while (xr.Read())
            {
                if (xr.NodeType != XmlNodeType.Element) continue;
                if (xr.LocalName != "StmtSimple") continue;
                var cost = xr.GetAttribute("StatementSubTreeCost");
                if (cost is null) continue;
                if (double.TryParse(cost, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > max)
                    max = v;
            }
            return max;
        }
        catch
        {
            return 0;
        }
    }
}
