using System.Globalization;
using System.Text.RegularExpressions;
using ServiceOpsAI.Constants;
using ServiceOpsAI.Data;
using Microsoft.Extensions.Logging;

namespace ServiceOpsAI.Services.AI.Providers.Retry
{
    /// <summary>
    /// Retry strategy for Google Gemini transient failures.
    /// Behavior:
    ///   1. Reads enable / max-attempts / fallback-delay knobs from <see cref="ApplicationDbContext.SystemSettings"/>
    ///      so the UI can tune them without redeploys.
    ///   2. On 429, parses the EXACT delay Google returns (text "Please retry in 52.1s" or structured
    ///      "retryDelay": "52s") and sleeps that long instead of guessing.
    ///   3. On 503 ("model overloaded" / "UNAVAILABLE"), backs off a short fixed delay — Google
    ///      doesn't tell us how long to wait, so we use a 3s default (server load clears fast).
    ///   4. Caps any wait at 5 min — anything longer means daily quota is gone, no point waiting.
    ///   5. Adds a 1s buffer so we land just after the quota window resets.
    /// All other failures (401, 404, parse, timeout) are returned immediately — waiting won't fix them.
    /// </summary>
    public sealed class GeminiRateLimitPolicy : IRetryPolicy
    {
        private const int MaxWaitMs = 300_000;
        private const int BufferMs = 1_000;
        private const int DefaultFallbackDelayMs = 30_000;
        private const int Default503BackoffMs = 3_000;
        private const int DefaultMaxAttempts = 2;

        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<GeminiRateLimitPolicy> _logger;

        public GeminiRateLimitPolicy(IServiceProvider serviceProvider, ILogger<GeminiRateLimitPolicy> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task<AiProviderResult> ExecuteAsync(
            Func<Task<AiProviderResult>> attempt,
            CancellationToken cancellationToken = default)
        {
            var enabled = ReadBoolSetting(SettingKeys.GeminiRetryOn429, defaultValue: true);
            var maxAttempts = enabled ? ReadIntSetting(SettingKeys.GeminiRetryMaxAttempts, DefaultMaxAttempts) : 1;
            var fallbackDelayMs = ReadIntSetting(SettingKeys.GeminiRetryDelayMs, DefaultFallbackDelayMs);

            var result = new AiProviderResult { Success = false, Error = "Not attempted" };

            for (var i = 1; i <= maxAttempts; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = await attempt();
                if (result.Success) return result;

                var transient = ClassifyTransient(result.Error);
                if (transient == TransientKind.None || i >= maxAttempts) return result;

                int waitMs;
                if (transient == TransientKind.RateLimit429)
                {
                    var googleDelayMs = ParseGoogleRetryDelayMs(result.Error) ?? fallbackDelayMs;
                    if (googleDelayMs > MaxWaitMs)
                    {
                        _logger.LogWarning(
                            "Gemini asked us to wait {DelayMs}ms — that's > 5 min, almost certainly daily quota exhausted. Bailing.",
                            googleDelayMs);
                        return result;
                    }
                    waitMs = googleDelayMs + BufferMs;
                    _logger.LogWarning(
                        "Gemini 429 on attempt {Attempt}/{Max}. Google asked us to wait {GoogleMs}ms; sleeping {WaitMs}ms before retry.",
                        i, maxAttempts, googleDelayMs, waitMs);
                }
                else // Overloaded503 — Google doesn't supply a delay, use a short fixed backoff
                {
                    waitMs = Default503BackoffMs;
                    _logger.LogWarning(
                        "Gemini 503 (overloaded) on attempt {Attempt}/{Max}. Sleeping {WaitMs}ms before retry.",
                        i, maxAttempts, waitMs);
                }

                await Task.Delay(waitMs, cancellationToken);
            }

            return result;
        }

        private enum TransientKind { None, RateLimit429, Overloaded503 }

        private static TransientKind ClassifyTransient(string? error)
        {
            if (string.IsNullOrEmpty(error)) return TransientKind.None;
            if (error.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase)) return TransientKind.RateLimit429;
            if (error.Contains("HTTP 503", StringComparison.OrdinalIgnoreCase)) return TransientKind.Overloaded503;
            return TransientKind.None;
        }

        /// <summary>
        /// Parse the "Please retry in N.NNNs" message OR the structured `retryDelay` field that Google
        /// includes in 429 responses. Returns the delay in milliseconds, or null if unparseable.
        /// </summary>
        private static int? ParseGoogleRetryDelayMs(string? errorBody)
        {
            if (string.IsNullOrEmpty(errorBody)) return null;

            var textMatch = Regex.Match(errorBody, @"retry in (\d+(?:\.\d+)?)s", RegexOptions.IgnoreCase);
            if (textMatch.Success &&
                double.TryParse(textMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
            {
                return (int)Math.Ceiling(sec * 1000);
            }

            var structMatch = Regex.Match(errorBody, @"""retryDelay""\s*:\s*""(\d+(?:\.\d+)?)s""", RegexOptions.IgnoreCase);
            if (structMatch.Success &&
                double.TryParse(structMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec2))
            {
                return (int)Math.Ceiling(sec2 * 1000);
            }

            return null;
        }

        private bool ReadBoolSetting(string key, bool defaultValue)
        {
            var raw = ReadSetting(key);
            return bool.TryParse(raw, out var v) ? v : defaultValue;
        }

        private int ReadIntSetting(string key, int defaultValue)
        {
            var raw = ReadSetting(key);
            return int.TryParse(raw, out var v) && v > 0 ? v : defaultValue;
        }

        private string? ReadSetting(string key)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return db.SystemSettings.FirstOrDefault(s => s.Key == key)?.Value;
        }
    }
}
