namespace AnalystAgent.Pipeline.Stages;

using System.Text.Json;
using ServiceOpsAI.Enums;
using ServiceOpsAI.Services.AI.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AnalystAgent.Abstractions;
using AnalystAgent.Configuration;
using AnalystAgent.Internal;

/// <summary>
/// LLM-based intent router that runs BEFORE the SQL-generation path and decides which
/// downstream handler should answer the question. The intent labels are:
///
/// <list type="bullet">
///   <item><c>Sql</c> — a data question about the configured schema</item>
///   <item><c>Chat</c> — greeting / meta / thanks (no data needed)</item>
///   <item><c>Tool</c> — needs an external lookup (weather, FX, news, …)</item>
///   <item><c>OutOfScope</c> — outside the domain (recipes, stock prices, sports, …)</item>
///   <item><c>Refinement</c> — refers to the prior turn ("now just the open ones")</item>
/// </list>
///
/// <para><b>Why this exists:</b> when the Copilot SQL workload is served by a NARROW
/// fine-tune (e.g. <c>copilot-nl2sql</c>), the model has lost generalist capabilities like
/// refusing OOS or recognising chat. The intent classifier becomes the front door — it
/// uses a generalist model (<c>AiClassifierModel</c>) to label the question, and only
/// SQL-classified questions ever reach the SQL specialist. This protects the specialist
/// from inputs it can't gracefully handle.</para>
///
/// <para><b>Schema-agnostic:</b> the prompt is parameterised on a <see cref="DomainHint"/>
/// describing the deployment's data domain (e.g. "support tickets"). When the engine is
/// packaged for another app, only the hint changes.</para>
/// </summary>
public interface IIntentClassifier
{
    /// <summary>Classify a user question. Returns the inferred intent + a confidence score
    /// in [0, 1] and the raw model response (useful for trace debugging).
    /// On any error (provider failure, JSON parse failure, etc.) returns
    /// <see cref="ClassifierIntent.Unknown"/> with confidence 0 — callers should fall through
    /// to the existing deterministic routers in that case.</summary>
    Task<IntentClassificationResult> ClassifyAsync(string question, CancellationToken cancellationToken = default);
}

/// <summary>Labels the classifier can emit. Order is stable — used in trace serialization.</summary>
public enum ClassifierIntent
{
    /// <summary>Classifier failed or output couldn't be parsed; treat as no decision.</summary>
    Unknown = 0,
    /// <summary>Data question against the configured schema — route to SQL generator.</summary>
    Sql = 1,
    /// <summary>Greeting, meta, or thanks — route to ConversationalHandler.</summary>
    Chat = 2,
    /// <summary>External lookup (weather, FX, …) — route to ToolHandler.</summary>
    Tool = 3,
    /// <summary>Outside the deployment's domain — refuse politely.</summary>
    OutOfScope = 4,
    /// <summary>Refers to prior turn — needs RefinementDetector context.</summary>
    Refinement = 5
}

/// <param name="Intent">The inferred intent. <see cref="ClassifierIntent.Unknown"/> on failure.</param>
/// <param name="Confidence">Model-reported confidence in [0, 1]. Use to apply higher floors
/// when the classifier is uncertain.</param>
/// <param name="RawResponse">The raw text the model emitted (for trace inspection).</param>
/// <param name="ElapsedMs">Latency of the classifier call — important since this runs on EVERY
/// question and contributes to overall latency.</param>
public sealed record IntentClassificationResult(
    ClassifierIntent Intent,
    double Confidence,
    string RawResponse,
    long ElapsedMs);

internal sealed class IntentClassifier : IIntentClassifier
{
    private readonly IAiProviderFactory _providerFactory;
    private readonly IOptionsMonitor<CopilotTextCatalog> _textCatalog;
    private readonly IOptionsMonitor<AnalystOptions> _options;
    private readonly ILogger<IntentClassifier> _logger;

    public IntentClassifier(
        IAiProviderFactory providerFactory,
        IOptionsMonitor<CopilotTextCatalog> textCatalog,
        IOptionsMonitor<AnalystOptions> options,
        ILogger<IntentClassifier> logger)
    {
        _providerFactory = providerFactory;
        _textCatalog = textCatalog;
        _options = options;
        _logger = logger;
    }

    public async Task<IntentClassificationResult> ClassifyAsync(
        string question,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(question))
        {
            sw.Stop();
            return new IntentClassificationResult(ClassifierIntent.Unknown, 0.0, string.Empty, sw.ElapsedMilliseconds);
        }

