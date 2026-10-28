param([hashtable] $Context)

# Direct loopback TCP; malformed proofs are created by the upstream peer through its Packet API.
# Does not establish multi-hop, replay resistance or impaired-link acceptance.
BeforeAll {
    . (Join-Path $PSScriptRoot '..\Support\Candidate.ps1')
    . (Join-Path $PSScriptRoot '..\Support\DeliveryPeer.ps1')
    Initialize-CompatibilityCandidate $Context
}

Describe 'Packet delivery confirmations with a fixed upstream peer' {
    It 'confirms an upstream delivery proof and reports zero packet loss' -Tag 'T006', 'C004' {
        $directory = New-CompatibilityCaseDirectory $Context 'T006'
        $probe = Invoke-CompatibilityProbe $Context $directory
        $probe.Candidate.Code | Should -Be 0 -Because "$($probe.Candidate.Output) $($probe.Candidate.Error)"
        $probe.Candidate.Output | Should -Match 'Valid reply from'
        $probe.Candidate.Output | Should -Match 'packet loss 0(?:\.0+)?%'
        @($probe.Observations | Where-Object {
            $_.event -eq 'probe_received' -and $_.destination -eq $probe.Destination -and $_.payload_bytes -eq 16
        }).Count | Should -BeGreaterThan 0
        $probe.PeerExitCode | Should -Be 0
        $probe.PeerError | Should -BeExactly ''
    }

    It 'times out without a delivery proof and reports complete packet loss' -Tag 'T007', 'C004' {
        $directory = New-CompatibilityCaseDirectory $Context 'T007'
        $probe = Invoke-CompatibilityProbe $Context $directory -WithoutProof
        $probe.Candidate.Code | Should -Be 2 -Because "$($probe.Candidate.Output) $($probe.Candidate.Error)"
        $probe.Candidate.Output | Should -Match 'Probe timed out'
        $probe.Candidate.Output | Should -Match 'packet loss 100(?:\.0+)?%'
        @($probe.Observations | Where-Object {
            $_.event -eq 'probe_received' -and $_.destination -eq $probe.Destination -and $_.payload_bytes -eq 16
        }).Count | Should -BeGreaterThan 0
        $probe.PeerExitCode | Should -Be 0
        $probe.PeerError | Should -BeExactly ''
    }

    It 'rejects a proof with a corrupted signature despite upstream packet receipt' -Tag 'T050', 'C004' {
        $directory = New-CompatibilityCaseDirectory $Context 'T050'
        $probe = Invoke-CompatibilityProbe $Context $directory -ProofMode 'invalid-signature'
        $probe.Candidate.Code | Should -Be 2 -Because "$($probe.Candidate.Output) $($probe.Candidate.Error)"
        $probe.Candidate.Output | Should -Match 'Probe timed out'
        $probe.Candidate.Output | Should -Match 'packet loss 100(?:\.0+)?%'
        @($probe.Observations | Where-Object {
            $_.event -eq 'probe_received' -and $_.destination -eq $probe.Destination -and $_.payload_bytes -eq 16
        }).Count | Should -BeGreaterThan 0
        @($probe.Observations | Where-Object {
            $_.event -eq 'proof_sent' -and $_.mode -eq 'invalid-signature' -and $_.sent -eq $true -and $_.payload_bytes -eq 96
        }).Count | Should -Be 1
        $probe.PeerExitCode | Should -Be 0
        $probe.PeerError | Should -BeExactly ''
    }

    It 'rejects a valid signature over a different packet hash' -Tag 'T051', 'C004' {
        $directory = New-CompatibilityCaseDirectory $Context 'T051'
        $probe = Invoke-CompatibilityProbe $Context $directory -ProofMode 'wrong-hash'
        $probe.Candidate.Code | Should -Be 2 -Because "$($probe.Candidate.Output) $($probe.Candidate.Error)"
        $probe.Candidate.Output | Should -Match 'Probe timed out'
        $probe.Candidate.Output | Should -Match 'packet loss 100(?:\.0+)?%'
        @($probe.Observations | Where-Object {
            $_.event -eq 'probe_received' -and $_.destination -eq $probe.Destination -and $_.payload_bytes -eq 16
        }).Count | Should -BeGreaterThan 0
        @($probe.Observations | Where-Object {
            $_.event -eq 'proof_sent' -and $_.mode -eq 'wrong-hash' -and $_.sent -eq $true -and $_.payload_bytes -eq 96
        }).Count | Should -Be 1
        $probe.PeerExitCode | Should -Be 0
        $probe.PeerError | Should -BeExactly ''
    }

    It 'rejects a truncated explicit delivery proof' -Tag 'T052', 'C004' {
        $directory = New-CompatibilityCaseDirectory $Context 'T052'
        $probe = Invoke-CompatibilityProbe $Context $directory -ProofMode 'truncated'
        $probe.Candidate.Code | Should -Be 2 -Because "$($probe.Candidate.Output) $($probe.Candidate.Error)"
        $probe.Candidate.Output | Should -Match 'Probe timed out'
        $probe.Candidate.Output | Should -Match 'packet loss 100(?:\.0+)?%'
        @($probe.Observations | Where-Object {
            $_.event -eq 'probe_received' -and $_.destination -eq $probe.Destination -and $_.payload_bytes -eq 16
        }).Count | Should -BeGreaterThan 0
        @($probe.Observations | Where-Object {
            $_.event -eq 'proof_sent' -and $_.mode -eq 'truncated' -and $_.sent -eq $true -and $_.payload_bytes -eq 95
        }).Count | Should -Be 1
        $probe.PeerExitCode | Should -Be 0
        $probe.PeerError | Should -BeExactly ''
    }
}
