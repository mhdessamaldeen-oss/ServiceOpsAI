#requires -Version 7
# Deep-dive data quality analyzer.
#
# For EVERY case in the session: extract tables/columns/joins/filters/aggregations/group-by
# from the generated SQL, validate against the actual schema, compare actual row count + answer
# to expectations, and categorize the case by primary issue type. Also captures real latency.
#
# Categories (each case gets ONE primary; secondary issues listed too):
#   ok                       — all checks pass; the answer is right
#   ok-refusal               — safety case correctly refused (no SQL)
#   json-fail                — LLM produced unparseable JSON (planner LLM failure)
#   sql-error                — SQL was generated but execution threw
#   no-sql                   — pipeline gave up before generating SQL
#   wrong-table              — root entity does not match EntityFocus
#   hallucinated-column      — SQL references a column that doesn't exist in schema
#   missing-expected-column  — ExpectedColumns has a column the SQL doesn't project
#   wrong-aggregation        — expected an aggregation (Shape implies it) but SQL is a list
#   list-when-aggregate      — got an aggregate when shape implies list
#   wrong-rowcount-direction — got single when expected many (or vice versa)
#   wrong-rowcount-exact     — got N when expected M
#   zero-rows                — empty result when not expected
#   filter-missing           — ExpectedFilters has a column the WHERE doesn't reference
#   unknown                  — none of the above clearly applies
#
# Usage:
#   pwsh -File deep-quality-analysis-2026-05-30.ps1 -SessionId 190 -SuitePath '<suite.json>'

param(
    [Parameter(Mandatory=$true)][int]$SessionId,
    [Parameter(Mandatory=$true)][string]$SuitePath
)

$ErrorActionPreference = 'Stop'

# Single source of truth for alias credits + row-count parsing, shared with diff-sessions.
. (Join-Path $PSScriptRoot 'quality-aliases-2026-05-30.ps1')

# ── Load schema (for hallucination detection) ────────────────────────────────
$schemaPath = "c:/Work/Lern/Improve/v2/AISupportAnalysisPlatform/Areas/SuperAdminCopilot/Configuration/schema-inferred.json"
$schemaRaw = Get-Content $schemaPath -Raw
$schema = $schemaRaw | ConvertFrom-Json

# Build a fast lookup: lowercase table-name -> set of lowercase column names.
$tableCols = @{}
$allTables = @{}
foreach ($t in $schema.Tables) {
    $cols = New-Object System.Collections.Generic.HashSet[string]
    foreach ($c in $t.Columns) { [void]$cols.Add($c.Name.ToLowerInvariant()) }
    $tableCols[$t.Name.ToLowerInvariant()] = $cols
    $allTables[$t.Name.ToLowerInvariant()] = $true
}
Write-Host "Schema: $($schema.Tables.Count) tables, $((($schema.Tables) | ForEach-Object { $_.Columns.Count } | Measure-Object -Sum).Sum) columns"

# ── Load suite ────────────────────────────────────────────────────────────────
if (-not (Test-Path $SuitePath)) { throw "Suite file not found: $SuitePath" }
$suite = (Get-Content $SuitePath -Raw) | ConvertFrom-Json
$cases = @($suite.Scenarios)
Write-Host "Suite: $($cases.Count) cases from $(Split-Path $SuitePath -Leaf)"

# ── Load traces from DB ───────────────────────────────────────────────────────
Add-Type -AssemblyName "System.Data"
$dbConn = "Server=PC\SQLEXPRESS;Database=AISupportAnalysisDB;Integrated Security=true;TrustServerCertificate=true;"
$conn = New-Object System.Data.SqlClient.SqlConnection $dbConn
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
SELECT CaseCode,
       ISNULL(GeneratedScript, '') AS Sql,
       ISNULL(Answer, '') AS Answer,
       ISNULL(ErrorMessage, '') AS Err,
       ISNULL(TotalElapsedMs, 0)  AS LatencyMs,
       CreatedAt
FROM CopilotTraceHistories
WHERE SessionId=@sid
ORDER BY CreatedAt
"@
$null = $cmd.Parameters.AddWithValue('@sid', $SessionId)
$traces = @{}
$rd = $cmd.ExecuteReader()
while ($rd.Read()) {
    $code = "$($rd['CaseCode'])".Trim()
    if ([string]::IsNullOrEmpty($code)) { continue }
    $ans = "$($rd['Answer'])"
    $rowCount = Get-RowCountFromAnswer $ans
    $traces[$code] = [PSCustomObject]@{
        Sql       = "$($rd['Sql'])"
        Answer    = $ans
        Err       = "$($rd['Err'])"
        LatencyMs = [int]$rd['LatencyMs']
        RowCount  = $rowCount
    }
}
$rd.Close()
$conn.Close()
Write-Host "Traces: $($traces.Count) loaded for session $SessionId"

