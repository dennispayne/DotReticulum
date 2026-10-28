param([hashtable] $Context)

# C005 is broader than this deliberately scoped direct TCP IFAC acceptance.
BeforeAll {
    . (Join-Path $PSScriptRoot '..\Support\Candidate.ps1')
    . (Join-Path $PSScriptRoot '..\Support\TransferPeer.ps1')
    Initialize-CompatibilityCandidate $Context
}

Describe 'Authenticated TCP interfaces with a fixed upstream rncp peer' {
    foreach ($case in @(
        @{ Id = 'T037'; Mismatch = 'none' },
        @{ Id = 'T038'; Mismatch = 'network name' },
        @{ Id = 'T039'; Mismatch = 'passphrase' }
    )) {
        It "isolates IFAC mismatch '$($case.Mismatch)' and transfers on a fresh matching connection" `
            -Tag $case.Id, 'C005' -TestCases @($case) {
            param($Id, $Mismatch)
            $directory = New-CompatibilityCaseDirectory $Context $Id
            $pair = New-CompatibilityTransferPair $Context $directory -NetworkName 'compatibility-owned' `
                -Passphrase 'public-test-only-passphrase'
            $candidateId = Get-CompatibilityTransferIdentity $pair.Candidate $pair.CandidateConfig $pair.Root 'candidate-id'
            $referenceId = Get-CompatibilityTransferIdentity $pair.Reference $pair.ReferenceConfig $pair.Root 'reference-id'
            $save = (New-Item -ItemType Directory -Path (Join-Path $pair.Root 'received')).FullName
            $bytes = [byte[]]::new(3211282)
            [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
            $source = Join-Path $pair.Root 'ifac.bin'
            [IO.File]::WriteAllBytes($source, $bytes)
            $listener = $null
            try {
                $listener = Start-CompatibilityTransferListener $pair.Reference $pair.ReferenceConfig $candidateId.Identity $save
                if ($Mismatch -ne 'none') {
                    $network = if ($Mismatch -eq 'network name') { 'different-owned-network' } else { 'compatibility-owned' }
                    $passphrase = if ($Mismatch -eq 'passphrase') { 'different-public-passphrase' } else { 'public-test-only-passphrase' }
                    $null = New-CompatibilityTransferConfig $pair.CandidateConfig $pair.Port -NetworkName $network -Passphrase $passphrase
                    $denied = Invoke-CompatibilityTransferCommand $pair.Candidate @('--config', $pair.CandidateConfig,
                        $source, $referenceId.Destination, '-w', '2') $pair.Root 'mismatch' -TimeoutSeconds 30
                    $denied.Code | Should -Be 1 -Because "$($denied.Output) $($denied.Error)"
                    $denied.Output | Should -Match 'Path not found|timed out|No path'
                    @(Get-ChildItem -LiteralPath $save -Filter '*.bin').Count | Should -Be 0
                    $listener.Process.HasExited | Should -BeFalse
                    $null = New-CompatibilityTransferConfig $pair.CandidateConfig $pair.Port `
                        -NetworkName 'compatibility-owned' -Passphrase 'public-test-only-passphrase'
                }
                $sent = Invoke-CompatibilityTransferCommand $pair.Candidate @('--config', $pair.CandidateConfig,
                    $source, $referenceId.Destination, '-w', '20') $pair.Root 'matching'
                $sent.Code | Should -Be 0 -Because "$($sent.Output) $($sent.Error)"
                $sent.Output | Should -Match 'copied to'
                Wait-CompatibilityTransferFile (Join-Path $save 'ifac.bin') $bytes.Length
                (Get-FileHash -LiteralPath (Join-Path $save 'ifac.bin')).Hash |
                    Should -Be (Get-FileHash -LiteralPath $source).Hash
                [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $save 'ifac.bin'))) |
                    Should -BeExactly ([Convert]::ToBase64String($bytes))
                @(Get-ChildItem -LiteralPath $save -Filter '*.bin').Count | Should -Be 1
                $listener.Process.HasExited | Should -BeFalse
            }
            finally { Stop-CompatibilityTransferListener $listener $save }
        }
    }
}
