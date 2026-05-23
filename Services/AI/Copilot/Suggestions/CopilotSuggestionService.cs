using System.Text.Json;
using AISupportAnalysisPlatform.Models.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AISupportAnalysisPlatform.Services.AI.Copilot.Suggestions;

/// <summary>
/// Reads suggestion prompts straight from the curated assessment catalog JSON
/// (<c>Services/AI/Copilot/Assessment/copilot-assessment.json</c>). The catalog already curates 70+
/// real-data-grounded questions across categories and difficulties — exactly the right set to surface
/// as "things that work."
///
/// We deliberately load the file directly instead of going through <c>CopilotAssessmentHandler</c>
/// because the handler depends on the orchestrator (which is the consumer of suggestions). Pulling
/// on the handler here would create a DI cycle:
///   <c>SuggestionService → AssessmentHandler → Orchestrator → SuggestionService</c>.
/// Loading the JSON ourselves keeps the suggestion service a leaf in the dependency graph.
/// Cached for the metadata TTL so we don't reread the file on every clarification.
/// </summary>
public sealed class CopilotSuggestionService : ICopilotSuggestionService
{
    private const string CacheKey = "copilot_suggestion_pool";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    private readonly IWebHostEnvironment _env;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CopilotSuggestionService> _logger;

    public CopilotSuggestionService(IWebHostEnvironment env, IMemoryCache cache, ILogger<CopilotSuggestionService> logger)
    {
        _env = env;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetSuggestionsAsync(int count = 4, CancellationToken cancellationToken = default)
    {
        var pool = await GetPoolAsync(cancellationToken);
        if (pool.Count == 0) return Array.Empty<string>();

        // Stratified pick: rotate across difficulties so the chips show range, not 4 trivial ones.
        // Order within each bucket is the catalog's curated SortOrder, so the most representative
        // example of each difficulty wins.
        var byDifficulty = pool
            .GroupBy(p => p.Difficulty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.SortOrder).Select(c => c.Question).ToList());

        var difficultyOrder = new[] { "Easy", "Medium", "Hard", "Complicated" };
        var picked = new List<string>();
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        while (picked.Count < count)
        {
            var startCount = picked.Count;
            foreach (var diff in difficultyOrder)
            {
                if (picked.Count >= count) break;
                if (!byDifficulty.TryGetValue(diff, out var questions)) continue;
                var idx = indexes.GetValueOrDefault(diff);
                if (idx >= questions.Count) continue;
                picked.Add(questions[idx]);
                indexes[diff] = idx + 1;
            }
            // Pool exhausted — break to avoid infinite loop when count > pool size.
            if (picked.Count == startCount) break;
        }

        return picked;
    }

    /// <summary>
    /// Live type-ahead. Tokenizes the user's partial input, stems each token, then ranks every
    /// catalog question by how many partial-stems show up in the question. Same plural-stripping
    /// the schema pruner uses, so "users" finds questions about Users, "tickts" still finds
    /// "tickets" via prefix-match. Results sorted by overlap-count desc then SortOrder asc.
    /// Empty input returns the standard stratified set.
    /// </summary>
    public async Task<IReadOnlyList<TypeAheadSuggestion>> GetTypeAheadAsync(string partial, int max = 6, CancellationToken cancellationToken = default)
    {
        var pool = await GetFullPoolAsync(cancellationToken);
        if (pool.Count == 0) return Array.Empty<TypeAheadSuggestion>();

        var partialTokens = TokenizeAndStem(partial);

        if (partialTokens.Count == 0)
        {
            // No input → mirror the chip default so the dropdown isn't empty when focused.
            var fallback = await GetSuggestionsAsync(max, cancellationToken);
            var lookup = pool.ToDictionary(p => p.Question, p => p, StringComparer.OrdinalIgnoreCase);
            return fallback
                .Select(q => lookup.TryGetValue(q, out var hit)
                    ? new TypeAheadSuggestion(hit.Question, hit.Category, hit.Difficulty)
                    : new TypeAheadSuggestion(q, "", "Medium"))
                .ToList();
        }

        var ranked = pool
            .Select(p => new
            {
                Item = p,
                Score = CountOverlap(partialTokens, p.QuestionStems),
                StartsWith = StartsWithAnyToken(p.Question, partialTokens)
            })
            .Where(r => r.Score > 0)
            // Two-pass ranking: more matched tokens wins; ties broken by "starts-with" then SortOrder.
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.StartsWith)
            .ThenBy(r => r.Item.SortOrder)
            .Take(max)
            .Select(r => new TypeAheadSuggestion(r.Item.Question, r.Item.Category, r.Item.Difficulty))
            .ToList();

