namespace SuperAdminCopilot.Pipeline.Stages;

using System.Text.RegularExpressions;

// Rewrites common Postgres/MySQL/SQLite idioms emitted by local LLMs to T-SQL equivalents.
// String-literal-aware: skips replacements inside 'quoted values'.
internal static class TsqlDialectNormalizer
{
    public static string Normalize(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;
        var s = sql;

        // `name` → [name]
        s = Regex.Replace(s, @"`([A-Za-z_][A-Za-z0-9_]*)`", "[$1]");

        // NOW() → GETDATE();  CURRENT_DATE → CAST(GETDATE() AS DATE).
        s = Regex.Replace(s, @"\bNOW\s*\(\s*\)", "GETDATE()", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bCURRENT_DATE\b", "CAST(GETDATE() AS DATE)", RegexOptions.IgnoreCase);

        // ILIKE → LIKE (T-SQL is case-insensitive by default collation).
        s = Regex.Replace(s, @"\bILIKE\b", "LIKE", RegexOptions.IgnoreCase);

        // a || b  →  a + b (string concatenation; skipped inside string literals).
        s = RewriteOutsideStringLiterals(s, @"\|\|", "+");

        // LIMIT/OFFSET → TOP(N) or OFFSET/FETCH (trailing-position only).
        s = RewriteLimitOffset(s);

        return s;
    }

    // Apply the replacement only outside single-quoted regions; '' is treated as an escape.
    private static string RewriteOutsideStringLiterals(string input, string pattern, string replacement)
    {
        var rx = new Regex(pattern);
        var sb = new System.Text.StringBuilder(input.Length);
        bool inString = false;
        int i = 0;
        int segmentStart = 0;
        while (i < input.Length)
        {
            var ch = input[i];
            if (ch == '\'')
            {
                if (inString && i + 1 < input.Length && input[i + 1] == '\'') { i += 2; continue; }
                if (!inString)
                {
                    var outside = input.Substring(segmentStart, i - segmentStart);
                    sb.Append(rx.Replace(outside, replacement));
                    segmentStart = i;
                    inString = true;
                }
                else
                {
                    sb.Append(input, segmentStart, i - segmentStart + 1);
                    segmentStart = i + 1;
                    inString = false;
                }
            }
            i++;
        }
        if (segmentStart < input.Length)
        {
            var tail = input.Substring(segmentStart);
            sb.Append(inString ? tail : rx.Replace(tail, replacement));
        }
        return sb.ToString();
    }

    private static readonly Regex LimitOnlyTail = new(
        @"\bLIMIT\s+(\d+)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LimitOffsetTail = new(
        @"\bLIMIT\s+(\d+)\s+OFFSET\s+(\d+)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OffsetLimitTail = new(
        @"\bOFFSET\s+(\d+)\s+LIMIT\s+(\d+)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LeadingSelect = new(
        @"^\s*SELECT\s+(?:DISTINCT\s+)?(?!TOP\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Conservative: only rewrites a trailing LIMIT / LIMIT-OFFSET; bails on multi-statement.
    private static string RewriteLimitOffset(string sql)
    {
        var trimmed = sql.TrimEnd();
        var hadSemi = trimmed.EndsWith(';');
        if (hadSemi) trimmed = trimmed[..^1].TrimEnd();

        var m = LimitOffsetTail.Match(trimmed);
        var offsetFirst = false;
        if (!m.Success)
        {
            m = OffsetLimitTail.Match(trimmed);
            offsetFirst = true;
        }
        if (m.Success)
        {
            var limitN = int.Parse(m.Groups[offsetFirst ? 2 : 1].Value);
            var offsetM = int.Parse(m.Groups[offsetFirst ? 1 : 2].Value);
            var prefix = trimmed[..m.Index].TrimEnd();
            // OFFSET/FETCH needs an existing ORDER BY; bail rather than fabricate one.
            if (!Regex.IsMatch(prefix, @"\bORDER\s+BY\b", RegexOptions.IgnoreCase)) return sql;
            return prefix + $" OFFSET {offsetM} ROWS FETCH NEXT {limitN} ROWS ONLY" + (hadSemi ? ";" : "");
        }

        m = LimitOnlyTail.Match(trimmed);
        if (m.Success)
        {
            var limitN = int.Parse(m.Groups[1].Value);
            var prefix = trimmed[..m.Index].TrimEnd();
            if (!LeadingSelect.IsMatch(prefix)) return sql;
            var rewritten = LeadingSelect.Replace(prefix, match => match.Value + $"TOP ({limitN}) ", count: 1);
            return rewritten + (hadSemi ? ";" : "");
        }

        return sql;
    }
}
