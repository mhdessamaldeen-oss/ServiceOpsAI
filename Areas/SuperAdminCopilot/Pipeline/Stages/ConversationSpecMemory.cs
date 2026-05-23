namespace SuperAdminCopilot.Pipeline.Stages;

using System.Collections.Concurrent;
using SuperAdminCopilot.Models;

/// <summary>
/// In-memory per-conversation cache of the most recent successful <see cref="QuerySpec"/>.
/// Powers multi-turn refinement: when the user follows up ("actually only the open ones"),
/// the orchestrator recalls the previous spec and asks the LLM to modify it rather than
/// generating from scratch.
///
/// <para>Bounded by entry count — old entries are evicted via simple FIFO. State is process-
/// local; restarting the host loses memory (acceptable — refinements are short-lived).</para>
/// </summary>
public interface IConversationSpecMemory
{
    /// <summary>Returns the most recent spec for the conversation, or null when none.</summary>
    QuerySpec? Recall(string? conversationId);
    /// <summary>Stores the latest successful spec for this conversation.</summary>
    void Remember(string? conversationId, QuerySpec spec);
}

internal sealed class ConversationSpecMemory : IConversationSpecMemory
{
    // Cap at 1000 conversations to keep memory bounded. Real workloads with more sessions
    // would need a proper LRU; this is dev-grade.
    private const int MaxEntries = 1000;
    private readonly ConcurrentDictionary<string, QuerySpec> _store = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _fifo = new();

    public QuerySpec? Recall(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return null;
        return _store.TryGetValue(conversationId, out var spec) ? spec : null;
    }

    public void Remember(string? conversationId, QuerySpec spec)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || spec is null) return;
        var isNew = !_store.ContainsKey(conversationId);
        _store[conversationId] = spec;
        if (isNew)
        {
            _fifo.Enqueue(conversationId);
            // Trim oldest when over cap.
            while (_store.Count > MaxEntries && _fifo.TryDequeue(out var old))
                _store.TryRemove(old, out _);
        }
    }
}
