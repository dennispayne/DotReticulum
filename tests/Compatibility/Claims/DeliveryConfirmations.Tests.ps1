param([hashtable] $Context)

# Direct loopback TCP; not forged-proof, multi-hop or impaired-link acceptance.
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
}
