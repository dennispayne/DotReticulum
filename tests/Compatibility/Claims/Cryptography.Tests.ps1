param([hashtable] $Context)

# Fixture-level functionality, not a cryptographic audit or complete forward-secrecy acceptance.
BeforeAll {
    . (Join-Path $PSScriptRoot '..\Support\Candidate.ps1')
    Initialize-CompatibilityCandidate $Context
    $fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\DotReticulum.Applications.Tests\Fixtures\parity7-rnid.json') -Raw | ConvertFrom-Json
    $key = [Convert]::ToHexString([byte[]](0..63)).ToLowerInvariant()
}

Describe 'Cryptographic communication' {
    It 'produces the independent detached Ed25519 signature bytes' -Tag 'TEST-CRYPTO-DETACHED-SIGNATURE', 'FUNC-CORE-CRYPTO' {
        $directory = New-CompatibilityCaseDirectory $Context 'TEST-CRYPTO-DETACHED-SIGNATURE'
        $inputPath = Join-Path $directory 'input'
        [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($fixture.message))
        $result = Invoke-CompatibilityTool $Context rnid @('-M', $key, '-s', $inputPath)
        $result.Code | Should -Be 0 -Because "$($result.Error) $($result.Output)"
        "$inputPath.rsg" | Should -Exist
        [Convert]::ToHexString([IO.File]::ReadAllBytes("$inputPath.rsg")).ToLowerInvariant() | Should -BeExactly $fixture.rsg
    }

    It 'decrypts the independent authenticated identity ciphertext' -Tag 'TEST-CRYPTO-IDENTITY-DECRYPTION', 'FUNC-CORE-CRYPTO' {
        $directory = New-CompatibilityCaseDirectory $Context 'TEST-CRYPTO-IDENTITY-DECRYPTION'
        $inputPath = Join-Path $directory 'input.rfe'
        $outputPath = Join-Path $directory 'output'
        [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($fixture.encrypted))
        $result = Invoke-CompatibilityTool $Context rnid @('-M', $key, '-d', $inputPath, '-w', $outputPath)
        $result.Code | Should -Be 0 -Because "$($result.Error) $($result.Output)"
        $outputPath | Should -Exist
        [Convert]::ToHexString([IO.File]::ReadAllBytes($outputPath)).ToLowerInvariant() | Should -BeExactly $fixture.message
    }
}
