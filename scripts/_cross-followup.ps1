$ErrorActionPreference = "Continue"
try { Wait-Process -Id 34452 -ErrorAction Stop } catch { }
& "C:\Work\Lern\Improve\v2\AISupportAnalysisPlatform\scripts\run-dialect-baseline.ps1" -Suites @("shapes/suite-shape-CROSS-2026-05-28.json")