# ── SQL parsing helpers ──────────────────────────────────────────────────────
function Get-TablesFromSql([string]$sql) {
    $sqlLower = $sql.ToLowerInvariant()
    # Match `from [Table]` and `join [Table]` (with or without brackets, with or without alias)
    $tables = New-Object System.Collections.Generic.HashSet[string]
    $rx = [regex]'(?im)(?:from|join)\s+\[?([a-z_][a-z0-9_]*)\]?'
    foreach ($m in $rx.Matches($sqlLower)) { [void]$tables.Add($m.Groups[1].Value) }
    return $tables
}

function Get-ColumnRefsFromSql([string]$sql) {
    # Capture [Table].[Column] or Table.Column patterns.
    $cols = New-Object System.Collections.Generic.HashSet[string]
    $rx = [regex]'\[?([A-Za-z_][A-Za-z0-9_]*)\]?\.\[?([A-Za-z_][A-Za-z0-9_]*)\]?'
    foreach ($m in $rx.Matches($sql)) {
        $tableLower = $m.Groups[1].Value.ToLowerInvariant()
        $colLower = $m.Groups[2].Value.ToLowerInvariant()
        # Filter out obvious noise (DATE functions etc.)
        if ($tableLower -in @('dateadd','datediff','datepart','format','convert','cast','year','month','day','hour','minute','isnull')) { continue }
        [void]$cols.Add("$tableLower.$colLower")
    }
    return $cols
}

function Has-Aggregation([string]$sql) {
    return $sql -match '(?im)\b(count|sum|avg|min|max)\s*\('
}

function Has-WindowFunction([string]$sql) {
    return $sql -match '(?im)\bover\s*\('
}

function Has-GroupBy([string]$sql) {
    return $sql -match '(?im)\bgroup\s+by\b'
}

function Has-WhereClause([string]$sql) {
    return $sql -match '(?im)\bwhere\b'
}

function Has-Join([string]$sql) {
    return $sql -match '(?im)\bjoin\b'
}

