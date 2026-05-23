namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Models;
using SuperAdminCopilot.Schema;
using SuperAdminCopilot.Semantic;

/// <summary>
/// MVP conversation memory — port of legacy <c>ConversationContextExtractor</c>. Walks the
/// chat history (newest first), finds the most-recently-discussed schema entity, and renders a
/// prompt-injectable context block that lets the planner resolve refinement-style follow-ups:
///
/// <code>
/// User: Show me open tickets
/// User: Sort by date         ← without context, "tickets" is missing entirely
/// </code>
///
/// <para><b>How it works:</b> two pieces are fed forward:
///   1. The <i>verbatim prior user question</i> — handles refinements like "now only critical"
///      or "sort by date" without needing a structured plan parser.
///   2. The <i>most-recently-mentioned entity</i> — case-insensitive, plural-tolerant token
///      match against semantic-layer entity names + table names + their CamelCase parts.
/// Both go into a `=== PRIOR CONVERSATION CONTEXT ===` block prepended to the planner prompt.
/// The planner is instructed to merge filters/sorts when the current question is a refinement
/// and ignore the block when the new question explicitly switches topics.</para>
///
/// <para><b>What this deliberately doesn't do:</b> NER, embedding similarity, deep coreference.
/// Those are upgrades for later — the cheap baseline solves ~70% of follow-ups for free.</para>
/// </summary>
public interface IConversationContext
{
    /// <summary>
    /// Build a prompt-injectable context block from the user's history. Empty string when
    /// there's nothing useful to add (no history, single-turn, no entity match found).
    /// </summary>
    string Build(IReadOnlyList<PriorTurn>? history);
}

internal sealed class ConversationContext : IConversationContext
{
    private static readonly Regex CamelCaseSplitter =
        new(@"[A-Z]?[a-z]+|[A-Z]+(?=[A-Z]|$)|\d+", RegexOptions.Compiled);

    private readonly ISemanticLayer _semantic;
    private readonly IEntityCatalog _catalog;
    private readonly IOptionsMonitor<CopilotOptions> _optionsMonitor;
    private readonly ILogger<ConversationContext> _logger;

    public ConversationContext(
        ISemanticLayer semantic,
        IEntityCatalog catalog,
        IOptionsMonitor<CopilotOptions> optionsMonitor,
        ILogger<ConversationContext> logger)
    {
        _semantic = semantic;
        _catalog = catalog;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    /// <summary>How many prior user turns to scan, sourced from <see cref="CopilotOptions.ConversationLookbackTurns"/>
    /// so admins can tune without recompile. Reads <c>.CurrentValue</c> per call to honor hot-reload.</summary>
    private int MaxLookbackTurns => _optionsMonitor.CurrentValue.ConversationLookbackTurns;

    public string Build(IReadOnlyList<PriorTurn>? history)
    {
        if (history is null || history.Count == 0) return string.Empty;
        var userTurns = history.Where(t => t.IsUser).ToList();
        if (userTurns.Count <= 1) return string.Empty;

        // Most recent prior turn = second-to-last user turn (the last is the current question).
        var priorQuestion = userTurns[^2]?.Content?.Trim();

        // Walk backwards skipping the current turn looking for an entity match.
        var priorTurns = userTurns.Take(userTurns.Count - 1).Reverse().Take(MaxLookbackTurns);
        EntityDefinition? priorEntity = null;
        foreach (var turn in priorTurns)
        {
            priorEntity = MatchEntity(turn.Content);
            if (priorEntity is not null) break;
        }

        if (priorEntity is null && string.IsNullOrWhiteSpace(priorQuestion))
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("=== PRIOR CONVERSATION CONTEXT ===");
        if (!string.IsNullOrWhiteSpace(priorQuestion))
        {
            // Truncate very long prior questions so the prompt stays compact.
            var trimmed = priorQuestion.Length > 240 ? priorQuestion[..240] + "…" : priorQuestion;
            sb.AppendLine($"Previous user question: \"{trimmed}\"");
        }
        if (priorEntity is not null)
        {
            sb.AppendLine($"Previously-discussed entity: **{priorEntity.Name}** (table: {priorEntity.Table}).");
        }
        sb.AppendLine("If the current question is a REFINEMENT of the previous (e.g. \"sort by date\", \"top 3\", \"now critical only\", \"only the open ones\"), MERGE its filters/sorts/limits with the previous question's intent — don't drop the previous filters. If the current question explicitly names a different entity OR is unrelated, ignore this context.");
        return sb.ToString();
    }

    /// <summary>Tokenize the message, plural-strip, then check each token against entity Name /
    /// Table AND every CamelCase-split component. First hit wins, biased to the order entities
    /// are declared in the semantic layer (most-prominent first).</summary>
    private EntityDefinition? MatchEntity(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var tokens = content
            .ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!', '(', ')', '[', ']', '\'', '"' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Select(StripPlural)
            .ToHashSet();
        if (tokens.Count == 0) return null;

        foreach (var entity in _semantic.Config?.Entities ?? Enumerable.Empty<EntityDefinition>())
        {
            if (!_catalog.TableExists(entity.Table)) continue;
            if (EntityVocabulary(entity).Any(v => tokens.Contains(v)))
                return entity;
        }
        return null;
    }

    private static IEnumerable<string> EntityVocabulary(EntityDefinition entity)
    {
        yield return StripPlural(entity.Name.ToLowerInvariant());
        if (!string.IsNullOrWhiteSpace(entity.Table))
            yield return StripPlural(entity.Table.ToLowerInvariant());

        foreach (Match m in CamelCaseSplitter.Matches(entity.Name))
            yield return StripPlural(m.Value.ToLowerInvariant());

        if (!string.IsNullOrWhiteSpace(entity.Table))
            foreach (Match m in CamelCaseSplitter.Matches(entity.Table))
                yield return StripPlural(m.Value.ToLowerInvariant());

        foreach (var syn in entity.Synonyms ?? Enumerable.Empty<string>())
            yield return StripPlural(syn.ToLowerInvariant());
    }

    private static string StripPlural(string s)
    {
        if (s.EndsWith("ies", StringComparison.Ordinal) && s.Length > 3) return s[..^3] + "y";
        if (s.EndsWith("es", StringComparison.Ordinal) && s.Length > 3) return s[..^2];
        if (s.EndsWith("s", StringComparison.Ordinal) &&
            !s.EndsWith("ss", StringComparison.Ordinal) &&
            s.Length > 1) return s[..^1];
        return s;
    }
}
