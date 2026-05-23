namespace SuperAdminCopilot.Abstractions;

using ServiceOpsAI.Services.AI.Providers;

/// <summary>
/// Per-question metrics scope for LLM calls. The orchestrator opens a scope at the top of
/// <c>AskAsync</c>; every call to <see cref="ILlmClient"/> inside that scope appends a
/// <see cref="LlmCallRecord"/> to the scope's log via <c>AsyncLocal</c> propagation. After the
/// question completes the orchestrator reads totals from the scope and stamps them on the
/// trace row. Call sites (<c>SpecExtractor</c>, <c>LlmDecomposer</c>, etc.) never see this —
/// the capture is a pure side-channel on top of the existing <c>Task&lt;string&gt;</c> contract.
///
/// <para>Async-local rather than DI-scoped so it survives the Task.Run / Task.WhenAll
/// awaits the decomposer uses to fan out sub-questions, without requiring every method on
/// the call path to plumb an explicit context object.</para>
/// </summary>
public sealed class LlmCallScope : IDisposable
{
    private static readonly AsyncLocal<LlmCallScope?> _current = new();
    private readonly LlmCallScope? _parent;
    private readonly List<LlmCallRecord> _records = new();

    /// <summary>The active scope on this async path, or null when no question is in flight.</summary>
    public static LlmCallScope? Current => _current.Value;

    /// <summary>Open a new scope. Always dispose. Nested scopes are allowed; only the
    /// innermost receives records. The orchestrator opens exactly one per question.</summary>
    public static LlmCallScope Begin() => new();

    private LlmCallScope()
    {
        _parent = _current.Value;
        _current.Value = this;
    }

    /// <summary>Append a record. Safe to call from any thread; the scope serialises via lock
    /// because the same question can fan out parallel sub-questions through Task.WhenAll.</summary>
    public void Record(LlmCallRecord record)
    {
        lock (_records) _records.Add(record);
    }

    /// <summary>Snapshot of every call recorded in this scope, in append order.</summary>
    public IReadOnlyList<LlmCallRecord> Records
    {
        get { lock (_records) return _records.ToArray(); }
    }

    public int CallCount => Records.Count;
    public int TotalPromptTokens => Records.Sum(r => r.Usage?.Prompt ?? 0);
    public int TotalCompletionTokens => Records.Sum(r => r.Usage?.Completion ?? 0);
    public long TotalElapsedMs => Records.Sum(r => r.ElapsedMs);

    public void Dispose()
    {
        // Pop only when the topmost scope on this async path matches us; otherwise the
        // dispose order got tangled and we leave the parent in place rather than corrupting
        // the chain.
        if (_current.Value == this) _current.Value = _parent;
    }
}

/// <summary>A single LLM call's telemetry. The orchestrator records one of these per call.
/// <c>Stage</c> matches the pipeline-step name (e.g. <c>StepSpecExtractor</c>) so per-call
/// rows can be joined back to the originating pipeline step in the investigation UI.</summary>
public sealed record LlmCallRecord(
    string Stage,
    string Provider,
    string? Model,
    TokenUsage? Usage,
    long ElapsedMs,
    bool Success,
    string? Error = null);

/// <summary>
/// Stage hint propagated via <c>AsyncLocal</c> so callers can label LLM calls without
/// changing the <see cref="ILlmClient"/> signature. <c>SpecExtractor</c> wraps its call in
/// <c>using LlmCallStageHint.Use("StepSpecExtractor")</c>; the bridge reads the hint when
/// it records the call. When no hint is active the bridge falls back to a generic "Llm".
/// </summary>
public sealed class LlmCallStageHint : IDisposable
{
    private static readonly AsyncLocal<string?> _value = new();
    private readonly string? _previous;
    public static string? Current => _value.Value;
    public static LlmCallStageHint Use(string stage) => new(stage);
    private LlmCallStageHint(string stage) { _previous = _value.Value; _value.Value = stage; }
    public void Dispose() { _value.Value = _previous; }
}
