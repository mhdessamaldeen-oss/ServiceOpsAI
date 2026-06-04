namespace AnalystAgent.Pipeline;

using System.Diagnostics;
using AnalystAgent.Abstractions;
using AnalystAgent.Models;

// Multi-sub-question fan-out: parallel by default, sequential when later sub-questions
// back-reference prior ones ("then", "those", "them").
internal sealed partial class AnalystOrchestrator
{
    private async Task<AnalystResponse> RunDecomposedAsync(
        AnalystRequest request, IReadOnlyList<string> subQuestions,
        Stopwatch totalSw, BroadcastingStepList steps,
        CancellationToken cancellationToken)
    {
        // Sequential when later sub-questions back-reference prior ones ("then", "those", "them"); parallel otherwise.
        var sequential = LooksLikeSequentialChain(subQuestions);
        AnalystResponse[] results;
        if (sequential)
        {
            results = new AnalystResponse[subQuestions.Count];
            for (int i = 0; i < subQuestions.Count; i++)
            {
                var sub = subQuestions[i];
                // Append prior step's rows so the LLM can resolve pronouns; downstream stages just see it in the question text.
                var augmentedQuestion = i == 0
                    ? sub
                    : sub + BuildPriorStepContext(i, subQuestions, results);
                results[i] = await _singleExecutor.ExecuteAsync(
                    new AnalystRequest(augmentedQuestion, request.ConversationId),
                    augmentedQuestion,
                    Stopwatch.StartNew(),
                    new BroadcastingStepList(_progress, TargetFor(request)),
                    cancellationToken);
            }
        }
        else
        {
            var tasks = subQuestions
                .Select(sub => _singleExecutor.ExecuteAsync(
                    new AnalystRequest(sub, request.ConversationId), sub, Stopwatch.StartNew(),
                    new BroadcastingStepList(_progress, TargetFor(request)), cancellationToken))
                .ToList();
            results = await Task.WhenAll(tasks);
        }

        var subReplies = new List<(string Q, AnalystResponse R)>(subQuestions.Count);
        for (int i = 0; i < subQuestions.Count; i++)
        {
            var sub = subQuestions[i];
            var subResp = results[i];
            subReplies.Add((sub, subResp));
            steps.RecordSubQuestion(i, sub, subResp);
        }

        var reply = new System.Text.StringBuilder();
        var sql = new System.Text.StringBuilder();
        var anyError = false;
        var totalRows = 0;
        for (int i = 0; i < subReplies.Count; i++)
        {
            var (q, r) = subReplies[i];
            reply.Append("**Q").Append(i + 1).Append(": ").Append(q).AppendLine("**");
            reply.AppendLine(r.Reply);
            reply.AppendLine();
            if (!string.IsNullOrEmpty(r.Sql))
            {
                if (sql.Length > 0) sql.AppendLine().AppendLine();
                sql.Append("-- Q").Append(i + 1).AppendLine().Append(r.Sql);
            }
            if (!string.IsNullOrEmpty(r.Error)) anyError = true;
            if (r.RowCount.HasValue) totalRows += r.RowCount.Value;
        }
        return await _persister.PersistAsync(request, totalSw, steps,
            reply: reply.ToString().TrimEnd(),
            sql: sql.Length > 0 ? sql.ToString() : null,
            rowCount: totalRows,
            error: anyError ? Text.DecomposedFailedSummary : null,
            cancellationToken: cancellationToken);
    }

    // Words that signal a sub-question refers back to a prior step's result.
    private static readonly System.Text.RegularExpressions.Regex SequentialBackReference = new(
        @"\b(then|those|these|them|their|the\s+above|from\s+(?:that|those|these|them))\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool LooksLikeSequentialChain(IReadOnlyList<string> subQuestions)
    {
        if (subQuestions is null || subQuestions.Count < 2) return false;
        for (int i = 1; i < subQuestions.Count; i++)
            if (SequentialBackReference.IsMatch(subQuestions[i])) return true;
        return false;
    }

    // Compact "Context from QN:" block appended to the next sub-question. Capped to keep prompt small.
    private static string BuildPriorStepContext(int stepIndex, IReadOnlyList<string> subQuestions, AnalystResponse[] results)
    {
        if (stepIndex == 0) return string.Empty;
        var prior = results[stepIndex - 1];
        if (prior?.Rows is null || prior.Rows.Count == 0) return string.Empty;
        const int maxRows = 10;
        const int maxCellChars = 60;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.Append("Context from Q").Append(stepIndex).Append(" (\"").Append(subQuestions[stepIndex - 1]).AppendLine("\"):");
        var rowsToShow = Math.Min(maxRows, prior.Rows.Count);
        for (int r = 0; r < rowsToShow; r++)
        {
            var row = prior.Rows[r];
            sb.Append("  - ");
            bool firstCol = true;
            foreach (var kv in row)
            {
                if (!firstCol) sb.Append("; ");
                firstCol = false;
                var v = kv.Value?.ToString() ?? "(null)";
                if (v.Length > maxCellChars) v = v[..maxCellChars] + "…";
                sb.Append(kv.Key).Append('=').Append(v);
            }
            sb.AppendLine();
        }
        if (prior.Rows.Count > rowsToShow)
            sb.Append("  … and ").Append(prior.Rows.Count - rowsToShow).AppendLine(" more row(s) not shown.");
        sb.AppendLine("Use these values to scope this question — filter on the identifying columns above (e.g. names, codes, IDs).");
        return sb.ToString();
    }
}
