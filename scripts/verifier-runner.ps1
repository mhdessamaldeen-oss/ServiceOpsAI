#requires -Version 7
<#
.SYNOPSIS
  Score session traces against the Expected* fields declared in shape-suite JSON files.
  Produces per-question PASS / PARTIAL / FAIL verdicts and per-shape semantic-correctness rates.

.PARAMETER SessionId
  CopilotChatSession Id to score.

.PARAMETER SuitesDir
  Folder holding suite-shape-*.json files (defaults to Areas/SuperAdminCopilot/Configuration/QuestionSuites/shapes).

.EXAMPLE
  ./verifier-runner.ps1 -SessionId 113
#>
param(
    [int]$SessionId = 113,
    [string]$SuitesDir = "C:\Work\Lern\Improve\v2\AISupportAnalysisPlatform\Areas\SuperAdminCopilot\Configuration\QuestionSuites\shapes"
)

$ErrorActionPreference = 'Stop'

# ─── Load expectations from suite JSON files (Code → Expected* map) ─────────
$expectations = @{}
Get-ChildItem -Path $SuitesDir -Filter "suite-shape-*.json" | ForEach-Object {
    $json = Get-Content $_.FullName -Raw | ConvertFrom-Json
    foreach ($s in $json.Scenarios) {
        if (-not $s.Code) { continue }
        $expectations[$s.Code] = $s
    }
}
"Loaded $($expectations.Count) expectations from $($SuitesDir | Split-Path -Leaf)"

# ─── Pull session traces ───────────────────────────────────────────────────
$tracesRaw = sqlcmd -S "PC\SQLEXPRESS" -d AISupportAnalysisDB -E -h-1 -s "`t" -W -y0 -Q @"
SET NOCOUNT ON;
SELECT CaseCode, ISNULL(GeneratedScript, '') AS Sql, ISNULL(ErrorMessage, '') AS Err
FROM CopilotTraceHistories WHERE SessionId = $SessionId AND CaseCode IS NOT NULL;
"@

$traces = @()
foreach ($line in $tracesRaw) {
    $cols = $line -split "`t"
    if ($cols.Count -ge 3) {
        $traces += [PSCustomObject]@{ Code = $cols[0].Trim(); Sql = $cols[1]; Err = $cols[2] }
    }
}
"Loaded $($traces.Count) traces from session $SessionId"

# ─── Score each trace ──────────────────────────────────────────────────────
function Test-MetricFn([string]$sql, [string]$fn) {
    switch ($fn.ToUpperInvariant()) {
        'COUNT'         { return $sql -match '\bCOUNT\s*\(' }
        'SUM'           { return $sql -match '\bSUM\s*\(' }
        'AVG'           { return $sql -match '\bAVG\s*\(' }
        'MIN'           { return $sql -match '\bMIN\s*\(' }
        'MAX'           { return $sql -match '\bMAX\s*\(' }
        'LIST'          { return -not ($sql -match '\b(COUNT|SUM|AVG|MIN|MAX)\s*\(') }
        'TOP_RANKED'    { return $sql -match '\bTOP\b' -or $sql -match '\bRANK\(\)\s+OVER' }
        'TIMESERIES'    { return $sql -match '\b(DATEADD|DATEPART|YEAR\s*\(|MONTH\s*\(|FORMAT\s*\()' }
        'COMPARE'       { return $sql -match '(CASE\s+WHEN|UNION)' }
        'COMPARE_PERIOD'{ return $sql -match '\bUNION\b' -or ([regex]::Matches($sql, 'CASE\s+WHEN')).Count -ge 2 }
        'WINDOW'        { return $sql -match '\bOVER\s*\(' }
        'GROUPBY'       { return $sql -match '\bGROUP\s+BY\b' }
        'RECURSIVE'     { return $sql -match '\bWITH\b' -and $sql -match '\bUNION\s+ALL\b' }
        'SELF_JOIN'     { return ([regex]::Matches($sql, 'JOIN\s+\[?\w+\]?\s+(AS\s+)?\w+')).Count -ge 1 }
        'EXISTS'        { return $sql -match '\bEXISTS\b' }
        'NOT_EXISTS'    { return $sql -match '\bNOT\s+EXISTS\b' -or ($sql -match 'LEFT\s+JOIN' -and $sql -match 'IS\s+NULL') }
        'UNION'         { return $sql -match '\bUNION\b' }
        'HAVING'        { return $sql -match '\bHAVING\b' }
        default         { return $true }
    }
}

