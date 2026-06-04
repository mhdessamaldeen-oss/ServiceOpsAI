namespace AnalystAgent.Execution;

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Abstractions;
using AnalystAgent.Configuration;
using AnalystAgent.Models;
using AnalystAgent.Schema;

internal sealed class ReadOnlyExecutor : IExecutor
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly AnalystOptions _options;
    private readonly ILogger<ReadOnlyExecutor> _logger;

    /// <summary>Defence-in-depth: only SELECT and WITH (CTEs) are allowed past this point.</summary>
    private static readonly Regex ReadOnlyGuard = new(
        @"^\s*(?:SELECT|WITH)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ReadOnlyExecutor(
        IDbConnectionFactory connectionFactory,
        IOptions<AnalystOptions> options,
        ILogger<ReadOnlyExecutor> logger)
    {
        _connectionFactory = connectionFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ExecutionResult> ExecuteAsync(CompiledSql compiled, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        // C3 — defence-in-depth: reject non-SELECT SQL before it touches the database.
        // The upstream SqlAstValidator already enforces this via ScriptDom; this is the
        // last-resort guard against any future code path that constructs CompiledSql manually.
        if (!ReadOnlyGuard.IsMatch(compiled.Sql))
        {
            _logger.LogError("[ReadOnlyExecutor] REJECTED non-SELECT SQL: {SqlPrefix}",
                compiled.Sql.Length > 80 ? compiled.Sql[..80] + "…" : compiled.Sql);
            return new ExecutionResult(rows, 0, sw.Elapsed,
                Error: "Executor rejected non-SELECT SQL (defence-in-depth guard).");
        }

        try
        {
            using var conn = _connectionFactory.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = compiled.Sql;
            cmd.CommandTimeout = _options.CommandTimeoutSeconds;

            foreach (var (name, value) in compiled.Parameters)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            // J5 — distinguish "result naturally has N rows" from "we capped reading at MaxRows".
            // We peek one row past the cap before deciding the result is truncated; the caller
            // (reply renderer / orchestrator trace) appends a "showing first N of many" hint
            // when IsTruncated is true so the user knows more rows exist.
            var truncated = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= _options.MaxRows) { truncated = true; break; }
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }

            return new ExecutionResult(rows, rows.Count, sw.Elapsed, IsTruncated: truncated);
        }
        catch (Exception ex)
        {
            return new ExecutionResult(rows, rows.Count, sw.Elapsed, Error: ex.Message);
        }
    }
}
