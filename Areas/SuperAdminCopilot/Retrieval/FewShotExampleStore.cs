namespace SuperAdminCopilot.Retrieval;

using System.Text;
using System.Text.Json;
using Humanizer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;

/// <summary>
/// One worked example: a question and the JSON spec the planner should ideally produce.
/// Tags are pre-computed keywords used by the keyword-overlap retriever — embedding-based
/// retrieval is a future upgrade (§4 of the abstraction guide), but for now we get most of
/// the value with a simple ranking.
/// </summary>
public sealed class FewShotExample
{
    public List<string> Tags { get; set; } = new();
    public string Question { get; set; } = "";
    /// <summary>The exemplar planner output. Kept as a JsonElement so we re-serialize it
    /// verbatim into the prompt.</summary>
    public JsonElement Spec { get; set; }
}

public interface IFewShotExampleStore
{
    /// <summary>
    /// Returns up to <paramref name="topK"/> examples ranked by keyword overlap with the
    /// user's question, then by tag matches. Returns an empty list if no examples were loaded
    /// or no example scored above zero.
    /// </summary>
    IReadOnlyList<FewShotExample> Retrieve(string question, int topK);
}

internal sealed class FewShotExampleStore : IFewShotExampleStore
{
    private readonly Lazy<IReadOnlyList<FewShotExample>> _examples;
    private readonly ISemanticLayer _semantic;
    private readonly IEntityCatalog _catalog;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public FewShotExampleStore(
        IOptions<CopilotOptions> options,
        ISemanticLayer semantic,
        IEntityCatalog catalog,
        ILogger<FewShotExampleStore> logger)
    {
        _semantic = semantic;
        _catalog = catalog;
        _examples = new Lazy<IReadOnlyList<FewShotExample>>(() => Load(options.Value.FewShotExamplesPath, logger));
    }

    public IReadOnlyList<FewShotExample> Retrieve(string question, int topK)
    {
        if (_examples.Value.Count == 0 || topK <= 0) return Array.Empty<FewShotExample>();
        var tokens = Tokenize(question);
        if (tokens.Count == 0) return Array.Empty<FewShotExample>();

        var scored = _examples.Value
            .Select(e => (Example: e, Score: Score(tokens, e)))
            .Where(t => t.Score > 0 && ExampleRootIsMentioned(question, t.Example))
            .OrderByDescending(t => t.Score)
            .Take(topK)
            .Select(t => t.Example)
            .ToList();
        return scored;
    }

