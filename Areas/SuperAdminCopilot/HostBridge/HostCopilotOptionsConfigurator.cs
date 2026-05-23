namespace SuperAdminCopilot.HostBridge;

using System.Globalization;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;
using SuperAdminCopilot.Schema;

/// <summary>
/// Host bridge that overlays admin-edited SystemSettings onto appsettings-bound CopilotOptions.
/// This keeps the reusable Copilot engine free of the host's settings table while still letting
/// the current app expose a Copilot Settings tab. Values are applied when options are created;
/// the app should be restarted after changing startup-scoped settings used by singletons.
/// </summary>
internal sealed class HostCopilotOptionsConfigurator : IPostConfigureOptions<CopilotOptions>
{
    private readonly IServiceProvider _services;
    private readonly ILogger<HostCopilotOptionsConfigurator> _logger;

    public HostCopilotOptionsConfigurator(
        IServiceProvider services,
        ILogger<HostCopilotOptionsConfigurator> logger)
    {
        _services = services;
        _logger = logger;
    }

    public void PostConfigure(string? name, CopilotOptions options)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = db.SystemSettings
                .AsNoTracking()
                .Where(s => s.Key.StartsWith("Copilot"))
                .ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase);

            Apply(settings, options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SuperAdminCopilot] Failed to overlay Copilot SystemSettings; using appsettings/default options.");
        }
    }

    private static void Apply(IReadOnlyDictionary<string, string> settings, CopilotOptions options)
    {
        if (TryGet(settings, SettingKeys.CopilotTableExposureMode, out var exposure)
            && Enum.TryParse<TableExposureMode>(exposure, ignoreCase: true, out var mode))
            options.TableExposureMode = mode;

        SetList(settings, SettingKeys.CopilotBlockedTables, v => options.BlockedTables = v);
        SetList(settings, SettingKeys.CopilotBlockedTablePatterns, v => options.BlockedTablePatterns = v);
        SetList(settings, SettingKeys.CopilotBlockedColumns, v => options.BlockedColumns = v);
        SetList(settings, SettingKeys.CopilotSensitiveColumns, v => options.SensitiveColumns = v);

        SetInt(settings, SettingKeys.CopilotRetrieverTopK, 1, 50, v => options.RetrieverTopK = v);
        if (TryGet(settings, SettingKeys.CopilotSchemaPromptStrategy, out var promptStrategy)
            && Enum.TryParse<SchemaPromptStrategy>(promptStrategy, ignoreCase: true, out var strategy))
            options.SchemaPromptStrategy = strategy;

        SetBool(settings, SettingKeys.CopilotUseVectorRetriever, v => options.UseVectorRetriever = v);
        SetBool(settings, SettingKeys.CopilotUsePastQuestionRag, v => options.UsePastQuestionRag = v);
        SetInt(settings, SettingKeys.CopilotFewShotTopK, 0, 20, v => options.FewShotTopK = v);
        SetBool(settings, SettingKeys.CopilotUseLlmExplainer, v => options.UseLlmExplainer = v);
        SetInt(settings, SettingKeys.CopilotMaxLlmCallsPerQuestion, 0, 20, v => options.MaxLlmCallsPerQuestion = v);
        SetInt(settings, SettingKeys.CopilotMaxSelfCorrectionRetries, 0, 5, v => options.MaxSelfCorrectionRetries = v);
        SetInt(settings, SettingKeys.CopilotLlmCallTimeoutSeconds, 5, 600, v => options.LlmCallTimeoutSeconds = v);
        SetInt(settings, SettingKeys.CopilotMaxQuestionWallClockSeconds, 0, 600, v => options.MaxQuestionWallClockSeconds = v);
        SetInt(settings, SettingKeys.CopilotMaxRows, 1, 100_000, v => options.MaxRows = v);
        SetInt(settings, SettingKeys.CopilotCommandTimeoutSeconds, 1, 600, v => options.CommandTimeoutSeconds = v);
        SetBool(settings, SettingKeys.CopilotEnableSchemaIntrospection, v => options.EnableSchemaIntrospection = v);
        SetBool(settings, SettingKeys.CopilotRestrictMetadataToConfiguredEntities, v => options.RestrictMetadataToConfiguredEntities = v);
        SetBool(settings, SettingKeys.CopilotEnableResultCache, v => options.EnableResultCache = v);
        SetInt(settings, SettingKeys.CopilotResultCacheTtlSeconds, 0, 86400, v => options.ResultCacheTtlSeconds = v);
        SetBool(settings, SettingKeys.CopilotEnableCostGate, v => options.EnableCostGate = v);
        SetDouble(settings, SettingKeys.CopilotMaxEstimatedQueryCost, 0.01, 100_000, v => options.MaxEstimatedQueryCost = v);
        SetDouble(settings, SettingKeys.CopilotAmbiguityClarificationThreshold, 0.0, 1.0, v => options.AmbiguityClarificationThreshold = v);
        SetDouble(settings, SettingKeys.CopilotResolverMinConfidence, 0.0, 1.0, v => options.ResolverMinConfidence = v);
    }

    private static bool TryGet(IReadOnlyDictionary<string, string> settings, string key, out string value) =>
        settings.TryGetValue(key, out value!) && !string.IsNullOrWhiteSpace(value);

    private static void SetBool(IReadOnlyDictionary<string, string> settings, string key, Action<bool> set)
    {
        if (TryGet(settings, key, out var raw) && bool.TryParse(raw, out var value)) set(value);
    }

    private static void SetInt(IReadOnlyDictionary<string, string> settings, string key, int min, int max, Action<int> set)
    {
        if (!TryGet(settings, key, out var raw)) return;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            set(Math.Clamp(value, min, max));
    }

    private static void SetDouble(IReadOnlyDictionary<string, string> settings, string key, double min, double max, Action<double> set)
    {
        if (!TryGet(settings, key, out var raw)) return;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            set(Math.Clamp(value, min, max));
    }

    private static void SetList(IReadOnlyDictionary<string, string> settings, string key, Action<List<string>> set)
    {
        if (!TryGet(settings, key, out var raw)) return;
        var values = raw
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        set(values);
    }
}
