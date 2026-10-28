param([hashtable] $Context)

BeforeAll {
    . (Join-Path $PSScriptRoot '..\Support\Candidate.ps1')
    . (Join-Path $PSScriptRoot '..\Support\ManagementPeer.ps1')
    Initialize-CompatibilityCandidate $Context
}

Describe 'Authenticated management requests to a fixed upstream transport' {
    It 'receives interface status through an authenticated link request and response' -Tag 'T053', 'C008' {
        $directory = New-CompatibilityCaseDirectory $Context 'T053'
        $query = Invoke-CompatibilityManagementQuery $Context $directory
        $query.Candidate.Code | Should -Be 0 -Because "$($query.Candidate.Output) $($query.Candidate.Error)"
        $status = $query.Candidate.Output | ConvertFrom-Json
        $status.transport_id | Should -BeExactly $query.TransportIdentity
        @($status.interfaces).Count | Should -BeGreaterThan 0
        @($status.interfaces | Where-Object name -Match 'Candidate').Count | Should -BeGreaterThan 0
        @($query.Observations | Where-Object {
            $_.event -eq 'ready' -and $_.transport_identity -eq $query.TransportIdentity
        }).Count | Should -Be 1
        @($query.Observations | Where-Object {
            $_.event -eq 'management_identified' -and $_.identity -eq $query.ManagementIdentity
        }).Count | Should -Be 1
        $query.PeerExitCode | Should -Be 0
        $query.PeerError | Should -BeExactly ''
    }

    It 'denies a management query from an identity not authorized by the upstream transport' -Tag 'T054', 'C030' {
        $directory = New-CompatibilityCaseDirectory $Context 'T054'
        $query = Invoke-CompatibilityManagementQuery $Context $directory -Unauthorized
        $query.Candidate.Code | Should -Be 2 -Because "$($query.Candidate.Output) $($query.Candidate.Error)"
        "$($query.Candidate.Output) $($query.Candidate.Error)" | Should -Match 'Could not get RNS status from remote transport instance'
        @($query.Observations | Where-Object {
            $_.event -eq 'ready' -and $_.transport_identity -eq $query.TransportIdentity
        }).Count | Should -Be 1
        @($query.Observations | Where-Object {
            $_.event -eq 'management_identified' -and $_.identity -eq $query.ManagementIdentity
        }).Count | Should -Be 1
        $query.Candidate.Output | Should -Not -Match '"interfaces"\s*:'
        $query.PeerExitCode | Should -Be 0
        $query.PeerError | Should -BeExactly ''
    }
}
