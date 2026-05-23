namespace SuperAdminCopilot.Semantic;

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;
using SuperAdminCopilot.Configuration;

/// <summary>
/// Host-free port of the legacy <c>TemporalExpressionParser</c>. Wraps Microsoft's
/// <c>Microsoft.Recognizers.Text.DateTime</c> recognizer to extract structured temporal scopes
/// from natural language. The QuestionShapeEngine's <c>TemporalScopeQuestionShape</c> handles the
/// common cases via regex (today / yesterday / last N days / this week / etc.); this parser is
/// the broader fallback for the rich expressions the regex doesn't cover ("Q1 2026", "between
/// Jan 15 and Feb 28", "the day before yesterday", "two weeks ago", multi-locale phrases).
///
/// <para><b>Used by</b>:
///   • <see cref="Pipeline.Stages.IntentNormalizer"/> — when the planner emitted no temporal
///     filter but the question contains a recognizable expression, the normalizer can inject
///     one (Phase D follow-up wiring; the parser is registered here so consumers can opt in).
///   • Future shape engine extensions for pattern coverage the regex misses.
///   Doesn't replace the existing regex shape — that's the cheap hot path. This is the fallback
///   for richer expressions when needed.</para>
///
/// <para>All output uses <see cref="TemporalScope"/>, a host-free record. We deliberately don't
/// import the host's <c>TemporalFilter</c>/<c>TemporalType</c> here so the new copilot stays
/// decoupled from the legacy types we're going to delete in Phase E.</para>
/// </summary>
public interface ITemporalParser
{
    /// <summary>True when the question contains any expression the recognizer parses.</summary>
    bool HasTemporalExpression(string text);

    /// <summary>Extract the first parseable temporal scope from <paramref name="text"/>, or null when none.</summary>
    TemporalScope? Parse(string text);
}

/// <summary>
/// Structured temporal scope extracted from a question. <see cref="Kind"/> classifies the
/// expression for downstream rendering / filter generation; <see cref="Start"/>/<see cref="End"/>
/// hold the resolved UTC instants. <see cref="OriginalText"/> preserves the user-typed phrase
/// so it can be quoted in trace / explainer output.
/// </summary>
public sealed record TemporalScope(
    TemporalScopeKind Kind,
    DateTime? Start,
    DateTime? End,
    int? Days,
    string OriginalText);

public enum TemporalScopeKind
{
    Today,
    Yesterday,
    LastNDays,
    SpecificRange,
}

internal sealed class TemporalParser : ITemporalParser
{
    // Default fallback when the catalog setting is empty or invalid. Microsoft.Recognizers
    // exposes culture as a string constant ("English", "French", etc.); we accept any value
    // it recognizes and fall back to English on unknown values.
    private const string FallbackCulture = Culture.English;

    private readonly IOptionsMonitor<CopilotTextCatalog> _textCatalog;
    private readonly ILogger<TemporalParser> _logger;

    public TemporalParser(IOptionsMonitor<CopilotTextCatalog> textCatalog, ILogger<TemporalParser> logger)
    {
        _textCatalog = textCatalog;
        _logger = logger;
    }

    // Resolves the configured culture on each call so a hot-reload of copilot-text.json takes
    // effect without restarting the host. Empty/missing config → English fallback.
    // Named EffectiveCulture (not Culture) to avoid shadowing Microsoft.Recognizers.Text.Culture.
    private string EffectiveCulture
    {
        get
        {
            var configured = _textCatalog.CurrentValue?.TemporalParserCulture;
            return string.IsNullOrWhiteSpace(configured) ? FallbackCulture : configured;
        }
    }

    public bool HasTemporalExpression(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            return DateTimeRecognizer.RecognizeDateTime(text, EffectiveCulture).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public TemporalScope? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        List<ModelResult> results;
        try
        {
            results = DateTimeRecognizer.RecognizeDateTime(text, EffectiveCulture);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[TemporalParser] recognizer threw for '{Text}' (culture={Culture}).", text, EffectiveCulture);
            return null;
        }

        foreach (var r in results)
        {
            var scope = TryMap(r);
            if (scope is not null) return scope;
        }
        return null;
    }

    private static TemporalScope? TryMap(ModelResult r)
    {
        if (r?.Resolution is null) return null;
        if (!r.Resolution.TryGetValue("values", out var raw)
            || raw is not List<Dictionary<string, string>> values
            || values.Count == 0) return null;

        var v = values[0];
        v.TryGetValue("type", out var type);

        // Range expressions: "between X and Y", "Q1 2026", "last week" (when recognizer
        // expands it to an explicit range).
        if (type is "daterange" or "datetimerange")
        {
            if (v.TryGetValue("start", out var s) && v.TryGetValue("end", out var e)
                && TryParseIso(s, out var start) && TryParseIso(e, out var end))
            {
                return new TemporalScope(TemporalScopeKind.SpecificRange, start, end, Days: null, OriginalText: r.Text);
            }
        }

        // Single dates: classify today/yesterday symbolically; everything else is a 24-hour range.
        if (type is "date" or "datetime")
        {
            if (v.TryGetValue("value", out var val) && TryParseIso(val, out var date))
            {
                var dayStart = date.Date;
                var dayEnd = dayStart.AddDays(1).AddTicks(-1);
                var todayLocal = DateTime.Today;
                var kind = dayStart == todayLocal
                    ? TemporalScopeKind.Today
                    : dayStart == todayLocal.AddDays(-1)
                        ? TemporalScopeKind.Yesterday
                        : TemporalScopeKind.SpecificRange;
                return new TemporalScope(kind, dayStart, dayEnd, Days: null, OriginalText: r.Text);
            }
        }

        // Durations: "in the last 7 days" sometimes lands here.
        if (type == "duration")
        {
            if (v.TryGetValue("value", out var sec)
                && double.TryParse(sec, NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                var days = Math.Max(1, (int)Math.Round(seconds / 86400.0));
                var now = DateTime.UtcNow;
                return new TemporalScope(TemporalScopeKind.LastNDays, now.AddDays(-days), now, days, OriginalText: r.Text);
            }
        }

        return null;
    }

    private static bool TryParseIso(string? s, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out result)
            || DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeLocal, out result);
    }
}