$results = @()
foreach ($t in $traces) {
    $exp = $expectations[$t.Code]
    if (-not $exp) {
        $results += [PSCustomObject]@{ Code=$t.Code; Verdict='NoExpectations'; Passed=0; Total=0; Issues='' }
        continue
    }

    $passed = @(); $failed = @()
    $sql = $t.Sql.ToLowerInvariant()

    # Refusal-class intents — verdict flips: SQL should be empty
    if ($exp.ExpectedIntent -in @('Refusal','Conversational','Knowledge','Metadata')) {
        if ([string]::IsNullOrWhiteSpace($t.Sql)) {
            $passed += "Intent $($exp.ExpectedIntent): no SQL generated"
        } else {
            $failed += "Intent $($exp.ExpectedIntent): SQL was generated (violation)"
        }
        $verdict = if ($failed.Count -eq 0) { 'Pass' } else { 'Fail' }
        $results += [PSCustomObject]@{ Code=$t.Code; Verdict=$verdict; Passed=$passed.Count; Total=$passed.Count + $failed.Count; Issues=($failed -join ' | ') }
        continue
    }

    if ([string]::IsNullOrWhiteSpace($t.Sql)) {
        $results += [PSCustomObject]@{ Code=$t.Code; Verdict='Fail'; Passed=0; Total=1; Issues="No SQL generated. Err: $($t.Err)" }
        continue
    }

    # Root entity
    if ($exp.ExpectedPrimaryEntity) {
        $entity = $exp.ExpectedPrimaryEntity.ToLowerInvariant()
        $fromIdx = $sql.IndexOf('from')
        if ($fromIdx -ge 0 -and $sql.IndexOf($entity, $fromIdx) -gt $fromIdx) { $passed += "Root $($exp.ExpectedPrimaryEntity)" }
        else { $failed += "Root $($exp.ExpectedPrimaryEntity) MISSING" }
    }

    # Operation pattern
    if ($exp.ExpectedOperation) {
        if (Test-MetricFn $sql $exp.ExpectedOperation) { $passed += "Operation $($exp.ExpectedOperation)" }
        else { $failed += "Operation $($exp.ExpectedOperation) NOT detected" }
    }

    # Limit
    if ($exp.ExpectedLimit -and $exp.ExpectedLimit -gt 0) {
        $n = [int]$exp.ExpectedLimit
        if ($sql -match "\btop\s*\(?\s*$n\b" -or $sql -match "\bfetch\s+next\s+$n\b") { $passed += "Limit $n" }
        else { $failed += "Limit $n MISSING" }
    }

    # Filters
    if ($exp.ExpectedFilters) {
        foreach ($f in $exp.ExpectedFilters) {
            if (-not $f.Column) { continue }
            $bare = if ($f.Column -match '\.') { $f.Column.Split('.')[1] } else { $f.Column }
            $bareLow = $bare.ToLowerInvariant()
            if ($f.Op -eq 'isnull' -or $f.Op -eq 'is_null') {
                if ($sql -match "\b$([regex]::Escape($bareLow))\s+is\s+null\b") { $passed += "Filter $($f.Column) IS NULL" }
                else { $failed += "Filter $($f.Column) should be IS NULL" }
            } elseif ($sql.Contains($bareLow)) { $passed += "Filter $($f.Column)" }
            else { $failed += "Filter $($f.Column) MISSING" }
        }
    }

    # Aggregations
    if ($exp.ExpectedAggregations) {
        foreach ($a in $exp.ExpectedAggregations) {
            if (-not $a.function) { continue }
            $fnLow = $a.function.ToLowerInvariant()
            $colBare = if ($a.column -eq '*' -or -not $a.column) { '*' }
                       elseif ($a.column -match '\.') { $a.column.Split('.')[1] }
                       else { $a.column }
            $pattern = if ($colBare -eq '*') { "\b$fnLow\s*\(\s*(?:distinct\s+)?\*\s*\)" }
                       else { "\b$fnLow\s*\(\s*(?:distinct\s+)?[^)]*$([regex]::Escape($colBare.ToLowerInvariant()))[^)]*\)" }
            if ($sql -match $pattern) { $passed += "Agg $($a.function)($($a.column))" }
            else { $failed += "Agg $($a.function)($($a.column)) MISSING" }
        }
    }

    # GROUP BY
    if ($exp.ExpectedGroupBy) {
        $groupIdx = $sql.IndexOf('group by')
        foreach ($g in $exp.ExpectedGroupBy) {
            if (-not $g) { continue }
            $bare = if ($g -match '\.') { $g.Split('.')[1] } else { $g }
            $lookFor = $bare.ToLowerInvariant()
            if ($groupIdx -ge 0 -and $sql.IndexOf($lookFor, $groupIdx) -gt $groupIdx) { $passed += "GroupBy $g" }
            else { $failed += "GroupBy $g MISSING" }
        }
    }

    $total = $passed.Count + $failed.Count
    if ($total -eq 0) {
        $verdict = 'NotApplicable'
    } else {
        $pct = $passed.Count / $total
        $verdict = if ($pct -ge 0.99) { 'Pass' } elseif ($pct -ge 0.60) { 'Partial' } else { 'Fail' }
    }
    $results += [PSCustomObject]@{ Code=$t.Code; Verdict=$verdict; Passed=$passed.Count; Total=$total; Issues=($failed -join ' | ') }
}

