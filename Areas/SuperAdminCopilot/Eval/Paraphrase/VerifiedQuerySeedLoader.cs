namespace SuperAdminCopilot.Eval.Paraphrase;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Reads the existing <c>verified-queries.json</c> catalog and projects each entry into a
/// <see cref="ParaphraseSeed"/> the <see cref="OfflineParaphraseGenerator"/> can expand.
///
/// <para><b>Workflow:</b></para>
/// <list type="number">
///   <item>Load seeds from the verified-query catalog (this loader).</item>
///   <item>Pass to <see cref="IOfflineParaphraseGenerator.GenerateAsync"/> to produce a
///         <see cref="ParaphraseSuite"/>.</item>
///   <item>Serialise the suite to <c>Areas/SuperAdminCopilot/Eval/Suites/</c>.</item>
///   <item>Run the suite via <see cref="IParaphraseRobustnessRunner"/>.</item>
/// </list>
///
/// <para><b>Read-only by design.</b> This loader never modifies <c>verified-queries.json</c>
/// — the live file is sacred while the assessment runs. Generated paraphrases land in NEW
/// suite files only.</para>
/// </summary>
public static class VerifiedQuerySeedLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Load every entry from the verified-queries catalog as a <see cref="ParaphraseSeed"/>.
    /// Existing <c>questionVariants</c> are dropped — the generator produces fresh ones tagged
    /// with the perturbation category, which the variant list cannot supply.
    /// </summary>
    public static async Task<IReadOnlyList<ParaphraseSeed>> LoadFromFileAsync(
        string verifiedQueriesPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(verifiedQueriesPath))
            throw new FileNotFoundException($"verified-queries.json not found at {verifiedQueriesPath}");

        var json = await File.ReadAllTextAsync(verifiedQueriesPath, cancellationToken);
        var catalog = JsonSerializer.Deserialize<VerifiedCatalog>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to parse {verifiedQueriesPath}");

        var seeds = new List<ParaphraseSeed>(catalog.Queries?.Count ?? 0);
        for (var i = 0; i < (catalog.Queries?.Count ?? 0); i++)
        {
            var q = catalog.Queries![i];
            if (string.IsNullOrWhiteSpace(q.Question) || string.IsNullOrWhiteSpace(q.Sql))
                continue; // skip empty/incomplete entries

            seeds.Add(new ParaphraseSeed(
                ClusterId: BuildClusterId(q.Id, i),
                Question: q.Question,
                ExpectedSql: q.Sql,
                EntityFocus: q.Tags?.FirstOrDefault() ?? "unknown",
                Category: q.Shape ?? "general",
                Note: q.Description));
        }

        return seeds;
    }

    private static string BuildClusterId(string? id, int fallbackIndex) =>
        string.IsNullOrWhiteSpace(id)
            ? $"PR-VQ-{fallbackIndex:D3}"
            : $"PR-VQ-{Sanitise(id)}";

    private static string Sanitise(string id) =>
        new string(id.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());

    private sealed class VerifiedCatalog
    {
        [JsonPropertyName("queries")] public List<VerifiedEntry>? Queries { get; set; }
    }

    private sealed class VerifiedEntry
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("question")] public string? Question { get; set; }
        [JsonPropertyName("sql")] public string? Sql { get; set; }
        [JsonPropertyName("shape")] public string? Shape { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }
}
