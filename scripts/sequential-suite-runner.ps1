#requires -Version 7
<#
.SYNOPSIS
  Runs each shape suite ONE AT A TIME (single-suite trigger). Polls each session to completion
  before triggering the next. Resilient to the multi-suite-mode stall observed in sessions 113-114.

.NOTES
  Designed for unattended overnight runs. Logs progress + final per-session counts to a log file.
#>
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$logPath = "C:\Work\Lern\Improve\v2\AISupportAnalysisPlatform\Tests\sequential-suite-run-$(Get-Date -Format 'yyyy-MM-dd-HHmm').log"
$null = New-Item -Path $logPath -ItemType File -Force

function Log([string]$m) {
    $line = "$(Get-Date -Format 'HH:mm:ss') $m"
    Write-Host $line
    Add-Content -Path $logPath -Value $line
}

function Login {
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $lp = Invoke-WebRequest -Uri "https://localhost:8899/Identity/Account/Login" -WebSession $s -UseBasicParsing -SkipCertificateCheck
    $t1 = [regex]::Match($lp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    $body = @{ "Input.Email"="admin@tech.local"; "Input.Password"="Admin@123"; "Input.RememberMe"="false"; "__RequestVerificationToken"=$t1 }
    try { Invoke-WebRequest -Uri "https://localhost:8899/Identity/Account/Login" -Method POST -Body $body -WebSession $s -UseBasicParsing -SkipCertificateCheck -MaximumRedirection 0 -ErrorAction Stop | Out-Null } catch { }
    return $s
}

function Get-FreshAFToken($session) {
    $pg = Invoke-WebRequest -Uri "https://localhost:8899/AiAnalysis/CopilotAssessment" -WebSession $session -UseBasicParsing -SkipCertificateCheck
    return [regex]::Match($pg.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
}

function Trigger-Suite($session, $suiteFile) {
    $token = Get-FreshAFToken $session
    $payload = "{`"SuiteFiles`":[`"$suiteFile`"]}"
    $headers = @{ "RequestVerificationToken" = $token; "X-Requested-With" = "XMLHttpRequest" }
    $r = Invoke-WebRequest -Uri "https://localhost:8899/AiAnalysis/RunCopilotAssessment" -Method POST -Body $payload -ContentType "application/json" -Headers $headers -WebSession $session -UseBasicParsing -SkipCertificateCheck
    $obj = $r.Content | ConvertFrom-Json
    return $obj.data
}

function Wait-ForSession($sessionId, $expected, $stallTolerance = 360) {
    while ($true) {
        Start-Sleep 45
        $cnt = (sqlcmd -S "PC\SQLEXPRESS" -d AISupportAnalysisDB -E -h-1 -W -s"|" -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM CopilotTraceHistories WHERE SessionId=$sessionId" | Select-Object -First 1).Trim()
        $stall = (sqlcmd -S "PC\SQLEXPRESS" -d AISupportAnalysisDB -E -h-1 -W -s"|" -Q "SET NOCOUNT ON; SELECT DATEDIFF(SECOND, ISNULL(MAX(CreatedAt), '2000-01-01'), SYSUTCDATETIME()) FROM CopilotTraceHistories WHERE SessionId=$sessionId" | Select-Object -First 1).Trim()
        Log "  session=$sessionId traces=$cnt/$expected stall=${stall}s"
        if ([int]$cnt -ge $expected) { return $true }
        if ([int]$cnt -gt 0 -and [int]$stall -gt $stallTolerance) { Log "  STALLED >${stallTolerance}s; moving on"; return $false }
        # Edge case: no traces ever started (e.g. orchestrator died). Give 90s grace before declaring stalled.
        if ([int]$cnt -eq 0 -and [int]$stall -gt 90 -and [int]$stall -lt 99000) { Log "  NEVER STARTED, moving on"; return $false }
    }
}

# Suite list — ordered foundation-first
$suites = @(
    @{ File="shapes/suite-shape-LOOKUP-2026-05-28.json";     Expected=16 },
    @{ File="shapes/suite-shape-FILTER-2026-05-28.json";     Expected=16 },
    @{ File="shapes/suite-shape-AGGREGATE-2026-05-28.json";  Expected=18 },
    @{ File="shapes/suite-shape-TOPN-2026-05-28.json";       Expected=16 },
    @{ File="shapes/suite-shape-GROUPBY-2026-05-28.json";    Expected=15 },
    @{ File="shapes/suite-shape-JOIN-2026-05-28.json";       Expected=15 },
    @{ File="shapes/suite-shape-TIMESERIES-2026-05-28.json"; Expected=16 },
    @{ File="shapes/suite-shape-COMPARE-2026-05-28.json";    Expected=16 },
    @{ File="shapes/suite-shape-WINDOW-2026-05-28.json";     Expected=12 },
    @{ File="shapes/suite-shape-EXISTS-2026-05-28.json";     Expected=10 },
    @{ File="shapes/suite-shape-HAVING-2026-05-28.json";     Expected=7 },
    @{ File="shapes/suite-shape-SELFJOIN-2026-05-28.json";   Expected=6 },
    @{ File="shapes/suite-shape-UNION-2026-05-28.json";      Expected=6 },
    @{ File="shapes/suite-shape-RECURSIVE-2026-05-28.json";  Expected=7 },
    @{ File="shapes/suite-shape-SAFETY-2026-05-28.json";     Expected=15 }
)

Log "─── Sequential suite runner started ──"
$session = Login
Log "Logged in as admin@tech.local"

$results = @()
foreach ($suite in $suites) {
    Log "▶ Triggering $($suite.File)"
    try {
        $data = Trigger-Suite -session $session -suiteFile $suite.File
        Log "  → sessionId=$($data.sessionId) totalCases=$($data.totalCases)"
        $ok = Wait-ForSession -sessionId $data.sessionId -expected $suite.Expected -stallTolerance 360
        $cnt = (sqlcmd -S "PC\SQLEXPRESS" -d AISupportAnalysisDB -E -h-1 -W -s"|" -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM CopilotTraceHistories WHERE SessionId=$($data.sessionId)" | Select-Object -First 1).Trim()
        $fails = (sqlcmd -S "PC\SQLEXPRESS" -d AISupportAnalysisDB -E -h-1 -W -s"|" -Q "SET NOCOUNT ON; SELECT SUM(CASE WHEN ErrorMessage IS NOT NULL THEN 1 ELSE 0 END) FROM CopilotTraceHistories WHERE SessionId=$($data.sessionId)" | Select-Object -First 1).Trim()
        $results += [PSCustomObject]@{ Suite=$suite.File; SessionId=$data.sessionId; Traces=$cnt; Expected=$suite.Expected; Failures=$fails; Complete=$ok }
        Log "✓ $($suite.File): traces=$cnt/$($suite.Expected) failures=$fails complete=$ok"
    } catch {
        Log "✗ $($suite.File) trigger failed: $($_.Exception.Message)"
        # Re-login on error (token may have expired)
        try { $session = Login } catch { Log "  re-login also failed"; break }
    }
    # Small breather between suites — let the previous suite's resources drain
    Start-Sleep 15
}

Log "─── Sequential run complete ──"
$results | Format-Table -AutoSize | Out-File -Append $logPath
$results | Format-Table -AutoSize
"Report: $logPath"
