#Requires -Version 7.0
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
    $catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'catalog.json') -Raw | ConvertFrom-Json
    foreach ($claim in $catalog.claims | Where-Object { $_.id -notin $tests.ClaimId }) {
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
