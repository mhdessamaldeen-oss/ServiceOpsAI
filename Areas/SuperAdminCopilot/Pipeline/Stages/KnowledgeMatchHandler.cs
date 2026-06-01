namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Application.Repair;
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

internal sealed class KnowledgeMatchHandler : IKnowledgeMatchHandler
{
    private readonly ISemanticLayer _semantic;
    private readonly IEntityCatalog _catalog;
    private readonly ILinguisticRegistry _registry;
    private readonly ILogger<KnowledgeMatchHandler> _logger;

    public string Name => "KnowledgeMatch";

    public KnowledgeMatchHandler(ISemanticLayer semantic, IEntityCatalog catalog, ILinguisticRegistry registry, ILogger<KnowledgeMatchHandler> logger)
    {
        _semantic = semantic;
        _catalog = catalog;
        _registry = registry;
        _logger = logger;
    }

    public KnowledgeMatchResult? TryHandle(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        // Aggregate markers ("average", "count", "متوسط", "عدد") flag this as a data query —
        // sourced from linguistic-cues.json aggregateMarkers per locale.
        if (_registry.LooksLikeAggregateQuery(question)) return null;

        // Knowledge-question verbs ("what is", "explain", "ما هو", "اشرح") + 1-3 token noun —
        // sourced from linguistic-cues.json knowledgeQuestion.verbs per locale.
        if (!_registry.LooksLikeKnowledgeQuestion(question, out var term) || string.IsNullOrEmpty(term))
            return null;

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
