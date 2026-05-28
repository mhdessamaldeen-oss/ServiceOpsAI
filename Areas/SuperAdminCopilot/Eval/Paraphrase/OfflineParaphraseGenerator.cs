namespace SuperAdminCopilot.Eval.Paraphrase;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SuperAdminCopilot.Abstractions;

/// <summary>
/// Offline batch generator that takes a small seed set of verified Q→SQL pairs and uses an
/// <see cref="ILlmClient"/> to produce many varied phrasings per seed. The output is a
/// fully-formed <see cref="ParaphraseSuite"/> with a FLAT <c>Scenarios[]</c> array — same
/// shape as every other suite under <c>Configuration/QuestionSuites/</c> so it drops in
/// without forking the loader.
///
/// <para><b>Cluster identity is per-scenario.</b> Each seed produces N+1 scenarios sharing
/// the same <see cref="ParaphraseScenario.ClusterId"/>: one <c>base</c> scenario echoing
/// the seed verbatim, plus N LLM-generated variations. The runner groups by
/// <c>ClusterId</c> at report-build time.</para>
///
/// <para><b>Pattern reference:</b> RingSQL (arXiv 2601.05451) — schema-independent templates +
/// LLM paraphrasing. Here the "templates" are the verified queries themselves; the LLM only
/// adds linguistic variety. SQL correctness is guaranteed by the seed; we only ask the LLM to
/// vary the natural-language surface.</para>
///
/// <para><b>Prompt design:</b> the prompt does NOT enumerate perturbation categories. It
/// asks the LLM to produce varied phrasings and label each variation with whatever
/// dimension it varied. The taxonomy emerges from the data; the report uses the labels
/// the LLM chose. Avoiding enumeration is deliberate — see
/// <c>Pipeline/Stages/Decomposed/README.md</c> for the principle.</para>
///
/// <para><b>Model choice:</b> quality matters more than cost here. Bind the
/// <see cref="ILlmClient"/> to your frontier model (Claude Sonnet, GPT-4-class) when
/// invoking — local 7B models produce bland paraphrases that don't actually stress-test
/// the pipeline.</para>
/// </summary>
public interface IOfflineParaphraseGenerator
{
    /// <summary>Generate paraphrases for every seed and return a complete suite.</summary>
    Task<ParaphraseSuite> GenerateAsync(
        IReadOnlyList<ParaphraseSeed> seeds,
        ParaphraseGenerationOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>One seed = one cluster's worth of paraphrases to generate. The canonical
/// question becomes the <c>base</c> scenario; the LLM produces all other scenarios in
/// the cluster.</summary>
public sealed record ParaphraseSeed(
    string ClusterId,
    string Question,
    string ExpectedSql,
    string Category,
    string EntityFocus = "",
    int? ExpectedMinRows = null,
    int? ExpectedMaxRows = null,
    string? Note = null);

/// <summary>Per-run options. <see cref="ParaphraseCount"/> controls breadth-vs-cost: 5 =
/// small suite, 15 = thorough. Higher counts produce more variety but cost more LLM tokens.</summary>
public sealed record ParaphraseGenerationOptions(
    string SuiteName,
    string SuiteDescription,
    int ParaphraseCount,
    string? PromptsFilePath = null);

internal sealed class OfflineParaphraseGenerator : IOfflineParaphraseGenerator
{
    private readonly ILlmClient _llm;
    private readonly ILogger<OfflineParaphraseGenerator> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public OfflineParaphraseGenerator(
        ILlmClient llm,
        ILogger<OfflineParaphraseGenerator> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    public async Task<ParaphraseSuite> GenerateAsync(
        IReadOnlyList<ParaphraseSeed> seeds,
        ParaphraseGenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        if (seeds is null || seeds.Count == 0)
            throw new ArgumentException("Seeds must contain at least one entry", nameof(seeds));

        var promptConfig = await LoadPromptConfigAsync(options.PromptsFilePath, cancellationToken);

        var scenarios = new List<ParaphraseScenario>(seeds.Count * (options.ParaphraseCount + 1));
        for (var i = 0; i < seeds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seed = seeds[i];
            _logger.LogInformation(
                "[ParaphraseGen] {Index}/{Total} cluster {ClusterId} — '{Question}'",
                i + 1, seeds.Count, seed.ClusterId, seed.Question);

            // Always emit the seed itself as the 'base' scenario, then LLM-generated variants.
            scenarios.Add(BuildBaseScenario(seed));
            try
            {
                var generated = await GenerateForSeedAsync(seed, options, promptConfig, cancellationToken);
                scenarios.AddRange(generated);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[ParaphraseGen] cluster {ClusterId} generation failed — base-only scenario emitted",
                    seed.ClusterId);
            }
        }

        return new ParaphraseSuite
        {
            Name = options.SuiteName,
            Version = "1.0",
            Description = options.SuiteDescription,
            Scenarios = scenarios,
        };
    }

    private async Task<IReadOnlyList<ParaphraseScenario>> GenerateForSeedAsync(
        ParaphraseSeed seed,
        ParaphraseGenerationOptions options,
        PromptConfig promptConfig,
        CancellationToken cancellationToken)
    {
        var userPrompt = promptConfig.UserPromptTemplate
            .Replace("{{question}}", seed.Question, StringComparison.Ordinal)
            .Replace("{{sql}}", seed.ExpectedSql, StringComparison.Ordinal)
            .Replace("{{count}}", options.ParaphraseCount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var raw = await _llm.GenerateJsonAsync(promptConfig.SystemPrompt, userPrompt, cancellationToken);
        var parsed = TryParseResponse(raw, seed.ClusterId);

        var clusterSuffix = ExtractClusterSuffix(seed.ClusterId);
        var scenarios = new List<ParaphraseScenario>(parsed.Count);
        var index = 1;
        foreach (var p in parsed)
        {
            if (string.IsNullOrWhiteSpace(p.Question)) continue;
            scenarios.Add(new ParaphraseScenario
            {
                Code = $"PR-{clusterSuffix}-{index:D2}-{Sanitise(p.Label)}",
                Question = p.Question.Trim(),
                Category = seed.Category,
                Difficulty = "Medium",
                ExpectedIntent = "DataQuery",
                EntityFocus = seed.EntityFocus,
                ExpectedSql = seed.ExpectedSql,
                ExpectedMinRows = seed.ExpectedMinRows,
                ExpectedMaxRows = seed.ExpectedMaxRows,
                MaxLatencyMs = 180000,
                ClusterId = seed.ClusterId,
                Perturbation = string.IsNullOrWhiteSpace(p.Label) ? "variation" : p.Label.Trim(),
                Language = string.IsNullOrWhiteSpace(p.Language) ? DetectLanguage(p.Question) : p.Language,
            });
            index++;
        }
        return scenarios;
    }

    private static ParaphraseScenario BuildBaseScenario(ParaphraseSeed seed) => new()
    {
        Code = $"PR-{ExtractClusterSuffix(seed.ClusterId)}-00-base",
        Question = seed.Question,
        Category = seed.Category,
        Difficulty = "Medium",
        ExpectedIntent = "DataQuery",
        EntityFocus = seed.EntityFocus,
        ExpectedSql = seed.ExpectedSql,
        ExpectedMinRows = seed.ExpectedMinRows,
        ExpectedMaxRows = seed.ExpectedMaxRows,
        MaxLatencyMs = 180000,
        ClusterId = seed.ClusterId,
        Perturbation = "base",
        Language = DetectLanguage(seed.Question),
        Note = seed.Note,
    };

    private List<ParsedParaphrase> TryParseResponse(string rawJson, string clusterIdForLog)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            _logger.LogWarning("[ParaphraseGen] cluster {ClusterId} — LLM returned empty response", clusterIdForLog);
            return new List<ParsedParaphrase>();
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<ParsedEnvelope>(rawJson, JsonOptions);
            return envelope?.Paraphrases ?? new List<ParsedParaphrase>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "[ParaphraseGen] cluster {ClusterId} — failed to parse JSON response. Raw: {Raw}",
                clusterIdForLog, Truncate(rawJson, 200));
            return new List<ParsedParaphrase>();
        }
    }

