param(
    [string]$BaseUrl = "http://localhost:5149",
    [string]$Email = "admin@tech.local",
    [string]$Password = "Admin@123",
    [switch]$StartApp
)

$ErrorActionPreference = "Stop"

function Get-FormToken {
    param([string]$Html)

    $match = [regex]::Match($Html, 'name="__RequestVerificationToken"\s+type="hidden"\s+value="([^"]+)"')
    if (-not $match.Success) {
        throw "Anti-forgery token not found."
    }

    return $match.Groups[1].Value
}

function Get-SelectOptions {
    param(
        [string]$Html,
        [string]$SelectName
    )

    $selectMatch = [regex]::Match($Html, "<select[^>]*name=`"$SelectName`"[^>]*>(.*?)</select>", [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $selectMatch.Success) {
        return @()
    }

    $optionMatches = [regex]::Matches($selectMatch.Groups[1].Value, '<option[^>]*value="([^"]*)"[^>]*>(.*?)</option>', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    return @($optionMatches | ForEach-Object {
        [pscustomobject]@{
            Value = $_.Groups[1].Value
            Text = ($_.Groups[2].Value -replace '<.*?>', '').Trim()
        }
    })
}

function Get-FirstFilledOptionValue {
    param(
        [string]$Html,
        [string]$SelectName
    )

    $options = Get-SelectOptions -Html $Html -SelectName $SelectName
    return ($options | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Value) } | Select-Object -First 1).Value
}

function Get-AlternateOptionValue {
    param(
        [string]$Html,
        [string]$SelectName,
        [string]$CurrentValue
    )

    $options = Get-SelectOptions -Html $Html -SelectName $SelectName
    $alt = $options | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Value) -and $_.Value -ne $CurrentValue } | Select-Object -First 1
    if ($alt) {
        return $alt.Value
    }

    return $CurrentValue
}

function Assert-Contains {
    param(
        [string]$Content,
        [string]$Needle,
        [string]$Message
    )

    if ($Content -notlike "*$Needle*") {
        throw $Message
    }
}

function Assert-NotLoginPage {
    param(
        [string]$Content,
        [string]$Context
    )

    if ($Content -like "*<title>Log in -*" -or $Content -like "*id=`"login-submit`"*") {
        throw "$Context returned the login page instead of authenticated content."
    }
}

$appProcess = $null

