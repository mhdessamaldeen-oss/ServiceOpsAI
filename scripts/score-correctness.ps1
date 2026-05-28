#requires -Version 7
# Correctness scorer for shape-suite trace runs.
#
# Two-stage gate:
#   1. Schema check — regex-match the emitted SQL against the Scenario's Expected* fields
#      (root entity, aggregation function+column, filter columns, operation shape).
#      Cheap, deterministic, runs offline. Catches "wrong table" / "missing filter" /
#      "wrong aggregation function" classes.
#   2. LLM judge — for schema-passing rows only, ask Ollama whether (Question, Answer) actually
#      answers the question. Catches "correct shape, wrong numbers" / "literal echo" / "no rows".
#
# Output: per-case CSV at Tests/correctness-<timestamp>.csv + a per-suite summary table.

param(
    [int[]]$SessionIds = @(147,148,149,150,151,152,153,154,155,156,157,158,159,160,161,162),
    [string]$DbServer = "PC\SQLEXPRESS",
    [string]$DbName = "AISupportAnalysisDB",
    [string]$OllamaUrl = "http://localhost:11434",
    [string]$JudgeModel = "qwen2.5-coder:7b",
    [switch]$SkipJudge,
    [string]$SuitesRoot = "C:\Work\Lern\Improve\v2\AISupportAnalysisPlatform\Areas\SuperAdminCopilot\Configuration\QuestionSuites"
)

$ts = Get-Date -Format 'yyyyMMdd-HHmm'
$csvPath = "C:\Work\Lern\Improve\v2\AISupportAnalysisPlatform\Tests\correctness-$ts.csv"
$logPath = "C:\Work\Lern\Improve\v2\AISupportAnalysisPlatform\Tests\correctness-$ts.log"
$null = New-Item -Path $csvPath -ItemType File -Force
$null = New-Item -Path $logPath -ItemType File -Force

function Log([string]$m) {
    $line = "$(Get-Date -Format 'HH:mm:ss') $m"
    Write-Host $line
    Add-Content -Path $logPath -Value $line
}

# Cache loaded suite JSONs by SourceSuite path so we don't re-read per case.
# DB stores SourceSuite as the bare stem (no "shapes/" prefix, no ".json"). Probe a few
# candidate locations so the same scorer handles legacy-non-shape suites and the new shape/ tree.
$suiteCache = @{}
function LoadSuite([string]$sourceSuite) {
    if ($suiteCache.ContainsKey($sourceSuite)) { return $suiteCache[$sourceSuite] }
    $name = $sourceSuite -replace '/','\'
    if (-not $name.EndsWith('.json', [System.StringComparison]::OrdinalIgnoreCase)) {
        $name = "$name.json"
    }
    $candidates = @(
        (Join-Path $SuitesRoot $name),
        (Join-Path $SuitesRoot ("shapes\$name")),
        (Join-Path $SuitesRoot ("archive\$name"))
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) { return $null }
    $json = Get-Content $found -Raw | ConvertFrom-Json
    $byCode = @{}
    foreach ($s in $json.Scenarios) { $byCode[$s.Code] = $s }
    $suiteCache[$sourceSuite] = $byCode
    return $byCode
}

# Schema check: returns @{ Pass=$bool; Reason=$string }. Pure structural fit against Expected* fields.
function SchemaCheck($scenario, $sql) {
    if ([string]::IsNullOrWhiteSpace($sql)) { return @{ Pass=$false; Reason="no SQL emitted" } }

    # 1. Root entity must appear as FROM target.
    if ($scenario.ExpectedPrimaryEntity) {
        $rootRx = "FROM\s+\[?$([regex]::Escape($scenario.ExpectedPrimaryEntity))\]?"
        if ($sql -notmatch $rootRx) {
            return @{ Pass=$false; Reason="wrong root entity (expected $($scenario.ExpectedPrimaryEntity))" }
        }
    }

    # 2. Operation shape — each shape has a structural marker.
    switch ($scenario.ExpectedOperation) {
        "COUNT"      { if ($sql -notmatch "COUNT\s*\(")            { return @{ Pass=$false; Reason="no COUNT" } } }
        "GROUPBY"    { if ($sql -notmatch "GROUP\s+BY")            { return @{ Pass=$false; Reason="no GROUP BY" } } }
        "TOPN"       { if ($sql -notmatch "TOP\s*\(|LIMIT\s+\d+")  { return @{ Pass=$false; Reason="no TOP/LIMIT" } } }
        "HAVING"     { if ($sql -notmatch "HAVING")                { return @{ Pass=$false; Reason="no HAVING" } } }
        "TIMESERIES" { if ($sql -notmatch "DATEPART|DATEFROMPARTS|DATE_TRUNC|FORMAT\(|DATEADD\(|MAKE_DATE") { return @{ Pass=$false; Reason="no time-bucket function" } } }
        "WINDOW"     { if ($sql -notmatch "OVER\s*\(")             { return @{ Pass=$false; Reason="no window function" } } }
    }

    # 3. Expected aggregations: SUM(...) / AVG(...) / COUNT(...) must appear with the right column (when given).
    if ($scenario.ExpectedAggregations) {
        foreach ($a in $scenario.ExpectedAggregations) {
            $fn = $a.function
            if ($fn) {
                $needle = if ($a.column -and $a.column -ne "*") {
                    # Match e.g. `SUM([Bills].[TotalAmount])`. Strip table-qualifier from match so SUM(Bills.TotalAmount) or SUM(TotalAmount) both pass.
                    $colBare = $a.column.Split('.')[-1]
                    "$fn\s*\(\s*(\[[\w]+\]\.)?\[?$([regex]::Escape($colBare))\]?"
                } else {
                    "$fn\s*\("
                }
                if ($sql -notmatch $needle) {
                    return @{ Pass=$false; Reason="missing $fn aggregation" }
                }
            }
        }
    }

    # 4. Expected filter columns must appear (value match is loose — we only check the column reference is there).
    if ($scenario.ExpectedFilters) {
        foreach ($f in $scenario.ExpectedFilters) {
            if (-not $f.Column) { continue }
            $colBare = $f.Column.Split('.')[-1]
            $colRx = "\[?$([regex]::Escape($colBare))\]?"
            if ($sql -notmatch $colRx) {
                return @{ Pass=$false; Reason="missing filter column $($f.Column)" }
            }
        }
    }

    # 5. Expected HAVING clauses
    if ($scenario.ExpectedHaving) {
        if ($sql -notmatch "HAVING") {
            return @{ Pass=$false; Reason="missing HAVING clause" }
        }
    }

    return @{ Pass=$true; Reason="ok" }
}

