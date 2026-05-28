#requires -Version 7
# Triggers the 7 missing suites with a FRESH login per suite to avoid the alternating 400 issue.

[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$logPath = "C:\Work\Lern\Improve\v2\AISupportAnalysisPlatform\Tests\fill-missing-$(Get-Date -Format 'yyyy-MM-dd-HHmm').log"
$null = New-Item -Path $logPath -ItemType File -Force

function Log([string]$m) {
    $line = "$(Get-Date -Format 'HH:mm:ss') $m"
    Write-Host $line; Add-Content -Path $logPath -Value $line
}

function FreshTrigger($suiteFile) {
    # Each call: brand-new session + brand-new tokens
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
        $row = (sqlcmd -S "PC\SQLEXPRESS" -d AISupportAnalysisDB -E -h-1 -W -s"|" -Q "SET NOCOUNT ON; SELECT COUNT(*), DATEDIFF(SECOND, ISNULL(MAX(CreatedAt), '2000-01-01'), SYSUTCDATETIME()) FROM CopilotTraceHistories WHERE SessionId=$sessionId" | Select-Object -First 1)
        $parts = $row.Split('|')
        $cnt = [int]$parts[0].Trim()
        $stall = [int]$parts[1].Trim()
        Log "  s=$sessionId traces=$cnt/$expected stall=${stall}s"
        if ($cnt -ge $expected) { return $cnt }
        if ($cnt -gt 0 -and $stall -gt 300) { Log "  stalled, moving on"; return $cnt }
        if ($cnt -eq 0 -and $stall -gt 90 -and $stall -lt 99000) { Log "  never started"; return 0 }
    }
}

# 7 missing suites
$missing = @(
    @{ File="shapes/suite-shape-FILTER-2026-05-28.json";    Expected=16 },
    @{ File="shapes/suite-shape-TOPN-2026-05-28.json";      Expected=16 },
    @{ File="shapes/suite-shape-JOIN-2026-05-28.json";      Expected=15 },
    @{ File="shapes/suite-shape-COMPARE-2026-05-28.json";   Expected=16 },
    @{ File="shapes/suite-shape-EXISTS-2026-05-28.json";    Expected=10 },
    @{ File="shapes/suite-shape-SELFJOIN-2026-05-28.json";  Expected=6 },
    @{ File="shapes/suite-shape-RECURSIVE-2026-05-28.json"; Expected=7 },
    @{ File="shapes/suite-shape-SAFETY-2026-05-28.json";    Expected=15 }
)
$results = @()
foreach ($m in $missing) {
    Log "▶ $($m.File)"
    try {
        $data = FreshTrigger -suiteFile $m.File
        Log "  sessionId=$($data.sessionId) totalCases=$($data.totalCases)"
        $got = WaitDone -sessionId $data.sessionId -expected $m.Expected
        $fails = (sqlcmd -S "PC\SQLEXPRESS" -d AISupportAnalysisDB -E -h-1 -W -s"|" -Q "SET NOCOUNT ON; SELECT SUM(CASE WHEN ErrorMessage IS NOT NULL THEN 1 ELSE 0 END) FROM CopilotTraceHistories WHERE SessionId=$($data.sessionId)" | Select-Object -First 1).Trim()
        $results += [PSCustomObject]@{ Suite=$m.File; SessionId=$data.sessionId; Traces=$got; Expected=$m.Expected; Failures=$fails }
        Log "  ✓ traces=$got/$($m.Expected) fails=$fails"
    } catch {
        Log "  ✗ $($_.Exception.Message)"
        $results += [PSCustomObject]@{ Suite=$m.File; SessionId=$null; Traces=0; Expected=$m.Expected; Failures='ERR' }
    }
    Start-Sleep 10
}
Log "─── Done ──"
$results | Format-Table -AutoSize | Out-File -Append $logPath
$results | Format-Table -AutoSize
"Log: $logPath"
