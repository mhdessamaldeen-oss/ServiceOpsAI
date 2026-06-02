namespace SuperAdminCopilot.Pipeline.Stages.Decomposed;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

/// <summary>
/// Loads the externalized prompt config for the Phase-2 decomposed stages from
/// <c>decomposed-prompts.json</c>. Cached per-process; the file is small and rarely changes
/// at runtime so a one-shot read is fine. If someone edits the file while the host is
/// running, call <see cref="Reload"/> to pick up the change.
///
/// <para>Keeping prompts in JSON (not in code) lets you iterate on phrasing — especially
/// the worked examples for bracket grammar — without recompiling. This is the same pattern
/// the existing <see cref="CopilotTextCatalog"/> uses for the live pipeline's prompts.</para>
/// </summary>
public interface IDecomposedPromptProvider
{
    (string SystemPrompt, string UserPromptTemplate) GetStructuralCueParser();
    (string SystemPrompt, string UserPromptTemplate) GetSchemaLinker();
    (string SystemPrompt, string UserPromptTemplate) GetQuerySpecComposer();

    /// <summary>Force a re-read of the JSON file. Call after editing prompts in dev.</summary>
    void Reload();
}

internal sealed class DecomposedPromptProvider : IDecomposedPromptProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly ILogger<DecomposedPromptProvider> _logger;
    private readonly string _filePath;
    private readonly object _gate = new();
    private DecomposedPromptConfig? _cache;

    public DecomposedPromptProvider(ILogger<DecomposedPromptProvider> logger, string? overridePath = null)
    {
        _logger = logger;
        _filePath = overridePath ?? ResolveDefaultPath();
    }

    public (string SystemPrompt, string UserPromptTemplate) GetStructuralCueParser()
    {
        var c = Ensure().StructuralCueParser
            ?? throw new InvalidOperationException("decomposed-prompts.json missing 'structuralCueParser' section");
        return (c.SystemPrompt, c.UserPromptTemplate);
    }

    public (string SystemPrompt, string UserPromptTemplate) GetSchemaLinker()
    {
        var c = Ensure().SchemaLinker
            ?? throw new InvalidOperationException("decomposed-prompts.json missing 'schemaLinker' section");
        return (c.SystemPrompt, c.UserPromptTemplate);
    }

    public (string SystemPrompt, string UserPromptTemplate) GetQuerySpecComposer()
    {
        var c = Ensure().QuerySpecComposer
            ?? throw new InvalidOperationException("decomposed-prompts.json missing 'querySpecComposer' section");
        return (c.SystemPrompt, c.UserPromptTemplate);
    }

    public void Reload()
    {
        lock (_gate) _cache = null;
    }

    private DecomposedPromptConfig Ensure()
    {
        if (_cache is not null) return _cache;
        lock (_gate)
        {
            if (_cache is not null) return _cache;
            _cache = LoadFromDisk();
            return _cache;
        }
    }

    private DecomposedPromptConfig LoadFromDisk()
    {
        if (!File.Exists(_filePath))
            throw new FileNotFoundException(
                $"decomposed-prompts.json not found at '{_filePath}'. Place the file next to the Decomposed stages or pass an override path.");

        var json = File.ReadAllText(_filePath);
        var parsed = JsonSerializer.Deserialize<DecomposedPromptConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to parse {_filePath}");
        _logger.LogInformation("[DecomposedPromptProvider] loaded prompts from {Path}", _filePath);
        return parsed;
    }

    private static string ResolveDefaultPath()
    {
        var primary = Path.Combine(AppContext.BaseDirectory,
            "Areas", "SuperAdminCopilot", "Pipeline", "Stages", "Decomposed",
            "decomposed-prompts.json");
        if (File.Exists(primary)) return primary;

        var fallback = Path.Combine(Directory.GetCurrentDirectory(),
            "Areas", "SuperAdminCopilot", "Pipeline", "Stages", "Decomposed",
            "decomposed-prompts.json");
        return fallback;
    }

    private sealed class DecomposedPromptConfig
    {
        [JsonPropertyName("structuralCueParser")] public PromptPair? StructuralCueParser { get; set; }
        [JsonPropertyName("schemaLinker")] public PromptPair? SchemaLinker { get; set; }
        [JsonPropertyName("querySpecComposer")] public PromptPair? QuerySpecComposer { get; set; }
    }

    private sealed class PromptPair
    {
        [JsonPropertyName("systemPrompt")] public string SystemPrompt { get; set; } = "";
        [JsonPropertyName("userPromptTemplate")] public string UserPromptTemplate { get; set; } = "";
    }
}