# LLM judge — strict structured prompt, returns "PASS" / "FAIL: <reason>".
function LlmJudge([string]$question, [string]$answer) {
    if ($SkipJudge) { return @{ Pass=$true; Reason="judge-skipped" } }
    if ([string]::IsNullOrWhiteSpace($answer)) { return @{ Pass=$false; Reason="empty answer" } }

    $prompt = @"
You are a strict grader. You will be given a USER QUESTION and an ASSISTANT ANSWER produced by a SQL-generating copilot.

Decide whether the ANSWER actually answers the question correctly in shape and intent.

Rules:
- PASS only if the answer addresses the question's actual intent (right entity, right counted/measured thing, right scope).
- FAIL if the answer is empty, says "no data found" without justification, lists wrong entities, picks the wrong metric, or returns counts that contradict the question's filter.
- Ignore minor formatting differences. Numbers and rendering style are not graded — only correctness of intent.
- Reply on ONE line starting with PASS or FAIL, followed by a colon and a brief reason (≤ 20 words).

USER QUESTION: $question

ASSISTANT ANSWER:
$answer

VERDICT:
"@

    $body = @{
        model = $JudgeModel
        prompt = $prompt
        stream = $false
        options = @{ temperature = 0.0; num_predict = 80 }
    } | ConvertTo-Json -Depth 5 -Compress

    try {
        $resp = Invoke-RestMethod -Uri "$OllamaUrl/api/generate" -Method POST -Body $body -ContentType "application/json" -TimeoutSec 90
        $verdict = $resp.response.Trim()
        if ($verdict -match "^PASS") {
            return @{ Pass=$true; Reason=$verdict }
        } else {
            return @{ Pass=$false; Reason=$verdict }
        }
    } catch {
        return @{ Pass=$false; Reason="judge error: $($_.Exception.Message)" }
    }
}

# ── Main loop ─────────────────────────────────────────────────────────────────
# Use SqlClient directly: avoids sqlcmd column-truncation and tab-delimiter quoting issues
# on cells that contain GeneratedScript bodies with embedded newlines/tabs/quotes.
Add-Type -AssemblyName "System.Data"
$connStr = "Server=$DbServer;Database=$DbName;Integrated Security=True;TrustServerCertificate=True;"
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$conn.Open()
try {
    $sessionList = $SessionIds -join ','
    $cmd = $conn.CreateCommand()
    $cmd.CommandTimeout = 60
    $cmd.CommandText = "SELECT SessionId, CaseCode, SourceSuite, Question, GeneratedScript, Answer, ErrorMessage FROM CopilotTraceHistories WHERE SessionId IN ($sessionList) ORDER BY SessionId, Id"
    $reader = $cmd.ExecuteReader()
    $dataRows = New-Object System.Collections.Generic.List[hashtable]
    while ($reader.Read()) {
        $dataRows.Add(@{
            SessionId       = $reader.GetValue(0)
            CaseCode        = if ($reader.IsDBNull(1)) { "" } else { $reader.GetString(1) }
            SourceSuite     = if ($reader.IsDBNull(2)) { "" } else { $reader.GetString(2) }
            Question        = if ($reader.IsDBNull(3)) { "" } else { $reader.GetString(3) }
            GeneratedScript = if ($reader.IsDBNull(4)) { "" } else { $reader.GetString(4) }
            Answer          = if ($reader.IsDBNull(5)) { "" } else { $reader.GetString(5) }
            ErrorMessage    = if ($reader.IsDBNull(6)) { "" } else { $reader.GetString(6) }
        })
    }
    $reader.Close()
} finally {
    $conn.Close()
}
Log "Loaded $($dataRows.Count) traces from sessions: $sessionList"