# ─── Aggregate per-shape ────────────────────────────────────────────────────
function Get-Shape([string]$code) {
    if ($code -match '^(\w+)-') { return $matches[1] }
    return 'OTHER'
}

"`n--- Per-shape verdict ---"
$byShape = $results | Group-Object { Get-Shape $_.Code }
$summary = @()
foreach ($g in $byShape | Sort-Object Name) {
    $pass = ($g.Group | Where-Object Verdict -eq 'Pass').Count
    $part = ($g.Group | Where-Object Verdict -eq 'Partial').Count
    $fail = ($g.Group | Where-Object Verdict -eq 'Fail').Count
    $na   = ($g.Group | Where-Object Verdict -eq 'NotApplicable').Count + ($g.Group | Where-Object Verdict -eq 'NoExpectations').Count
    $tot  = $g.Count
    $semPct = if ($tot - $na -gt 0) { [int](100 * ($pass + 0.5 * $part) / ($tot - $na)) } else { 0 }
    $summary += [PSCustomObject]@{ Shape=$g.Name; Total=$tot; Pass=$pass; Partial=$part; Fail=$fail; NA=$na; SemPct=$semPct }
}
$summary | Format-Table -AutoSize

"`n--- Failures with issues ---"
$results | Where-Object Verdict -in 'Fail','Partial' | Sort-Object Code | Format-Table Code, Verdict, Passed, Total, @{Name='Issue';Expression={$_.Issues.Substring(0, [Math]::Min(120, $_.Issues.Length))}} -AutoSize -Wrap

# Output to file
$reportPath = "C:\Work\Lern\Improve\v2\AISupportAnalysisPlatform\Tests\verifier-report-session-$SessionId.txt"
$summary | Format-Table -AutoSize | Out-File $reportPath
$results | Where-Object Verdict -in 'Fail','Partial' | Format-Table -AutoSize -Wrap | Out-File -Append $reportPath
"`nReport written: $reportPath"
