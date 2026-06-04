namespace AnalystAgent.Pipeline.Stages;

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceOpsAI.Services.AI.Providers.Roles;
using AnalystAgent.Abstractions;
using AnalystAgent.Configuration;
using AnalystAgent.Schema;

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

    /// <summary>
    /// Grounded overload: <paramref name="groundingHints"/> are deterministic, schema-resolved
    /// facts (e.g. "'Malki' is a Region -> JOIN Regions ON Tickets.RegionId = Regions.Id; filter
    /// Regions.NameEn") injected verbatim so the model uses the RIGHT join column and value instead
    /// of guessing. Validated live (2026-06-02): grounding turned a silently-wrong join into a
    /// correct one across 12/12 shapes on qwen2.5-coder:7b. Empty hints == the bare overload above.
    /// </summary>
    Task<DirectSqlResult> EmitAsync(
        string question,
        IReadOnlyList<string> candidateTableNames,
        IReadOnlyList<string> groundingHints,
        CancellationToken cancellationToken = default);
}

public sealed record DirectSqlResult(
    string? Sql, string? Error, string? Prompt, string? RawLlmOutput, bool SchemaWasCompacted = false);

internal sealed class LlmDirectSqlEmitter : ILlmDirectSqlEmitter
{
    private readonly ILlmClient _llm;
    private readonly ISchemaKnowledge _knowledge;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textCatalog;
    private readonly IOptions<AnalystOptions> _options;
    private readonly ILogger<LlmDirectSqlEmitter> _logger;
    private readonly Sql.Dialects.ISqlDialect _dialect;

    public LlmDirectSqlEmitter(
        IRoleBoundLlmClientFactory llmFactory,
        ISchemaKnowledge knowledge,
        IOptionsMonitor<CopilotTextCatalog> textCatalog,
        IOptions<AnalystOptions> options,
        Sql.Dialects.ISqlDialect dialect,
        ILogger<LlmDirectSqlEmitter> logger)
    {
        _llm = llmFactory.For(AiRole.QuerySpecComposer);
        _knowledge = knowledge;
        _textCatalog = textCatalog;
        _options = options;
        _dialect = dialect;
        _logger = logger;
    }

    public Task<DirectSqlResult> EmitAsync(
        string question,
        IReadOnlyList<string> candidateTableNames,
        CancellationToken cancellationToken = default)
        => EmitAsync(question, candidateTableNames, System.Array.Empty<string>(), cancellationToken);

    public async Task<DirectSqlResult> EmitAsync(
        string question,
        IReadOnlyList<string> candidateTableNames,
        IReadOnlyList<string> groundingHints,
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

        var prompt = BuildPromptAsync(question, tables, groundingHints ?? System.Array.Empty<string>(), out var schemaWasCompacted);
        _logger.LogInformation("[LlmDirectSqlEmitter] prompt {Len} chars ({Tables} tables, compacted={Compacted}) for q='{Q}'", prompt.Length, tables.Count, schemaWasCompacted, question);
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
        sql = _dialect.NormalizeRawSql(sql);

        return new DirectSqlResult(sql, null, prompt, raw, schemaWasCompacted);
    }

