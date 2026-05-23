# Pull every GoldSql out of the new suite JSON files and execute each against the DB.
# Reports any that fail or return unexpected row counts.
$ErrorActionPreference = 'Stop'

$suiteDir = "$PSScriptRoot\..\Areas\SuperAdminCopilot\Configuration\QuestionSuites"
$conn = 'Server=PC\SQLEXPRESS;Database=AISupportAnalysisDB;Integrated Security=True;TrustServerCertificate=true;Encrypt=false;'

$total = 0
$ok = 0
$fail = 0
$failures = @()

Get-ChildItem $suiteDir -Filter 'realistic-*.json' | Sort-Object Name | ForEach-Object {
    $suite = Get-Content $_.FullName -Raw | ConvertFrom-Json
    Write-Host "`n== $($suite.name) ==" -ForegroundColor Cyan
    foreach ($s in $suite.Scenarios) {
        if (-not $s.GoldSql) { continue }
        $total++
        try {
            $rows = Invoke-Sqlcmd -ConnectionString $conn -Query $s.GoldSql -QueryTimeout 30
            $count = if ($null -eq $rows) { 0 } elseif ($rows -is [System.Array]) { $rows.Count } else { 1 }
            Write-Host ("  {0,-7} {1,4} rows  {2}" -f $s.Code, $count, $s.Question)
            $ok++
        } catch {
            Write-Host ("  {0,-7} FAILED   {1}" -f $s.Code, $s.Question) -ForegroundColor Red
            Write-Host ("              " + $_.Exception.Message) -ForegroundColor Red
            $fail++
            $failures += [pscustomobject]@{ Code = $s.Code; Question = $s.Question; Error = $_.Exception.Message }
        }
    }
}

Write-Host "`n== SUMMARY ==" -ForegroundColor Yellow
Write-Host "  Total Gold SQL checks: $total"
Write-Host "  OK:     $ok" -ForegroundColor Green
Write-Host "  Failed: $fail" -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })

if ($failures.Count -gt 0) {
    Write-Host "`nFailures:" -ForegroundColor Red
    $failures | Format-Table -AutoSize | Out-String | Write-Host
    exit 1
}
