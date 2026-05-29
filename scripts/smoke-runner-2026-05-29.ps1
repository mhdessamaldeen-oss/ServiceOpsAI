#requires -Version 7
# Smoke runner — triggers the 80-case smoke suite, logs progress, exits when done.
# Spawned detached via Start-Process so it survives the Claude session.

[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$logPath = "$env:TEMP\smoke-2026-05-29-run.log"
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

Log "=== Smoke run start (80 cases, smoke-post-fixes-2026-05-29.json) ==="
$session = Login
Log "Logged in"
$token = Get-FreshAFToken $session
$suiteFile = "smoke-post-fixes-2026-05-29.json"
$payload = "{`"SuiteFiles`":[`"$suiteFile`"]}"
$headers = @{ "RequestVerificationToken" = $token; "X-Requested-With" = "XMLHttpRequest" }
try {
    $r = Invoke-WebRequest -Uri "https://localhost:8899/AiAnalysis/RunCopilotAssessment" -Method POST -Body $payload -ContentType "application/json" -Headers $headers -WebSession $session -UseBasicParsing -SkipCertificateCheck
    $obj = $r.Content | ConvertFrom-Json
    Log "Triggered: sessionId=$($obj.data.sessionId) caseCount=$($obj.data.caseCount)"
    $sessionId = $obj.data.sessionId
    $expected = $obj.data.caseCount
} catch {
    Log "TRIGGER FAILED: $_"
    exit 1
}

# Poll DB for completion, log every 60s.
while ($true) {
    Start-Sleep 60
    $cnt = (sqlcmd -S "PC\SQLEXPRESS" -d AISupportAnalysisDB -E -h-1 -W -s"|" -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM CopilotTraceHistories WHERE SessionId=$sessionId" | Select-Object -First 1).Trim()
    $stall = (sqlcmd -S "PC\SQLEXPRESS" -d AISupportAnalysisDB -E -h-1 -W -s"|" -Q "SET NOCOUNT ON; SELECT DATEDIFF(SECOND, ISNULL(MAX(CreatedAt), '2000-01-01'), SYSUTCDATETIME()) FROM CopilotTraceHistories WHERE SessionId=$sessionId" | Select-Object -First 1).Trim()
    Log "  session=$sessionId traces=$cnt/$expected stall=${stall}s"
    if ([int]$cnt -ge $expected) { Log "=== Suite finished ==="; break }
    if ([int]$cnt -gt 0 -and [int]$stall -gt 360) { Log "STALLED >360s — exiting poll loop"; break }
    if ([int]$cnt -eq 0 -and [int]$stall -gt 180 -and [int]$stall -lt 99000) { Log "NEVER STARTED — exiting poll loop"; break }
}
Log "Smoke run runner exiting"
