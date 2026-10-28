param([hashtable] $Context)

# One deterministic identity/name fixture; not a statistical uniqueness proof.
BeforeAll {
    . (Join-Path $PSScriptRoot '..\Support\Candidate.ps1')
    Initialize-CompatibilityCandidate $Context
    $fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\DotReticulum.Applications.Tests\Fixtures\parity7-rnid.json') -Raw | ConvertFrom-Json
    $key = [Convert]::ToHexString([byte[]](0..63)).ToLowerInvariant()
}

Describe 'Cryptographic addressing' {
    It 'persists the exact imported 64-byte identity' -Tag 'T002', 'C002' {
        $directory = New-CompatibilityCaseDirectory $Context 'T002'
        $outputPath = Join-Path $directory 'identity'
        $result = Invoke-CompatibilityTool $Context rnid @('-M', $key, '-X', '-w', $outputPath)
        $result.Code | Should -Be 0 -Because "$($result.Error) $($result.Output)"
        $outputPath | Should -Exist
        [Convert]::ToHexString([IO.File]::ReadAllBytes($outputPath)).ToLowerInvariant() | Should -BeExactly $key
    }

    It 'derives the independent rns.id destination hash' -Tag 'T003', 'C002' {
        $result = Invoke-CompatibilityTool $Context rnid @('-M', $key, '-H', 'rns.id')
        $result.Code | Should -Be 0 -Because "$($result.Error) $($result.Output)"
        $result.Output.Contains($fixture.destination, [StringComparison]::OrdinalIgnoreCase) | Should -BeTrue
    }
}
