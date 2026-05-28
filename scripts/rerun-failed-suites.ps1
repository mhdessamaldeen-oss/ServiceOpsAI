#requires -Version 7
# Re-trigger the 6 remaining suites that had code-fixable failures.
# AGG already re-ran as session 131; SAFETY refusals are correct.

[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$logPath = "C:\Work\Lern\Improve\v2\AISupportAnalysisPlatform\Tests\rerun-failed-$(Get-Date -Format 'yyyy-MM-dd-HHmm').log"
$null = New-Item -Path $logPath -ItemType File -Force
function Log([string]$m) { $line = "$(Get-Date -Format 'HH:mm:ss') $m"; Write-Host $line; Add-Content $logPath $line }

function Trigger($suiteFile) {
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $lp = Invoke-WebRequest -Uri "https://localhost:8899/Identity/Account/Login" -WebSession $s -UseBasicParsing -SkipCertificateCheck
    $t1 = [regex]::Match($lp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    $body = @{ "Input.Email"="admin@tech.local"; "Input.Password"="Admin@123"; "Input.RememberMe"="false"; "__RequestVerificationToken"=$t1 }
    try { Invoke-WebRequest -Uri "https://localhost:8899/Identity/Account/Login" -Method POST -Body $body -WebSession $s -UseBasicParsing -SkipCertificateCheck -MaximumRedirection 0 -ErrorAction Stop | Out-Null } catch { }
    $pg = Invoke-WebRequest -Uri "https://localhost:8899/AiAnalysis/CopilotAssessment" -WebSession $s -UseBasicParsing -SkipCertificateCheck
    $t2 = [regex]::Match($pg.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    $payload = "{`"SuiteFiles`":[`"$suiteFile`"]}"
    $headers = @{ "RequestVerificationToken" = $t2; "X-Requested-With" = "XMLHttpRequest" }
    $r = Invoke-WebRequest -Uri "https://localhost:8899/AiAnalysis/RunCopilotAssessment" -Method POST -Body $payload -ContentType "application/json" -Headers $headers -WebSession $s -UseBasicParsing -SkipCertificateCheck
    return ($r.Content | ConvertFrom-Json).data
}
function WaitDone($sessionId, $expected) {
    while ($true) {
        Start-Sleep 45
        $cnt = ([int](sqlcmd -S "PC\SQLEXPRESS" -d AISupportAnalysisDB -E -h-1 -W -s"|" -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM CopilotTraceHistories WHERE SessionId=$sessionId" | Select-Object -First 1).Trim())
        $stall = ([int](sqlcmd -S "PC\SQLEXPRESS" -d AISupportAnalysisDB -E -h-1 -W -s"|" -Q "SET NOCOUNT ON; SELECT DATEDIFF(SECOND, ISNULL(MAX(CreatedAt), '2000-01-01'), SYSUTCDATETIME()) FROM CopilotTraceHistories WHERE SessionId=$sessionId" | Select-Object -First 1).Trim())
        Log "  s=$sessionId traces=$cnt/$expected stall=${stall}s"
        if ($cnt -ge $expected) { return $cnt }
        if ($cnt -gt 0 -and $stall -gt 360) { Log "  stalled, moving on"; return $cnt }
        if ($cnt -eq 0 -and $stall -gt 90 -and $stall -lt 99000) { Log "  never started"; return 0 }
    }
}

$suites = @(
    @{ File="shapes/suite-shape-GROUPBY-2026-05-28.json";    Expected=15 },
    @{ File="shapes/suite-shape-TIMESERIES-2026-05-28.json"; Expected=16 },
    @{ File="shapes/suite-shape-WINDOW-2026-05-28.json";     Expected=12 },
    @{ File="shapes/suite-shape-HAVING-2026-05-28.json";     Expected=7 },
    @{ File="shapes/suite-shape-FILTER-2026-05-28.json";     Expected=16 },
    @{ File="shapes/suite-shape-COMPARE-2026-05-28.json";    Expected=16 }
)
$results = @()
foreach ($s in $suites) {
    Log "▶ $($s.File)"
    try {
        $d = Trigger -suiteFile $s.File
        Log "  sessionId=$($d.sessionId)"
        $got = WaitDone -sessionId $d.sessionId -expected $s.Expected
        $fails = ([int](sqlcmd -S "PC\SQLEXPRESS" -d AISupportAnalysisDB -E -h-1 -W -s"|" -Q "SET NOCOUNT ON; SELECT ISNULL(SUM(CASE WHEN ErrorMessage IS NOT NULL THEN 1 ELSE 0 END),0) FROM CopilotTraceHistories WHERE SessionId=$($d.sessionId)" | Select-Object -First 1).Trim())
        $results += [PSCustomObject]@{ Suite=$s.File; Sid=$d.sessionId; Traces=$got; Expected=$s.Expected; Fails=$fails }
    } catch { Log "  $($_.Exception.Message)"; $results += [PSCustomObject]@{ Suite=$s.File; Sid=$null; Traces=0; Expected=$s.Expected; Fails="ERR" } }
    Start-Sleep 10
}
Log "─── Done ──"
$results | Format-Table -AutoSize | Out-File -Append $logPath
$results | Format-Table -AutoSize
"Log: $logPath"