        IAiProvider? provider = null;
        string? prompt = null;
        try
        {
            // Use the Classifier workload. Falls back to Copilot's model when unset —
            // wired in OllamaAiProvider.GetModelNameForWorkload + AiProviderFactory.
            provider = _providerFactory.GetProviderForWorkload(AiWorkloadType.Classifier);
            prompt = BuildPrompt(question, _textCatalog.CurrentValue);

            // Plain GenerateAsync — the prompt itself instructs the model to emit a JSON
            // object. The parser tolerates code fences and leading prose. (GenerateJsonAsync
            // is only on WorkloadAwareProvider, not the IAiProvider interface; not worth
            // the cast for this single call.)
            var result = await provider.GenerateAsync(prompt);
            sw.Stop();

            var raw = result?.ResponseText ?? string.Empty;
            // Record into the per-question trace scope. IntentClassifier calls the provider DIRECTLY
            // (Classifier workload, not via ILlmClient), so without this its prompt + output would be the
            // ONE LLM call missing from the trace. Shared helper → preview always, full text under eval.
            RecordTrace(prompt, raw, result, provider?.ModelName, sw.ElapsedMilliseconds, result?.Success ?? true, null);

            var parsed = Parse(raw);
            _logger.LogInformation(
                "[IntentClassifier] '{Q}' → {Intent} (conf={Conf:F2}) in {Ms}ms (model={Model})",
                Truncate(question, 80), parsed.Intent, parsed.Confidence, sw.ElapsedMilliseconds, provider?.ModelName);

            return parsed with { ElapsedMs = sw.ElapsedMilliseconds, RawResponse = raw };
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordTrace(prompt, null, null, provider?.ModelName, sw.ElapsedMilliseconds, false, ex.Message);
            _logger.LogWarning(ex,
                "[IntentClassifier] classification failed in {Ms}ms — falling through to deterministic routers",
                sw.ElapsedMilliseconds);
            return new IntentClassificationResult(ClassifierIntent.Unknown, 0.0, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Append this classifier call to the per-question <see cref="LlmCallScope"/> so the
    /// investigation trace + export include it. Best-effort: tracing must never break classification.</summary>
    private void RecordTrace(string? prompt, string? response, AiProviderResult? result, string? fallbackModel,
        long elapsedMs, bool success, string? error)
    {
        try
        {
            var opts = _options.CurrentValue;
            LlmCallScope.Current?.Record(LlmTraceCapture.BuildRecord(
                stage: "IntentClassifier",
                provider: result?.ProviderType.ToString() ?? "Classifier",
                model: result?.ModelUsed ?? fallbackModel,
                usage: result?.Usage,
                elapsedMs: elapsedMs,
                success: success,
                error: error,
                prompt: prompt,
                response: response,
                previewCap: opts.LlmTracePreviewMaxChars,
                fullCap: opts.LlmTraceFullMaxChars));
        }
        catch { /* tracing must never break classification */ }
    }

    private static string BuildPrompt(string question, CopilotTextCatalog text)
    {
        // Pick the prompt variant matching the question's language. The template comes from
        // the catalog (hot-reloadable; per-deployment overridable via copilot-text.json) so
        // adding a new locale is a JSON edit — no code change. {0} is replaced with the
        // question text via string.Format.
        var language = QuestionLanguageDetector.Detect(question);
        var template = language == QuestionLanguageDetector.Arabic
            ? text.IntentClassifierPromptAr
            : text.IntentClassifierPromptEn;
        return string.Format(template, question);
    }

    private static IntentClassificationResult Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new IntentClassificationResult(ClassifierIntent.Unknown, 0.0, raw, 0);

        // Strip common LLM artifacts: code fences, leading/trailing prose. We tolerate
        // models that wrap the JSON in markdown despite the "no markdown" instruction.
        var trimmed = raw.Trim();
        var fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var fenceEnd = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd > fenceStart)
                trimmed = trimmed.Substring(fenceStart + 3, fenceEnd - fenceStart - 3).Trim();
            if (trimmed.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(4).TrimStart();
        }
        // Find the JSON object substring (defensive — some models still prepend text).
        var jsonStart = trimmed.IndexOf('{');
        var jsonEnd = trimmed.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
            return new IntentClassificationResult(ClassifierIntent.Unknown, 0.0, raw, 0);
        var json = trimmed.Substring(jsonStart, jsonEnd - jsonStart + 1);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var intentStr = root.TryGetProperty("intent", out var i) ? i.GetString() ?? "" : "";
            var conf = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetDouble() : 0.0;
            return new IntentClassificationResult(MapLabel(intentStr), Math.Clamp(conf, 0.0, 1.0), raw, 0);
        }
        catch (JsonException)
        {
            return new IntentClassificationResult(ClassifierIntent.Unknown, 0.0, raw, 0);
        }
    }

    private static ClassifierIntent MapLabel(string intent) => intent.Trim().ToUpperInvariant() switch
    {
        "SQL"            => ClassifierIntent.Sql,
        "CHAT"           => ClassifierIntent.Chat,
        "TOOL"           => ClassifierIntent.Tool,
        "OUT_OF_SCOPE"   => ClassifierIntent.OutOfScope,
        "OOS"            => ClassifierIntent.OutOfScope,
        "REFINEMENT"     => ClassifierIntent.Refinement,
        "REFINE"         => ClassifierIntent.Refinement,
        _                => ClassifierIntent.Unknown
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";
}
