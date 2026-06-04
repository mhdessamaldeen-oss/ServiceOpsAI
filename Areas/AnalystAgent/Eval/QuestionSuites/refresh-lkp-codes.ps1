# Suite generator — refreshes hardcoded natural-key codes in any suite JSON by sampling
# live values from the database. Universal: works for any entity that has a naturalKeyColumn
# declared in semantic-layer.json, any future suite, any re-seed.
#
# How it works:
#   1. Reads the target suite JSON.
#   2. For each case whose Question contains a token matching a known naturalKeyFormat regex
#      (e.g. SA-100, OUT-2025-100, PAY-2025-9999, TKT-00001), queries the live DB for the
#      first available value of that natural-key column on the matched entity.
#   3. Replaces the stale token in-place with the live value.
#   4. Writes the suite back, preserving formatting.
#
# Usage:
#   pwsh ./refresh-lkp-codes.ps1 -SuiteFile <path> [-Server PC\SQLEXPRESS] [-Database AISupportAnalysisDB]
#
# Operator workflow: run this once before locking a baseline. The suite then stays valid
# across re-seeds without manual editing.

param(
    [Parameter(Mandatory = $true)]
    [string]$SuiteFile,
    [string]$Server = 'PC\SQLEXPRESS',
    [string]$Database = 'AISupportAnalysisDB',
    [string]$SemanticLayerFile = ''
)

if (-not (Test-Path $SuiteFile)) {
    Write-Error "Suite file not found: $SuiteFile"
    exit 1
}

# Resolve semantic-layer.json if not provided — assume it sits next to the suite folder.
if ([string]::IsNullOrEmpty($SemanticLayerFile)) {
    $candidate = Join-Path (Split-Path (Split-Path $SuiteFile -Parent) -Parent) 'semantic-layer.json'
    if (Test-Path $candidate) {
        $SemanticLayerFile = $candidate
    } else {
        Write-Error "semantic-layer.json not found. Pass -SemanticLayerFile explicitly."
        exit 1
    }
}

# Load semantic layer to find naturalKeyColumn + naturalKeyFormat per entity.
$layer = Get-Content $SemanticLayerFile -Raw -Encoding UTF8 | ConvertFrom-Json
$entities = @()
foreach ($e in $layer.entities) {
    if (-not [string]::IsNullOrEmpty($e.naturalKeyColumn) -and -not [string]::IsNullOrEmpty($e.naturalKeyFormat)) {
        $entities += [PSCustomObject]@{
            Table       = $e.table
            NaturalKey  = $e.naturalKeyColumn
            FormatRegex = $e.naturalKeyFormat
        }
    }
}
if ($entities.Count -eq 0) {
    Write-Host "No entities have naturalKeyColumn+naturalKeyFormat declared; nothing to refresh."
    exit 0
}

# Load suite. The suite uses /* ... */ block comments between case-groups — strip them
# before parsing.
$suiteRaw = Get-Content $SuiteFile -Raw -Encoding UTF8
$suiteCleaned = [regex]::Replace($suiteRaw, '/\*[\s\S]*?\*/', '')
$suite = $suiteCleaned | ConvertFrom-Json

# Cache: one DB query per entity (we only need ONE live value per entity).
$liveValues = @{}
foreach ($e in $entities) {
    $sql = "SELECT TOP 1 [$($e.NaturalKey)] AS v FROM [$($e.Table)] WHERE [$($e.NaturalKey)] IS NOT NULL ORDER BY [$($e.NaturalKey)]"
    try {
        $rows = & sqlcmd -S $Server -d $Database -Q $sql -h -1 -W 2>&1
        # First non-empty, non-dashes line is the value.
        $val = $rows | Where-Object { $_ -and $_ -notmatch '^-+$' -and $_ -notmatch 'rows affected' } | Select-Object -First 1
        if ($val) {
            $liveValues[$e.Table] = $val.Trim()
            Write-Host "  $($e.Table).$($e.NaturalKey) = $($val.Trim())"
        }
    } catch {
        Write-Warning "Failed to pull live value for $($e.Table): $_"
    }
}

# For each scenario, scan question text for tokens matching any entity's naturalKeyFormat
# regex. If matched, replace with the live value.
$replaced = 0
foreach ($sc in $suite.Scenarios) {
    if ([string]::IsNullOrEmpty($sc.Question)) { continue }
    foreach ($e in $entities) {
        if (-not $liveValues.ContainsKey($e.Table)) { continue }
        $live = $liveValues[$e.Table]
        # Match the hardcoded token using the entity's format regex.
        $matches = [regex]::Matches($sc.Question, $e.FormatRegex, 'IgnoreCase')
        foreach ($m in $matches) {
            $stale = $m.Value
            if ($stale -eq $live) { continue }  # already current
            $sc.Question = $sc.Question -replace [regex]::Escape($stale), $live
            $replaced++
            Write-Host "  $($sc.Code): replaced '$stale' -> '$live'"
        }
    }
}

if ($replaced -eq 0) {
    Write-Host "No stale natural-key tokens found; suite already current."
    exit 0
}

# Write back. We rebuild the JSON via ConvertTo-Json — note this drops block comments.
$json = $suite | ConvertTo-Json -Depth 10
Set-Content -Path $SuiteFile -Value $json -Encoding utf8 -NoNewline
Write-Host ""
Write-Host "Refreshed $replaced natural-key reference(s) in $SuiteFile"
Write-Host "Note: block comments in the original file were dropped by the JSON round-trip."
