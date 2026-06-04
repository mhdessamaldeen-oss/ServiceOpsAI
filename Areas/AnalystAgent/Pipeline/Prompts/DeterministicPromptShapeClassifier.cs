namespace AnalystAgent.Pipeline.Prompts;

using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Deterministic <see cref="IPromptShapeClassifier"/> — reads patterns from
/// <c>Configuration/Prompts/shape-classifier.json</c> at first use and matches the
/// (lowercased) question against each pattern in declared order. First match wins; if
/// none matches, the configured <c>default</c> shape is returned.
///
/// <para>Pattern types per shape:
/// <list type="bullet">
///   <item><b>keywords</b>: case-insensitive substring match on the lowercased question.</item>
///   <item><b>regex</b>: case-insensitive regex match (Compiled, lazy-built).</item>
/// </list>
/// Mixing both is fine; either type matching counts as a hit for that shape.</para>
///
/// <para>Patterns are loaded once on first <see cref="Classify"/> call and cached.
/// The file is not reload-on-change — restart to pick up edits (acceptable since the
/// classifier is registered as a singleton).</para>
/// </summary>
internal sealed class DeterministicPromptShapeClassifier : IPromptShapeClassifier
{
    private const string RelativePath = "Areas/AnalystAgent/Configuration/shape-classifier.json";

    private readonly ILogger<DeterministicPromptShapeClassifier> _logger;
    private readonly Lazy<LoadedConfig> _config;

    public DeterministicPromptShapeClassifier(ILogger<DeterministicPromptShapeClassifier> logger)
    {
        _logger = logger;
        _config = new Lazy<LoadedConfig>(Load, isThreadSafe: true);
    }

    public PromptShape Classify(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return _config.Value.Default;
        var lower = question.ToLowerInvariant();
        foreach (var pattern in _config.Value.Patterns)
        {
            foreach (var keyword in pattern.Keywords)
            {
                if (string.IsNullOrEmpty(keyword)) continue;
                if (lower.Contains(keyword)) return pattern.Shape;
            }
            foreach (var regex in pattern.Regexes)
            {
                if (regex.IsMatch(lower)) return pattern.Shape;
            }
        }
        return _config.Value.Default;
    }

    private LoadedConfig Load()
    {
        var fullPath = Path.Combine(Directory.GetCurrentDirectory(), RelativePath);
        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("[PromptShapeClassifier] config file not found at {Path}; defaulting all questions to FILTER.", fullPath);
            return new LoadedConfig(PromptShape.FILTER, Array.Empty<CompiledPattern>());
        }

        try
        {
            var raw = File.ReadAllText(fullPath);
            using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var root = doc.RootElement;

            var defaultName = root.TryGetProperty("default", out var d) ? d.GetString() : null;
            var defaultShape = ParseShape(defaultName) ?? PromptShape.FILTER;

            var compiled = new List<CompiledPattern>();
            if (root.TryGetProperty("patterns", out var patternsEl) && patternsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in patternsEl.EnumerateArray())
                {
                    var shapeName = p.TryGetProperty("shape", out var s) ? s.GetString() : null;
                    var shape = ParseShape(shapeName);
                    if (shape is null) continue;

                    var keywords = new List<string>();
                    if (p.TryGetProperty("keywords", out var kw) && kw.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var k in kw.EnumerateArray())
                        {
                            var kv = k.GetString();
                            if (!string.IsNullOrEmpty(kv)) keywords.Add(kv.ToLowerInvariant());
                        }
                    }

                    var regexes = new List<Regex>();
                    if (p.TryGetProperty("regex", out var rx) && rx.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in rx.EnumerateArray())
                        {
                            var rv = r.GetString();
                            if (string.IsNullOrEmpty(rv)) continue;
                            try
                            {
                                regexes.Add(new Regex(rv, RegexOptions.Compiled | RegexOptions.IgnoreCase));
                            }
                            catch (ArgumentException ex)
                            {
                                _logger.LogWarning(ex, "[PromptShapeClassifier] invalid regex '{Regex}' for shape {Shape}; skipping.", rv, shape);
                            }
                        }
                    }

                    compiled.Add(new CompiledPattern(shape.Value, keywords, regexes));
                }
            }

            _logger.LogInformation("[PromptShapeClassifier] loaded {Count} patterns; default={Default}.", compiled.Count, defaultShape);
            return new LoadedConfig(defaultShape, compiled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PromptShapeClassifier] failed to load patterns; defaulting all questions to FILTER.");
            return new LoadedConfig(PromptShape.FILTER, Array.Empty<CompiledPattern>());
        }
    }

    private static PromptShape? ParseShape(string? name) =>
        Enum.TryParse<PromptShape>(name, ignoreCase: true, out var s) ? s : null;

    private sealed record LoadedConfig(PromptShape Default, IReadOnlyList<CompiledPattern> Patterns);
    private sealed record CompiledPattern(PromptShape Shape, IReadOnlyList<string> Keywords, IReadOnlyList<Regex> Regexes);
}
