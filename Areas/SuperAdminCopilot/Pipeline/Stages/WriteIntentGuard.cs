namespace SuperAdminCopilot.Pipeline.Stages;

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperAdminCopilot.Configuration;

/// <summary>
/// Deterministic gate that refuses questions expressing a WRITE intent before they reach the
/// LLM. The pipeline is read-only by design — the read-only executor would reject DML at the
/// end anyway, but by then we've burned an LLM call to extract a spec and another to compile
/// it. This gate stops write-shaped questions in milliseconds.
/// </summary>
/// <remarks>
/// <para>Universal, entity-agnostic: the check is on the VERB, not the noun. Multi-language
/// (English + Arabic by default). Verb patterns live in <c>write-intent-verbs.json</c> —
/// operators add a verb or a new language by editing the file; no recompile. Compiled regexes
/// are cached per options snapshot and rebuilt automatically on options reload via
/// <see cref="IOptionsMonitor{TOptions}.OnChange"/>.</para>
/// </remarks>
public interface IWriteIntentGuard
{
    /// <summary>Returns a refusal reason when the question expresses a write intent; null otherwise.</summary>
    WriteIntentResult? Check(string question);
}

/// <summary>Outcome of <see cref="IWriteIntentGuard.Check"/>. <see cref="MatchedVerb"/> carries
/// the substring that tripped the rule so the trace can show "refused because of: delete".</summary>
public sealed record WriteIntentResult(string Reason, string MatchedVerb, string Language);

internal sealed class WriteIntentGuard : IWriteIntentGuard, IDisposable
{
    private readonly IOptionsMonitor<WriteIntentOptions> _options;
    private readonly ILogger<WriteIntentGuard> _logger;
    private readonly IDisposable? _changeSubscription;

    /// <summary>
    /// Compiled regex cache. Lazily populated on first <see cref="Check"/> call and rebuilt
    /// when the JSON file changes (operator added a language / verb). Volatile field swap is
    /// atomic — concurrent readers always see either the old or the new compiled set, never a
    /// half-built state.
    /// </summary>
    private volatile CompiledRules _rules = CompiledRules.Empty;

    public WriteIntentGuard(IOptionsMonitor<WriteIntentOptions> options, ILogger<WriteIntentGuard> logger)
    {
        _options = options;
        _logger = logger;
        _rules = Compile(options.CurrentValue);
        // Recompile on JSON change so operators don't need to restart the host after editing
        // write-intent-verbs.json. Same pattern as CopilotTextCatalog's reload behaviour.
        _changeSubscription = options.OnChange(opts =>
        {
            try
            {
                _rules = Compile(opts);
                _logger.LogInformation("[WriteIntentGuard] Reloaded {Count} verb pattern(s) across {Languages} language(s).",
                    _rules.TotalPatterns, _rules.LanguageCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WriteIntentGuard] Failed to recompile patterns on options change. Keeping the previous compiled set.");
            }
        });
    }

    public WriteIntentResult? Check(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        var rules = _rules;
        foreach (var rule in rules.Rules)
        {
            var m = rule.Pattern.Match(question);
            if (m.Success)
            {
                return new WriteIntentResult(
                    Reason: $"This pipeline is read-only. The question expresses a '{rule.Verb}' intent ({rule.Language}) which is not supported.",
                    MatchedVerb: m.Value,
                    Language: rule.Language);
            }
        }
        return null;
    }

    public void Dispose() => _changeSubscription?.Dispose();

    private static CompiledRules Compile(WriteIntentOptions options)
    {
        if (options?.Languages is not { Count: > 0 }) return CompiledRules.Empty;
        var compiled = new List<CompiledRule>(capacity: 32);
        foreach (var lang in options.Languages)
        {
            if (lang.Verbs is null) continue;
            foreach (var verb in lang.Verbs)
            {
                if (string.IsNullOrWhiteSpace(verb.Pattern)) continue;
                Regex regex;
                try
                {
                    regex = new Regex(verb.Pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                }
                catch (ArgumentException ex)
                {
                    // One bad regex shouldn't break the gate. Skip it; the host log captures
                    // the offending entry so the operator can fix the JSON.
                    System.Diagnostics.Debug.WriteLine($"[WriteIntentGuard] Skipping invalid pattern '{verb.Pattern}' ({lang.Locale}): {ex.Message}");
                    continue;
                }
                compiled.Add(new CompiledRule(regex, verb.Verb ?? "", lang.Locale ?? ""));
            }
        }
        return new CompiledRules(compiled, options.Languages.Count);
    }

    private sealed record CompiledRule(Regex Pattern, string Verb, string Language);

    private sealed class CompiledRules
    {
        public static readonly CompiledRules Empty = new(Array.Empty<CompiledRule>(), 0);
        public CompiledRules(IReadOnlyList<CompiledRule> rules, int languageCount)
        {
            Rules = rules;
            LanguageCount = languageCount;
        }
        public IReadOnlyList<CompiledRule> Rules { get; }
        public int LanguageCount { get; }
        public int TotalPatterns => Rules.Count;
    }
}
