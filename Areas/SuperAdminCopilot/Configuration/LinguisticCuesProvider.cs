namespace SuperAdminCopilot.Configuration;

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Loads <c>linguistic-cues.json</c> at construction, compiles all regexes, and caches.
/// Singleton — one instance per app lifetime. The file is read once; subsequent edits require
/// app restart (consistent with how copilot-options.json works).
/// </summary>
internal sealed class LinguisticCuesProvider : ILinguisticCuesProvider
{
    private static readonly RegexOptions DefaultOptions =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    public CompiledLinguisticCues Compiled { get; }

    public LinguisticCuesProvider(ILogger<LinguisticCuesProvider> logger)
    {
        var path = LocateConfigFile();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            logger.LogWarning("[LinguisticCuesProvider] linguistic-cues.json NOT FOUND. Pipeline will run with empty cue set; cue-based phases become no-ops.");
            Compiled = CompiledLinguisticCues.Empty;
            return;
        }

        try
        {
            var raw = File.ReadAllText(path);
            // Tolerate trailing commas and // comments — copilot-options.json uses the same loader idiom.
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var dto = JsonSerializer.Deserialize<LinguisticCues>(raw, options);
            if (dto is null)
            {
                logger.LogWarning("[LinguisticCuesProvider] {Path} parsed to null. Using empty cue set.", path);
                Compiled = CompiledLinguisticCues.Empty;
                return;
            }

            Compiled = Compile(dto);
            logger.LogInformation(
                "[LinguisticCuesProvider] Loaded {Locales} locale(s) from {Path}.",
                Compiled.Locales.Count, path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LinguisticCuesProvider] failed to load {Path}. Falling back to empty cue set.", path);
            Compiled = CompiledLinguisticCues.Empty;
        }
    }

    /// <summary>
    /// Public for test injection. Compiles a typed <see cref="LinguisticCues"/> DTO to its
    /// regex-compiled <see cref="CompiledLinguisticCues"/> form. Pure function — no I/O.
    /// </summary>
    public static CompiledLinguisticCues Compile(LinguisticCues dto)
    {
        var compiledLocales = new Dictionary<string, CompiledLocaleCues>(StringComparer.OrdinalIgnoreCase);
        foreach (var (locale, raw) in dto.Locales)
        {
            compiledLocales[locale] = new CompiledLocaleCues
            {
                Temporal = CompileTemporal(raw.Temporal),
                AbsenceRegex     = CompileAlternation(raw.Absence,    wholeWord: true),
                AllTimeRegex     = CompileAlternation(raw.AllTime,    wholeWord: true),
                DistinctRegex    = CompileAlternation(raw.Distinct,   wholeWord: false),  // these include spaces
                NegationRegex    = CompileAlternation(raw.Negation,   wholeWord: false),
                RecencyDescRegex = CompileAlternation(raw.Recency?.Desc, wholeWord: true),
                RecencyAscRegex  = CompileAlternation(raw.Recency?.Asc,  wholeWord: true),
                SuperlativeMaxRegex    = CompileAlternation(raw.Superlative?.Max,    wholeWord: true),
                SuperlativeMinRegex    = CompileAlternation(raw.Superlative?.Min,    wholeWord: true),
                SuperlativeTopRegex    = CompileAlternation(raw.Superlative?.Top,    wholeWord: true),
                SuperlativeBottomRegex = CompileAlternation(raw.Superlative?.Bottom, wholeWord: true),
                RangeBetween = CompileRegexList(raw.Range?.Between),
                RangeGt      = CompileRegexList(raw.Range?.Gt),
                RangeGte     = CompileRegexList(raw.Range?.Gte),
                RangeLt      = CompileRegexList(raw.Range?.Lt),
                RangeLte     = CompileRegexList(raw.Range?.Lte),
                RangeEq      = CompileRegexList(raw.Range?.Eq),
            };
        }
        return new CompiledLinguisticCues
        {
            Version = dto.Version,
            Locales = compiledLocales,
        };
    }

    /// <summary>Compile a list of temporal cues to typed regex objects, preserving Start/End/Label.</summary>
    private static IReadOnlyList<CompiledTemporalCue> CompileTemporal(List<TemporalCue>? cues)
    {
        if (cues is null || cues.Count == 0) return System.Array.Empty<CompiledTemporalCue>();
        var list = new List<CompiledTemporalCue>(cues.Count);
        foreach (var c in cues)
        {
            if (string.IsNullOrWhiteSpace(c.Pattern) || string.IsNullOrWhiteSpace(c.Start)) continue;
            try
            {
                var rx = new Regex(c.Pattern, DefaultOptions);
                list.Add(new CompiledTemporalCue(rx, c.Start, c.End, string.IsNullOrEmpty(c.Op) ? "gte" : c.Op, c.Label ?? ""));
            }
            catch (ArgumentException)
            {
                // Skip malformed regex; SchemaConfigValidator (Day 4) will fail-fast at startup.
            }
        }
        return list;
    }

    /// <summary>
    /// Compile a list of plain phrases (or regex fragments) into a single alternation regex.
    /// When <paramref name="wholeWord"/> is true, phrases are wrapped with <c>\b…\b</c>; when
    /// false, the phrases may contain spaces and are emitted as-is (case-insensitive substring).
    /// </summary>
    private static Regex? CompileAlternation(List<string>? phrases, bool wholeWord)
    {
        if (phrases is null || phrases.Count == 0) return null;
        var parts = new List<string>(phrases.Count);
        foreach (var p in phrases)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            // If the entry already looks like a regex (contains escape, char class, group), emit verbatim.
            var looksLikeRegex = p.Contains('\\') || p.Contains('[') || p.Contains('(') || p.Contains('?');
            var part = looksLikeRegex ? p : Regex.Escape(p);
            if (wholeWord && !looksLikeRegex) part = $@"\b{part}\b";
            parts.Add(part);
        }
        if (parts.Count == 0) return null;
        var pattern = "(?:" + string.Join("|", parts) + ")";
        try { return new Regex(pattern, DefaultOptions); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Compile a list of full regex patterns to <see cref="Regex"/> objects, skipping malformed entries.</summary>
    private static IReadOnlyList<Regex> CompileRegexList(List<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0) return System.Array.Empty<Regex>();
        var list = new List<Regex>(patterns.Count);
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try { list.Add(new Regex(p, DefaultOptions)); }
            catch (ArgumentException) { /* swallow — Day-4 validator surfaces these */ }
        }
        return list;
    }

    /// <summary>
    /// Locate the config file. Looks first in the running app's content root + Areas path; falls
    /// back to a probe-relative path when invoked from a unit test. Returns null when not found.
    /// </summary>
    private static string? LocateConfigFile()
    {
        var rel = Path.Combine("Areas", "SuperAdminCopilot", "Configuration", "linguistic-cues.json");
        // Probe upwards from AppContext.BaseDirectory — handles both run-from-bin and run-from-test scenarios.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, rel);
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        // Also try Directory.GetCurrentDirectory()
        var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), rel);
        return File.Exists(cwdCandidate) ? cwdCandidate : null;
    }
}
