namespace SuperAdminCopilot.Semantic;

using System.Text.RegularExpressions;
using FuzzierSharp;
using Humanizer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Schema;

public enum SemanticCandidateKind
{
    Table,
    Column,
    Entity,
}

public sealed record SemanticCandidate(
    SemanticCandidateKind Kind,
    string Name,
    string? Table,
    double Score,
    string Source,
    string MatchedLabel);

public interface ISemanticCandidateResolver
{
    IReadOnlyList<SemanticCandidate> ResolveTables(string text, int limit = 5);
    IReadOnlyList<SemanticCandidate> ResolveColumns(string text, string? table = null, int limit = 5);
    Task<SemanticCandidate?> ResolveEntityAsync(string text, double minScore = 0.72, CancellationToken cancellationToken = default);
}

internal sealed class SemanticCandidateResolver : ISemanticCandidateResolver
{
    private readonly ISchemaMetadataMap _metadataMap;
    private readonly IEntityEmbeddingMatcher _embeddingMatcher;
    private readonly CopilotOptions _options;
    private readonly ILogger<SemanticCandidateResolver> _logger;

    public SemanticCandidateResolver(
        ISchemaMetadataMap metadataMap,
        IEntityEmbeddingMatcher embeddingMatcher,
        IOptions<CopilotOptions> options,
        ILogger<SemanticCandidateResolver> logger)
    {
        _metadataMap = metadataMap;
        _embeddingMatcher = embeddingMatcher;
        _options = options.Value;
        _logger = logger;
    }

    public IReadOnlyList<SemanticCandidate> ResolveTables(string text, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<SemanticCandidate>();
        var queryLabels = BuildQueryLabels(text);
        var candidates = new List<SemanticCandidate>();

        foreach (var table in _metadataMap.Tables)
        {
            var best = ScoreBest(queryLabels, table.Labels);
            if (best.Score <= 0) continue;
            candidates.Add(new SemanticCandidate(
                SemanticCandidateKind.Table,
                table.Name,
                table.Name,
                best.Score,
                best.Source,
                best.Label));
        }

        return candidates
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .Where(c => c.Score >= Math.Max(0.58, _options.ResolverMinConfidence))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Name)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    public IReadOnlyList<SemanticCandidate> ResolveColumns(string text, string? table = null, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<SemanticCandidate>();
        var queryLabels = BuildQueryLabels(text);
        var tables = string.IsNullOrWhiteSpace(table)
            ? _metadataMap.Tables.Select(t => t.Name)
            : new[] { table! };

        var candidates = new List<SemanticCandidate>();
        foreach (var tableName in tables)
        {
            foreach (var column in _metadataMap.GetColumns(tableName))
            {
                var best = ScoreBest(queryLabels, column.Labels);
                if (best.Score <= 0) continue;
                candidates.Add(new SemanticCandidate(
                    SemanticCandidateKind.Column,
                    column.Name,
                    column.Table,
                    best.Score,
                    best.Source,
                    best.Label));
            }
        }

        return candidates
            .Where(c => c.Score >= Math.Max(0.60, _options.ResolverMinConfidence))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Table)
            .ThenBy(c => c.Name)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    public async Task<SemanticCandidate?> ResolveEntityAsync(string text, double minScore = 0.72, CancellationToken cancellationToken = default)
    {
        var requiredScore = Math.Max(minScore, _options.ResolverMinConfidence);
        var lexicalCandidates = ResolveTables(text, limit: 2);
        if (lexicalCandidates.Count >= 2 &&
            lexicalCandidates[0].Score - lexicalCandidates[1].Score <= _options.AmbiguityClarificationThreshold)
        {
            _logger.LogDebug(
                "[SemanticCandidateResolver] ambiguous entity phrase '{Text}' between {First} ({FirstScore:F2}) and {Second} ({SecondScore:F2}).",
                text, lexicalCandidates[0].Name, lexicalCandidates[0].Score, lexicalCandidates[1].Name, lexicalCandidates[1].Score);
            return null;
        }

        var lexical = lexicalCandidates.FirstOrDefault();
        if (lexical is not null && lexical.Score >= requiredScore)
            return lexical with { Kind = SemanticCandidateKind.Entity };

        if (!_embeddingMatcher.IsAvailable) return lexical;

        try
        {
            var entity = await _embeddingMatcher.FindAsync(text, (float)requiredScore, cancellationToken);
            if (entity is null) return lexical;
            return new SemanticCandidate(
                SemanticCandidateKind.Entity,
                entity.Name,
                entity.Table,
                Math.Max(requiredScore, 0.72),
                "embedding",
                entity.Name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SemanticCandidateResolver] embedding fallback failed for '{Text}'.", text);
            return lexical;
        }
    }

    private static (double Score, string Source, string Label) ScoreBest(
        IReadOnlyList<string> queries,
        IReadOnlyList<string> labels)
    {
        var bestScore = 0.0;
        var bestSource = "";
        var bestLabel = "";

        foreach (var q in queries)
        {
            foreach (var label in labels)
            {
                var normalizedLabel = Normalize(label);
                if (string.IsNullOrWhiteSpace(normalizedLabel)) continue;

                double score;
                string source;
                if (string.Equals(q, normalizedLabel, StringComparison.OrdinalIgnoreCase))
                {
                    score = 1.0;
                    source = "exact";
                }
                else if (ContainsTokenPhrase(q, normalizedLabel) || ContainsTokenPhrase(normalizedLabel, q))
                {
                    score = 0.88;
                    source = "phrase";
                }
                else
                {
                    var weighted = Fuzz.WeightedRatio(q, normalizedLabel) / 100.0;
                    var tokenSet = Fuzz.TokenSetRatio(q, normalizedLabel) / 100.0;
                    score = Math.Max(weighted, tokenSet);
                    source = "fuzzier-sharp";
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestSource = source;
                    bestLabel = label;
                }
            }
        }

        return (bestScore, bestSource, bestLabel);
    }

    private static IReadOnlyList<string> BuildQueryLabels(string text)
    {
        var normalized = Normalize(text);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(normalized)) labels.Add(normalized);

        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            labels.Add(token);
            labels.Add(token.Singularize(false));
            labels.Add(token.Pluralize(false));
        }

        labels.Add(normalized.Singularize(false));
        labels.Add(normalized.Pluralize(false));
        return labels.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    private static bool ContainsTokenPhrase(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle)) return false;
        var normalizedHaystack = Normalize(haystack);
        var normalizedNeedle = Normalize(needle);
        if (string.IsNullOrWhiteSpace(normalizedHaystack) || string.IsNullOrWhiteSpace(normalizedNeedle)) return false;
        var pattern = $@"(?:^|\s){Regex.Escape(normalizedNeedle)}(?:\s|$)";
        return Regex.IsMatch(normalizedHaystack, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var chars = text.Trim().Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        return string.Join(' ', new string(chars.ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
