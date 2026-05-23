using AISupportAnalysisPlatform.Models.AI;

namespace AISupportAnalysisPlatform.Services.AI
{
    public static class TicketLanguageDetector
    {
        public static string DetectLabel(params string?[] parts) =>
            DetectProfile(parts).PreferredLocalization;

        public static TicketLanguageProfile DetectProfile(params string?[] parts)
        {
            var arabicCount = 0;
            var latinCount = 0;

            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                foreach (var ch in part)
                {
                    if (IsArabicCharacter(ch))
                    {
                        arabicCount++;
                    }
                    else if (IsLatinCharacter(ch))
                    {
                        latinCount++;
                    }
                }
            }

            var label = ResolveLabel(arabicCount, latinCount);
            var preferredLocalization = arabicCount > latinCount ? "Arabic" : "English";

            if (arabicCount == 0 && latinCount == 0)
            {
                preferredLocalization = "English";
            }

            return new TicketLanguageProfile(label, preferredLocalization, arabicCount, latinCount);
        }

        public static bool IsLikelyArabic(params string?[] parts)
        {
            var profile = DetectProfile(parts);
            return profile.HasArabic && (profile.Label == TicketLanguageLabel.Arabic || profile.Label == TicketLanguageLabel.Mixed);
        }

        private static TicketLanguageLabel ResolveLabel(int arabicCount, int latinCount)
        {
            if (arabicCount == 0 && latinCount == 0) return TicketLanguageLabel.English;
            if (arabicCount == 0) return TicketLanguageLabel.English;
            if (latinCount == 0) return TicketLanguageLabel.Arabic;

            var dominantCount = Math.Max(arabicCount, latinCount);
            var secondaryCount = Math.Min(arabicCount, latinCount);
            if (secondaryCount >= 8 && (float)secondaryCount / dominantCount >= 0.25f)
            {
                return TicketLanguageLabel.Mixed;
            }

            return arabicCount >= latinCount ? TicketLanguageLabel.Arabic : TicketLanguageLabel.English;
        }

        private static bool IsArabicCharacter(char ch) =>
            (ch >= '\u0600' && ch <= '\u06FF') ||
            (ch >= '\u0750' && ch <= '\u077F') ||
            (ch >= '\u08A0' && ch <= '\u08FF') ||
            (ch >= '\uFB50' && ch <= '\uFDFF') ||
            (ch >= '\uFE70' && ch <= '\uFEFF');

        private static bool IsLatinCharacter(char ch) =>
            (ch >= 'A' && ch <= 'Z') ||
            (ch >= 'a' && ch <= 'z');
    }

    public sealed record TicketLanguageProfile(
        TicketLanguageLabel Label,
        string PreferredLocalization,
        int ArabicCharacters,
        int LatinCharacters)
    {
        public bool HasArabic => ArabicCharacters > 0;
        public bool HasLatin => LatinCharacters > 0;
        public bool IsMixed => Label == TicketLanguageLabel.Mixed;
    }
}
