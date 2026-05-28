namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceOpsAI.Services.AI.Providers.Roles;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Schema;

// Escape valve for question shapes the form-filling QuerySpec can't express: window
// functions (running totals, ranks, percentiles), recursive CTEs, complex multi-CTE
// analytics, etc. Activated as a LAST-RESORT fallback when the standard
// SpecExtractor -> Compiler -> Executor loop exhausts its retries without producing a
// runnable query. Output is raw T-SQL fed straight to the executor; the existing
// SqlAstValidator + ReadOnlyExecutor read-only guard remain the safety net (no DML,
// no multi-statement, no DDL — same guarantees as the form-filling path).
public interface ILlmDirectSqlEmitter
{
    Task<DirectSqlResult> EmitAsync(
        string question,
        IReadOnlyList<string> candidateTableNames,
        CancellationToken cancellationToken = default);
}

public sealed record DirectSqlResult(string? Sql, string? Error, string? Prompt, string? RawLlmOutput);

internal sealed class LlmDirectSqlEmitter : ILlmDirectSqlEmitter
{
    private readonly ILlmClient _llm;
    private readonly ISchemaKnowledge _knowledge;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textCatalog;
    private readonly ILogger<LlmDirectSqlEmitter> _logger;

    public LlmDirectSqlEmitter(
        IRoleBoundLlmClientFactory llmFactory,
        ISchemaKnowledge knowledge,
        IOptionsMonitor<CopilotTextCatalog> textCatalog,
        ILogger<LlmDirectSqlEmitter> logger)
    {
        _llm = llmFactory.For(AiRole.QuerySpecComposer);
        _knowledge = knowledge;
        _textCatalog = textCatalog;
        _logger = logger;
    }

    public async Task<DirectSqlResult> EmitAsync(
        string question,
        IReadOnlyList<string> candidateTableNames,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new DirectSqlResult(null, "empty question", null, null);
        if (candidateTableNames is null || candidateTableNames.Count == 0)
            return new DirectSqlResult(null, "no candidate tables", null, null);

        var tables = candidateTableNames
            .Select(n => _knowledge.GetTable(n))
            .Where(t => t is not null)
            .Cast<InferredTable>()
            .ToList();
        if (tables.Count == 0)
            return new DirectSqlResult(null, "candidate tables not found in schema knowledge", null, null);

        var prompt = BuildPrompt(question, tables);
        string raw;
        try
        {
            using var hint = LlmCallStageHint.Use("LlmDirectSqlEmitter");
            var systemPrompt = _textCatalog.CurrentValue.DirectSqlSystemPrompt;
            raw = await _llm.GenerateTextAsync(systemPrompt, prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LlmDirectSqlEmitter] LLM call failed");
            return new DirectSqlResult(null, ex.Message, prompt, null);
        }

        var sql = ExtractSql(raw);
        if (string.IsNullOrWhiteSpace(sql))
            return new DirectSqlResult(null, "no SQL extracted from LLM output", prompt, raw);

        // Rewrite non-T-SQL idioms (LIMIT, backticks, NOW, ILIKE, ||) before validation.
        sql = TsqlDialectNormalizer.Normalize(sql);

        return new DirectSqlResult(sql, null, prompt, raw);
    }

