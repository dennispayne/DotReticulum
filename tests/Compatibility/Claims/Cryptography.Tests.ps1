param([hashtable] $Context)

# CLI file/signature contracts, not a cryptographic audit or forward-secrecy acceptance.
# Independent parity7-rnid fixtures retain their recorded revision, generator and source hash.
BeforeAll {
    . (Join-Path $PSScriptRoot '..\Support\Candidate.ps1')
    . (Join-Path $PSScriptRoot '..\Support\Identity.ps1')
    Initialize-CompatibilityCandidate $Context
    $fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\DotReticulum.Applications.Tests\Fixtures\parity7-rnid.json') -Raw | ConvertFrom-Json
    $key = [Convert]::ToHexString([byte[]](0..63)).ToLowerInvariant()
    $reference = @{} + $Context
    $reference.Implementation = 'Upstream'
    Initialize-CompatibilityCandidate $reference
}

Describe 'Cryptographic communication' {
    It 'produces the independent detached Ed25519 signature bytes' -Tag 'T004', 'C003' {
        $directory = New-CompatibilityCaseDirectory $Context 'T004'
        $inputPath = Join-Path $directory 'input'
        [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($fixture.message))
        $result = Invoke-CompatibilityTool $Context rnid @('-M', $key, '-s', $inputPath)
        $result.Code | Should -Be 0 -Because "$($result.Error) $($result.Output)"
        "$inputPath.rsg" | Should -Exist
        [Convert]::ToHexString([IO.File]::ReadAllBytes("$inputPath.rsg")).ToLowerInvariant() | Should -BeExactly $fixture.rsg
    }

    It 'decrypts the independent authenticated identity ciphertext' -Tag 'T005', 'C003' {
        $directory = New-CompatibilityCaseDirectory $Context 'T005'
        $inputPath = Join-Path $directory 'input.rfe'
        $outputPath = Join-Path $directory 'output'
        [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($fixture.encrypted))
        $result = Invoke-CompatibilityTool $Context rnid @('-M', $key, '-d', $inputPath, '-w', $outputPath)
        $result.Code | Should -Be 0 -Because "$($result.Error) $($result.Output)"
        $outputPath | Should -Exist
        [Convert]::ToHexString([IO.File]::ReadAllBytes($outputPath)).ToLowerInvariant() | Should -BeExactly $fixture.message
    }

    It 'validates independent canonical and raw Ed25519 signatures using only the public identity' -Tag 'T008', 'C013' {
        $inputPath = New-CompatibilitySignedInput $Context 'T008' $fixture
        foreach ($signature in @($fixture.rsg, $fixture.raw)) {
            [IO.File]::WriteAllBytes("$inputPath.rsg", [Convert]::FromHexString($signature))
            $result = Invoke-CompatibilityTool $Context rnid @('-m', $fixture.public, '-V', $inputPath)
            $result.Code | Should -Be 0 -Because "$($result.Error) $($result.Output)"
            $result.Output | Should -Match 'Signature is valid'
        }
    }

    It 'rejects a modified independent signature without modifying the signed file' -Tag 'T009', 'C013' {
        $inputPath = New-CompatibilitySignedInput $Context 'T009' $fixture
        $signature = [Convert]::FromHexString($fixture.rsg)
        $signature[0] = $signature[0] -bxor 1
        [IO.File]::WriteAllBytes("$inputPath.rsg", $signature)
        $result = Invoke-CompatibilityTool $Context rnid @('-m', $fixture.public, '-V', $inputPath)
        $result.Code | Should -Not -Be 0
        $result.Output | Should -Match 'Invalid signature'
        [Convert]::ToHexString([IO.File]::ReadAllBytes($inputPath)) | Should -Be $fixture.message
    }

    It 'rejects an independently signed message changed after signing' -Tag 'T010', 'C013' {
        $inputPath = New-CompatibilitySignedInput $Context 'T010' $fixture
        $message = [Convert]::FromHexString($fixture.message)
        $message[0] = $message[0] -bxor 1
        [IO.File]::WriteAllBytes($inputPath, $message)
        $result = Invoke-CompatibilityTool $Context rnid @('-m', $fixture.public, '-V', $inputPath)
        $result.Code | Should -Not -Be 0
        $result.Output | Should -Match 'Invalid signature'
        [Convert]::ToHexString([IO.File]::ReadAllBytes("$inputPath.rsg")) | Should -Be $fixture.rsg
    }

    It 'rejects changes to the encrypted file IV, ciphertext and authentication tag without plaintext' -Tag 'T011', 'C003' {
        $directory = New-CompatibilityCaseDirectory $Context 'T011'
        $original = [Convert]::FromHexString($fixture.encrypted)
        foreach ($offset in @(32, 48, ($original.Length - 1))) {
            $tampered = [byte[]]$original.Clone()
            $tampered[$offset] = $tampered[$offset] -bxor 1
            Assert-CompatibilityDecryptRejected $Context $directory "tampered-$offset" $tampered $key
        }
    }

    It 'rejects decrypting the independent ciphertext with another private identity without plaintext' -Tag 'T012', 'C003' {
        $directory = New-CompatibilityCaseDirectory $Context 'T012'
        $wrongKey = [Convert]::ToHexString([byte[]](64..127))
        Assert-CompatibilityDecryptRejected $Context $directory 'wrong-key' ([Convert]::FromHexString($fixture.encrypted)) $wrongKey
    }

    It 'rejects truncated ephemeral keys, tokens and encrypted file tails without plaintext' -Tag 'T013', 'C003' {
        $directory = New-CompatibilityCaseDirectory $Context 'T013'
        $original = [Convert]::FromHexString($fixture.encrypted)
        foreach ($length in @(1, 31, 32, 63, ($original.Length - 1))) {
            Assert-CompatibilityDecryptRejected $Context $directory "truncated-$length" ([byte[]]$original[0..($length - 1)]) $key
        }
    }

    It 'encrypts for a public identity into a file the pinned reference can decrypt' -Tag 'T014', 'C003' {
        $directory = New-CompatibilityCaseDirectory $Context 'T014'
        $inputPath = Join-Path $directory 'input'
        $outputPath = Join-Path $directory 'reference-plaintext'
        [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($fixture.message))
        $encrypted = Invoke-CompatibilityTool $Context rnid @('-m', $fixture.public, '-e', $inputPath)
        $encrypted.Code | Should -Be 0 -Because "$($encrypted.Error) $($encrypted.Output)"
        "$inputPath.rfe" | Should -Exist
        $decrypted = Invoke-CompatibilityTool $reference rnid @('-M', $key, '-d', "$inputPath.rfe", '-w', $outputPath)
        $decrypted.Code | Should -Be 0 -Because "$($decrypted.Error) $($decrypted.Output)"
        [Convert]::ToHexString([IO.File]::ReadAllBytes($outputPath)) | Should -Be $fixture.message
    }

    It 'refuses to create a signature when the identity contains only public keys' -Tag 'T015', 'C003' {
        $directory = New-CompatibilityCaseDirectory $Context 'T015'
        $inputPath = Join-Path $directory 'input'
        [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($fixture.message))
        $result = Invoke-CompatibilityTool $Context rnid @('-m', $fixture.public, '-s', $inputPath)
        $result.Code | Should -Not -Be 0
        $result.Output | Should -Match 'does not hold a private key'
        "$inputPath.rsg" | Should -Not -Exist
    }

    It 'refuses to decrypt a file when the identity contains only public keys' -Tag 'T016', 'C003' {
        $directory = New-CompatibilityCaseDirectory $Context 'T016'
        $inputPath = Join-Path $directory 'input.rfe'
        $outputPath = Join-Path $directory 'plaintext'
        [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($fixture.encrypted))
        $result = Invoke-CompatibilityTool $Context rnid @('-m', $fixture.public, '-d', $inputPath, '-w', $outputPath)
        $result.Code | Should -Not -Be 0
        $result.Output | Should -Match 'does not hold a private key'
        $outputPath | Should -Not -Exist
    }
}
