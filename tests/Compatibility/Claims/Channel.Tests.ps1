param([hashtable] $Context)

BeforeAll {
    . (Join-Path $PSScriptRoot '..\Support\Candidate.ps1')
    . (Join-Path $PSScriptRoot '..\Support\TransferPeer.ps1')
    . (Join-Path $PSScriptRoot '..\Support\ChannelPeer.ps1')
    Initialize-CompatibilityCandidate $Context
}

# The actual rnsh CLI consumes Channel messages; the upstream application peer
# executes no commands. This bounds C009 to ordered Channel delivery and the
# Buffer StreamDataMessage encoding, not RawChannelReader/Writer APIs or loss.
Describe 'Channel stream delivery through actual executable consumers' {
    It 'persists exact ordered stream bytes in both directions with upstream Channel and Buffer message codecs' -Tag 'T057', 'C009' {
        if ($Context.Implementation -eq 'Upstream' -and $IsWindows) {
            Set-ItResult -Skipped -Because 'The pinned upstream rnsh CLI requires POSIX; its shared baseline runs on Linux.'
            return
        }
        $directory = New-CompatibilityCaseDirectory $Context 'T057'
        $random = [byte[]]::new(65536)
        [Security.Cryptography.RandomNumberGenerator]::Fill($random)
        $inputText = "candidate-start`n" + [Convert]::ToBase64String($random) + "`ncandidate-end`n"
        [Security.Cryptography.RandomNumberGenerator]::Fill($random)
        $outputText = "reference-start`n" + [Convert]::ToBase64String($random) + "`nreference-end`n"
        $observed = Invoke-CompatibilityChannelStreams $Context $directory $inputText $outputText
        $observed.PeerExitCode | Should -Be 0 -Because $observed.PeerError
        $observed.Candidate.Code | Should -Be 0 -Because $observed.Candidate.Error
        [IO.File]::ReadAllText($observed.ReceivedFile) | Should -BeExactly $inputText
        [IO.File]::ReadAllText($observed.CandidateFile) | Should -BeExactly $outputText
        $received = @($observed.Events | Where-Object event -EQ 'received')
        $received.Count | Should -Be 1
        $received[0].messages | Should -BeGreaterThan 2
        $received[0].bytes | Should -Be ([Text.Encoding]::UTF8.GetByteCount($inputText))
        $received[0].eof | Should -BeTrue
        $received[0].command | Should -Be @('channel-fixture-no-execution')
        $sent = @($observed.Events | Where-Object event -EQ 'sent')
        $sent.Count | Should -Be 1
        $sent[0].messages | Should -BeGreaterThan 2
        $sent[0].eof | Should -BeTrue
    }
}
