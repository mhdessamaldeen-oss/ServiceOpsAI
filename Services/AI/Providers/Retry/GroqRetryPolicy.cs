using System.Globalization;
using System.Text.RegularExpressions;
using AISupportAnalysisPlatform.Constants;
using AISupportAnalysisPlatform.Data;

namespace AISupportAnalysisPlatform.Services.AI.Providers.Retry
{
    /// <summary>
    /// Retry strategy for Groq's transient failures. Mirrors GeminiRateLimitPolicy but tuned for Groq:
    ///   - 429 with Retry-After header → sleep that long
    ///   - 503/502/500 → short fixed backoff (Groq's LPU is normally rock-solid; rare blips)
    ///   - 401/400/404/parse → return immediately
    /// All knobs are read from SystemSettings so the UI can tune behavior without redeploys.
    /// </summary>
    public sealed class GroqRetryPolicy : IRetryPolicy
    {
        private const int MaxWaitMs = 300_000;
        private const int BufferMs = 500;
        private const int DefaultFallbackDelayMs = 5_000;
        private const int Default5xxBackoffMs = 2_000;
        private const int DefaultMaxAttempts = 2;

        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<GroqRetryPolicy> _logger;

        public GroqRetryPolicy(IServiceProvider serviceProvider, ILogger<GroqRetryPolicy> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task<AiProviderResult> ExecuteAsync(
            Func<Task<AiProviderResult>> attempt,
            CancellationToken cancellationToken = default)
        {
            var enabled = ReadBoolSetting(SettingKeys.GroqRetryOn429, defaultValue: true);
            var maxAttempts = enabled ? ReadIntSetting(SettingKeys.GroqRetryMaxAttempts, DefaultMaxAttempts) : 1;
            var fallbackDelayMs = ReadIntSetting(SettingKeys.GroqRetryDelayMs, DefaultFallbackDelayMs);

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
                    var groqDelayMs = ParseGroqRetryAfterMs(result.Error) ?? fallbackDelayMs;
                    if (groqDelayMs > MaxWaitMs)
                    {
                        _logger.LogWarning(
                            "Groq asked us to wait {DelayMs}ms — > 5 min, almost certainly daily quota exhausted. Bailing.",
                            groqDelayMs);
                        return result;
                    }
                    waitMs = groqDelayMs + BufferMs;
                    _logger.LogWarning(
                        "Groq 429 on attempt {Attempt}/{Max}. Sleeping {WaitMs}ms before retry.",
                        i, maxAttempts, waitMs);
                }
                else
                {
                    waitMs = Default5xxBackoffMs;
                    _logger.LogWarning(
                        "Groq {Status} (transient) on attempt {Attempt}/{Max}. Sleeping {WaitMs}ms before retry.",
                        transient, i, maxAttempts, waitMs);
                }

                await Task.Delay(waitMs, cancellationToken);
            }

            return result;
        }

        private enum TransientKind { None, RateLimit429, ServerError5xx }

        private static TransientKind ClassifyTransient(string? error)
        {
            if (string.IsNullOrEmpty(error)) return TransientKind.None;
            if (error.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase)) return TransientKind.RateLimit429;
            if (error.Contains("HTTP 500", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("HTTP 502", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("HTTP 503", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("HTTP 504", StringComparison.OrdinalIgnoreCase))
            {
                return TransientKind.ServerError5xx;
            }
            return TransientKind.None;
        }

        /// <summary>
        /// Parse Groq's 429 hint. Groq follows OpenAI's pattern: a `retry-after-ms` header (we surface it
        /// in the error string) or a body containing "Please try again in N.NNs".
        /// </summary>
        private static int? ParseGroqRetryAfterMs(string? errorBody)
        {
            if (string.IsNullOrEmpty(errorBody)) return null;

            var msMatch = Regex.Match(errorBody, @"retry-after-ms[""\s:]*(\d+)", RegexOptions.IgnoreCase);
            if (msMatch.Success && int.TryParse(msMatch.Groups[1].Value, out var ms)) return ms;

            var sMatch = Regex.Match(errorBody, @"try again in (\d+(?:\.\d+)?)s", RegexOptions.IgnoreCase);
            if (sMatch.Success &&
                double.TryParse(sMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
            {
                return (int)Math.Ceiling(sec * 1000);
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
