# SupportFlow AI Platform - Expert Launch Script
# Forces the app to port 7081 and triggers the Auto-Login bypass

$ErrorActionPreference = "Stop"

Write-Host "--- SupportFlow AI Platform: Modern Launch ---" -ForegroundColor Cyan
Write-Host "Enforcing Port: 7081" -ForegroundColor Yellow
Write-Host "Mode: Testing/Fast-Login" -ForegroundColor Green

# 1. Ensure the app is built
Write-Host "Checking build status..."
dotnet build --no-incremental

# 2. Start the application on the specific ports
# Use --urls to override any launchSettings.json
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "https://localhost:7081;http://localhost:5081"

Write-Host "Launching Browser to: https://localhost:7081/Identity/Account/Login?testing=true" -ForegroundColor Cyan
Start-Process "https://localhost:7081/Identity/Account/Login?testing=true"

Write-Host "Starting Kestrel Server..." -ForegroundColor Cyan
dotnet run --no-build --urls "https://localhost:7081;http://localhost:5081"