try {
    if ($StartApp) {
        $projectRoot = Split-Path -Parent $PSScriptRoot
        $exePath = Join-Path $projectRoot "bin\Debug\net10.0\AISupportAnalysisPlatform.exe"
        if (-not (Test-Path $exePath)) {
            throw "App executable not found at $exePath. Build the project first."
        }

        $existing = Get-NetTCPConnection -State Listen -LocalPort ([uri]$BaseUrl).Port -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty OwningProcess
        if ($existing) {
            Stop-Process -Id $existing -Force
            Start-Sleep -Seconds 2
        }

        $appProcess = Start-Process -FilePath $exePath -ArgumentList "--urls", $BaseUrl -WorkingDirectory $projectRoot -PassThru

        $ready = $false
        foreach ($attempt in 1..30) {
            Start-Sleep -Seconds 1
            try {
                $probe = Invoke-WebRequest -Uri "$BaseUrl/Identity/Account/Login" -UseBasicParsing -TimeoutSec 5
                if ($probe.StatusCode -eq 200) {
                    $ready = $true
                    break
                }
            }
            catch {
            }
        }

        if (-not $ready) {
            throw "App did not become ready at $BaseUrl."
        }
    }

    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

    Write-Host "[1/8] Login"
    $loginPage = Invoke-WebRequest -Uri "$BaseUrl/Identity/Account/Login" -WebSession $session
    $loginToken = Get-FormToken -Html $loginPage.Content
    $loginForm = @{
        "Input.Email" = $Email
        "Input.Password" = $Password
        "Input.RememberMe" = "false"
        __RequestVerificationToken = $loginToken
    }
    $loginResponse = Invoke-WebRequest -Uri "$BaseUrl/Identity/Account/Login" -Method Post -Body $loginForm -WebSession $session -MaximumRedirection 10
    Assert-NotLoginPage -Content $loginResponse.Content -Context "Login"

    Write-Host "[2/8] Create ticket"
    $createPage = Invoke-WebRequest -Uri "$BaseUrl/Tickets/Create" -WebSession $session
    Assert-NotLoginPage -Content $createPage.Content -Context "Tickets/Create"
    $createToken = Get-FormToken -Html $createPage.Content

    $categoryId = Get-FirstFilledOptionValue -Html $createPage.Content -SelectName "CategoryId"
    $priorityId = Get-FirstFilledOptionValue -Html $createPage.Content -SelectName "PriorityId"
    $sourceId = Get-FirstFilledOptionValue -Html $createPage.Content -SelectName "SourceId"
    $entityId = Get-FirstFilledOptionValue -Html $createPage.Content -SelectName "EntityId"

    $stamp = Get-Date -Format "yyyyMMddHHmmss"
    $title = "Codex Smoke $stamp"
    $tempFile = Join-Path ([System.IO.Path]::GetTempPath()) "ticket-smoke-$stamp.txt"
    Set-Content -Path $tempFile -Value "Smoke verification attachment $stamp"
    $createForm = @{
        __RequestVerificationToken = $createToken
        Title = $title
        Description = "Ticket workflow regression verification created at $stamp."
        CategoryId = $categoryId
        PriorityId = $priorityId
        SourceId = $sourceId
        EntityId = $entityId
        ProductArea = "Smoke Product"
        EnvironmentName = "UAT"
        BrowserName = "Firefox"
        OperatingSystem = "Windows 11"
        ExternalReferenceId = "SMOKE-$stamp"
        ExternalSystemName = "Regression Harness"
        ImpactScope = "Team"
        AffectedUsersCount = "3"
    }

    $createResponse = Invoke-WebRequest -Uri "$BaseUrl/Tickets/Create" -Method Post -Form $createForm -WebSession $session -MaximumRedirection 10
    Assert-NotLoginPage -Content $createResponse.Content -Context "Ticket create submit"
    Assert-Contains -Content $createResponse.Content -Needle $title -Message "Created ticket did not appear on the queue page after submit."

    $idMatch = [regex]::Match($createResponse.Content, 'href="[^"]*/Tickets/Details/(\d+)"')
    if (-not $idMatch.Success) {
        throw "Created ticket details link was not found on the queue page."
    }
    $ticketId = $idMatch.Groups[1].Value
    Write-Host "  Created ticket id: $ticketId"

    Write-Host "[3/8] Edit ticket"
    $editPage = Invoke-WebRequest -Uri "$BaseUrl/Tickets/Edit/$ticketId" -WebSession $session
    Assert-NotLoginPage -Content $editPage.Content -Context "Tickets/Edit"
    $editToken = Get-FormToken -Html $editPage.Content
    $statusId = Get-FirstFilledOptionValue -Html $editPage.Content -SelectName "StatusId"
    $assignedToUserId = Get-FirstFilledOptionValue -Html $editPage.Content -SelectName "AssignedToUserId"
    $editForm = @{
        __RequestVerificationToken = $editToken
        Id = $ticketId
        Title = "$title Updated"
        Description = "Edited ticket workflow regression verification."
        CategoryId = $categoryId
        PriorityId = $priorityId
        SourceId = $sourceId
        EntityId = $entityId
        StatusId = $statusId
        AssignedToUserId = $assignedToUserId
        ProductArea = "Smoke Product"
        EnvironmentName = "Production"
        BrowserName = "Firefox"
        OperatingSystem = "Windows 11"
        ExternalReferenceId = "SMOKE-$stamp-EDIT"
        ExternalSystemName = "Regression Harness"
        ImpactScope = "Department"
        AffectedUsersCount = "7"
        TechnicalAssessment = "Smoke assessment"
        EscalationLevel = ""
        EscalatedToUserId = ""
        ResolutionSummary = ""
        PendingReason = "Awaiting validation"
        RootCause = ""
        VerificationNotes = ""
        ResolutionApprovedByUserId = ""
        DueDate = ""
    }
    Invoke-WebRequest -Uri "$BaseUrl/Tickets/Edit/$ticketId" -Method Post -Form $editForm -WebSession $session -MaximumRedirection 10 | Out-Null

    $detailsPage = Invoke-WebRequest -Uri "$BaseUrl/Tickets/Details/$ticketId" -WebSession $session
    Assert-NotLoginPage -Content $detailsPage.Content -Context "Tickets/Details"
    Assert-Contains -Content $detailsPage.Content -Needle "$title Updated" -Message "Edited title did not render on the details page."
    Assert-Contains -Content $detailsPage.Content -Needle "SMOKE-$stamp-EDIT" -Message "Edited external reference did not render on the details page."

    Write-Host "[4/8] Add comment"
    $detailsToken = Get-FormToken -Html $detailsPage.Content
    $commentText = "Smoke comment $stamp"
    $commentForm = @{
        __RequestVerificationToken = $detailsToken
        ticketId = $ticketId
        content = $commentText
        files = Get-Item $tempFile
    }
    Invoke-WebRequest -Uri "$BaseUrl/Comments/Add" -Method Post -Form $commentForm -WebSession $session -MaximumRedirection 10 | Out-Null
    $detailsAfterComment = Invoke-WebRequest -Uri "$BaseUrl/Tickets/Details/$ticketId" -WebSession $session
    Assert-NotLoginPage -Content $detailsAfterComment.Content -Context "Tickets/Details after comment"
    Assert-Contains -Content $detailsAfterComment.Content -Needle $commentText -Message "Comment did not render on the details page."
    Assert-Contains -Content $detailsAfterComment.Content -Needle "ticket-smoke-$stamp.txt" -Message "Comment attachment did not render on the details page."

    Write-Host "[5/8] Quick override"
    $overrideToken = Get-FormToken -Html $detailsAfterComment.Content
    $currentPriorityMatch = [regex]::Match($detailsAfterComment.Content, '<select name="priorityId"[^>]*>.*?<option[^>]*selected="selected"[^>]*value="([^"]+)"', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $currentPriorityId = if ($currentPriorityMatch.Success) { $currentPriorityMatch.Groups[1].Value } else { $priorityId }
    $alternatePriorityId = Get-AlternateOptionValue -Html $detailsAfterComment.Content -SelectName "priorityId" -CurrentValue $currentPriorityId
    $currentEntityMatch = [regex]::Match($detailsAfterComment.Content, '<select name="entityId"[^>]*>.*?<option[^>]*selected="selected"[^>]*value="([^"]+)"', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $currentEntityId = if ($currentEntityMatch.Success) { $currentEntityMatch.Groups[1].Value } else { $entityId }
    $alternateEntityId = Get-AlternateOptionValue -Html $detailsAfterComment.Content -SelectName "entityId" -CurrentValue $currentEntityId
    $overrideForm = @{
        __RequestVerificationToken = $overrideToken
        id = $ticketId
        priorityId = $alternatePriorityId
        entityId = $alternateEntityId
    }
    Invoke-WebRequest -Uri "$BaseUrl/Tickets/QuickOverride" -Method Post -Body $overrideForm -WebSession $session -MaximumRedirection 10 | Out-Null

    Write-Host "[6/8] Related cases and AI status endpoints"
    $semanticResponse = Invoke-WebRequest -Uri "$BaseUrl/Tickets/RunSemanticSearch/$ticketId" -Method Post -WebSession $session
    $semanticJson = $semanticResponse.Content | ConvertFrom-Json
    if (-not $semanticJson.success) {
        throw "Semantic search returned failure."
    }

    $statusResponse = Invoke-WebRequest -Uri "$BaseUrl/AiAnalysis/GetStatus/$ticketId" -WebSession $session
    $statusJson = $statusResponse.Content | ConvertFrom-Json
    if (-not $statusJson.status) {
        throw "AI status endpoint returned no status."
    }

    $historyResponse = Invoke-WebRequest -Uri "$BaseUrl/AiAnalysis/GetRunHistory?ticketId=$ticketId" -WebSession $session
    if ($historyResponse.StatusCode -ne 200) {
        throw "AI run history endpoint failed."
    }

    Write-Host "[7/8] Archive created ticket"
    $cleanupPage = Invoke-WebRequest -Uri "$BaseUrl/Tickets/Details/$ticketId" -WebSession $session
    $cleanupToken = Get-FormToken -Html $cleanupPage.Content
    $deleteForm = @{
        __RequestVerificationToken = $cleanupToken
        id = $ticketId
    }
    Invoke-WebRequest -Uri "$BaseUrl/Tickets/Delete/$ticketId" -Method Post -Body $deleteForm -WebSession $session -MaximumRedirection 10 | Out-Null

    Write-Host "[8/8] Done"
    Write-Host "Ticket workflow smoke test passed for ticket $ticketId."
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force
    }
}
