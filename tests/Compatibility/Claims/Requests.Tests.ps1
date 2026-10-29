param([hashtable] $Context)

BeforeAll {
    . (Join-Path $PSScriptRoot '..\Support\Candidate.ps1')
    . (Join-Path $PSScriptRoot '..\Support\ManagementPeer.ps1')
    . (Join-Path $PSScriptRoot '..\Support\TransferPeer.ps1')
    . (Join-Path $PSScriptRoot '..\Support\RequestPeer.ps1')
    Initialize-CompatibilityCandidate $Context
}

Describe 'Application request and response resources through an authorized executable consumer' {
    It 'persists large request input and returns exact command output beyond the negotiated packet payload' -Tag 'T059', 'C008' {
        $directory = New-CompatibilityCaseDirectory $Context 'T059'
        $random = [byte[]]::new(16384)
        [Random]::new(59).NextBytes($random)
        # Identical ASCII-safe input across candidates; above TCP MDU, below Windows' argv limit.
        $inputText = 'request-start:' + [Convert]::ToBase64String($random) + ':request-end'
        $query = Invoke-CompatibilityLargeRequest $Context $directory $inputText
        $query.Candidate.Code | Should -Be 0 -Because $query.Candidate.Error
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($query.PersistedFile)) |
            Should -BeExactly ([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($inputText)))
        $query.Candidate.Output.EndsWith($inputText, [StringComparison]::Ordinal) | Should -BeTrue
        [regex]::Matches($query.Candidate.Output, 'request-start:').Count | Should -Be 1
        $execution = @($query.Observations | Where-Object event -EQ 'execution')
        $execution.Count | Should -Be 1
        $execution[0].identity | Should -BeExactly $query.Identity
        $execution[0].executed | Should -BeTrue
        $execution[0].exit_code | Should -Be 0
        $execution[0].stdout_bytes | Should -Be ([Text.Encoding]::UTF8.GetByteCount($inputText))
        $execution[0].stderr_bytes | Should -Be 0
        $execution[0].request_bytes | Should -BeGreaterThan $execution[0].mdu
        $execution[0].response_bytes | Should -BeGreaterThan $execution[0].mdu
        $execution[0].mdu | Should -BeGreaterThan 0
        $execution[0].packed_request_id | Should -BeExactly $execution[0].request_id
        $resources = @($query.Observations | Where-Object event -EQ 'request_resource')
        $resources.Count | Should -Be 1
        $resources[0].is_request | Should -BeTrue
        $resources[0].request_id | Should -BeExactly $execution[0].request_id
        $resources[0].mdu | Should -Be $execution[0].mdu
        $query.PeerError | Should -BeExactly ''
    }
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
        $responses = @($query.Observations | Where-Object event -EQ 'management_response')
        $responses.Count | Should -Be 1
        $responses[0].packed_bytes | Should -BeLessOrEqual $responses[0].mdu
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
        @($query.Observations | Where-Object event -EQ 'management_response').Count | Should -Be 0
        @($query.Observations | Where-Object {
            $_.event -eq 'management_identified' -and $_.identity -eq $query.ManagementIdentity
        }).Count | Should -Be 1
        $query.Candidate.Output | Should -Not -Match '"interfaces"\s*:'
        $query.PeerExitCode | Should -Be 0
        $query.PeerError | Should -BeExactly ''
    }

    It 'receives an intact nested management response exceeding the negotiated packet payload' -Tag 'T058', 'C008' {
        $directory = New-CompatibilityCaseDirectory $Context 'T058'
        $query = Invoke-CompatibilityManagementQuery $Context $directory -AdditionalPeers 24
        $query.Candidate.Code | Should -Be 0 -Because "$($query.Candidate.Output) $($query.Candidate.Error)"
        $status = $query.Candidate.Output | ConvertFrom-Json
        $status.transport_id | Should -BeExactly $query.TransportIdentity
        $responses = @($query.Observations | Where-Object event -EQ 'management_response')
        $responses.Count | Should -Be 1
        $response = $responses[0]
        $response.identity | Should -BeExactly $query.ManagementIdentity
        $response.packed_bytes | Should -BeGreaterThan $response.mdu
        $response.mdu | Should -BeGreaterThan 0
        @($response.interface_names | Where-Object { $_ -match 'Client on Candidate/' }).Count |
            Should -BeGreaterOrEqual 25
        @($status.interfaces.name | Sort-Object) | Should -Be @($response.interface_names | Sort-Object)
        @($query.ExpectedPeers | Where-Object { $_ -notin $status.interfaces.name }).Count | Should -Be 0
        $query.PeerExitCode | Should -Be 0
        $query.PeerError | Should -BeExactly ''
    }
}
