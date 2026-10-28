#Requires -Version 7.0

<#
.SYNOPSIS
Lists compatibility cases and their quoted upstream claims.
.DESCRIPTION
Discovers tests without running candidates or fetching a reference. Use
-IncludeUncoveredClaims to include claims with no acceptance case.
.PARAMETER IncludeUncoveredClaims
Also emits catalogued claims without an acceptance case. Their test-specific
fields are empty; discovery still performs no downloads or node startup.
.EXAMPLE
.\Get-CompatibilityTests.ps1 -IncludeUncoveredClaims
#>
[CmdletBinding()]
param([switch] $IncludeUncoveredClaims)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Support\Discovery.ps1')
$tests = @(Get-CompatibilityDiscovery)
foreach ($test in $tests) {
    [pscustomobject]@{
        TestId = $test.Id
        ClaimId = $test.ClaimId
        Claim = $test.Claim
        Name = $test.Name
        Citation = $test.Citation
        CitationUri = $test.CitationUri
        File = $test.File
        Line = $test.Line
        Tags = $test.Tags
    }
}
if ($IncludeUncoveredClaims) {
    foreach ($claim in Get-CompatibilityClaims | Where-Object { $_.id -notin $tests.ClaimId }) {
        [pscustomobject]@{
            TestId = $null
            ClaimId = $claim.id
            Claim = $claim.claim
            Name = $null
            Citation = "$($claim.citation.path):$($claim.citation.lines)"
            CitationUri = if ($claim.citation.PSObject.Properties['uri']) { $claim.citation.uri } else { $null }
            File = $null
            Line = $null
            Tags = @()
        }
    }
}
