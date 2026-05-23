namespace SuperAdminCopilot.Retrieval;

using FuzzierSharp;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;

internal sealed class KeywordRetriever : IRetriever
{
    private readonly IEntityCatalog _catalog;
    private readonly ISchemaMetadataMap _metadataMap;
    private readonly ICopilotSchemaAccessPolicy _schemaPolicy;
    private readonly CopilotOptions _options;

    public KeywordRetriever(
        IEntityCatalog catalog,
        ISchemaMetadataMap metadataMap,
        ICopilotSchemaAccessPolicy schemaPolicy,
        IOptions<CopilotOptions> options)
    {
        _catalog = catalog;
        _metadataMap = metadataMap;
        _schemaPolicy = schemaPolicy;
        _options = options.Value;
    }

    public Task<SchemaSlice> RetrieveAsync(string question, int topK, string? tableHint = null, CancellationToken cancellationToken = default)
    {
        var k = topK > 0 ? topK : _options.RetrieverTopK;
        var tokens = Tokenize(question);

        var allowedTables = _metadataMap.Tables;
        var scores = allowedTables
            .Select(t => (t.Name, Score: ScoreTable(t, tokens)))
            .OrderByDescending(s => s.Score)
            .ToList();

        // ── Semantic hint boost ──────────────────────────────────────────
        // When the QuestionRewriter resolved a target entity, boost that table to
        // the top of the rankings so the planner always sees the right schema.
        // This fixes the core issue: "show me unattended cases" would keyword-match
        // against every table equally, but the rewriter KNOWS it's about Tickets.
        if (!string.IsNullOrEmpty(tableHint))
        {
            for (int i = 0; i < scores.Count; i++)
            {
                if (string.Equals(scores[i].Name, tableHint, StringComparison.OrdinalIgnoreCase))
                {
                    // Boost to guaranteed #1 position
                    scores[i] = (scores[i].Name, scores[i].Score + 1000);
                    break;
                }
            }
            scores = scores.OrderByDescending(s => s.Score).ToList();
        }

        var primary = scores.Where(s => s.Score > 0).Take(k).Select(s => s.Name).ToList();

        if (primary.Count == 0)
            primary = allowedTables.Take(k).Select(t => t.Name).ToList();
        else
        {
            var withNeighbors = new HashSet<string>(primary, StringComparer.OrdinalIgnoreCase);
            foreach (var t in primary.ToList())
                foreach (var n in _catalog.Graph.Neighbors(t))
                    if (withNeighbors.Count < k * 2 && _schemaPolicy.IsTableAllowed(n)) withNeighbors.Add(n);
            primary = withNeighbors.ToList();
        }

        // Build scores dictionary for trace output
        var scoreDict = scores.Where(s => primary.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(s => s.Name, s => (double)s.Score, StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(new SchemaSlice(primary, SchemaPromptFormatter.Format(_catalog, primary, _options.SchemaPromptStrategy, _schemaPolicy), scoreDict));
    }

    private int ScoreTable(SchemaTableMetadata table, IReadOnlyList<string> tokens)
    {
        int score = 0;
        foreach (var label in table.Labels)
            score += ScoreLabel(label, tokens, exactWeight: 4, partialWeight: 2);

        foreach (var col in _metadataMap.GetColumns(table.Name))
        {
            foreach (var label in col.Labels)
                score += ScoreLabel(label, tokens, exactWeight: 2, partialWeight: 1);
        }
        return score;
    }

    private static int ScoreLabel(string label, IReadOnlyList<string> tokens, int exactWeight, int partialWeight)
    {
        var normalized = Normalize(label);
        if (string.IsNullOrWhiteSpace(normalized)) return 0;

        var score = 0;
        foreach (var t in tokens)
        {
            if (string.IsNullOrEmpty(t)) continue;
            if (normalized == t || normalized == t + "s" || (normalized + "s") == t) score += exactWeight;
            else if (normalized.StartsWith(t, StringComparison.OrdinalIgnoreCase)) score += partialWeight;
            else if (normalized.Contains(t, StringComparison.OrdinalIgnoreCase)) score += 1;
            // FuzzySharp fallback: catch morphological variants the exact/substring checks miss.
            // "resolve" ≈ "resolved", "comment" ≈ "commenting", etc. Conservative threshold (75)
            // since this is the fallback retriever — VectorRetriever handles deep semantic matching.
            else if (t.Length >= 4 && normalized.Length >= 4 && Fuzz.PartialRatio(normalized, t) >= 75) score += 1;
        }
        return score;
    }

    private static IReadOnlyList<string> Tokenize(string question) =>
        Normalize(question)
                .Split(new[] { ' ', '\t', '\n', '\r', '?', '.', ',', ';', ':', '"', '\'', '(', ')', '/', '\\' },
                       StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2)
                .Distinct()
                .ToList();

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var chars = text.Trim().Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        return new string(chars.ToArray());
    }
}