    private async Task<PromptConfig> LoadPromptConfigAsync(string? overridePath, CancellationToken ct)
    {
        var path = overridePath ?? Path.Combine(
            AppContext.BaseDirectory, "Areas", "SuperAdminCopilot", "Eval", "Paraphrase",
            "paraphrase-expansion-prompts.json");

        if (!File.Exists(path))
        {
            var alt = Path.Combine(Directory.GetCurrentDirectory(),
                "Areas", "SuperAdminCopilot", "Eval", "Paraphrase",
                "paraphrase-expansion-prompts.json");
            if (File.Exists(alt)) path = alt;
        }

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"paraphrase-expansion-prompts.json not found at '{path}'. Pass an explicit PromptsFilePath in options to override.");

        var json = await File.ReadAllTextAsync(path, ct);
        var parsed = JsonSerializer.Deserialize<PromptConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to parse {path}");
        if (string.IsNullOrWhiteSpace(parsed.SystemPrompt) || string.IsNullOrWhiteSpace(parsed.UserPromptTemplate))
            throw new InvalidOperationException($"{path} missing systemPrompt or userPromptTemplate");
        return parsed;
    }

    private static string ExtractClusterSuffix(string clusterId)
    {
        var dash = clusterId.LastIndexOf('-');
        return dash >= 0 && dash + 1 < clusterId.Length ? clusterId[(dash + 1)..] : clusterId;
    }

    private static string DetectLanguage(string text)
    {
        if (string.IsNullOrEmpty(text)) return "en";
        var hasArabic = text.Any(c => c >= 0x0600 && c <= 0x06FF);
        var hasLatin = text.Any(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
        return (hasArabic, hasLatin) switch
        {
            (true, true) => "mixed",
            (true, false) => "ar",
            _ => "en",
        };
    }

    private static string Sanitise(string? label) =>
        string.IsNullOrWhiteSpace(label)
            ? "variation"
            : new string(label.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private sealed class PromptConfig
    {
        [JsonPropertyName("systemPrompt")] public string SystemPrompt { get; set; } = "";
        [JsonPropertyName("userPromptTemplate")] public string UserPromptTemplate { get; set; } = "";
    }

    private sealed class ParsedEnvelope
    {
        [JsonPropertyName("paraphrases")] public List<ParsedParaphrase> Paraphrases { get; set; } = new();
    }

    private sealed class ParsedParaphrase
    {
        [JsonPropertyName("question")] public string Question { get; set; } = "";
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("language")] public string Language { get; set; } = "";
    }
}
