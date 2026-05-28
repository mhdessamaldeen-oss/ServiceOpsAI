#requires -Version 7
# Re-runs every shape-isolated suite against the dialect-refactored compiler.
# Models on fill-missing-suites.ps1: fresh login per suite (avoids the alternating-AF-token bug)
# and per-session stall detection (avoids the post-18-traces hang).

param(
    # Default = the 15 suites that still need rerunning after the dialect refactor.
    # COUNT already validated standalone (session 147, 22/22, 0 errors).
    [string[]]$Suites = @(
        "shapes/suite-shape-LOOKUP-2026-05-28.json",
        "shapes/suite-shape-FILTER-2026-05-28.json",
        "shapes/suite-shape-AGGREGATE-2026-05-28.json",
        "shapes/suite-shape-TOPN-2026-05-28.json",
        "shapes/suite-shape-GROUPBY-2026-05-28.json",
        "shapes/suite-shape-JOIN-2026-05-28.json",
        "shapes/suite-shape-TIMESERIES-2026-05-28.json",
        "shapes/suite-shape-COMPARE-2026-05-28.json",
        "shapes/suite-shape-WINDOW-2026-05-28.json",
        "shapes/suite-shape-EXISTS-2026-05-28.json",
        "shapes/suite-shape-HAVING-2026-05-28.json",
        "shapes/suite-shape-SELFJOIN-2026-05-28.json",
        "shapes/suite-shape-UNION-2026-05-28.json",
        "shapes/suite-shape-RECURSIVE-2026-05-28.json",
        "shapes/suite-shape-SAFETY-2026-05-28.json"
    ),
    [string]$BaseUrl = "https://localhost:8899",
    [string]$DbServer = "PC\SQLEXPRESS",
    [string]$DbName = "AISupportAnalysisDB"
)

[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$logPath = "C:\Work\Lern\Improve\v2\AISupportAnalysisPlatform\Tests\dialect-baseline-$(Get-Date -Format 'yyyy-MM-dd-HHmm').log"
$null = New-Item -Path $logPath -ItemType File -Force

function Log([string]$m) {
    $line = "$(Get-Date -Format 'HH:mm:ss') $m"
    Write-Host $line
    Add-Content -Path $logPath -Value $line
}

function Get-CaseCount([string]$suiteFile) {
    $path = "C:\Work\Lern\Improve\v2\AISupportAnalysisPlatform\Areas\SuperAdminCopilot\Configuration\QuestionSuites\$($suiteFile -replace '/','\')"
    $json = Get-Content $path -Raw | ConvertFrom-Json
    if ($json.Scenarios) { return $json.Scenarios.Count }
    if ($json.cases)     { return $json.cases.Count }
    return 0
}

function FreshTrigger($suiteFile) {
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $lp = Invoke-WebRequest -Uri "$BaseUrl/Identity/Account/Login" -WebSession $s -UseBasicParsing -SkipCertificateCheck
    $t1 = [regex]::Match($lp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    $body = @{ "Input.Email"="admin@tech.local"; "Input.Password"="Admin@123"; "Input.RememberMe"="false"; "__RequestVerificationToken"=$t1 }
    try { Invoke-WebRequest -Uri "$BaseUrl/Identity/Account/Login" -Method POST -Body $body -WebSession $s -UseBasicParsing -SkipCertificateCheck -MaximumRedirection 0 -ErrorAction Stop | Out-Null } catch { }
    $pg = Invoke-WebRequest -Uri "$BaseUrl/AiAnalysis/CopilotAssessment" -WebSession $s -UseBasicParsing -SkipCertificateCheck
    $t2 = [regex]::Match($pg.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    $payload = "{`"SuiteFiles`":[`"$suiteFile`"]}"
    $headers = @{ "RequestVerificationToken" = $t2; "X-Requested-With" = "XMLHttpRequest" }
    $r = Invoke-WebRequest -Uri "$BaseUrl/AiAnalysis/RunCopilotAssessment" -Method POST -Body $payload -ContentType "application/json" -Headers $headers -WebSession $s -UseBasicParsing -SkipCertificateCheck
    return ($r.Content | ConvertFrom-Json).data
}

function WaitDone($sessionId, $expected) {
    while ($true) {
        Start-Sleep 45
        $row = (sqlcmd -S $DbServer -d $DbName -E -h-1 -W -s"|" -Q "SET NOCOUNT ON; SELECT COUNT(*), DATEDIFF(SECOND, ISNULL(MAX(CreatedAt), '2000-01-01'), SYSUTCDATETIME()) FROM CopilotTraceHistories WHERE SessionId=$sessionId" | Select-Object -First 1)
        $parts = $row.Split('|')
        $cnt = [int]$parts[0].Trim()
        $stall = [int]$parts[1].Trim()
        Log "  s=$sessionId traces=$cnt/$expected stall=${stall}s"
        if ($cnt -ge $expected) { return $cnt }
        if ($cnt -gt 0 -and $stall -gt 300) { Log "  STALL: moving on"; return $cnt }
        if ($cnt -eq 0 -and $stall -gt 120 -and $stall -lt 99000) { Log "  never started"; return 0 }
    }
}

$results = @()
foreach ($suite in $Suites) {
    $expected = Get-CaseCount $suite
    Log "▶ $suite (expected $expected cases)"
    try {
        $data = FreshTrigger -suiteFile $suite
        Log "  sessionId=$($data.sessionId) totalCases=$($data.totalCases)"
        $got = WaitDone -sessionId $data.sessionId -expected $expected

        # Tally pass / fail for this session. "Pass" = compiler + executor produced an answer
        # with no error logged. Matches the prior baseline's pass criterion.
        $tally = (sqlcmd -S $DbServer -d $DbName -E -h-1 -W -s"|" -Q "SET NOCOUNT ON; SELECT
            ISNULL(SUM(CASE WHEN ErrorMessage IS NULL     THEN 1 ELSE 0 END), 0),
            ISNULL(SUM(CASE WHEN ErrorMessage IS NOT NULL THEN 1 ELSE 0 END), 0),
            COUNT(*)
        FROM CopilotTraceHistories WHERE SessionId=$($data.sessionId)" | Select-Object -First 1).Split('|')
        $okCount  = [int]$tally[0].Trim()
        $errCount = [int]$tally[1].Trim()
        $total    = [int]$tally[2].Trim()

        $results += [PSCustomObject]@{
            Suite     = $suite
            SessionId = $data.sessionId
            Traces    = $got
            Expected  = $expected
            Ok        = $okCount
            Errors    = $errCount
            Total     = $total
            PassPct   = if ($total -gt 0) { [math]::Round(100.0 * $okCount / $total, 1) } else { 0 }
        }
        Log "  ✓ ok=$okCount errors=$errCount total=$total"
    } catch {
        Log "  ✗ $($_.Exception.Message)"
        $results += [PSCustomObject]@{ Suite=$suite; SessionId=$null; Traces=0; Expected=$expected; Ok=0; Errors=0; Total=0; PassPct=0 }
    }
    Start-Sleep 10
}

Log "─── Done ──"
$results | Format-Table -AutoSize | Out-String | Add-Content -Path $logPath
$results | Format-Table -AutoSize

$grandOk    = ($results | Measure-Object -Property Ok -Sum).Sum
$grandTotal = ($results | Measure-Object -Property Total -Sum).Sum
$grandPct   = if ($grandTotal -gt 0) { [math]::Round(100.0 * $grandOk / $grandTotal, 1) } else { 0 }
Log "OVERALL: $grandOk / $grandTotal = ${grandPct}%  (baseline 94.3%)"

"Log: $logPath"