    // Build the user prompt: the deterministic grounding block + the COMPLETE schema slice (primary
    // keys, the foreign-key JOIN MAP, the soft-delete column, column types + sample values), then the
    // question. NO hardcoded worked-examples: a capable model writes correct SQL — joins, windows,
    // CTEs, UNION — when it has the REAL schema. Examples were a crutch that (a) didn't cover every
    // shape (e.g. double-top-N had none) and (b) secretly carried the join knowledge that belongs in
    // the schema itself. Owner's call 2026-06-03; consistent with "The Death of Schema Linking?"
    // (arXiv 2408.07702): give the model more correct schema, not a near-miss template to imitate.
    private string BuildPromptAsync(string question, IReadOnlyList<InferredTable> tables, IReadOnlyList<string> groundingHints, out bool wasCompacted)
    {
        wasCompacted = false;
        // Token-budget aware. The model's REAL context window (Ollama num_ctx) is finite; if the prompt
        // overflows it Ollama SILENTLY TRUNCATES the tail — dropping the question or schema and producing
        // a wrong/empty answer (a likely cause of the hard/Arabic-question failures). So: render the
        // query-scoped schema, and if it still exceeds the configured prompt budget, re-render EVERY
        // table compactly (keys + FK join-map + label + lookups) so it fits — never a blind cut, because
        // the PK/FK map is always kept so joins stay expressible. PromptCharBudget ≈ num_ctx − output.
        var budgetChars = Math.Max(6000, _options.Value.PromptCharBudget);
        var suffixes = _options.Value.BilingualLocaleSuffixes;
        var prompt = AssemblePrompt(question, tables, groundingHints, compactAll: false, suffixes);
        if (prompt.Length > budgetChars)
        {
            var compact = AssemblePrompt(question, tables, groundingHints, compactAll: true, suffixes);
            _logger.LogInformation("[LlmDirectSqlEmitter] prompt {Full} chars > budget {Budget} → compacted to {Compact} for q='{Q}'",
                prompt.Length, budgetChars, compact.Length, question);
            prompt = compact;
            wasCompacted = true;
        }
        return prompt;
    }

