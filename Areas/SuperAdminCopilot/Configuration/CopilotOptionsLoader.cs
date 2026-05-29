namespace SuperAdminCopilot.Configuration;

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Single-source loader for <see cref="CopilotOptions"/>. The file
/// <c>Areas/SuperAdminCopilot/Configuration/copilot-options.json</c> is the ONLY runtime
/// source for Copilot tuning knobs — this module no longer reads from the host's
/// appsettings.json.
///
/// <para>Resolution order (later layers overlay earlier):
/// (1) C# defaults declared on <see cref="CopilotOptions"/> →
/// (2) values bound from copilot-options.json (this loader) →
/// (3) Profile preset block from the same file (applied by <see cref="ApplyProfile"/>) →
/// (4) SystemSettings DB rows applied by <c>HostCopilotOptionsConfigurator</c>.</para>
///
/// <para>The file is JSON-with-comments for self-documentation. We round-trip through
/// <see cref="JsonNode"/> with comment-handling enabled, then feed the cleaned JSON into
/// a stream-based <see cref="IConfiguration"/> source. Reload-on-change is intentionally
/// not supported — most consumers are singletons that wouldn't observe the change
/// without a restart anyway; runtime tuning happens via the SystemSettings overlay.</para>
/// </summary>
internal static class CopilotOptionsLoader
{
    public const string RelativePath = "Areas/SuperAdminCopilot/Configuration/copilot-options.json";

    public static IConfiguration BuildConfiguration()
    {
        var fullPath = Path.Combine(Directory.GetCurrentDirectory(), RelativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Copilot settings file not found. Expected at: {fullPath}. " +
                $"This file is required — runtime tuning is no longer read from appsettings.json.",
                fullPath);
        }

        var raw = File.ReadAllText(fullPath);
        var node = JsonNode.Parse(
            raw,
            nodeOptions: null,
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        if (node is null)
        {
            throw new InvalidDataException($"copilot-options.json parsed to null. File: {fullPath}");
        }

        var cleanedJson = node.ToJsonString();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(cleanedJson));
        return new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
    }

    /// <summary>
    /// Apply the Profile preset overlay from copilot-options.json onto the bound options.
    /// Called as an <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{TOptions}"/>
    /// step BEFORE the SystemSettings DB overlay, so operator changes still win.
    /// </summary>
    public static void ApplyProfile(IConfiguration copilotConfig, CopilotOptions options)
    {
        var section = copilotConfig.GetSection(CopilotOptions.SectionName);
        var profileName = section["Profile"];
        if (string.IsNullOrWhiteSpace(profileName)) return;

        var profileSection = section.GetSection($"Profiles:{profileName}");
        if (!profileSection.Exists()) return;

        var icc = profileSection["IntentClassifierDecisiveConfidence"];
        if (double.TryParse(icc, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var iccVal))
            options.IntentClassifierDecisiveConfidence = iccVal;

        var vq = profileSection["VerifiedQueryMinSimilarity"];
        if (double.TryParse(vq, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var vqVal))
            options.VerifiedQueryMinSimilarity = vqVal;

        var pq = profileSection["PastQuestionRagMinSimilarity"];
        if (double.TryParse(pq, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pqVal))
            options.PastQuestionRagMinSimilarity = pqVal;

        // Planner capability tier overlay. Profile presets advertise the model strength
        // the operator is running ("Weak" for local 7B, "Medium" for local 32B, "Strong"
        // for cloud Claude / GPT-4o). SpecRepair phases use this to auto-skip the
        // language-pattern crutches once the planner is strong enough to handle the
        // intent natively. A typo here silently leaves Weak in effect on a cloud profile —
        // exactly the bug that's expensive to catch in benchmarks. Fail-fast instead.
        var tier = profileSection["PlannerCapabilityTier"];
        if (!string.IsNullOrWhiteSpace(tier))
        {
            if (System.Enum.TryParse<PlannerCapabilityTier>(tier, ignoreCase: true, out var tierVal))
                options.PlannerCapabilityTier = tierVal;
            else
                throw new InvalidDataException(
                    $"Profile '{profileName}' has invalid PlannerCapabilityTier '{tier}'. " +
                    $"Valid values: {string.Join(", ", System.Enum.GetNames<PlannerCapabilityTier>())}.");
        }
    }
}
