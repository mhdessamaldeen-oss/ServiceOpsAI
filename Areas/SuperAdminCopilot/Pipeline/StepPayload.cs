namespace SuperAdminCopilot.Pipeline;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Structured payload every pipeline stage writes into <c>PipelineStep.TechnicalData</c>.
/// The investigation-tab renderer detects this shape (presence of <c>"_payload": "v1"</c>)
/// and renders labeled <em>Input</em> / <em>Output</em> / <em>Reason</em> sections in the
/// step's detail panel — instead of dumping the raw text into a single pre-box.
/// </summary>
/// <remarks>
/// <para>Plain-text TechnicalData written by legacy traces (before this struct existed)
/// won't parse as JSON; the renderer treats those as "no structured detail" per the
/// approved plan's legacy-traces decision (hide, don't fall back).</para>
/// <para>The static <c>Serialize</c> method is the only entry point — stages never
/// hand-roll JSON. Keeps the schema enforceable from one place.</para>
/// </remarks>
public sealed record StepPayload(
    [property: JsonPropertyName("kind")]    string Kind,
    [property: JsonPropertyName("input")]   string? Input = null,
    [property: JsonPropertyName("output")]  string? Output = null,
    [property: JsonPropertyName("reason")]  string? Reason = null,
    [property: JsonPropertyName("details")] object? Details = null)
{
    /// <summary>Tag used by the renderer to recognise this schema. Bump if breaking changes.</summary>
    [JsonPropertyName("_payload")] public string Version => "v1";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialise to compact JSON for embedding in <c>PipelineStep.TechnicalData</c>.</summary>
    public static string Serialize(StepPayload payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>Convenience: build + serialise in one call. Stage code becomes a single line.</summary>
    public static string Of(string kind, string? input = null, string? output = null,
        string? reason = null, object? details = null) =>
        Serialize(new StepPayload(kind, input, output, reason, details));
}

/// <summary>
/// Canonical <see cref="StepPayload.Kind"/> values. Mirrors the existing
/// <c>StageNames.Kind*</c> set plus extras for steps that don't currently have a Kind.
/// </summary>
public static class StepPayloadKinds
{
    public const string LlmCall       = "llm-call";
    public const string SqlExecution  = "sql-execution";
    public const string SqlCompile    = "sql-compile";
    public const string SqlValidate   = "sql-validate";
    public const string FunctionCall  = "function-call";
    public const string ToolDispatch  = "tool-dispatch";
    public const string Gate          = "gate";       // AccessPolicy / OperationalGuard
    public const string Branch        = "branch";     // Conversational / KnowledgeMatch / VerifiedQuery / SemanticSearch decisions
    public const string Decompose     = "decompose";
    public const string ShapeCheck    = "shape-check"; // RowShapeSanity
    public const string SubQuestion   = "sub-question";
}
