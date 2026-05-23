namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text.Json;
using AISupportAnalysisPlatform.Enums;
using AISupportAnalysisPlatform.Services.AI.Providers;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<IntentClassifier> _logger;

    /// <summary>Deployment-specific domain hint injected into the classifier prompt.
    /// Intentionally NAMES NO TABLES — the classifier's job is to label SQL vs CHAT vs TOOL vs
    /// OUT_OF_SCOPE vs REFINEMENT, not to know the schema. Listing tables here biases the LLM
    /// toward whichever name appears first (previously "Tickets, …" which dominated routing).
    /// The schema linker downstream picks the actual table per question; that path is
    /// schema-driven and stays correct when tables are added/removed without code changes.</summary>
    private const string DomainHint =
        "Internal business data system. Questions in scope are questions whose answer can be " +
        "computed from the configured relational schema (lists, counts, joins, aggregates, " +
        "comparisons, refinements of prior answers).";

    public IntentClassifier(
        IAiProviderFactory providerFactory,
        ILogger<IntentClassifier> logger)
    {
        _providerFactory = providerFactory;
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

        try
        {
            // Use the Classifier workload. Falls back to Copilot's model when unset —
            // wired in OllamaAiProvider.GetModelNameForWorkload + AiProviderFactory.
            var provider = _providerFactory.GetProviderForWorkload(AiWorkloadType.Classifier);
            var prompt = BuildPrompt(question);

            // Plain GenerateAsync — the prompt itself instructs the model to emit a JSON
            // object. The parser tolerates code fences and leading prose. (GenerateJsonAsync
            // is only on WorkloadAwareProvider, not the IAiProvider interface; not worth
            // the cast for this single call.)
            var result = await provider.GenerateAsync(prompt);
            sw.Stop();

            var raw = result?.ResponseText ?? string.Empty;
            var parsed = Parse(raw);
            _logger.LogInformation(
                "[IntentClassifier] '{Q}' → {Intent} (conf={Conf:F2}) in {Ms}ms (model={Model})",
                Truncate(question, 80), parsed.Intent, parsed.Confidence, sw.ElapsedMilliseconds, provider.ModelName);

            return parsed with { ElapsedMs = sw.ElapsedMilliseconds, RawResponse = raw };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex,
                "[IntentClassifier] classification failed in {Ms}ms — falling through to deterministic routers",
                sw.ElapsedMilliseconds);
            return new IntentClassificationResult(ClassifierIntent.Unknown, 0.0, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private static string BuildPrompt(string question)
    {
        // Single-shot prompt with the label vocabulary, the domain hint, and a few-shot
        // line per label so the model anchors on shape rather than vibes. Few-shots are
        // intentionally minimal to keep the classifier prompt short — every call pays for
        // these tokens.
        return $@"You classify user questions for a domain assistant. Output ONLY a JSON object with two fields:
  ""intent"": one of [""SQL"", ""CHAT"", ""TOOL"", ""OUT_OF_SCOPE"", ""REFINEMENT""]
  ""confidence"": a number in [0, 1]

Domain: {DomainHint}

Label meanings:
- SQL: question about the data in the schema above (e.g. counts, lists, joins, aggregates)
- CHAT: greeting / meta / thanks (e.g. ""hi"", ""what can you do"", ""thanks"")
- TOOL: needs external real-time data (e.g. ""what is the weather"", ""price of Apple stock"", ""USD to EUR rate"")
- OUT_OF_SCOPE: unrelated to this domain (e.g. ""best recipe"", ""movie recommendations"", ""capital of France"")
- REFINEMENT: refers to a prior question (e.g. ""now just the open ones"", ""break it down by status"")

Examples:
- ""how many open tickets"" → {{""intent"": ""SQL"", ""confidence"": 0.98}}
- ""hello"" → {{""intent"": ""CHAT"", ""confidence"": 0.99}}
- ""what is the weather in Riyadh"" → {{""intent"": ""TOOL"", ""confidence"": 0.95}}
- ""latest news about AI"" → {{""intent"": ""TOOL"", ""confidence"": 0.92}}
- ""current bitcoin price"" → {{""intent"": ""TOOL"", ""confidence"": 0.93}}
- ""best recipe for sourdough"" → {{""intent"": ""OUT_OF_SCOPE"", ""confidence"": 0.97}}
- ""who won the world cup"" → {{""intent"": ""OUT_OF_SCOPE"", ""confidence"": 0.96}}
- ""now just the critical ones"" → {{""intent"": ""REFINEMENT"", ""confidence"": 0.92}}

Question: {question}

Output only the JSON object — no preamble, no markdown.";
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
