namespace AnalystAgent.Abstractions;

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
/// rows can be joined back to the originating pipeline step in the investigation UI.
/// <para>The full prompt and response are captured (truncated by the bridge if they exceed
/// <c>PromptCaptureMaxChars</c>) so the investigation page can show "what did we send and
/// what came back" without operators having to grep the log file. NULL preview fields mean
/// the value wasn't captured (operator opted out OR the response failed before completing).</para></summary>
public sealed record LlmCallRecord(
    string Stage,
    string Provider,
    string? Model,
    TokenUsage? Usage,
    long ElapsedMs,
    bool Success,
    string? Error = null,
    string? PromptPreview = null,
    string? ResponsePreview = null,
    int? PromptFullLength = null,
    int? ResponseFullLength = null,
    int RetryAttempt = 0,
    // "llm" = a generation call (chat/SQL/explainer); "embedding" = a bge-m3 vector call (no Response,
    // PromptPreview carries the embedded text). Lets the trace UI + export distinguish the two.
    string Kind = "llm",
    // FULL (untruncated, bounded by LlmTraceFullMaxChars) prompt + response. Populated only when the
    // active LlmTraceCaptureScope is Full (eval/assessment runs); null in Preview mode (normal chat),
    // where only the 4000-char PromptPreview/ResponsePreview are kept to bound DB growth.
    string? PromptFull = null,
    string? ResponseFull = null);

/// <summary>How much per-call text the bridge captures. <see cref="Preview"/> (default) keeps only the
/// ~4000-char preview; <see cref="Full"/> additionally stores the untruncated prompt + response (bounded
/// by <c>AnalystOptions.LlmTraceFullMaxChars</c>). Assessment/eval runs open a Full scope so a trace can
/// be inspected end-to-end; normal chat stays in Preview to keep the trace table lean.</summary>
public enum LlmTraceCaptureMode { Preview = 0, Full = 1 }

/// <summary>AsyncLocal capture-mode flag, mirroring <see cref="LlmCallStageHint"/>. Set by the assessment
/// handler around each case (<c>using var _ = LlmTraceCaptureScope.Full();</c>); the bridge reads
/// <see cref="Current"/> when it records a call. Defaults to Preview when no scope is active.</summary>
public sealed class LlmTraceCaptureScope : IDisposable
{
    private static readonly AsyncLocal<LlmTraceCaptureMode> _mode = new();
    private readonly LlmTraceCaptureMode _previous;
    public static LlmTraceCaptureMode Current => _mode.Value;          // enum default 0 == Preview
    public static LlmTraceCaptureScope Full() => new(LlmTraceCaptureMode.Full);
    private LlmTraceCaptureScope(LlmTraceCaptureMode mode) { _previous = _mode.Value; _mode.Value = mode; }
    public void Dispose() { _mode.Value = _previous; }
}

/// <summary>Builds an <see cref="LlmCallRecord"/> applying the standard PREVIEW truncation (always) and
/// FULL-text capture (only when the active <see cref="LlmTraceCaptureScope"/> is Full). Shared by the
/// <c>ILlmClient</c> bridge and by call sites that invoke a provider directly (IntentClassifier, the
/// embedder) so every recorded call truncates identically — one place owns the policy.</summary>
public static class LlmTraceCapture
{
    public static LlmCallRecord BuildRecord(
        string stage, string provider, string? model, TokenUsage? usage,
        long elapsedMs, bool success, string? error,
        string? prompt, string? response, int previewCap, int fullCap,
        int retryAttempt = 0, string kind = "llm")
    {
        static string? Cut(string? s, int cap) =>
            string.IsNullOrEmpty(s) ? null : (s!.Length > cap ? s.Substring(0, cap) : s);
        previewCap = System.Math.Max(0, previewCap);
        var promptPreview = previewCap > 0 ? Cut(prompt, previewCap) : null;
        var respPreview = previewCap > 0 ? Cut(response, previewCap) : null;
        string? promptFull = null, respFull = null;
        if (LlmTraceCaptureScope.Current == LlmTraceCaptureMode.Full)
        {
            var fc = System.Math.Max(0, fullCap);
            promptFull = fc > 0 ? Cut(prompt, fc) : null;
            respFull = fc > 0 ? Cut(response, fc) : null;
        }
        return new LlmCallRecord(
            Stage: stage, Provider: provider, Model: model, Usage: usage,
            ElapsedMs: elapsedMs, Success: success, Error: error,
            PromptPreview: promptPreview, ResponsePreview: respPreview,
            PromptFullLength: string.IsNullOrEmpty(prompt) ? null : prompt!.Length,
            ResponseFullLength: string.IsNullOrEmpty(response) ? null : response!.Length,
            RetryAttempt: retryAttempt, Kind: kind, PromptFull: promptFull, ResponseFull: respFull);
    }
}

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
