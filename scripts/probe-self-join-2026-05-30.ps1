#requires -Version 7
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
$logPath = "$env:TEMP\probe-self-join-2026-05-30-run.log"
$null = New-Item -Path $logPath -ItemType File -Force
function Log([string]$m) { $line = "$(Get-Date -Format 'HH:mm:ss') $m"; Add-Content -Path $logPath -Value $line; Write-Host $line }
$s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$lp = Invoke-WebRequest -Uri "https://localhost:8899/Identity/Account/Login" -WebSession $s -UseBasicParsing -SkipCertificateCheck
$t1 = [regex]::Match($lp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
$body = @{ "Input.Email"="admin@tech.local"; "Input.Password"="Admin@123"; "Input.RememberMe"="false"; "__RequestVerificationToken"=$t1 }
try { Invoke-WebRequest -Uri "https://localhost:8899/Identity/Account/Login" -Method POST -Body $body -WebSession $s -UseBasicParsing -SkipCertificateCheck -MaximumRedirection 0 -ErrorAction Stop | Out-Null } catch { }
Log "Logged in"
$pg = Invoke-WebRequest -Uri "https://localhost:8899/AiAnalysis/CopilotAssessment" -WebSession $s -UseBasicParsing -SkipCertificateCheck
$token = [regex]::Match($pg.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
$suiteFile = "probe-self-join-2026-05-30.json"
$payload = "{`"SuiteFiles`":[`"$suiteFile`"]}"
$headers = @{ "RequestVerificationToken" = $token; "X-Requested-With" = "XMLHttpRequest" }
try {
    $r = Invoke-WebRequest -Uri "https://localhost:8899/AiAnalysis/RunCopilotAssessment" -Method POST -Body $payload -ContentType "application/json" -Headers $headers -WebSession $s -UseBasicParsing -SkipCertificateCheck
    $obj = $r.Content | ConvertFrom-Json
    Log "Triggered: sessionId=$($obj.data.sessionId) caseCount=$($obj.data.caseCount)"
} catch { Log "TRIGGER FAILED: $_"; exit 1 }
