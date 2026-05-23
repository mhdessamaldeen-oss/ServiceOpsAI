namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Abstractions;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Pipeline.Routing;
using SuperAdminCopilot.Semantic;

/// <summary>
/// Pre-planner short-circuit for semantic-similarity / semantic-search questions. The host
/// already has a tuned vector-similarity service (today indexing Tickets, tomorrow Orders or
/// KB articles); this handler detects the question shape and calls it directly, bypassing
/// planner + compiler + validator (none of which can express "find rows whose vector is close
/// to this other row's vector").
///
/// Two patterns, both generic over entity type:
///   1. <b>Similar-to-entity</b>: "similar tickets to TCK-2026-00050" / "orders like ORD-100".
///      The captured noun ("tickets", "orders") is resolved through the semantic layer to an
///      entity; the captured ID is matched against that entity's NaturalKeyFormat regex.
///   2. <b>Free-text semantic search</b>: "tickets about login error" / "articles matching
///      printer queue stuck" / "search for authentication issues". Calls
///      <see cref="ISemanticSearch.SearchByTextAsync"/> with the user's phrase, restricted to
///      whatever entity the noun anchored.
///
/// Patterns are composed at construction time from each entity's <c>Synonyms</c> +
/// <c>NaturalKeyFormat</c>. Entities without a NaturalKeyFormat skip pattern 1; entities
/// without searchable text skip pattern 2.
///
/// Returns <c>null</c> when no pattern matches — caller falls through to the normal pipeline.
/// </summary>
public interface ISemanticSearchHandler
{
    Task<SemanticSearchHandlerResult?> TryHandleAsync(string question, CancellationToken cancellationToken = default);
}

public sealed record SemanticSearchHandlerResult(
    string Sql,
    ExecutionResult Result,
    string Mode,                                         // "similar-to-entity" | "text-search"
    IReadOnlyList<SemanticSearchHit> Hits);              // raw hits — orchestrator threads these into CopilotResponse.SimilarEntities

internal sealed class SemanticSearchHandler : ISemanticSearchHandler, IRoutingProbe
{
    private readonly ISemanticSearch _search;
    private readonly ISemanticLayer _semanticLayer;
    private readonly ILogger<SemanticSearchHandler> _logger;

    public string Name => "SemanticSearch";

    /// <summary>Probe — replays the SAME gating used inside TryHandleAsync: if the
    /// DeterministicSqlIntent guard fires, the question is NOT a semantic search even when a
    /// synonym + verb appear; fall through. Otherwise build the per-entity patterns and report
    /// match. Cheap regex work — no DB or vector call.</summary>
    public Task<RouterDecision?> ProbeAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return Task.FromResult<RouterDecision?>(null);

        // Pattern 1 — similarity-to-entity (natural-key code) always wins over the guard,
        // because a code is a strong signal even when the surrounding question has comparators.
        foreach (var entity in _semanticLayer.Config.Entities)
        {
            if (string.IsNullOrEmpty(entity.NaturalKeyFormat)) continue;
            var similarityPattern = BuildSimilarityRegex(entity);
            if (similarityPattern is null) continue;
            if (similarityPattern.IsMatch(question))
                return Task.FromResult<RouterDecision?>(new RouterDecision(
                    IntentLabel.SemanticSearch, 1.0, Name, $"similar-to {entity.Name}"));
        }

        // Pattern 2 — free-text search. Skip when the deterministic-SQL guard says it's SQL.
        if (DeterministicSqlIntent.IsMatch(question))
            return Task.FromResult<RouterDecision?>(null);