# Write CSV header
"SessionId,CaseCode,SourceSuite,ExecOk,SchemaPass,SchemaReason,JudgePass,JudgeReason,Final" | Set-Content -Path $csvPath -Encoding UTF8

$results = New-Object System.Collections.Generic.List[object]
foreach ($row in $dataRows) {
    $sid          = $row.SessionId
    $caseCode     = $row.CaseCode
    $sourceSuite  = $row.SourceSuite
    $question     = $row.Question
    $sql          = $row.GeneratedScript
    $answer       = $row.Answer
    $errorMsg     = $row.ErrorMessage
    $execOk       = [string]::IsNullOrWhiteSpace($errorMsg)

    if (-not $sourceSuite) { continue }
    $suite = LoadSuite $sourceSuite
    if (-not $suite -or -not $suite.ContainsKey($caseCode)) {
        $results.Add([PSCustomObject]@{
            SessionId=$sid; CaseCode=$caseCode; SourceSuite=$sourceSuite;
            ExecOk=$execOk; SchemaPass=$false; SchemaReason="case not in suite";
            JudgePass=$false; JudgeReason="skipped"; Final="MISSING"
        })
        continue
    }
    $scenario = $suite[$caseCode]

    # Stage 1 — schema check on the emitted SQL.
    $schemaResult = SchemaCheck $scenario $sql
    if (-not $execOk) {
        # Execution error supersedes schema concerns; record it.
        $final = "EXEC_FAIL"
        $results.Add([PSCustomObject]@{
            SessionId=$sid; CaseCode=$caseCode; SourceSuite=$sourceSuite;
            ExecOk=$false; SchemaPass=$schemaResult.Pass; SchemaReason=$schemaResult.Reason;
            JudgePass=$false; JudgeReason="exec failed"; Final=$final
        })
        Log "  [$sid/$caseCode] EXEC_FAIL"
        continue
    }
    if (-not $schemaResult.Pass) {
        $results.Add([PSCustomObject]@{
            SessionId=$sid; CaseCode=$caseCode; SourceSuite=$sourceSuite;
            ExecOk=$true; SchemaPass=$false; SchemaReason=$schemaResult.Reason;
            JudgePass=$false; JudgeReason="schema-fail"; Final="SCHEMA_FAIL"
        })
        Log "  [$sid/$caseCode] SCHEMA_FAIL: $($schemaResult.Reason)"
        continue
    }

    # Stage 2 — LLM judge.
    $judge = LlmJudge $question $answer
    $final = if ($judge.Pass) { "PASS" } else { "JUDGE_FAIL" }
    $results.Add([PSCustomObject]@{
        SessionId=$sid; CaseCode=$caseCode; SourceSuite=$sourceSuite;
        ExecOk=$true; SchemaPass=$true; SchemaReason="ok";
        JudgePass=$judge.Pass; JudgeReason=$judge.Reason; Final=$final
    })
    Log "  [$sid/$caseCode] $final  $($judge.Reason)"
}

# Write all rows to CSV.
foreach ($r in $results) {
    $esc = { param($s) if ($null -eq $s) { "" } else { '"' + ($s -replace '"','""') + '"' } }
    $line = "$($r.SessionId),$($r.CaseCode),$($r.SourceSuite),$($r.ExecOk),$($r.SchemaPass),$(& $esc $r.SchemaReason),$($r.JudgePass),$(& $esc $r.JudgeReason),$($r.Final)"
    Add-Content -Path $csvPath -Value $line
}

# Summary by suite
Log "─── Per-suite summary ──"
$grouped = $results | Group-Object -Property SourceSuite
foreach ($g in $grouped) {
    $total      = $g.Count
    $passes     = ($g.Group | Where-Object { $_.Final -eq "PASS" }).Count
    $schemaFail = ($g.Group | Where-Object { $_.Final -eq "SCHEMA_FAIL" }).Count
    $judgeFail  = ($g.Group | Where-Object { $_.Final -eq "JUDGE_FAIL" }).Count
    $execFail   = ($g.Group | Where-Object { $_.Final -eq "EXEC_FAIL"  }).Count
    $pct        = if ($total -gt 0) { [math]::Round(100.0 * $passes / $total, 1) } else { 0 }
    Log ("  {0,-60} {1,3}/{2,3}  ({3,5}%)  exec={4}  schema={5}  judge={6}" -f $g.Name,$passes,$total,$pct,$execFail,$schemaFail,$judgeFail)
}

$gTotal  = $results.Count
$gPass   = ($results | Where-Object { $_.Final -eq "PASS" }).Count
$gPct    = if ($gTotal -gt 0) { [math]::Round(100.0 * $gPass / $gTotal, 1) } else { 0 }
Log "OVERALL CORRECTNESS: $gPass / $gTotal = ${gPct}%"
Log "Detailed CSV: $csvPath"