    private bool ExampleRootIsMentioned(string question, FewShotExample example)
    {
        if (!TryGetExampleRoot(example, out var root)) return true;
        if (!_catalog.TableExists(root)) return false;

        var normalizedQuestion = NormalizeWords(question);
        if (string.IsNullOrWhiteSpace(normalizedQuestion)) return false;

        // Semantic-layer token check: split question into words and check each against the
        // synonym dictionary. This handles "case" → Tickets, "reply" → TicketComments, etc.
        // without requiring exact substring containment of the full entity label.
        var entity = _semantic.GetEntityForTable(root);
        var tokens = normalizedQuestion.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3)
            .ToList();
        foreach (var token in tokens)
        {
            var resolved = _semantic.GetEntityByNameOrSynonym(token);
            if (resolved is not null && string.Equals(resolved.Table, root, StringComparison.OrdinalIgnoreCase))
                return true;
            // Humanizer morphological variants: "cases" → "case" → synonym hit
            var singular = token.Singularize(inputIsKnownToBePlural: false);
            if (!string.Equals(singular, token, StringComparison.OrdinalIgnoreCase))
            {
                resolved = _semantic.GetEntityByNameOrSynonym(singular);
                if (resolved is not null && string.Equals(resolved.Table, root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        // Fallback: phrase containment for multi-word labels ("ai analysis", "user role")
        foreach (var label in RootLabels(root))
        {
            if (ContainsNormalizedPhrase(normalizedQuestion, label))
                return true;
        }

        return false;
    }

    private IEnumerable<string> RootLabels(string root)
    {
        yield return root;
        yield return SplitIdentifier(root);

        var entity = _semantic.GetEntityForTable(root);
        if (entity is null) yield break;

        yield return entity.Name;
        yield return SplitIdentifier(entity.Name);
        foreach (var synonym in entity.Synonyms)
            yield return synonym;
    }

    private static bool TryGetExampleRoot(FewShotExample example, out string root)
    {
        root = "";
        if (example.Spec.ValueKind != JsonValueKind.Object) return false;
        if (!example.Spec.TryGetProperty("root", out var rootProp)) return false;
        root = rootProp.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(root);
    }

    /// <summary>
    /// Score = (shape-signal token in tags hits × 10) + (other token in tags × 3) +
    /// (token in question × 1). Shape-signal tokens ("how", "many", "per", "by", "top",
    /// "between", "without", "starts", "max", etc.) are the strongest predictors of SQL
    /// shape — when the question carries one and the example's tags advertise the same
    /// shape, that's the example we want. Without this weighting an aggregation-shaped
    /// question can retrieve list-shaped examples that just happen to share a few keywords,
    /// which biases the planner toward the wrong SQL template.
    /// </summary>
    private static int Score(IReadOnlyList<string> questionTokens, FewShotExample example)
    {
        var tagSet = new HashSet<string>(example.Tags, StringComparer.OrdinalIgnoreCase);
        var qTokens = Tokenize(example.Question);
        var qSet = new HashSet<string>(qTokens, StringComparer.OrdinalIgnoreCase);

        int score = 0;
        foreach (var t in questionTokens)
        {
            if (tagSet.Contains(t))
            {
                // Shape-signaling token in BOTH question and example tags → strongest possible
                // match. Boosting this above the generic tag-match weight kills the
                // "all 3 retrieved examples are the wrong shape" failure mode where a count
                // question pulls in list examples just because they share an entity word.
                score += ShapeSignalTokens.Contains(t) ? 10 : 3;
            }
            else if (qSet.Contains(t))
            {
                score += 1;
            }
        }
        return score;
    }

    /// <summary>
    /// Tokens that strongly hint at one SQL shape vs another. Words are intentionally
    /// language-level (English question grammar), NOT domain-specific. Adding a new shape
    /// to the deterministic ShapeEngine should also add its anchor words here so retrieval
    /// stays aligned with the engine.
    /// </summary>
    private static readonly HashSet<string> ShapeSignalTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        // count shape
        "how", "many", "count", "total", "number", "distinct", "unique",
        // list shape
        "show", "list", "find", "display", "give",
        // top-N / ordering shape
        "top", "first", "oldest", "newest", "latest", "earliest", "recent",
        // group-by shape
        "per", "by", "group", "each",
        // having shape
        "least", "most", "more", "fewer", "exactly",
        // numeric / between
        "greater", "less", "between", "above", "below", "than",
        // like / text
        "starts", "ends", "containing", "contains", "mentioning", "about",
        // anti-join / null
        "without", "missing", "no", "never",
        // max/min
        "max", "maximum", "min", "minimum", "largest", "smallest", "highest", "lowest",
        // temporal
        "today", "yesterday", "last", "this", "previous", "month", "week", "year", "day", "hour",
        "days", "hours", "weeks", "months", "years",
        // boolean / state
        "deleted", "inactive", "active", "unresolved", "resolved", "unconfirmed", "escalated",
    };

    private static IReadOnlyList<string> Tokenize(string text) =>
        (text ?? "").ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '?', '.', ',', ';', ':', '"', '\'', '(', ')', '/', '\\', '-' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .Distinct()
            .ToList();

    private static bool ContainsNormalizedPhrase(string normalizedHaystack, string phrase)
    {
        var normalizedPhrase = NormalizeWords(phrase);
        if (string.IsNullOrWhiteSpace(normalizedHaystack) || string.IsNullOrWhiteSpace(normalizedPhrase))
            return false;

        return normalizedHaystack.Equals(normalizedPhrase, StringComparison.OrdinalIgnoreCase)
            || normalizedHaystack.StartsWith(normalizedPhrase + " ", StringComparison.OrdinalIgnoreCase)
            || normalizedHaystack.EndsWith(" " + normalizedPhrase, StringComparison.OrdinalIgnoreCase)
            || normalizedHaystack.Contains(" " + normalizedPhrase + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var chars = text.Trim().Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        return string.Join(' ', new string(chars.ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string SplitIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return "";
        var chars = new List<char>(identifier.Length + 8);
        for (var i = 0; i < identifier.Length; i++)
        {
            var ch = identifier[i];
            if (i > 0 && char.IsUpper(ch) && !char.IsWhiteSpace(identifier[i - 1]))
                chars.Add(' ');
            chars.Add(ch);
        }

        return new string(chars.ToArray()).Replace('_', ' ').Trim();
    }

    private static IReadOnlyList<FewShotExample> Load(string configuredPath, ILogger logger)
    {
        var path = ResolvePath(configuredPath);
        if (!File.Exists(path))
        {
            logger.LogWarning("[SuperAdminCopilot] Few-shot examples file not found at {Path}.", path);
            return Array.Empty<FewShotExample>();
        }
        try
        {
            using var stream = File.OpenRead(path);
            var examples = JsonSerializer.Deserialize<List<FewShotExample>>(stream, JsonOpts) ?? new();
            logger.LogInformation("[SuperAdminCopilot] Loaded {Count} few-shot examples.", examples.Count);
            return examples;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SuperAdminCopilot] Failed to load few-shot examples from {Path}.", path);
            return Array.Empty<FewShotExample>();
        }
    }

    private static string ResolvePath(string configured)
    {
        if (Path.IsPathRooted(configured) && File.Exists(configured)) return configured;
        var byBase = Path.Combine(AppContext.BaseDirectory, configured);
        if (File.Exists(byBase)) return byBase;
        return Path.Combine(Directory.GetCurrentDirectory(), configured);
    }
}
