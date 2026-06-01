#requires -Version 7
# Shared alias-credit logic used by BOTH the deep analyzer + the diff analyzer.
# Single source of truth — change here and both scripts pick it up.
#
# The function Test-ExpectedFieldHit returns $true when the SQL contains a column reference
# that satisfies an expected suite column name, even if the literal spellings differ
# (FullNameEn for FullName, TicketStatuses.Name for Status, etc.). This is HONEST CREDIT,
# not gaming — the column IS the entity's label.

function Test-ExpectedFieldHit {
    param(
        [Parameter(Mandatory=$true)][string]$ExpectedField,
        [Parameter(Mandatory=$true)][string]$SqlLower
    )

    if ([string]::IsNullOrEmpty($ExpectedField)) { return $true }
    $bare = $ExpectedField
    if ($bare.Contains('.')) { $bare = $bare.Substring($bare.IndexOf('.')+1) }
    $bareLower = $bare.ToLowerInvariant()

    # Tier 1 — direct substring match (the cheap, common case).
    if ($SqlLower.Contains($bareLower)) { return $true }

    # Tier 2 — aggregate-alias synonyms. Any column ending in 'count' / 'total' / 'sum' etc.
    # credits the corresponding aggregation function.
    switch -Regex ($bareLower) {
        '^(count|tally|cnt|\w*count)$'                              { if ($SqlLower -match '\bcount\s*\(') { return $true } }
        '^(total|sum|amount|grandtotal|\w*amount|\w*total)$'        { if (($SqlLower -match '\bsum\s*\(') -or ($SqlLower -match 'totalamount') -or ($SqlLower -match '\bamount\b')) { return $true } }
        '^(average|avg|mean|averagehours|averagedays|averageminutes|\w*hours|\w*days)$' { if ($SqlLower -match '\bavg\s*\(') { return $true } }
        '^(max|maximum|highest|largest|biggest|peak|latest)$'       { if ($SqlLower -match '\bmax\s*\(') { return $true } }
        '^(min|minimum|lowest|smallest|earliest|oldest)$'           { if ($SqlLower -match '\bmin\s*\(') { return $true } }
        '^(period|month|year|quarter|week|day)$'                    { if ($SqlLower -match '\b(periodstart|monthstart|year\s*\(|month\s*\(|datepart|format|dateadd)') { return $true } }
    }

    # Tier 3 — entity-label synonyms. Suite often uses a short label ('Region', 'Customer',
    # 'Status') that maps to a localised or lookup-table column in the actual SQL.
    switch -Regex ($bareLower) {
        # Generic person/entity name → localised name column
        '^(fullname|name|displayname|label|title|customername|technicianname|employeename)$' {
            if (($SqlLower -match '\bfullnameen\b') -or ($SqlLower -match '\bfullnamear\b') -or ($SqlLower -match '\bnameen\b') -or ($SqlLower -match '\bnamear\b') -or ($SqlLower -match '\btitleen\b') -or ($SqlLower -match '\btitlear\b') -or ($SqlLower -match '\bdisplayname\b')) { return $true }
        }
        # Region label
        '^(region|regionname)$' { if (($SqlLower -match '\bregions?\.\[?nameen\]?') -or ($SqlLower -match '\bregions?\.\[?namear\]?') -or ($SqlLower -match '\bnameen\b')) { return $true } }
        # Status — common lookup-via-FK pattern: SQL projects TicketStatuses.Name
        '^(status|statusname|state)$' {
            if (($SqlLower -match '\bticketstatuses?\.\[?name\]?') -or ($SqlLower -match '\bstatuses?\.\[?name\]?') -or ($SqlLower -match '\.\[?status\]?') -or ($SqlLower -match '\bstatusid\b')) { return $true }
        }
        # Priority — lookup table label column
        '^(priority|priorityname)$' {
            if (($SqlLower -match '\bticketpriorities?\.\[?name\]?') -or ($SqlLower -match '\bpriorities?\.\[?name\]?') -or ($SqlLower -match '\.\[?priorityid\]?')) { return $true }
        }
        # Category
        '^(category|categoryname|complainttype|complainttypename|resolutiontype)$' {
            if (($SqlLower -match '\bticketcategories?\.\[?name\]?') -or ($SqlLower -match '\bcategories?\.\[?name\]?') -or ($SqlLower -match '\bcomplainttypes?\.\[?nameen\]?') -or ($SqlLower -match '\bresolutiontypes?\.\[?nameen\]?')) { return $true }
        }
        # Service type
        '^(servicetype|servicetypename|service)$' {
            if (($SqlLower -match '\bservicetypes?\.\[?nameen\]?') -or ($SqlLower -match '\bservicetypeid\b')) { return $true }
        }
        # Department
        '^(department|departmentname)$' {
            if ($SqlLower -match '\bdepartments?\.\[?nameen\]?') { return $true }
        }
        # Customer
        '^(customer|customername)$' {
            if (($SqlLower -match '\bcustomers?\.\[?fullnameen\]?') -or ($SqlLower -match '\bcustomerid\b')) { return $true }
        }
        # Technician
        '^(technician|technicianname)$' {
            if (($SqlLower -match '\btechnicians?\.\[?fullnameen\]?') -or ($SqlLower -match '\btechnicianid\b')) { return $true }
        }
        # Asset
        '^(asset|assetname)$' {
            if ($SqlLower -match '\bassets?\.\[?nameen\]?') { return $true }
        }
    }

    return $false
}

# Row-count parser — also shared. Handles "Returned N row(s)" and "Count: N" (single value, 1 row).
function Get-RowCountFromAnswer {
    param([Parameter(Mandatory=$true)][string]$Answer)
    if ([string]::IsNullOrEmpty($Answer)) { return -1 }
    if ($Answer -match 'Returned\s+(\d+)\s+row') { return [int]$Matches[1] }
    if ($Answer -match 'Count:\s*(\d+)') { return 1 }  # Single-row aggregate answer.
    if ($Answer -match '(?i)^(sum|avg|min|max):\s*[\d.]+') { return 1 }  # Other single-row aggregates.
    return -1
}
