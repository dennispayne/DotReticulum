param([hashtable] $Context)

# Fixture provenance: GenerateParity7RnidVectors.py and parity7-rnid.json; no new crypto vectors.
BeforeAll {
    . (Join-Path $PSScriptRoot '..\Support\Candidate.ps1')
    Initialize-CompatibilityCandidate $Context
    $fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\DotReticulum.Applications.Tests\Fixtures\parity7-rnid.json') -Raw | ConvertFrom-Json
    $key = [Convert]::ToHexString([byte[]](0..63)).ToLowerInvariant()
    $reference = @{} + $Context
    $reference.Implementation = 'Upstream'
    Initialize-CompatibilityCandidate $reference
}

Describe 'Identity generation and management' {
    It 'persists a generated identity that another process reloads and the reference verifies' -Tag 'T017', 'C010' {
        $directory = New-CompatibilityCaseDirectory $Context 'T017'
        $identityPath = Join-Path $directory 'identity.rid'
        $inputPath = Join-Path $directory 'message'
        [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($fixture.message))
        $generated = Invoke-CompatibilityTool $Context rnid @('-g', $identityPath)
        $generated.Code | Should -Be 0 -Because "$($generated.Error) $($generated.Output)"
        $saved = [IO.File]::ReadAllBytes($identityPath)
        $signed = Invoke-CompatibilityTool $Context rnid @('-i', $identityPath, '-s', $inputPath)
        $signed.Code | Should -Be 0 -Because "$($signed.Error) $($signed.Output)"
        $verified = Invoke-CompatibilityTool $reference rnid @('-i', $identityPath, '-V', $inputPath)
        $verified.Code | Should -Be 0 -Because "$($verified.Error) $($verified.Output)"
        [Convert]::ToHexString([IO.File]::ReadAllBytes($identityPath)) | Should -BeExactly ([Convert]::ToHexString($saved))
    }

    It 'refuses to replace an existing identity while generating a new one' -Tag 'T018', 'C010' {
        $directory = New-CompatibilityCaseDirectory $Context 'T018'
        $identityPath = Join-Path $directory 'identity.rid'
        [IO.File]::WriteAllBytes($identityPath, [Convert]::FromHexString($key))
        $result = Invoke-CompatibilityTool $Context rnid @('-g', $identityPath)
        $result.Code | Should -Not -Be 0
        $result.Output | Should -Match 'already exists'
        [Convert]::ToHexString([IO.File]::ReadAllBytes($identityPath)) | Should -Be $key
    }

    It 'exports only public keys to the public file format and reloads the same identity' -Tag 'T019', 'C012' {
        $directory = New-CompatibilityCaseDirectory $Context 'T019'
        $outputPath = Join-Path $directory 'identity'
        $exported = Invoke-CompatibilityTool $Context rnid @('-M', $key, '-w', $outputPath)
        $exported.Code | Should -Be 0 -Because "$($exported.Error) $($exported.Output)"
        "$outputPath.pub" | Should -Exist
        $outputPath | Should -Not -Exist
        [Convert]::ToHexString([IO.File]::ReadAllBytes("$outputPath.pub")) | Should -Be $fixture.public
        $loaded = Invoke-CompatibilityTool $Context rnid @('-m', "$outputPath.pub", '-p')
        $loaded.Code | Should -Be 0
        $loaded.Output | Should -Match ([regex]::Escape($fixture.hash))
        $loaded.Output | Should -Not -Match ([regex]::Escape($key))
    }

    It 'imports the independent public identity in hex, base32 and URL-safe base64 formats' -Tag 'T020', 'C011' {
        foreach ($encoded in @($fixture.public, $fixture.base32, $fixture.base64)) {
            $result = Invoke-CompatibilityTool $Context rnid @('-m', $encoded, '-p', '-x')
            $result.Code | Should -Be 0 -Because "$($result.Error) $($result.Output)"
            $result.Output | Should -Match ([regex]::Escape($fixture.hash))
            $result.Output | Should -Match ("Public Identity Keys\s*:\s*" + [regex]::Escape($fixture.public))
        }
    }

    It 'imports private identities from file, base32 and URL-safe base64 without changing the saved keys' -Tag 'T021', 'C011' {
        $directory = New-CompatibilityCaseDirectory $Context 'T021'
        $inputPath = Join-Path $directory 'private.rid'
        [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($key))
        $base32Case = $fixture.cli_cases | Where-Object { $_.arguments -ccontains '-X' -and $_.arguments -ccontains '-B' } | Select-Object -First 1
        $privateBase32 = [regex]::Match($base32Case.stdout, 'Private Identity Keys\s*:\s*(\S+)').Groups[1].Value
        $privateBase32 | Should -Not -BeNullOrEmpty
        $privateBase64 = [Convert]::ToBase64String([Convert]::FromHexString($key)).Replace('+', '-').Replace('/', '_')
        $index = 0
        foreach ($encoded in @($inputPath, $privateBase32, $privateBase64)) {
            $outputPath = Join-Path $directory "imported-$index.rid"
            $result = Invoke-CompatibilityTool $Context rnid @('-M', $encoded, '-X', '-w', $outputPath)
            $result.Code | Should -Be 0 -Because "$($result.Error) $($result.Output)"
            [Convert]::ToHexString([IO.File]::ReadAllBytes($outputPath)) | Should -Be $key
            $index++
        }
    }

    It 'rejects malformed or short identity imports without creating key files' -Tag 'T022', 'C011' {
        $directory = New-CompatibilityCaseDirectory $Context 'T022'
        foreach ($option in @('-m', '-M')) {
            foreach ($invalid in @('not-an-identity!', $key.Substring(0, 62))) {
                $outputPath = Join-Path $directory "$($option.TrimStart('-'))-$($invalid.Length)"
                $result = Invoke-CompatibilityTool $Context rnid @($option, $invalid, '-w', $outputPath)
                $result.Code | Should -Not -Be 0
                $result.Output | Should -Match 'Could not decode'
                $outputPath | Should -Not -Exist
                "$outputPath.pub" | Should -Not -Exist
            }
        }
    }
}
