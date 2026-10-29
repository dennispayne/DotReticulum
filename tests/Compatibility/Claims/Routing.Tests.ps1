param([hashtable] $Context)

BeforeAll {
    . (Join-Path $PSScriptRoot '..\Support\Candidate.ps1')
    . (Join-Path $PSScriptRoot '..\Support\TransferPeer.ps1')
    . (Join-Path $PSScriptRoot '..\Support\RoutingPeer.ps1')
    Initialize-CompatibilityCandidate $Context
}

# Candidate --UDP-- fixed upstream transport --TCP-- fixed upstream rncp.
# Exact two-hop observations and persisted bytes bound the heterogeneous claim;
# this does not establish radio routing, alternate routes or impairment recovery.
Describe 'Self-configuring UDP-to-TCP routing through a fixed upstream transport' {
    foreach ($case in @(
        @{ Id = 'T055'; Direction = 'upload' },
        @{ Id = 'T056'; Direction = 'receive' }
    )) {
        It "persists exact $($case.Direction) bytes through a two-hop heterogeneous path without static routes" `
            -Tag $case.Id, 'C007' -TestCases @($case) {
            param($Id, $Direction)
            $directory = New-CompatibilityCaseDirectory $Context $Id
            $pair = New-CompatibilityRoutingPair $Context $directory
            $router = $null
            $listener = $null
            $save = (New-Item -ItemType Directory -Path (Join-Path $pair.Root 'received')).FullName
            try {
                $router = Start-CompatibilityRoutingTransport $Context $pair
                $candidateId = Get-CompatibilityTransferIdentity $pair.Candidate $pair.CandidateConfig $pair.Root 'candidate-id'
                $referenceId = Get-CompatibilityTransferIdentity $pair.Reference $pair.ReferenceConfig $pair.Root 'reference-id'
                $bytes = [byte[]]::new(3211282)
                [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
                $source = Join-Path $pair.Root 'routed.bin'
                [IO.File]::WriteAllBytes($source, $bytes)
                $sender = if ($Direction -eq 'upload') { $pair.Candidate } else { $pair.Reference }
                $receiver = if ($Direction -eq 'upload') { $pair.Reference } else { $pair.Candidate }
                $sendConfig = if ($Direction -eq 'upload') { $pair.CandidateConfig } else { $pair.ReferenceConfig }
                $receiveConfig = if ($Direction -eq 'upload') { $pair.ReferenceConfig } else { $pair.CandidateConfig }
                $allowed = if ($Direction -eq 'upload') { $candidateId.Identity } else { $referenceId.Identity }
                $destination = if ($Direction -eq 'upload') { $referenceId.Destination } else { $candidateId.Destination }
                $listener = Start-CompatibilityTransferListener $receiver $receiveConfig $allowed $save
                $sent = Invoke-CompatibilityTransferCommand $sender @('--config', $sendConfig,
                    '-w', '30', $source, $destination) $pair.Root 'routed-send'
                $sent.Code | Should -Be 0 -Because "$($sent.Output) $($sent.Error)"
                $received = Join-Path $save 'routed.bin'
                Wait-CompatibilityTransferFile $received $bytes.Length
                [Convert]::ToBase64String([IO.File]::ReadAllBytes($received)) |
                    Should -BeExactly ([Convert]::ToBase64String($bytes))
                $listener.Process.HasExited | Should -BeFalse
                $router.Process.HasExited | Should -BeFalse
            }
            finally {
                Stop-CompatibilityTransferListener $listener $save
                Stop-CompatibilityTransferListener $router $pair.RouterConfig
            }
            $observations = if ($Direction -eq 'upload') {
                Get-Content -LiteralPath (Join-Path $save 'listener-stderr.txt')
            } else { $sent.Error -split '\r?\n' }
            $resources = @($observations | Where-Object { $_ -match '^\{"event": "resource"' } |
                ForEach-Object { $_ | ConvertFrom-Json })
            $resources.Count | Should -BeGreaterThan 0
            @($resources | Where-Object hops -NE 2).Count | Should -Be 0
        }
    }
}