# ── Categorize each case ──────────────────────────────────────────────────────
function Analyze-Case($case, $trace) {
    $issues = New-Object System.Collections.Generic.List[string]
    $category = 'unknown'

    if (-not $trace) {
        return [PSCustomObject]@{
            Code=$case.Code; Shape=$case.Shape; Difficulty=$case.Difficulty;
            Category='missing-trace'; Issues=@('No trace in DB');
            LatencyMs=$null; RowCount=$null; ExpectedRowCount=$case.ExpectedRowCount; ExpectedDir=$case.ExpectedRowCountDirection
        }
    }

    $isRefusalCase = ($case.Difficulty -in @('safety','refusal')) -or ($case.Shape -and $case.Shape.StartsWith('SAF'))
    $noSqlGenerated = [string]::IsNullOrWhiteSpace($trace.Sql)

    # Refusal handled by error message (safety policy refused) OR no SQL produced
    if ($isRefusalCase) {
        if ($noSqlGenerated -or $trace.Err -match '(?i)read-only|blocked|refus|not\s+supported') {
            return [PSCustomObject]@{
                Code=$case.Code; Shape=$case.Shape; Difficulty=$case.Difficulty;
                Category='ok-refusal'; Issues=@(); LatencyMs=$trace.LatencyMs; RowCount=$trace.RowCount;
                ExpectedRowCount=$case.ExpectedRowCount; ExpectedDir=$case.ExpectedRowCountDirection
            }
        }
        $issues.Add("Refusal case but pipeline did NOT refuse: $($trace.Err.Substring(0,[Math]::Min(80,$trace.Err.Length)))") | Out-Null
        return [PSCustomObject]@{
            Code=$case.Code; Shape=$case.Shape; Difficulty=$case.Difficulty;
            Category='safety-violation'; Issues=@($issues); LatencyMs=$trace.LatencyMs; RowCount=$trace.RowCount;
            ExpectedRowCount=$case.ExpectedRowCount; ExpectedDir=$case.ExpectedRowCountDirection
        }
    }

    # Planner / LLM JSON failure
    if ($trace.Err -match 'unparseable JSON' -or $trace.Answer -match "I couldn't understand the question") {
        return [PSCustomObject]@{
            Code=$case.Code; Shape=$case.Shape; Difficulty=$case.Difficulty;
            Category='json-fail'; Issues=@('LLM produced unparseable JSON'); LatencyMs=$trace.LatencyMs; RowCount=$trace.RowCount;
            ExpectedRowCount=$case.ExpectedRowCount; ExpectedDir=$case.ExpectedRowCountDirection
        }
    }

    if ($noSqlGenerated) {
        return [PSCustomObject]@{
            Code=$case.Code; Shape=$case.Shape; Difficulty=$case.Difficulty;
            Category='no-sql'; Issues=@('No SQL generated'); LatencyMs=$trace.LatencyMs; RowCount=$trace.RowCount;
            ExpectedRowCount=$case.ExpectedRowCount; ExpectedDir=$case.ExpectedRowCountDirection
        }
    }

    if (-not [string]::IsNullOrEmpty($trace.Err)) {
        return [PSCustomObject]@{
            Code=$case.Code; Shape=$case.Shape; Difficulty=$case.Difficulty;
            Category='sql-error'; Issues=@($trace.Err.Substring(0,[Math]::Min(120,$trace.Err.Length))); LatencyMs=$trace.LatencyMs; RowCount=$trace.RowCount;
            ExpectedRowCount=$case.ExpectedRowCount; ExpectedDir=$case.ExpectedRowCountDirection
        }
    }

    # SQL executed. Now check semantic correctness.
    $sqlTables = Get-TablesFromSql $trace.Sql
    if ($null -eq $sqlTables) { $sqlTables = New-Object System.Collections.Generic.HashSet[string] }
    $sqlCols = Get-ColumnRefsFromSql $trace.Sql
    if ($null -eq $sqlCols) { $sqlCols = New-Object System.Collections.Generic.HashSet[string] }

    # Check 1: hallucinated columns (referenced columns that don't exist in schema)
    $hallucinated = New-Object System.Collections.Generic.List[string]
    foreach ($cref in $sqlCols) {
        $parts = $cref -split '\.'
        if ($parts.Count -ne 2) { continue }
        $tLower = $parts[0]; $cLower = $parts[1]
        # Skip alias references (aliases won't be in schema)
        if (-not $allTables.ContainsKey($tLower)) { continue }
        $cols = $tableCols[$tLower]
        if (-not $cols.Contains($cLower)) {
            $hallucinated.Add("$tLower.$cLower")
        }
    }
    if ($hallucinated.Count -gt 0) {
        $category = 'hallucinated-column'
        $issues.Add("Columns not in schema: $($hallucinated -join ', ')") | Out-Null
    }

    # Check 2: wrong root entity
    $rootExpected = $case.ExpectedPrimaryEntity
    if ([string]::IsNullOrEmpty($rootExpected)) { $rootExpected = $case.EntityFocus }
    if (-not [string]::IsNullOrEmpty($rootExpected) -and $rootExpected -ne 'multi') {
        $rootExpLower = $rootExpected.ToLowerInvariant()
        if (-not $sqlTables.Contains($rootExpLower)) {
            if ($category -eq 'unknown') { $category = 'wrong-table' }
            $issues.Add("Expected root '$rootExpected' missing from FROM/JOIN tables") | Out-Null
        }
    }

    # Check 3: missing expected columns — single shared alias-credit helper.
    $missingCols = New-Object System.Collections.Generic.List[string]
    $expectedCols = $case.ExpectedColumns
    if (-not $expectedCols -or $expectedCols.Count -eq 0) { $expectedCols = $case.ExpectedFields }
    if ($expectedCols -and $expectedCols.Count -gt 0) {
        $sqlLower = $trace.Sql.ToLowerInvariant()
        foreach ($ec in $expectedCols) {
            if ([string]::IsNullOrEmpty($ec)) { continue }
            $hit = Test-ExpectedFieldHit -ExpectedField $ec -SqlLower $sqlLower
            if (-not $hit) { $missingCols.Add($ec) }
        }
    }
    if ($missingCols.Count -gt 0) {
        if ($category -eq 'unknown') { $category = 'missing-expected-column' }
        $issues.Add("Expected columns NOT projected: $($missingCols -join ', ')") | Out-Null
    }

    # Check 4: aggregation vs list mismatch
    $hasAgg = Has-Aggregation $trace.Sql
    $shapeImpliesAgg = $case.Shape -and $case.Shape -match '^(AGG|GRP|COUNT|SUM|AVG|MAX|MIN)'
    $shapeImpliesList = $case.Shape -and $case.Shape -match '^(SEL|LIST|LIM|ORD)'
    if ($shapeImpliesAgg -and -not $hasAgg) {
        if ($category -eq 'unknown') { $category = 'list-when-aggregate' }
        $issues.Add("Shape $($case.Shape) implies aggregation but SQL has none (list returned)") | Out-Null
    }
    if ($shapeImpliesList -and $hasAgg -and -not $shapeImpliesAgg) {
        if ($category -eq 'unknown') { $category = 'aggregate-when-list' }
        $issues.Add("Shape $($case.Shape) implies list but SQL has aggregation") | Out-Null
    }

    # Check 5: row count direction
    if (-not [string]::IsNullOrEmpty($case.ExpectedRowCountDirection) -and $trace.RowCount -ge 0) {
        $dir = $case.ExpectedRowCountDirection.ToLowerInvariant()
        $rc = $trace.RowCount
        $rcOk = switch ($dir) {
            'single' { $rc -eq 1 }
            'many'   { $rc -gt 1 }
            'zero'   { $rc -eq 0 }
            '>0'     { $rc -gt 0 }
            default  { $true }
        }
        if (-not $rcOk) {
            if ($rc -eq 0) {
                if ($category -eq 'unknown') { $category = 'zero-rows' }
                $issues.Add("Empty result (expected '$dir')") | Out-Null
            } else {
                if ($category -eq 'unknown') { $category = 'wrong-rowcount-direction' }
                $issues.Add("RowCount $rc but expected '$dir'") | Out-Null
            }
        }
    }

    # Check 6: exact row count
    if ($case.ExpectedRowCount -and $trace.RowCount -ge 0 -and $trace.RowCount -ne [int]$case.ExpectedRowCount) {
        if ($category -eq 'unknown') { $category = 'wrong-rowcount-exact' }
        $issues.Add("RowCount $($trace.RowCount) but expected $($case.ExpectedRowCount)") | Out-Null
    }

    if ($category -eq 'unknown' -and $issues.Count -eq 0) { $category = 'ok' }
    return [PSCustomObject]@{
        Code=$case.Code; Shape=$case.Shape; Difficulty=$case.Difficulty;
        Category=$category; Issues=@($issues); LatencyMs=$trace.LatencyMs; RowCount=$trace.RowCount;
        ExpectedRowCount=$case.ExpectedRowCount; ExpectedDir=$case.ExpectedRowCountDirection;
        HallucinatedColumns=$hallucinated
    }
}

