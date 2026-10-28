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
        Name = $test.Name
        UpstreamCitation = ($test.UpstreamCitation | ForEach-Object { "$($_.path):$($_.lines) ($($_.heading))" }) -join '; '
        Citations = $test.UpstreamCitation
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
            Name = $claim.claim
            UpstreamCitation = ($claim.citations | ForEach-Object { "$($_.path):$($_.lines) ($($_.heading))" }) -join '; '
            Citations = $claim.citations
            File = $null
            Line = $null
            Tags = @()
        }
    }
}
