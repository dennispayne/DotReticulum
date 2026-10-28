. (Join-Path $PSScriptRoot 'Reference.ps1')

function Initialize-CompatibilityPester {
    Import-Module Pester -MinimumVersion 5.7.1 -ErrorAction Stop
}

function New-CompatibilityConfiguration {
    param([hashtable] $Context)
    $configuration = New-PesterConfiguration
    $claims = Join-Path $PSScriptRoot '..\Claims'
    $files = @(Get-ChildItem -LiteralPath $claims -Filter '*.Tests.ps1' -File | Sort-Object `
        @{ Expression = { if ($_.Name -eq 'GitRetrieval.Tests.ps1') { 0 } else { 1 } } }, Name)
    if (!$files.Count) { throw 'No compatibility test files were found.' }
    $configuration.Run.Container = @(
        foreach ($file in $files) {
            New-PesterContainer -Path $file.FullName -Data @{ Context = $Context }
        }
    )
    $configuration.Run.PassThru = $true
    $configuration.Output.Verbosity = 'None'
    $configuration
}

function Get-CompatibilityTestMetadata {
    param([object[]] $Tests)
    if (!$Tests.Count) { throw 'No compatibility tests were discovered.' }
    $catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\catalog.json') -Raw | ConvertFrom-Json
    $claims = @{}
    foreach ($claim in $catalog.claims) {
        if ($claim.id -cnotmatch '^FUNC-[A-Z0-9]+(?:-[A-Z0-9]+)*$') { throw "Invalid catalog claim: $($claim.id)" }
        if ($claims.ContainsKey($claim.id)) { throw "Duplicate catalog claim: $($claim.id)" }
        $claims[$claim.id] = $claim
    }
    $ids = @{}
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
    foreach ($test in $Tests) {
        $testIds = @($test.Tag | Where-Object { $_ -like 'TEST-*' })
        $claimIds = @($test.Tag | Where-Object { $_ -like 'FUNC-*' })
        if ($testIds.Count -ne 1 -or $testIds[0] -cnotmatch '^TEST-[A-Z0-9]+(?:-[A-Z0-9]+)*$') {
            throw "Test '$($test.ExpandedPath -join '.')' requires exactly one stable TEST- tag."
        }
        $id = $testIds[0]
        if ($ids.ContainsKey($id)) { throw "Duplicate test ID: $id" }
        $ids[$id] = $true
        if ($claimIds.Count -ne 1 -or $claimIds[0] -cnotmatch '^FUNC-[A-Z0-9]+(?:-[A-Z0-9]+)*$' -or !$claims.ContainsKey($claimIds[0])) {
            throw "Test '$id' requires exactly one known FUNC- claim tag."
        }
        [pscustomobject]@{
            Id = $id
            ClaimId = $claimIds[0]
            Name = "$($test.Block.ExpandedPath) > $($test.ExpandedName)"
            File = [IO.Path]::GetRelativePath($root, $test.ScriptBlock.File).Replace('\', '/')
            Line = $test.ScriptBlock.StartPosition.StartLine
            UpstreamCitation = $claims[$claimIds[0]].citations
            Tags = @($test.Tag)
            Test = $test
        }
    }
}

function Get-CompatibilityDiscovery {
    param([hashtable] $Context = @{})
    Initialize-CompatibilityPester
    $configuration = New-CompatibilityConfiguration $Context
    $configuration.Run.SkipRun = $true
    $result = Invoke-Pester -Configuration $configuration
    if ($result.FailedContainersCount -gt 0 -or $result.FailedBlocksCount -gt 0) { throw 'Compatibility test discovery failed.' }
    @(Get-CompatibilityTestMetadata $result.Tests)
}
