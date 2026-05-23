namespace SuperAdminCopilot.Internal;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> instances for the copilot area. Two profiles
/// cover every read path in the codebase:
/// <list type="bullet">
///   <item><see cref="Lenient"/> — for parsing user-/admin-authored JSON files and LLM output.
///   Case-insensitive property names, comments skipped, trailing commas allowed, string enums.</item>
///   <item><see cref="CompactWrite"/> — for serializing specs into prompts / trace technical fields.
///   Drops nulls, no indentation.</item>
/// </list>
/// Files that need a one-off variant (e.g. <c>CachingExecutor.ParamHashOpts</c> for hashing) can
/// still keep their own; this class is the home for the common case so nine near-identical
/// singletons stop drifting.
/// </summary>
internal static class CopilotJson
{
    public static readonly JsonSerializerOptions Lenient = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions CompactWrite = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