    private static string AssemblePrompt(string question, IReadOnlyList<InferredTable> tables,
        IReadOnlyList<string> groundingHints, bool compactAll, IReadOnlyList<string>? localeSuffixes = null)
    {
        var sb = new StringBuilder();
        sb.Append("Question: \"").Append(question).AppendLine("\"");
        // Per-question language signal so an Arabic question SELECTs/GROUP BYs the Ar label column,
        // not the En default. Stronger than the generic system-prompt rule (the model sees it inline).
        if (string.Equals(Internal.QuestionLanguageDetector.Detect(question),
                          Internal.QuestionLanguageDetector.Arabic, StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("Language: ARABIC — when a table has both an En and an Ar label column "
                + "(NameEn/NameAr, FullNameEn/FullNameAr, TitleEn/TitleAr), project and GROUP BY the Ar column.");
        sb.AppendLine();

        // Deterministic grounding — resolved values / right join column / bilingual NameEn-NameAr.
        // The single most important block: without it the model joined Tickets.DepartmentId not RegionId.
        AppendGroundingBlock(sb, groundingHints);

        AppendSchemaBlock(sb, tables, question, groundingHints, compactAll, localeSuffixes);

        sb.AppendLine();
        sb.AppendLine("Output ONLY the SELECT statement (or a WITH ... AS (...) SELECT statement for CTEs). No commentary, no markdown fence.");
        return sb.ToString();
    }

    /// <summary>Renders the schema slice the model needs to write correct SQL on its own — but SCOPED
    /// TO THE QUERY so the prompt stays small. Tables the query is ABOUT (named in the question or
    /// referenced by grounding) get FULL columns; FK-neighbour tables that exist only so the model can
    /// JOIN to them get a COMPACT form (primary key + foreign keys + label/natural-key + any lookup
    /// column with sample values). Every table still shows its PK + FK JOIN MAP + soft-delete column,
    /// so joins are always expressible; the bulk (wide non-key columns of peripheral tables) is what
    /// gets trimmed. <c>internal static</c> so the exact wording is unit-testable.</summary>
    internal static void AppendSchemaBlock(StringBuilder sb, IReadOnlyList<InferredTable> tables,
        string question, IReadOnlyList<string> groundingHints, bool compactAll = false,
        IReadOnlyList<string>? localeSuffixes = null)
    {
        var ql = " " + (question ?? string.Empty).ToLowerInvariant() + " ";
        var hintBlob = " " + string.Join(" ", groundingHints ?? System.Array.Empty<string>()).ToLowerInvariant() + " ";
        var suffixes = localeSuffixes is { Count: > 0 } ? localeSuffixes : new[] { "En", "Ar" };

        bool IsFocal(InferredTable t)
        {
            if (compactAll) return false;                    // over budget — everything compact
            if (t.Columns.Count <= 7) return true;            // tiny table — compacting saves nothing
            var n = t.Name.ToLowerInvariant();
            if (ql.Contains(n) || hintBlob.Contains(n)) return true;
            if (n.Length > 3 && n.EndsWith("s") && (ql.Contains(n[..^1]) || hintBlob.Contains(n[..^1]))) return true;
            return false;
        }

        // Is THIS table named in the question or surfaced by a grounded value/hint? (the IsFocal name-match
        // WITHOUT the tiny-table shortcut). Gates the FK->label emission: surface a neighbor's label only
        // when the question actually references that neighbor — otherwise "show me the tariffs" sees
        // (label: ServiceTypes.NameEn) and projects it (the over-fetch regression). Deterministic.
        bool IsNameReferenced(string name)
        {
            var n = name.ToLowerInvariant();
            if (ql.Contains(n) || hintBlob.Contains(n)) return true;
            if (n.Length > 3 && n.EndsWith("s") && (ql.Contains(n[..^1]) || hintBlob.Contains(n[..^1]))) return true;
            return false;
        }

        // FK->label resolution map: for each in-slice table, its DISPLAY/filter column (Roles.LabelColumn,
        // which LabelPriority already resolves to the REAL column — plain "Name" over "NameEn"). Emitted on
        // each FK line so the model filters/joins by the human value using the correct column name instead
        // of guessing one (the "TicketStatuses.NameEn" wrong-name failure). Deterministic — no LLM.
        var fkTargetLabels = tables
            .Where(x => !string.IsNullOrWhiteSpace(x.Roles.LabelColumn))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Roles.LabelColumn!, StringComparer.OrdinalIgnoreCase);

        sb.AppendLine("Schema (use ONLY these tables and columns; JOIN on the foreign keys shown — never invent a join column):");
        foreach (var t in tables)
        {
            var focal = IsFocal(t);
            sb.Append("- ").Append(t.Name);
            // GENERATION CHANNEL: emit the declarative GrainNote ONLY — never the Description. Description
            // is retrieval-only and may carry imperative prose ("Apply a Status filter ONLY when…") that a
            // 7B obeys literally and turns into unrequested WHERE conditions. GrainNote states facts only.
            if (!string.IsNullOrEmpty(t.GrainNote)) sb.Append(" — ").Append(t.GrainNote);
            if (!focal) sb.Append("  [join/lookup table — key columns only]");
            sb.AppendLine();
            if (t.PrimaryKey.Count > 0)
                sb.Append("    PK: ").AppendLine(string.Join(", ", t.PrimaryKey));
            foreach (var fk in t.ForeignKeysOut)
            {
                sb.Append("    FK: ").Append(t.Name).Append('.').Append(fk.Column)
                  .Append(" -> ").Append(fk.Table).Append('.').Append(fk.ReferencedColumn);
                if (fkTargetLabels.TryGetValue(fk.Table, out var lbl) && IsNameReferenced(fk.Table))
                    sb.Append("  (label column: ").Append(fk.Table).Append('.').Append(lbl).Append(')');
                sb.AppendLine();
            }
            // Soft-delete / active-flag rules are NOT emitted per-table here anymore. Per-table imperatives
            // ("add WHERE IsDeleted = 0", "do NOT filter it unless…") are exactly the prose-as-instruction
            // that made the 7B bolt unrequested conditions onto every query. The ONE declarative rule in
            // DirectSqlSystemPrompt ("add IsDeleted=0 only for a table that has it; never filter a status /
            // lifecycle / lookup column unless the question names a value") now owns this globally.

            var cols = focal ? t.Columns : SelectKeyColumns(t);
            // Bilingual bases on THIS table (a base with ≥2 locale-suffixed variants, e.g. NameEn+NameAr).
            // Tagging the columns inline tells the model the plain "Name" does not exist — reducing how
            // often the downstream Name→NameEn self-heal has to fire.
            var bilingualBases = BilingualBaseNames(t, suffixes);
            sb.AppendLine("    Columns:");
            foreach (var c in cols)
            {
                sb.Append("      ").Append(t.Name).Append('.').Append(c.Name).Append(" (").Append(c.Type);
                if (c.Nullable) sb.Append(", nullable");
                sb.Append(')');
                if (c.SampleValues is { Count: > 0 })
                    sb.Append("  e.g. ").Append(string.Join(" | ", c.SampleValues.Take(6)));
                var baseName = StripLocaleSuffix(c.Name, suffixes);
                if (baseName is not null && bilingualBases.Contains(baseName))
                    sb.Append("  [localized — there is no plain \"").Append(baseName)
                      .Append("\" column; use ").Append(string.Join(" / ", suffixes.Select(s => baseName + s))).Append(']');
                sb.AppendLine();
            }
            if (!focal && t.Columns.Count > cols.Count)
                sb.Append("      (+").Append(t.Columns.Count - cols.Count).AppendLine(" more columns)");
        }
    }

    // Returns the base name if a column ends with one of the configured locale suffixes (NameEn → Name);
    // null otherwise. Ordinal/case-sensitive so "Open" is not mistaken for an "…En" column.
    private static string? StripLocaleSuffix(string column, IReadOnlyList<string> suffixes)
    {
        foreach (var s in suffixes)
            if (!string.IsNullOrEmpty(s) && column.Length > s.Length && column.EndsWith(s, StringComparison.Ordinal))
                return column[..^s.Length];
        return null;
    }

    // Base names on a table that carry ≥2 locale variants — a genuine bilingual pair (NameEn + NameAr),
    // not an incidental suffix collision. Only these get the "no plain column" tag.
    private static HashSet<string> BilingualBaseNames(InferredTable t, IReadOnlyList<string> suffixes)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in t.Columns)
        {
            var b = StripLocaleSuffix(c.Name, suffixes);
            if (b is not null) counts[b] = counts.TryGetValue(b, out var n) ? n + 1 : 1;
        }
        return new HashSet<string>(
            counts.Where(kv => kv.Value >= 2).Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
    }