    // Build the user prompt: shape-detected gold examples + table list + question. Mirrors the
    // SpecExtractor's table rendering so the LLM sees the same schema slice. Shape-routed
    // examples are the key correctness lever for advanced SQL (window / recursive / self-join /
    // EXISTS / UNION / HAVING-multi) — without an example to imitate, a 7B local model
    // generates simpler shapes that compile but don't answer the question. Per DIN-SQL §3.2
    // (shape-classified few-shot) and CHESS §4 (worked-example prompting).
    private static string BuildPrompt(string question, IReadOnlyList<InferredTable> tables)
    {
        var sb = new StringBuilder();
        sb.Append("Question: \"").Append(question).AppendLine("\"");
        sb.AppendLine();

        // Shape-detected gold examples. Detection is the same keyword logic as the upstream
        // QuestionShapeClassifier — we re-detect here so the prompt knows WHICH shape it's
        // emitting and can use a tailored template. Generic case: no examples appended (the
        // tables + system-prompt instructions are enough for ordinary analytics).
        var advancedShape = DetectAdvancedShape(question);
        if (advancedShape != AdvancedShape.None)
        {
            sb.AppendLine("Worked example for this shape (USE THE SAME PATTERN — substitute YOUR tables/columns):");
            sb.AppendLine(GetShapeExample(advancedShape));
            sb.AppendLine();
        }

        sb.AppendLine("Available tables:");
        foreach (var t in tables)
        {
            sb.Append("- ").Append(t.Name);
            if (!string.IsNullOrEmpty(t.Description)) sb.Append(" — ").Append(t.Description);
            sb.AppendLine();
            sb.AppendLine("  Columns:");
            foreach (var c in t.Columns)
            {
                sb.Append("    ").Append(t.Name).Append('.').Append(c.Name)
                    .Append(" (").Append(c.Type);
                if (c.Nullable) sb.Append(", nullable");
                sb.AppendLine(")");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Output ONLY the SELECT statement (or a WITH ... AS (...) SELECT statement for CTEs). No commentary, no markdown fence.");
        return sb.ToString();
    }

    /// <summary>Fine-grained shape detection for prompt-example selection. Mirrors the keyword
    /// vocabulary in <c>CopilotTextCatalog.QuestionShapeComplexHints</c>.</summary>
    private enum AdvancedShape { None, WindowRank, WindowRunning, WindowLag, Recursive, SelfJoin, Exists, Union, HavingMultiAgg }

    private static AdvancedShape DetectAdvancedShape(string question)
    {
        var q = question.ToLowerInvariant();
        // Order matters — most-specific patterns first.
        if (q.Contains("running total") || q.Contains("running sum") || q.Contains("cumulative")) return AdvancedShape.WindowRunning;
        if (q.Contains("lag ") || q.Contains("lead ") || q.Contains("compared to the previous")
            || q.Contains("vs previous") || q.Contains("change from previous") || q.Contains("change vs")
            || q.Contains("month over month") || q.Contains("year over year") || q.Contains("quarter over quarter"))
            return AdvancedShape.WindowLag;
        if (q.Contains("rank ") || q.Contains("ranked ") || q.Contains("ranking") || q.Contains("ranks ")
            || q.Contains("percentile") || q.Contains("median") || q.Contains("row_number")) return AdvancedShape.WindowRank;
        if (q.Contains("recursive") || q.Contains("all child") || q.Contains("children of")
            || q.Contains("parent chain") || q.Contains("parent of") || q.Contains("ancestor")
            || q.Contains("descendant") || q.Contains("hierarchy")) return AdvancedShape.Recursive;
        if (q.Contains("same customer as") || q.Contains("same region as") || q.Contains("same department as")
            || q.Contains("same service type as") || q.Contains("other tickets from the same")
            || q.Contains("other bills from the same") || q.Contains("other customers in the same"))
            return AdvancedShape.SelfJoin;
        if (q.Contains("everything created today") || q.Contains("everything from today")
            || q.Contains("combined activity") || q.Contains("all activity from")
            || q.Contains("tickets and bills and outages") || q.Contains("across tickets bills outages"))
            return AdvancedShape.Union;
        // Detect HAVING-multi-agg AFTER simpler patterns: two "with more than"/"with at least" clauses joined by "and".
        if ((System.Text.RegularExpressions.Regex.Matches(q, @"\b(?:with\s+(?:more\s+than|at\s+least|fewer\s+than|less\s+than))\b").Count >= 2)
            || q.Contains(" and more than ") || q.Contains(" and at least ") || q.Contains(" and fewer than "))
            return AdvancedShape.HavingMultiAgg;
        if (q.Contains("have at least one") || q.Contains("have at least")
            || q.Contains("with at least one")
            || q.Contains("who have any")
            || q.Contains("where the customer also has") || q.Contains("where the ticket also has")
            || q.Contains("have ever had") || q.Contains("ever filed") || q.Contains("ever issued")
            || q.Contains("never filed") || q.Contains("never had")
            || q.Contains("without any"))
            return AdvancedShape.Exists;
        return AdvancedShape.None;
    }

    /// <summary>Gold-quality T-SQL example per advanced shape. Built on Syrian-utility-ops
    /// schema (Tickets/Bills/Outages/Customers/Regions) — same domain the model is asked about.
    /// Examples follow Spider/BIRD gold-SQL conventions: explicit joins, qualified columns,
    /// idiomatic T-SQL (no LIMIT, GETDATE() / DATEADD, OFFSET-FETCH or TOP).</summary>
    private static string GetShapeExample(AdvancedShape shape) => shape switch
    {
        AdvancedShape.WindowRank => """
SELECT
    Customers.Id, Customers.FullNameEn,
    SUM(Bills.TotalAmount) AS TotalBilled,
    RANK() OVER (ORDER BY SUM(Bills.TotalAmount) DESC) AS [Rank]
FROM Customers
INNER JOIN Bills ON Bills.CustomerId = Customers.Id
GROUP BY Customers.Id, Customers.FullNameEn
ORDER BY [Rank];
""",
        AdvancedShape.WindowRunning => """
SELECT
    DATEADD(month, DATEDIFF(month, 0, Bills.IssuedAt), 0) AS MonthStart,
    COUNT(*) AS MonthlyCount,
    SUM(COUNT(*)) OVER (ORDER BY DATEADD(month, DATEDIFF(month, 0, Bills.IssuedAt), 0) ROWS UNBOUNDED PRECEDING) AS RunningTotal
FROM Bills
GROUP BY DATEADD(month, DATEDIFF(month, 0, Bills.IssuedAt), 0)
ORDER BY MonthStart;
""",
        AdvancedShape.WindowLag => """
WITH MonthlyCounts AS (
    SELECT DATEADD(month, DATEDIFF(month, 0, Tickets.CreatedAt), 0) AS MonthStart, COUNT(*) AS TicketCount
    FROM Tickets WHERE Tickets.IsDeleted = 0
    GROUP BY DATEADD(month, DATEDIFF(month, 0, Tickets.CreatedAt), 0)
)
SELECT
    MonthStart,
    TicketCount,
    LAG(TicketCount, 1) OVER (ORDER BY MonthStart) AS PreviousMonth,
    TicketCount - LAG(TicketCount, 1) OVER (ORDER BY MonthStart) AS ChangeVsPreviousMonth
FROM MonthlyCounts
ORDER BY MonthStart;
""",
        AdvancedShape.Recursive => """
WITH TicketTree AS (
    SELECT t.Id, t.TicketNumber, t.ParentTicketId, 0 AS Depth
    FROM Tickets t
    WHERE t.TicketNumber = 'TKT-00010'
    UNION ALL
    SELECT child.Id, child.TicketNumber, child.ParentTicketId, parent.Depth + 1
    FROM Tickets child
    INNER JOIN TicketTree parent ON child.ParentTicketId = parent.Id
    WHERE child.IsDeleted = 0
)
SELECT * FROM TicketTree WHERE Depth > 0 ORDER BY Depth, TicketNumber;
""",
        AdvancedShape.SelfJoin => """
SELECT other.TicketNumber, other.Title, other.CreatedAt
FROM Tickets target
INNER JOIN Tickets other ON other.CustomerId = target.CustomerId AND other.Id <> target.Id
WHERE target.TicketNumber = 'TKT-00020'
  AND target.IsDeleted = 0 AND other.IsDeleted = 0
ORDER BY other.CreatedAt DESC;
""",
        AdvancedShape.Exists => """
SELECT Customers.Id, Customers.FullNameEn
FROM Customers
WHERE EXISTS (
    SELECT 1 FROM Bills
    WHERE Bills.CustomerId = Customers.Id AND Bills.Status = 'Paid'
);
""",
        AdvancedShape.Union => """
SELECT 'Ticket' AS Kind, Tickets.TicketNumber AS Reference, Tickets.CreatedAt AS [When]
FROM Tickets WHERE CAST(Tickets.CreatedAt AS DATE) = CAST(GETDATE() AS DATE) AND Tickets.IsDeleted = 0
UNION ALL
SELECT 'Bill', Bills.BillNumber, Bills.IssuedAt
FROM Bills WHERE CAST(Bills.IssuedAt AS DATE) = CAST(GETDATE() AS DATE)
UNION ALL
SELECT 'Outage', Outages.OutageNumber, Outages.StartedAt
FROM Outages WHERE CAST(Outages.StartedAt AS DATE) = CAST(GETDATE() AS DATE)
ORDER BY [When] DESC;
""",
        AdvancedShape.HavingMultiAgg => """
SELECT Regions.NameEn,
       COUNT(DISTINCT Tickets.Id) AS TicketCount,
       COUNT(DISTINCT Outages.Id) AS OutageCount
FROM Regions
LEFT JOIN Tickets ON Tickets.RegionId = Regions.Id AND Tickets.IsDeleted = 0
LEFT JOIN Outages ON Outages.RegionId = Regions.Id
GROUP BY Regions.NameEn
HAVING COUNT(DISTINCT Tickets.Id) > 5 AND COUNT(DISTINCT Outages.Id) > 3;
""",
        _ => "",
    };

    // Strip code fences and explanation. Accept the first SELECT statement we can find.
    private static string? ExtractSql(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Strip ```sql ... ``` or ``` ... ``` fences if the model wraps despite the
        // system prompt's no-markdown instruction (qwen2.5-coder routinely does).
        var fence = Regex.Match(raw, "```(?:sql|tsql)?\\s*(.+?)\\s*```", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var candidate = fence.Success ? fence.Groups[1].Value : raw;

        // Trim leading prose. Accept SELECT or WITH (CTE) as the valid start.
        // Picking the earliest of the two keywords means a "WITH cte AS (SELECT ...) SELECT ..."
        // statement keeps its WITH prefix instead of being chopped down to the inner SELECT,
        // which would be syntactically broken.
        var withIdx = FindKeywordIndex(candidate, "WITH");
        var selectIdx = FindKeywordIndex(candidate, "SELECT");
        var startIdx = withIdx >= 0 && (selectIdx < 0 || withIdx < selectIdx) ? withIdx : selectIdx;
        if (startIdx < 0) return null;
        var sql = candidate.Substring(startIdx).Trim();

        // Drop any trailing prose after the final semicolon.
        var lastSemi = sql.LastIndexOf(';');
        if (lastSemi > 0 && lastSemi < sql.Length - 1)
            sql = sql.Substring(0, lastSemi + 1);

        return sql.Length > 0 ? sql : null;
    }

    // Case-insensitive whole-word index search. Avoids matching SELECT inside a CTE column
    // name like "WITH selected_rows AS (..." or WITH inside a column called "WITHHELD".
    private static int FindKeywordIndex(string text, string keyword)
    {
        int from = 0;
        while (from < text.Length)
        {
            var idx = text.IndexOf(keyword, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;
            bool leftBoundary = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]) && text[idx - 1] != '_';
            int afterIdx = idx + keyword.Length;
            bool rightBoundary = afterIdx >= text.Length || (!char.IsLetterOrDigit(text[afterIdx]) && text[afterIdx] != '_');
            if (leftBoundary && rightBoundary) return idx;
            from = idx + 1;
        }
        return -1;
    }
}
