using System.Text.Json;
using ServiceOpsAI.Models.AI;

namespace ServiceOpsAI.Services.AI.Copilot.Diagnostics
{
    /// <summary>
    /// Per-call diagnostic collector. Each agentic service (Classifier, Planner, Validator,
    /// Executor) records the work it actually did into a sink, and the orchestrator drains
    /// those entries onto the matching top-level step as SubSteps. This is what powers the
    /// "click a node → see every prompt, response, SQL, row count, duration" tree in the UI.
    ///
    /// Sinks are throw-away (one per top-level phase). Pass `null` to opt out — every recorder
    /// is a no-op when the sink is null, so production code stays untouched if a caller doesn't
    /// want to instrument.
    /// </summary>
    public sealed class CopilotTraceSink
    {
        private readonly List<CopilotExecutionStep> _entries = new();

        public IReadOnlyList<CopilotExecutionStep> Entries => _entries;

        /// <summary>
        /// Generic recorder — use when none of the typed helpers fit. Free-form action + detail.
        /// </summary>
        public void Record(
            CopilotExecutionLayer layer,
            string action,
            string detail,
            CopilotStepStatus status = CopilotStepStatus.Ok,
            long elapsedMs = 0,
            string? technicalData = null,
            string? location = null)
        {
            var start = DateTime.UtcNow.AddMilliseconds(-elapsedMs);
            _entries.Add(new CopilotExecutionStep
            {
                Layer = layer,
                Action = action,
                Detail = detail,
                Status = status,
                ElapsedMs = elapsedMs,
                StartedAt = start,
                CompletedAt = DateTime.UtcNow,
                TechnicalData = technicalData,
                Location = location
            });
        }

        /// <summary>
        /// Record a single LLM provider call with full prompt + raw response so the user can
        /// reproduce or diff it in the inspector. The TechnicalData payload is a typed JSON
        /// shape the front-end recognises (kind="llm-call"); the Detail field has a one-line
        /// human-readable summary for grep-ability in logs.
        /// </summary>
        public void RecordLlmCall(
            CopilotExecutionLayer layer,
            string stage,
            string providerName,
            string modelName,
            string prompt,
            string responseText,
            long elapsedMs,
            bool providerSuccess,
            string? providerError = null,
            string? location = null)
        {
            var status = providerSuccess
                ? CopilotStepStatus.Ok
                : CopilotStepStatus.Error;

            var summary = providerSuccess
                ? $"LLM call OK — sent {prompt?.Length ?? 0} chars to {providerName}/{modelName}, got {responseText?.Length ?? 0} chars in {elapsedMs}ms."
                : $"LLM call FAILED — {providerName}/{modelName}: {providerError ?? "unknown error"}";

            var payload = JsonSerializer.Serialize(new
            {
                kind = "llm-call",
                stage,
                provider = providerName,
                model = modelName,
                prompt = prompt ?? string.Empty,
                response = responseText ?? string.Empty,
                providerSuccess,
                providerError = providerError ?? string.Empty,
                promptLength = prompt?.Length ?? 0,
                responseLength = responseText?.Length ?? 0
            }, new JsonSerializerOptions { WriteIndented = true });

            _entries.Add(new CopilotExecutionStep
            {
                Layer = layer,
                Action = $"LLM call [{stage}]",
                Detail = summary,
                Status = status,
                ElapsedMs = elapsedMs,
                StartedAt = DateTime.UtcNow.AddMilliseconds(-elapsedMs),
                CompletedAt = DateTime.UtcNow,
                TechnicalData = payload,
                Location = location ?? $"{providerName}/{modelName}"
            });
        }

        /// <summary>
        /// Record a SQL execution attempt — generated SQL, row count, error if any. The
        /// front-end recognises kind="sql-execution" and renders the SQL in a dedicated
        /// monospace block with a Copy button.
        /// </summary>
        public void RecordSqlExecution(
            string sql,
            int rowCount,
            int columnCount,
            long elapsedMs,
            bool success,
            string? errorMessage = null,
            string? location = null)
        {
            var status = success ? CopilotStepStatus.Ok : CopilotStepStatus.Error;
            var summary = success
                ? $"SQL OK — {rowCount} row(s), {columnCount} column(s) in {elapsedMs}ms."
                : $"SQL FAILED — {errorMessage ?? "unknown error"}";

            var payload = JsonSerializer.Serialize(new
            {
                kind = "sql-execution",
                sql = sql ?? string.Empty,
                rowCount,
                columnCount,
                success,
                errorMessage = errorMessage ?? string.Empty,
                elapsedMs
            }, new JsonSerializerOptions { WriteIndented = true });

            _entries.Add(new CopilotExecutionStep
            {
                Layer = CopilotExecutionLayer.DataExecution,
                Action = "SQL execution [Database]",
                Detail = summary,
                Status = status,
                ElapsedMs = elapsedMs,
                StartedAt = DateTime.UtcNow.AddMilliseconds(-elapsedMs),
                CompletedAt = DateTime.UtcNow,
                TechnicalData = payload,
                Location = location ?? "SuperAdminCopilot"
            });
        }

        /// <summary>
        /// Record a deterministic preflight / function call — schema lookups, regex checks,
        /// JSON parses, plan normalization. Kept generic because the variety is huge.
        /// </summary>
        public void RecordFunctionCall(
            CopilotExecutionLayer layer,
            string functionName,
            string description,
            string input,
            string output,
            long elapsedMs,
            CopilotStepStatus status = CopilotStepStatus.Ok,
            string? location = null)
        {
            var payload = JsonSerializer.Serialize(new
            {
                kind = "function-call",
                function = functionName,
                description,
                input = input ?? string.Empty,
                output = output ?? string.Empty,
                inputLength = input?.Length ?? 0,
                outputLength = output?.Length ?? 0
            }, new JsonSerializerOptions { WriteIndented = true });

            _entries.Add(new CopilotExecutionStep
            {
                Layer = layer,
                Action = $"Function: {functionName}",
                Detail = description,
                Status = status,
                ElapsedMs = elapsedMs,
                StartedAt = DateTime.UtcNow.AddMilliseconds(-elapsedMs),
                CompletedAt = DateTime.UtcNow,
                TechnicalData = payload,
                Location = location
            });
        }

        /// <summary>Drain all collected entries into a target step's SubSteps list.</summary>
        public void DrainInto(CopilotExecutionStep parent)
        {
            if (parent == null || _entries.Count == 0) return;
            parent.SubSteps.AddRange(_entries);
            _entries.Clear();
        }
    }
}
