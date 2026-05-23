namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Pipeline.Routing;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;

/// <summary>
/// F1.d — Knowledge-match handler. Catches concept-shaped questions ("what is a ticket?",
/// "explain ticket priorities", "tell me about users") and answers from the semantic layer's
/// entity descriptions instead of running SQL. Different from <see cref="IMetadataHandler"/>:
/// metadata answers schema questions ("what columns are in Tickets"), knowledge-match answers
/// vocabulary / glossary questions ("what is a ticket").
///
/// <para><b>Returns null</b> when the question doesn't match a knowledge-shaped pattern OR the
/// resolved token doesn't have a semantic-layer entry — caller falls through to the planner.
/// Conservative on purpose: false positives shadow real data queries that start with
/// "what is the average…".</para>
/// </summary>
public interface IKnowledgeMatchHandler
{
    KnowledgeMatchResult? TryHandle(string question);
}

public sealed record KnowledgeMatchResult(string Reply, string Term);

internal sealed class KnowledgeMatchHandler : IKnowledgeMatchHandler, IRoutingProbe
{
    private readonly ISemanticLayer _semantic;
    private readonly IEntityCatalog _catalog;
    private readonly ILogger<KnowledgeMatchHandler> _logger;

    public string Name => "KnowledgeMatch";

    /// <summary>Probe — replays the exact same gating that TryHandle uses: rejects data-query
    /// phrasings, then checks for a knowledge-shape match AND that the captured term resolves
    /// to a semantic-layer entry. Without the resolution check the probe would over-claim
    /// concept questions whose term doesn't exist in this domain.</summary>
    public Task<RouterDecision?> ProbeAsync(string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return Task.FromResult<RouterDecision?>(null);
        if (DataQueryClassifier.IsMatch(question)) return Task.FromResult<RouterDecision?>(null);

        var term = ExtractKnowledgeTerm(question);
        if (term is null) return Task.FromResult<RouterDecision?>(null);

        var resolves = _semantic.GetEntityByNameOrSynonym(term) is not null
                    || _semantic.GetMetric(term) is not null
                    || _semantic.GetDimension(term) is not null;
        if (!resolves)
        {
            var stripped = StripPlural(term);
            resolves = !string.Equals(stripped, term, StringComparison.OrdinalIgnoreCase)
                       && _semantic.GetEntityByNameOrSynonym(stripped) is not null;
        }
        return Task.FromResult<RouterDecision?>(resolves
            ? new RouterDecision(IntentLabel.Knowledge, 0.95, Name, $"term='{term}' resolved")
            : null);
    }