    // For a non-focal join/lookup table: expose ONLY the JOIN KEYS (PK, FKs, soft-delete). The label and
    // natural-key are DISPLAY columns — surfacing them on a neighbor table the question never named makes
    // the 7B project them ("show me the tariffs" -> ServiceTypes.NameEn, Regions.NameEn, the over-fetch).
    // When the question DOES name the table it becomes focal (IsFocal) and its full columns — including the
    // label — are shown by the focal path, so "tickets by region" still gets the region name.
    private static List<InferredColumn> SelectKeyColumns(InferredTable t)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pk in t.PrimaryKey) keep.Add(pk);
        foreach (var fk in t.ForeignKeysOut) keep.Add(fk.Column);
        if (!string.IsNullOrWhiteSpace(t.Roles.SoftDeleteColumn)) keep.Add(t.Roles.SoftDeleteColumn!);
        return t.Columns.Where(c => keep.Contains(c.Name)).ToList();
    }

    /// <summary>
    /// Renders the deterministic grounding block (resolved tables/columns/values) the model must use
    /// verbatim. No-op when there are no hints, so the bare path is byte-identical to before.
    /// Extracted as <c>internal static</c> so the exact wording is unit-testable without mocking the
    /// LLM/schema/dialect stack.
    /// </summary>
    internal static void AppendGroundingBlock(StringBuilder sb, IReadOnlyList<string>? groundingHints)
    {
        if (groundingHints is null || groundingHints.Count == 0) return;
        sb.AppendLine("Resolved context (use these EXACT tables/columns/values; do NOT invent names):");
        foreach (var h in groundingHints)
        {
            if (string.IsNullOrWhiteSpace(h)) continue;
            sb.Append("  - ").AppendLine(h.Trim());
        }
        sb.AppendLine();
    }


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
