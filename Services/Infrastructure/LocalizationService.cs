using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using System.Globalization;
using System.Collections.Concurrent;
using Microsoft.Extensions.Localization;

namespace AISupportAnalysisPlatform.Services.Infrastructure
{
    public interface ILocalizationService
    {
        string Get(string key);
        string Get(string key, string language);
        string CurrentLanguage { get; }
        string Direction { get; }
        bool IsRtl { get; }
    }

    public class LocalizationService : ILocalizationService
    {
        private readonly string _contentRootPath;
        private readonly IStringLocalizer<SharedResource> _localizer;
        // Cache entries are stamped with the source file's last-write time. When we
        // detect the file has changed on disk we re-read it — so editing
        // Resources/ar.json (or en.json) takes effect on the next request without
        // an app restart. Without this, the cache snapshot at startup is frozen
        // for the whole process lifetime.
        private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

        private sealed record CacheEntry(Dictionary<string, string> Dict, DateTime LastWriteUtc);

        public LocalizationService(IWebHostEnvironment env, IStringLocalizer<SharedResource> localizer)
        {
            _contentRootPath = env.ContentRootPath;
            _localizer = localizer;
        }

        public string CurrentLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        public string Direction => IsRtl ? "rtl" : "ltr";
        public bool IsRtl => CurrentLanguage == "ar";

        public string Get(string key) => Get(key, CurrentLanguage);

        public string Get(string key, string language)
        {
            // Local JSON dictionary is the source of truth — check it first so that
            // edits to ar.json/en.json take effect immediately. Only fall through
            // to the IStringLocalizer (resx-based) chain when the JSON has no
            // entry for the key. Previously this order was reversed, which meant
            // any value already cached by IStringLocalizer would shadow newer
            // JSON entries even after the file changed on disk.
            var dict = GetDictionary(language);
            if (dict.TryGetValue(key, out var value))
            {
                return value;
            }

            using var _ = new CultureScope(ToCultureInfo(language));
            var packageValue = _localizer[key];
            if (!packageValue.ResourceNotFound && !string.Equals(packageValue.Value, key, StringComparison.Ordinal))
            {
                return packageValue.Value;
            }

            return key;
        }

        private Dictionary<string, string> GetDictionary(string language)
        {
            var lang = language.ToLower() switch {
                "arabic" or "ar" => "ar",
                _ => "en"
            };

            var path = Path.Combine(_contentRootPath, "Resources", $"{lang}.json");
            if (File.Exists(path))
            {
                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (_cache.TryGetValue(lang, out var cached) && cached.LastWriteUtc == lastWrite)
                {
                    return cached.Dict;
                }

                var json = File.ReadAllText(path);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                _cache[lang] = new CacheEntry(dict, lastWrite);
                return dict;
            }

            // Fallback to English if the requested-language file is missing.
            if (lang != "en")
            {
                var enPath = Path.Combine(_contentRootPath, "Resources", "en.json");
                if (File.Exists(enPath))
                {
                    var enLastWrite = File.GetLastWriteTimeUtc(enPath);
                    if (_cache.TryGetValue("en", out var cachedEn) && cachedEn.LastWriteUtc == enLastWrite)
                    {
                        return cachedEn.Dict;
                    }

                    var enJson = File.ReadAllText(enPath);
                    var enDict = JsonSerializer.Deserialize<Dictionary<string, string>>(enJson) ?? new();
                    _cache["en"] = new CacheEntry(enDict, enLastWrite);
                    return enDict;
                }
            }

            return new Dictionary<string, string>();
        }

        private static CultureInfo ToCultureInfo(string language)
        {
            var lang = language.ToLowerInvariant() switch
            {
                "arabic" or "ar" => "ar",
                _ => "en"
            };

            return CultureInfo.GetCultureInfo(lang);
        }

        private sealed class CultureScope : IDisposable
        {
            private readonly CultureInfo _originalCulture;
            private readonly CultureInfo _originalUiCulture;

            public CultureScope(CultureInfo culture)
            {
                _originalCulture = CultureInfo.CurrentCulture;
                _originalUiCulture = CultureInfo.CurrentUICulture;
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
            }

            public void Dispose()
            {
                CultureInfo.CurrentCulture = _originalCulture;
                CultureInfo.CurrentUICulture = _originalUiCulture;
            }
        }
    }
}