        foreach (var entity in _semanticLayer.Config.Entities)
        {
            if (entity.Synonyms is not { Count: > 0 }) continue;
            var textPattern = BuildTextSearchRegex(entity);
            if (textPattern is null) continue;
            if (textPattern.IsMatch(question))
                return Task.FromResult<RouterDecision?>(new RouterDecision(
                    IntentLabel.SemanticSearch, 0.9, Name, $"text-search on {entity.Name}"));
        }
        return Task.FromResult<RouterDecision?>(null);
    }

    /// <summary>Verbs that anchor a similarity question. Always followed by an optional entity-noun
    /// hint and the natural-key value.</summary>
    private const string SimilarityVerbs =
        @"similar|like|duplicate(?:s)?|related|nearest|matching|comparable\s+to|akin\s+to";

    /// <summary>Verbs that anchor a free-text search. Followed by entity noun (optional) + phrase.
    /// Intentionally narrow: only verbs that strongly signal a semantic / similarity intent.
    /// Verbs deliberately excluded because they collide with deterministic SQL semantics:
    ///   • <c>with</c>       — almost always a join or a WHERE clause anchor
    ///                         ("Tickets with status=Open", "Users with unconfirmed email").
    ///   • <c>like</c>       — ambiguous with SQL LIKE; the similarity-to-entity path covers
    ///                         the legitimate "like &lt;CODE&gt;" usage.
    ///   • <c>containing</c> — natural-language synonym for SQL LIKE substring match;
    ///                         the planner handles these correctly.
    /// Keep <c>matching</c> and <c>mentioning</c> since they have a strong semantic-search
    /// reading; the <see cref="DeterministicSqlIntent"/> guard below catches the cases where
    /// they happen to appear inside an obviously-SQL question.</summary>
    private const string SearchVerbs =
        @"about|matching|similar\s+to|relating\s+to|regarding|on\s+the\s+topic\s+of|mentioning";

    /// <summary>
    /// Strong signals that a question is a deterministic SQL query, not a semantic search.
    /// When any of these match we fall through to the planner regardless of which verb the
    /// question used. Domain-agnostic — these are English heuristics, no entity-specific terms.
    ///
    /// <para>The patterns are grouped by category so the rule set is easy to extend:</para>
    /// <list type="bullet">
    ///   <item>Numeric comparators ("greater than 50", "at least 3", "between 1 and 10")</item>
    ///   <item>Aggregation verbs ("how many", "count", "top 5", "average", "sum of")</item>
    ///   <item>Anti-join / NULL markers ("without", "with no", "missing", "never")</item>
    ///   <item>Prefix/suffix LIKE markers ("starts with", "ends with")</item>
    ///   <item>Boolean-flag adjectives ("unconfirmed", "inactive", "deleted", "escalated")</item>
    ///   <item>Temporal markers — relative ("last 7 days", "this month") and absolute (ISO dates)</item>
    ///   <item>Bare comparison operators (<c>&lt;</c>, <c>&gt;</c>, <c>=</c>)</item>
    /// </list>
    /// </summary>
    private static readonly Regex DeterministicSqlIntent = new(
        // Numeric comparators
        @"\b(greater|less|more|fewer)\s+than\b|\bat\s+(least|most)\b|\bno\s+(less|more)\s+than\b|" +
        @"\bbetween\s+\S+\s+and\s+\S+\b|\bequal(?:s)?\s+to\b|" +
        // Aggregations
        @"\bhow\s+(many|much)\b|\b(count|total|number)\s+of\b|\bsum\s+of\b|" +
        @"\b(average|avg|min(imum)?|max(imum)?|smallest|largest|highest|lowest)\b|" +
        @"\btop\s+\d+\b|\bfirst\s+\d+\b|\blast\s+\d+\b|\boldest\b|\bnewest\b|" +
        @"\bmost\s+(active|recent|common|frequent)\b|" +
        // Anti-join / NULL markers
        @"\bwithout(?:\s+any)?\b|\bwith\s+no\b|\bmissing(?:\s+(a|an|any))?\b|\bnever\s+\w+(?:ed|d)\b|" +
        // Prefix/suffix LIKE markers
        @"\bstarts?\s+with\b|\bends?\s+with\b|\bbegins?\s+with\b|" +
        // Boolean-flag adjectives (state predicates that imply WHERE flag=X)
        @"\b(unconfirmed|inactive|deleted|escalated|resolved|unresolved|active|enabled|disabled|locked|archived)\b|" +
        // Relative time windows
        @"\b(today|yesterday|tomorrow)\b|" +
        @"\b(last|this|past|next)\s+(hour|day|week|month|year|quarter|\d+\s+(hours?|days?|weeks?|months?|years?))\b|" +
        @"\bin\s+the\s+last\s+\d+\b|" +
        // Absolute dates (ISO + "before/after/on YYYY")
        @"\d{4}-\d{2}-\d{2}\b|\b(before|after|on)\s+\d{4}\b|" +
        // Bare comparison operators
        @"[<>](?!=?\s*=)|(?<!\w)=(?!=)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public SemanticSearchHandler(ISemanticSearch search, ISemanticLayer semanticLayer, ILogger<SemanticSearchHandler> logger)
    {
        _search = search;
        _semanticLayer = semanticLayer;
        _logger = logger;
    }

    public async Task<SemanticSearchHandlerResult?> TryHandleAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        if (!_search.IsAvailable) return null;

        // Deterministic-SQL guard — when the question carries strong SQL signals (comparators,
        // aggregations, anti-join markers, date math, etc.) we fall through to the planner.
        // Pattern 1 (similar-to-entity) bypasses the guard because a natural-key code is itself
        // a hard signal — "similar to TCK-2026-00050 with status open" should still route here.
        // Pattern 2 (free-text) is the false-positive surface; the guard protects it.
        var deterministicMatch = DeterministicSqlIntent.Match(question);

        // Pattern 1 — similarity to a specific record. Walk every entity that declares a
        // NaturalKeyFormat and try its regex against the question. First entity to anchor
        // (entity synonym present near the ID + format matches) wins.
        foreach (var entity in _semanticLayer.Config.Entities)
        {
            if (string.IsNullOrEmpty(entity.NaturalKeyFormat)) continue;

            var similarityPattern = BuildSimilarityRegex(entity);
            if (similarityPattern is null) continue;

            var m = similarityPattern.Match(question);
            if (!m.Success) continue;
            var code = m.Groups["code"].Value;
            _logger.LogInformation("[SemanticSearchHandler] similarity match: entity={Entity} code={Code}", entity.Name, code);
            var hits = await _search.FindSimilarToEntityAsync(entity.Name, code, topK: 10, cancellationToken);
            return BuildResult(
                mode: "similar-to-entity",
                hits: hits,
                sqlSummary: $"-- Semantic-search: {entity.Name} similar to {code}");
        }

        // Pattern 2 — free-text search. Try each entity with declared searchable columns / synonyms;
        // an entity wins when the question carries one of its synonyms followed by a search verb
        // ("tickets about X" / "orders matching Y").
        //
        // Skip Pattern 2 entirely when the deterministic-SQL guard fired — those questions look
        // grammatically like "<noun> with X" but semantically mean a WHERE/JOIN/aggregation that
        // the planner handles. Without this guard the handler would hijack questions like
        // "users with unconfirmed emails" or "tickets with due date before 2026-05-01".
        if (deterministicMatch.Success)
        {
            _logger.LogDebug(
                "[SemanticSearchHandler] free-text path skipped — DeterministicSqlIntent matched '{Token}'",
                deterministicMatch.Value);
            return null;
        }

        foreach (var entity in _semanticLayer.Config.Entities)
        {
            if (entity.Synonyms is not { Count: > 0 }) continue;

            var textSearchPattern = BuildTextSearchRegex(entity);
            if (textSearchPattern is null) continue;

            var m = textSearchPattern.Match(question);
            if (!m.Success) continue;

            var queryText = m.Groups["query"].Value.Trim().Trim(',', '.', ';').Trim();
            if (queryText.Length < 3) continue;

            _logger.LogInformation("[SemanticSearchHandler] text-search match: entity={Entity} q='{Query}'", entity.Name, queryText);
            var hits = await _search.SearchByTextAsync(queryText, topK: 10, entity.Name, cancellationToken);
            return BuildResult(
                mode: "text-search",
                hits: hits,
                sqlSummary: $"-- Semantic-search: {entity.Name} matching '{queryText}'");
        }

        return null;
    }

    /// <summary>
    /// Build a similarity regex for the entity: "<verb> [entity-synonym] [to|of|for] [#]<code>".
    /// Returns null when the entity has no synonyms — without a noun anchor the regex would
    /// false-match too aggressively. The natural-key format regex is embedded as the &lt;code&gt;
    /// capture group's body so non-matching codes don't trigger.
    /// </summary>
    private static Regex? BuildSimilarityRegex(EntityDefinition entity)
    {
        if (entity.Synonyms is not { Count: > 0 }) return null;
        if (string.IsNullOrEmpty(entity.NaturalKeyFormat)) return null;

        var nounAlt = string.Join("|", entity.Synonyms.Select(Regex.Escape));
        // Strip the format regex's surrounding ^ and $ anchors so we can splice it into a
        // larger pattern. Most authors write the format as a complete-string match.
        var formatBody = StripAnchors(entity.NaturalKeyFormat!);

        var pattern = new StringBuilder()
            .Append(@"\b(?:").Append(SimilarityVerbs).Append(@")\s+")
            .Append(@"(?:(?:").Append(nounAlt).Append(@")\s+)?")
            .Append(@"(?:to\s+|of\s+|for\s+)?[`'""]?")
            .Append(@"(?<code>").Append(formatBody).Append(@")[`'""]?")
            .ToString();

        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
        catch (ArgumentException)
        {
            // Malformed format regex in config — skip this entity rather than crash the request.
            return null;
        }
    }

    /// <summary>
    /// Build a text-search regex for the entity: "[verb] &lt;entity-synonym&gt; &lt;search-verb&gt; X"
    /// or "search/find &lt;entity-synonym&gt; ... X". Captures the phrase into &lt;query&gt;.
    /// </summary>
    private static Regex? BuildTextSearchRegex(EntityDefinition entity)
    {
        if (entity.Synonyms is not { Count: > 0 }) return null;
        var nounAlt = string.Join("|", entity.Synonyms.Select(Regex.Escape));

        var pattern = new StringBuilder()
            .Append(@"\b(?:")
            .Append(@"(?:").Append(nounAlt).Append(@")\s+(?:").Append(SearchVerbs).Append(@")")
            .Append(@"|search\s+(?:for\s+)?")
            .Append(@"|find\s+(?:").Append(nounAlt).Append(@")\s+(?:").Append(SearchVerbs).Append(@")")
            .Append(@")\s+[`'""]?(?<query>[^`'""\?\.!]+?)[`'""]?\s*[?.!]?\s*$")
            .ToString();

        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Strip leading <c>^</c> and trailing <c>$</c> from a regex pattern so it can be
    /// composed into a larger pattern. Leaves the body untouched otherwise.</summary>
    private static string StripAnchors(string format)
    {
        var body = format;
        if (body.StartsWith("^")) body = body[1..];
        if (body.EndsWith("$"))   body = body[..^1];
        return body;
    }

    /// <summary>
    /// Project semantic-search hits into an <see cref="ExecutionResult"/> so the rest of the
    /// pipeline (Explainer + tracing) treats it like any other result. Each hit becomes a
    /// dictionary row keyed on NaturalKey / DisplayLabel / Score so the user sees something useful.
    /// </summary>
    private static SemanticSearchHandlerResult BuildResult(string mode, IReadOnlyList<SemanticSearchHit> hits, string sqlSummary)
    {
        var rows = hits.Select(h => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["NaturalKey"] = h.NaturalKey,
            ["Label"] = h.DisplayLabel,
            ["Score"] = Math.Round(h.Score, 3),
        }).ToList();
        var result = new ExecutionResult(rows, rows.Count, TimeSpan.Zero);
        return new SemanticSearchHandlerResult(sqlSummary, result, mode, hits);
    }
}