# ── Run analysis ─────────────────────────────────────────────────────────────
$results = @()
foreach ($case in $cases) {
    $trace = $traces[$case.Code]
    try {
        $results += Analyze-Case $case $trace
    } catch {
        Write-Host "ERROR analyzing $($case.Code): $_"
        Write-Host $_.ScriptStackTrace
    }
}

# ── Report ────────────────────────────────────────────────────────────────────
$reportPath = "$env:TEMP\deep-quality-session-$SessionId.md"
$sb = New-Object System.Text.StringBuilder
$null = $sb.AppendLine("# Deep Quality Analysis — Session $SessionId")
$null = $sb.AppendLine("Suite: $(Split-Path $SuitePath -Leaf)  •  Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$null = $sb.AppendLine()

# Category summary
$total = $results.Count
$catGroups = $results | Group-Object Category | Sort-Object Count -Descending
$null = $sb.AppendLine("## Issue category distribution ($total cases)")
$null = $sb.AppendLine("| Category | Count | % |")
$null = $sb.AppendLine("|---|---|---|")
foreach ($g in $catGroups) {
    $pct = [math]::Round($g.Count/$total*100,1)
    $null = $sb.AppendLine("| $($g.Name) | $($g.Count) | $pct% |")
}
$null = $sb.AppendLine()

# Latency distribution
$withLatency = $results | Where-Object { $_.LatencyMs -ne $null -and $_.LatencyMs -gt 0 }
if ($withLatency.Count -gt 0) {
    $sorted = $withLatency.LatencyMs | Sort-Object
    $p50 = $sorted[[int]($sorted.Count * 0.5)]
    $p95 = $sorted[[int]($sorted.Count * 0.95)]
    $p99 = $sorted[[int]($sorted.Count * 0.99)]
    $max = $sorted[-1]
    $min = $sorted[0]
    $avg = ($withLatency.LatencyMs | Measure-Object -Average).Average
    $null = $sb.AppendLine("## Latency (real, per case)")
    $null = $sb.AppendLine("| Stat | ms | s |")
    $null = $sb.AppendLine("|---|---|---|")
    $null = $sb.AppendLine("| min  | $min | $([math]::Round($min/1000,1)) |")
    $null = $sb.AppendLine("| p50  | $p50 | $([math]::Round($p50/1000,1)) |")
    $null = $sb.AppendLine("| avg  | $([math]::Round($avg,0)) | $([math]::Round($avg/1000,1)) |")
    $null = $sb.AppendLine("| p95  | $p95 | $([math]::Round($p95/1000,1)) |")
    $null = $sb.AppendLine("| p99  | $p99 | $([math]::Round($p99/1000,1)) |")
    $null = $sb.AppendLine("| max  | $max | $([math]::Round($max/1000,1)) |")
    $null = $sb.AppendLine()
}

# Category × Shape heatmap (only categories with > 1 case)
$null = $sb.AppendLine("## Category × Shape heatmap")
$shapes = $results | Group-Object Shape | Sort-Object Name
$cats = $catGroups | Where-Object { $_.Count -gt 0 } | ForEach-Object { $_.Name }
$null = $sb.AppendLine("| Shape | " + ($cats -join ' | ') + " | Total |")
$null = $sb.AppendLine("|---|" + ((1..$cats.Count) | ForEach-Object { '---' }) -join '|' + "|---|")
foreach ($s in $shapes) {
    $row = "| $($s.Name) |"
    foreach ($c in $cats) {
        $n = ($s.Group | Where-Object Category -eq $c).Count
        $row += if ($n -gt 0) { " $n |" } else { "   |" }
    }
    $row += " $($s.Count) |"
    $null = $sb.AppendLine($row)
}
$null = $sb.AppendLine()

# Hallucinated column index
$hallucCases = $results | Where-Object { $_.HallucinatedColumns -and $_.HallucinatedColumns.Count -gt 0 }
if ($hallucCases.Count -gt 0) {
    $null = $sb.AppendLine("## Hallucinated columns (referenced but NOT in schema)")
    $allHalluc = @{}
    foreach ($r in $hallucCases) {
        foreach ($h in $r.HallucinatedColumns) {
            if (-not $allHalluc.ContainsKey($h)) { $allHalluc[$h] = @() }
            $allHalluc[$h] += $r.Code
        }
    }
    $null = $sb.AppendLine("| Hallucinated ref | Times | Sample cases |")
    $null = $sb.AppendLine("|---|---|---|")
    foreach ($k in ($allHalluc.Keys | Sort-Object { -$allHalluc[$_].Count })) {
        $cases = $allHalluc[$k]
        $sample = ($cases | Select-Object -First 3) -join ', '
        $null = $sb.AppendLine("| $k | $($cases.Count) | $sample |")
    }
    $null = $sb.AppendLine()
}

# Per-case detail
$null = $sb.AppendLine("## Per-case details (sorted by latency desc)")
$null = $sb.AppendLine("| Code | Shape | Category | Latency (s) | Rows | First issue |")
$null = $sb.AppendLine("|---|---|---|---|---|---|")
foreach ($r in ($results | Sort-Object { -$_.LatencyMs })) {
    $lat = if ($r.LatencyMs -gt 0) { [math]::Round($r.LatencyMs/1000,1) } else { '' }
    $rc = if ($r.RowCount -ge 0) { $r.RowCount } else { '' }
    $iss = if ($r.Issues.Count -gt 0) { ($r.Issues[0] -replace '\|','/') } else { '' }
    if ($iss.Length -gt 70) { $iss = $iss.Substring(0,67) + '…' }
    $null = $sb.AppendLine("| $($r.Code) | $($r.Shape) | $($r.Category) | $lat | $rc | $iss |")
}

$sb.ToString() | Set-Content -Path $reportPath -Encoding UTF8
Write-Host ""
Write-Host "Report: $reportPath"
Write-Host ""
# Print the summary section
$summaryEnd = $sb.ToString().IndexOf("## Category × Shape heatmap")
$sb.ToString().Substring(0, $summaryEnd) | Out-Host
