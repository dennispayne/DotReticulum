param([hashtable] $Context)

# One deterministic identity/name fixture; not a statistical uniqueness proof.
BeforeAll {
    . (Join-Path $PSScriptRoot '..\Support\Candidate.ps1')
    Initialize-CompatibilityCandidate $Context
    $fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\DotReticulum.Applications.Tests\Fixtures\parity7-rnid.json') -Raw | ConvertFrom-Json
    $key = [Convert]::ToHexString([byte[]](0..63)).ToLowerInvariant()
}

Describe 'Cryptographic addressing' {
    It 'persists the exact imported 64-byte identity' -Tag 'TEST-ADDRESSING-IDENTITY-IMPORT', 'FUNC-CORE-ADDRESSING' {
        $directory = New-CompatibilityCaseDirectory $Context 'TEST-ADDRESSING-IDENTITY-IMPORT'
        $outputPath = Join-Path $directory 'identity'
        $result = Invoke-CompatibilityTool $Context rnid @('-M', $key, '-X', '-w', $outputPath)
        $result.Code | Should -Be 0 -Because "$($result.Error) $($result.Output)"
        $outputPath | Should -Exist
        [Convert]::ToHexString([IO.File]::ReadAllBytes($outputPath)).ToLowerInvariant() | Should -BeExactly $key
    }

    It 'derives the independent rns.id destination hash' -Tag 'TEST-ADDRESSING-DESTINATION-HASH', 'FUNC-CORE-ADDRESSING' {
        $result = Invoke-CompatibilityTool $Context rnid @('-M', $key, '-H', 'rns.id')
        $result.Code | Should -Be 0 -Because "$($result.Error) $($result.Output)"
        $result.Output.Contains($fixture.destination, [StringComparison]::OrdinalIgnoreCase) | Should -BeTrue
    }
}
