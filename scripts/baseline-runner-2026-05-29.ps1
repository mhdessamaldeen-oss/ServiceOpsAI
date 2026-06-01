#requires -Version 7
# Baseline 305-case runner. Same pattern as smoke runner but points at the full baseline
# suite. Fire-and-forget: triggers the suite then exits. The orchestrator processes cases
# in the background; the operator polls the DB independently.

[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$logPath = "$env:TEMP\baseline-305-2026-05-29-run.log"
$null = New-Item -Path $logPath -ItemType File -Force
function Log([string]$m) { $line = "$(Get-Date -Format 'HH:mm:ss') $m"; Add-Content -Path $logPath -Value $line; Write-Host $line }

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

Log "=== Baseline 305-case run start ==="
$session = Login
Log "Logged in"
$token = Get-FreshAFToken $session
$suiteFile = "baseline-finetune-prep-2026-05-29.json"
$payload = "{`"SuiteFiles`":[`"$suiteFile`"]}"
$headers = @{ "RequestVerificationToken" = $token; "X-Requested-With" = "XMLHttpRequest" }
try {
    $r = Invoke-WebRequest -Uri "https://localhost:8899/AiAnalysis/RunCopilotAssessment" -Method POST -Body $payload -ContentType "application/json" -Headers $headers -WebSession $session -UseBasicParsing -SkipCertificateCheck
    $obj = $r.Content | ConvertFrom-Json
    Log "Triggered: sessionId=$($obj.data.sessionId) caseCount=$($obj.data.caseCount)"
} catch { Log "TRIGGER FAILED: $_"; exit 1 }
Log "Trigger complete. Orchestrator processes 305 cases in background. Poll DB manually."
