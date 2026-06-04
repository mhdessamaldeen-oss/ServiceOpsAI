namespace AnalystAgent.Eval.ExpectationVerifier;

using System.Text.Json;
using System.Text.RegularExpressions;
using ServiceOpsAI.Models.AI;

/// <summary>
/// Default implementation of <see cref="IExpectationVerifier"/>. Performs case-insensitive
/// substring + regex matching of expected facets against the generated SQL. Each Expected*
/// field on <see cref="CopilotAssessmentCase"/> contributes one check; verdict is derived
/// from the pass ratio.
/// </summary>
internal sealed class ExpectationVerifier : IExpectationVerifier
{
    private const double PassThreshold = 0.99;
    private const double PartialThreshold = 0.60;

    public VerificationResult Verify(CopilotAssessmentCase testCase, string? generatedSql)
    {
        var passed = new List<string>();
        var failed = new List<string>();

        // For NON-DATA cases (refusal / clarification / chat / tool) no SQL is expected. The verdict
        // keys on whether SQL was produced. (The previous branch compared against intent names —
        // "Refusal"/"Conversational"/"Knowledge"/"Metadata" — that are NOT CopilotIntentKind members,
        // so it never fired. Keyed now on the real signals: ExpectedInvalid / ExpectedClarification and
        // the no-SQL intent kinds.)
        var noSqlExpected =
            testCase.ExpectedInvalid == true ||
            testCase.ExpectedClarification == true ||
            testCase.ExpectedIntent is CopilotIntentKind.GeneralChat
                or CopilotIntentKind.ExternalToolQuery
                or CopilotIntentKind.Unsupported
                or CopilotIntentKind.Clarification;
        if (noSqlExpected)
        {
            if (string.IsNullOrWhiteSpace(generatedSql))
            {
                passed.Add("Non-data case → no SQL generated, correct.");
                return new VerificationResult(VerificationVerdict.Pass, 1, 1, passed, failed);
            }
            failed.Add($"Non-data case → SQL was generated (violation): {Truncate(generatedSql, 80)}");
            return new VerificationResult(VerificationVerdict.Fail, 0, 1, passed, failed);
        }

        if (string.IsNullOrWhiteSpace(generatedSql))
        {
            failed.Add("No SQL generated.");
            return new VerificationResult(VerificationVerdict.Fail, 0, 1, passed, failed);
        }

        var sql = generatedSql.ToLowerInvariant();

        // Check 1 — ExpectedPrimaryEntity must appear in FROM
        if (!string.IsNullOrEmpty(testCase.ExpectedPrimaryEntity))
        {
            var entity = testCase.ExpectedPrimaryEntity!.ToLowerInvariant();
            // Quick string match: the entity name appears somewhere after FROM
            var fromIdx = sql.IndexOf("from", StringComparison.Ordinal);
            if (fromIdx >= 0 && sql.IndexOf(entity, fromIdx, StringComparison.Ordinal) > fromIdx)
                passed.Add($"Root entity '{testCase.ExpectedPrimaryEntity}' present.");
            else
                failed.Add($"Root entity '{testCase.ExpectedPrimaryEntity}' MISSING from FROM clause.");
        }

        // Check 2 — ExpectedOperation must align with the aggregation function used
        if (!string.IsNullOrEmpty(testCase.ExpectedOperation))
        {
            var op = testCase.ExpectedOperation!.ToUpperInvariant();
            var found = op switch
            {
                "COUNT"             => Regex.IsMatch(sql, @"\bcount\s*\("),
                "SUM"               => Regex.IsMatch(sql, @"\bsum\s*\("),
                "AVG"               => Regex.IsMatch(sql, @"\bavg\s*\("),
                "MIN"               => Regex.IsMatch(sql, @"\bmin\s*\("),
                "MAX"               => Regex.IsMatch(sql, @"\bmax\s*\("),
                "LIST"              => !Regex.IsMatch(sql, @"\b(count|sum|avg|min|max)\s*\("),
                "TOP_RANKED"        => sql.Contains("top ") || sql.Contains("offset ") || sql.Contains("rank() over"),
                "TIMESERIES"        => Regex.IsMatch(sql, @"\b(dateadd|datepart|year\s*\(|month\s*\(|format\s*\()"),
                "COMPARE"           => sql.Contains("case when") || sql.Contains("union"),
                "COMPARE_PERIOD"    => sql.Contains("union") || sql.Contains("period") || Regex.Matches(sql, @"case\s+when").Count >= 2,
                "WINDOW"            => sql.Contains(" over (") || sql.Contains(" over("),
                "RECURSIVE"         => sql.Contains("with") && sql.Contains("union") && sql.Contains(" recursive") || (sql.Contains("with ") && Regex.IsMatch(sql, @"\bwith\s+\w+\s+as\b") && sql.Contains("union all")),
                "SELF_JOIN"         => Regex.Matches(sql, @"\bfrom\b").Count >= 1 && Regex.IsMatch(sql, @"join\s+\[?\w+\]?\s+(?:as\s+)?\w+\s+on"),
                "EXISTS"            => sql.Contains(" exists"),
                "NOT_EXISTS"        => sql.Contains("not exists") || (sql.Contains("left join") && sql.Contains("is null")),
                "UNION"             => sql.Contains(" union "),
                "HAVING"            => sql.Contains(" having "),
                _                   => true   // unknown operation tag — don't fail on it
            };
            if (found) passed.Add($"Operation {op} pattern present.");
            else failed.Add($"Operation {op} pattern NOT detected in SQL.");
        }

        // Check 3 — ExpectedLimit (TOP N)
        if (testCase.ExpectedLimit is > 0)
        {
            var n = testCase.ExpectedLimit.Value;
            var topMatch = Regex.IsMatch(sql, $@"\btop\s*\(?\s*{n}\b") || Regex.IsMatch(sql, $@"\bfetch\s+next\s+{n}\b");
            if (topMatch) passed.Add($"Limit {n} present.");
            else failed.Add($"Limit {n} MISSING.");
        }

        // Check 4 — ExpectedFilters: each expected filter's column must appear somewhere
        if (testCase.ExpectedFilters is { Count: > 0 })
        {
            foreach (object? f in testCase.ExpectedFilters)
            {
                (string col, string op, _) = UnwrapFilter(f);
                if (string.IsNullOrEmpty(col)) continue;
                var bareCol = col.Contains('.') ? col.Substring(col.IndexOf('.') + 1) : col;
                if (sql.Contains(bareCol.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    // If op is isnull/notnull, check for IS NULL pattern.
                    if (op == "isnull" || op == "is_null")
                    {
                        if (Regex.IsMatch(sql, $@"\b{Regex.Escape(bareCol.ToLowerInvariant())}\s+is\s+null\b"))
                            passed.Add($"Filter {col} IS NULL present.");
                        else
                            failed.Add($"Filter {col} should be IS NULL but isn't.");
                    }
                    else
                    {
                        passed.Add($"Filter on {col} present.");
                    }
                }
                else
                {
                    failed.Add($"Filter on {col} MISSING.");
                }
            }
        }

        // Check 5 — ExpectedFields: each expected SELECT column must appear
        if (testCase.ExpectedFields is { Count: > 0 })
        {
            foreach (var fld in testCase.ExpectedFields)
            {
                if (string.IsNullOrEmpty(fld)) continue;
                var bare = fld.Contains('.') ? fld.Substring(fld.IndexOf('.') + 1) : fld;
                if (sql.Contains(bare.ToLowerInvariant(), StringComparison.Ordinal))
                    passed.Add($"Field {fld} projected.");
                else
                    failed.Add($"Field {fld} MISSING from SELECT.");
            }
        }

        // Check 6 — ExpectedAggregations: each function/column pair must appear
        if (testCase.ExpectedAggregations is { Count: > 0 })
        {
            foreach (object? a in testCase.ExpectedAggregations)
            {
                (string fn, string col, bool distinct) = UnwrapAggregation(a);
                if (string.IsNullOrEmpty(fn)) continue;
                var fnLower = fn.ToLowerInvariant();
                var colBare = string.IsNullOrEmpty(col) || col == "*"
                    ? "*"
                    : (col.Contains('.') ? col.Substring(col.IndexOf('.') + 1) : col);

                // Match `count(`...`)` then either `*` or the column name within the parens (allowing DISTINCT).
                var pattern = colBare == "*"
                    ? $@"\b{fnLower}\s*\(\s*(?:distinct\s+)?\*\s*\)"
                    : $@"\b{fnLower}\s*\(\s*(?:distinct\s+)?[^)]*{Regex.Escape(colBare.ToLowerInvariant())}[^)]*\)";
                if (Regex.IsMatch(sql, pattern))
                {
                    passed.Add($"Aggregation {fn}({col}{(distinct ? " DISTINCT" : "")}) present.");
                }
                else
                {
                    failed.Add($"Aggregation {fn}({col}) MISSING.");
                }
            }
        }

        // Check 7 — ExpectedGroupBy: each expected GROUP BY expression should match by substring
        if (testCase.ExpectedGroupBy is { Count: > 0 })
        {
            var groupIdx = sql.IndexOf("group by", StringComparison.Ordinal);
            foreach (var g in testCase.ExpectedGroupBy)
            {
                if (string.IsNullOrEmpty(g)) continue;
                var bare = g.Contains('.') ? g.Substring(g.IndexOf('.') + 1) : g;
                // For DATEADD bucket expressions, match the function name + column
                var lookFor = bare.ToLowerInvariant();
                if (groupIdx >= 0 && sql.IndexOf(lookFor, groupIdx, StringComparison.Ordinal) > groupIdx)
                    passed.Add($"GROUP BY {g} present.");
                else
                    failed.Add($"GROUP BY {g} MISSING.");
            }
        }

        // Verdict
        var total = passed.Count + failed.Count;
        if (total == 0)
            return new VerificationResult(VerificationVerdict.NotApplicable, 0, 0, passed, failed);
        var pct = (double)passed.Count / total;
        var verdict = pct >= PassThreshold ? VerificationVerdict.Pass
                    : pct >= PartialThreshold ? VerificationVerdict.Partial
                    : VerificationVerdict.Fail;
        return new VerificationResult(verdict, passed.Count, total, passed, failed);
    }

    private static (string Column, string Op, object? Value) UnwrapFilter(object? f)
    {
        if (f is null) return ("", "", null);
        if (f is JsonElement je)
        {
            string col = ""; string op = ""; object? val = null;
            if (je.TryGetProperty("Column", out var c)) col = c.GetString() ?? "";
            else if (je.TryGetProperty("column", out c)) col = c.GetString() ?? "";
            if (je.TryGetProperty("Op", out var o)) op = (o.GetString() ?? "").ToLowerInvariant();
            else if (je.TryGetProperty("op", out o)) op = (o.GetString() ?? "").ToLowerInvariant();
            if (je.TryGetProperty("Value", out var v)) val = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            else if (je.TryGetProperty("value", out v)) val = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            return (col, op, val);
        }
        // Reflection fallback for runtime dictionary / anonymous types
        var type = f.GetType();
        var colProp = type.GetProperty("Column") ?? type.GetProperty("column");
        var opProp = type.GetProperty("Op") ?? type.GetProperty("op");
        var valProp = type.GetProperty("Value") ?? type.GetProperty("value");
        return (
            colProp?.GetValue(f)?.ToString() ?? "",
            (opProp?.GetValue(f)?.ToString() ?? "").ToLowerInvariant(),
            valProp?.GetValue(f));
    }

    private static (string Function, string Column, bool Distinct) UnwrapAggregation(object? a)
    {
        if (a is null) return ("", "", false);
        if (a is JsonElement je)
        {
            string fn = ""; string col = ""; bool dist = false;
            if (je.TryGetProperty("function", out var f)) fn = f.GetString() ?? "";
            else if (je.TryGetProperty("Function", out f)) fn = f.GetString() ?? "";
            if (je.TryGetProperty("column", out var c)) col = c.GetString() ?? "";
            else if (je.TryGetProperty("Column", out c)) col = c.GetString() ?? "";
            if (je.TryGetProperty("distinct", out var d) && d.ValueKind == JsonValueKind.True) dist = true;
            else if (je.TryGetProperty("Distinct", out d) && d.ValueKind == JsonValueKind.True) dist = true;
            return (fn, col, dist);
        }
        var type = a.GetType();
        var fnProp = type.GetProperty("function") ?? type.GetProperty("Function");
        var colProp = type.GetProperty("column") ?? type.GetProperty("Column");
        var distProp = type.GetProperty("distinct") ?? type.GetProperty("Distinct");
        return (
            fnProp?.GetValue(a)?.ToString() ?? "",
            colProp?.GetValue(a)?.ToString() ?? "",
            distProp?.GetValue(a) is bool b && b);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