        return ranked;
    }

    private static int CountOverlap(IReadOnlyList<string> partialStems, IReadOnlyCollection<string> questionStems)
    {
        var hits = 0;
        foreach (var t in partialStems)
        {
            if (questionStems.Contains(t)) hits++;
        }
        return hits;
    }

    private static bool StartsWithAnyToken(string question, IReadOnlyList<string> partialStems)
    {
        var first = question.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(first)) return false;
        var firstStem = StripPlural(first.ToLowerInvariant().Trim('.', ',', ':', ';', '?', '!'));
        return partialStems.Contains(firstStem);
    }

    private static List<string> TokenizeAndStem(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!', '(', ')', '[', ']', '\'', '"' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .Select(StripPlural)
            .Distinct()
            .ToList();
    }

    private static string StripPlural(string s)
    {
        if (s.EndsWith("ies", StringComparison.Ordinal) && s.Length > 3) return s[..^3] + "y";
        if (s.EndsWith("es", StringComparison.Ordinal) && s.Length > 3) return s[..^2];
        if (s.EndsWith("s", StringComparison.Ordinal) && !s.EndsWith("ss", StringComparison.Ordinal) && s.Length > 1) return s[..^1];
        return s;
    }

    private async Task<IReadOnlyList<(string Question, string Difficulty, int SortOrder)>> GetPoolAsync(CancellationToken ct)
    {
        var full = await GetFullPoolAsync(ct);
        return full.Select(p => (p.Question, p.Difficulty, p.SortOrder)).ToList();
    }

    /// <summary>
    /// Internal richer pool: includes Category + pre-stemmed token set per question, used for
    /// fast type-ahead matching without re-tokenizing on every keystroke.
    /// </summary>
    internal sealed record CatalogEntry(string Question, string Category, string Difficulty, int SortOrder, HashSet<string> QuestionStems);

    private async Task<IReadOnlyList<CatalogEntry>> GetFullPoolAsync(CancellationToken ct)
    {
        const string fullCacheKey = "copilot_suggestion_full_pool";
        if (_cache.TryGetValue(fullCacheKey, out IReadOnlyList<CatalogEntry>? cached) && cached != null)
        {
            return cached;
        }

        var path = ResolveCatalogPath();
        if (path == null)
        {
            _logger.LogWarning("Copilot assessment catalog not found; suggestion pool will be empty.");
            return Array.Empty<CatalogEntry>();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("Scenarios", out var scenarios) || scenarios.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CatalogEntry>();
            }

            var pool = scenarios.EnumerateArray()
                .Where(s => s.TryGetProperty("Question", out var q) && !string.IsNullOrWhiteSpace(q.GetString()))
                .Where(s => !s.TryGetProperty("IncludeInCopilotLibrary", out var incl) || incl.ValueKind != JsonValueKind.False)
                .Select(s =>
                {
                    var question = s.GetProperty("Question").GetString() ?? string.Empty;
                    var stems = new HashSet<string>(TokenizeAndStem(question), StringComparer.OrdinalIgnoreCase);
                    return new CatalogEntry(
                        Question: question,
                        Category: s.TryGetProperty("Category", out var c) ? (c.GetString() ?? "General") : "General",
                        Difficulty: s.TryGetProperty("Difficulty", out var d) ? (d.GetString() ?? "Medium") : "Medium",
                        SortOrder: s.TryGetProperty("SortOrder", out var so) && so.TryGetInt32(out var soi) ? soi : 0,
                        QuestionStems: stems);
                })
                .ToList()
                .AsReadOnly();

            _cache.Set(fullCacheKey, (IReadOnlyList<CatalogEntry>)pool, CacheDuration);
            return pool;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read assessment catalog at {Path}; suggestions will be empty.", path);
            return Array.Empty<CatalogEntry>();
        }
    }

    /// <summary>
    /// Try a few well-known locations for the assessment catalog. Mirrors the search order in
    /// <c>CopilotAssessmentHandler.LoadDataAsync</c> so both services find the same file.
    /// </summary>
    private string? ResolveCatalogPath()
    {
        var candidates = new[]
        {
            Path.Combine(_env.ContentRootPath, "Services", "AI", "Copilot", "Assessment", "copilot-assessment.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Services", "AI", "Copilot", "Assessment", "copilot-assessment.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "copilot-assessment.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Services", "AI", "Copilot", "Assessment", "copilot-assessment.json")
        };

        foreach (var candidate in candidates.Distinct())
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
