namespace SuperAdminCopilot.Tests.Architecture;

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Architecture test — enforces the locked principle: natural-language vocabulary lives in
/// <c>linguistic-cues.json</c> + the <c>LinguisticRegistry</c>, and NOWHERE ELSE in the
/// <c>Areas/SuperAdminCopilot/</c> source tree.
///
/// <para>The test scans every .cs file under <c>Areas/SuperAdminCopilot/</c> EXCLUDING
/// the <c>Infrastructure/Linguistic/</c> folder. If it finds a regex/literal whose pattern
/// contains a non-ASCII letter (Arabic, CJK, Cyrillic, …), the test fails.</para>
///
/// <para>A second test enforces that domain entity names ("Tickets", "Bills", "Outages", …)
/// do not appear as conditional string literals in repair-rule or stage code — keep them in
/// <c>semantic-layer.json</c>. Allowed only in the infrastructure layer that bridges JSON ↔ code.</para>
///
/// <para>This is the line in the sand. The copilot cannot regress to the deleted v2 spaghetti —
/// the build catches every drift before merge.</para>
/// </summary>
public class NoHardcodedVocabTests
{
    [Fact]
    public void NoNonAsciiRegexLiteralOutsideLinguisticRegistry()
    {
        var copilotRoot = LocateCopilotRoot();
        Assert.True(Directory.Exists(copilotRoot), $"SuperAdminCopilot source root not found at {copilotRoot}");

        var allowedSubpath = Path.Combine("Infrastructure", "Linguistic");
        var offenders = Directory.EnumerateFiles(copilotRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(allowedSubpath, System.StringComparison.OrdinalIgnoreCase))
            .Select(p => new { Path = p, Hits = FindNonAsciiRegexLiterals(File.ReadAllText(p)) })
            .Where(x => x.Hits.Count > 0)
            .ToList();

        if (offenders.Count == 0) return;

        var report = string.Join("\n", offenders.Select(o =>
            $"  {Path.GetFileName(o.Path)}:\n    " + string.Join("\n    ", o.Hits)));
        Assert.Fail(
            "Non-ASCII regex literals found outside Infrastructure/Linguistic.\n" +
            "These should move to linguistic-cues.json or be removed.\n\n" + report);
    }

    [Fact]
    public void NoEntityNameStringEqualityOutsideSemanticLayer()
    {
        // Tickets / Bills / Outages / Customers / Payments / WorkOrders / Technicians /
        // Departments / Regions are domain entity names — they must not appear as string
        // literals in repair rules or stage code. Allowed only in semantic-layer.json,
        // semantic-layer adapter classes, and migrations.
        var copilotRoot = LocateCopilotRoot();
        if (!Directory.Exists(copilotRoot)) return;

        // Detect ONLY string-literal tokens of the form "EntityName" (with surrounding quotes
        // as in source). The regex requires the quoted name to NOT be immediately followed by
        // an identifier char — prevents flagging legitimate phrases ("TicketsAreProcessed").
        // Skips // and /// comment lines.
        string[] entityNames = { "Tickets", "Bills", "Outages", "Customers", "Payments",
                                 "WorkOrders", "Technicians", "Departments", "Regions",
                                 "MeterReadings" };
        string[] allowedDirs = { Path.Combine("Infrastructure", "Linguistic"),
                                 Path.Combine("Infrastructure", "Schema") };

        var anyEntity = new Regex("\"(" + string.Join("|", entityNames) + ")\"(?![A-Za-z])",
                                  RegexOptions.Compiled);

        var offenders = new System.Collections.Generic.List<string>();
        foreach (var path in Directory.EnumerateFiles(copilotRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (allowedDirs.Any(d => path.Contains(d, System.StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//")) continue;       // // and ///
                if (anyEntity.IsMatch(line))
                {
                    offenders.Add(path + ":  " + trimmed);
                    break;
                }
            }
        }

        if (offenders.Count == 0) return;
        Assert.Fail("Entity-name literals found outside infrastructure layer:\n" +
                    string.Join("\n", offenders.Select(p => "  " + p)));
    }

    private static System.Collections.Generic.List<string> FindNonAsciiRegexLiterals(string source)
    {
        var hits = new System.Collections.Generic.List<string>();
        // Pattern 1: new Regex(@"..." or new Regex("...")
        // Pattern 2: Regex.Match(..., @"...")
        // We look for any @"..." or "..." string that contains a non-ASCII letter AND is in
        // a context likely to be a regex (preceded by Regex / Match / IsMatch). Conservative:
        // we flag any verbatim string containing non-ASCII letters in a .cs file.
        var stringLit = new Regex(@"@?""([^""\r\n]*)""", RegexOptions.Compiled);
        foreach (Match m in stringLit.Matches(source))
        {
            var content = m.Groups[1].Value;
            if (string.IsNullOrEmpty(content)) continue;
            if (!HasNonAsciiLetter(content)) continue;
            // Skip XML doc comments (cheap heuristic: lines starting with ///).
            var line = source.Substring(0, m.Index).LastIndexOf('\n');
            var ctx = source.Substring(line + 1, System.Math.Min(80, m.Index - line - 1));
            if (ctx.TrimStart().StartsWith("///")) continue;
            hits.Add($"line ~{LineOf(source, m.Index)}: {Truncate(content, 60)}");
        }
        return hits;
    }

    private static bool HasNonAsciiLetter(string s)
    {
        foreach (var c in s)
            if (c > 127 && char.IsLetter(c)) return true;
        return false;
    }

    private static int LineOf(string source, int index)
        => source.Substring(0, index).Count(c => c == '\n') + 1;

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "…";

    private static string LocateCopilotRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, "Areas", "SuperAdminCopilot", "");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "Areas", "SuperAdminCopilot", "");
    }
}