    /// <summary>
    /// English vocabulary patterns: "what is a/an X", "explain X", "what does X mean",
    /// "tell me about X", "describe X". The trailing X must be 1-3 words. Deliberately excludes
    /// patterns that start with "what is the average / total / count / number / difference
    /// between" because those are data queries, not vocabulary questions.
    /// </summary>
    private static readonly Regex KnowledgePhrase = new(
        @"^\s*(?:" +
        @"what\s+is\s+(?:(?:a|an|the)\s+)?(?<term>[a-z][\w-]*(?:\s+[a-z][\w-]*){0,2})|" +
        @"what\s+are\s+(?<term2>[a-z][\w-]*(?:\s+[a-z][\w-]*){0,2})|" +
        @"explain\s+(?:the\s+)?(?<term3>[a-z][\w-]*(?:\s+[a-z][\w-]*){0,2})|" +
        @"what\s+does\s+(?<term4>[a-z][\w-]*(?:\s+[a-z][\w-]*){0,2})\s+mean|" +
        @"tell\s+me\s+about\s+(?:the\s+)?(?<term5>[a-z][\w-]*(?:\s+[a-z][\w-]*){0,2})|" +
        @"describe\s+(?:the\s+)?(?<term6>[a-z][\w-]*(?:\s+[a-z][\w-]*){0,2})" +
        @")[?.\s]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Arabic vocabulary patterns: ما هو X / ما هي X / اشرح X / أخبرني عن X / وضح X / عرّف X.
    /// X captures 1–3 tokens (Arabic word characters plus Latin letters, since users often
    /// mix Arabic verbs with English entity names like "ما هو AspNetUsers"). The terminator
    /// allows trailing question marks (Arabic ؟ U+061F or ASCII ?) or punctuation.
    /// </summary>
    private static readonly Regex KnowledgePhraseAr = new(
        @"^\s*(?:" +
        @"ما\s+(?:هو|هي|هما)\s+(?<term>[\p{L}_][\p{L}\p{Nd}_-]*(?:\s+[\p{L}_][\p{L}\p{Nd}_-]*){0,2})|" +
        @"اشرح\s+(?:لي\s+)?(?<term2>[\p{L}_][\p{L}\p{Nd}_-]*(?:\s+[\p{L}_][\p{L}\p{Nd}_-]*){0,2})|" +
        @"أخبرني\s+عن\s+(?<term3>[\p{L}_][\p{L}\p{Nd}_-]*(?:\s+[\p{L}_][\p{L}\p{Nd}_-]*){0,2})|" +
        @"وضح\s+(?:لي\s+)?(?<term4>[\p{L}_][\p{L}\p{Nd}_-]*(?:\s+[\p{L}_][\p{L}\p{Nd}_-]*){0,2})|" +
        @"عرّف\s+(?<term5>[\p{L}_][\p{L}\p{Nd}_-]*(?:\s+[\p{L}_][\p{L}\p{Nd}_-]*){0,2})|" +
        @"عرف\s+(?<term6>[\p{L}_][\p{L}\p{Nd}_-]*(?:\s+[\p{L}_][\p{L}\p{Nd}_-]*){0,2})" +
        @")[?؟.\s]*$",
        RegexOptions.Compiled);

    /// <summary>Words that — even when matching the regex above — flag the question as a real
    /// data query rather than a knowledge question. "What is the average resolution time" should
    /// NOT route here; "what is the count" likewise. Includes Arabic aggregate terms.</summary>
    private static readonly Regex DataQueryClassifier = new(
        @"\b(?:average|avg|total|count|number\s+of|sum|min|max|distinct|unique|" +
        @"percentage|ratio|difference\s+between|how\s+many|top\s+\d+)\b" +
        @"|(?:متوسط|إجمالي|اجمالي|مجموع|عدد|أعلى|اعلى|أدنى|ادنى|نسبة|كم\s+عدد)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Match against either the English or Arabic knowledge-phrase regex and return
    /// the captured term, or null if neither matches.</summary>
    private static string? ExtractKnowledgeTerm(string question)
    {
        var m = KnowledgePhrase.Match(question);
        if (!m.Success) m = KnowledgePhraseAr.Match(question);
        if (!m.Success) return null;
        var term = (m.Groups["term"].Value
            + m.Groups["term2"].Value
            + m.Groups["term3"].Value
            + m.Groups["term4"].Value
            + m.Groups["term5"].Value
            + m.Groups["term6"].Value).Trim();
        return string.IsNullOrWhiteSpace(term) ? null : term;
    }

    public KnowledgeMatchHandler(ISemanticLayer semantic, IEntityCatalog catalog, ILogger<KnowledgeMatchHandler> logger)
    {
        _semantic = semantic;
        _catalog = catalog;
        _logger = logger;
    }

    public KnowledgeMatchResult? TryHandle(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        if (DataQueryClassifier.IsMatch(question)) return null;

        var term = ExtractKnowledgeTerm(question);
        if (term is null) return null;

        // Try entity / metric / dimension lookups in priority order.
        var entity = _semantic.GetEntityByNameOrSynonym(term);
        if (entity is not null) return new KnowledgeMatchResult(BuildEntityReply(entity), term);

        var metric = _semantic.GetMetric(term);
        if (metric is not null) return new KnowledgeMatchResult(BuildMetricReply(metric), term);

        var dim = _semantic.GetDimension(term);
        if (dim is not null) return new KnowledgeMatchResult(BuildDimensionReply(dim), term);

        // Try plural-stripped variants ("priorities" → "priority", "statuses" → "status").
        var stripped = StripPlural(term);
        if (!string.Equals(stripped, term, StringComparison.OrdinalIgnoreCase))
        {
            entity = _semantic.GetEntityByNameOrSynonym(stripped);
            if (entity is not null) return new KnowledgeMatchResult(BuildEntityReply(entity), term);
        }

        _logger.LogDebug("[KnowledgeMatch] no semantic-layer entry for term '{Term}' from '{Q}'.", term, question);
        return null;
    }

    private string BuildEntityReply(EntityDefinition e)
    {
        var sb = new StringBuilder();
        sb.Append("**").Append(e.Name).AppendLine("**");
        if (!string.IsNullOrWhiteSpace(e.Description)) sb.AppendLine(e.Description);
        else sb.Append("Department backed by the `").Append(e.Table).AppendLine("` table.");

        if (e.Synonyms is { Count: > 0 })
            sb.Append("\nAlso called: ").AppendLine(string.Join(", ", e.Synonyms));

        if (_catalog.TableExists(e.Table))
        {
            var cols = _catalog.GetColumns(e.Table).Take(8).Select(c => c.ColumnName).ToList();
            if (cols.Count > 0)
                sb.Append("\nKey columns: ").AppendLine(string.Join(", ", cols));
        }
        sb.Append("\nYou can ask things like \"how many ").Append(e.Name.ToLowerInvariant())
          .Append("s do we have?\" or \"top 5 ").Append(e.Name.ToLowerInvariant()).Append("s by ...\".");
        return sb.ToString();
    }

    private static string BuildMetricReply(MetricDefinition m)
    {
        var sb = new StringBuilder();
        sb.Append("**Metric: ").Append(m.Name).AppendLine("**");
        if (!string.IsNullOrWhiteSpace(m.Description)) sb.AppendLine(m.Description);
        sb.Append("\nFormula: `").Append(m.Expression).AppendLine("`");
        if (m.Synonyms is { Count: > 0 })
            sb.Append("\nAlso called: ").AppendLine(string.Join(", ", m.Synonyms));
        return sb.ToString();
    }

    private static string BuildDimensionReply(DimensionDefinition d)
    {
        var sb = new StringBuilder();
        sb.Append("**Dimension: ").Append(d.Name).AppendLine("**");
        if (!string.IsNullOrWhiteSpace(d.Description)) sb.AppendLine(d.Description);
        if (!string.IsNullOrWhiteSpace(d.Expression))
            sb.Append("\nDefined as: `").Append(d.Expression).AppendLine("`");
        else if (!string.IsNullOrWhiteSpace(d.Column))
            sb.Append("\nColumn: `").Append(d.Column).AppendLine("`");
        if (d.Synonyms is { Count: > 0 })
            sb.Append("\nAlso called: ").AppendLine(string.Join(", ", d.Synonyms));
        return sb.ToString();
    }

    private static string StripPlural(string s)
    {
        if (s.EndsWith("ies", StringComparison.Ordinal) && s.Length > 3) return s[..^3] + "y";
        if (s.EndsWith("es", StringComparison.Ordinal) && s.Length > 3) return s[..^2];
        if (s.EndsWith("s", StringComparison.Ordinal) && !s.EndsWith("ss", StringComparison.Ordinal) && s.Length > 1) return s[..^1];
        return s;
    }
}
