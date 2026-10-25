param([string]$Application, [string]$Python, [string]$Upstream)

BeforeAll {
    . (Join-Path $PSScriptRoot 'TestProcess.ps1')
    $cli = (Resolve-Path -LiteralPath $Application).Path
    $executable = if ([IO.Path]::GetExtension($cli) -eq '.dll') { 'dotnet' } else { $cli }
    $prefix = @(if ($executable -eq 'dotnet') { $cli })
    function Invoke-ShellCli([string[]]$Arguments) {
        Invoke-TestProcess $executable ($prefix + @('rnsh') + $Arguments)
    }
}

Describe 'Parity 7 rnsh process contracts' {
    It 'has a distinct shell command and rejects malformed destinations' {
        $help = Invoke-ShellCli @('--help')
        $help.Code | Should -Be 0 -Because $help.Error
        $help.Output | Should -Match 'Remote Shell'
        $help.Output | Should -Match '--rnsconfig'
        $invalid = Invoke-ShellCli @('invalid', '--', 'dotnet', '--version')
        $invalid.Code | Should -Be 2
        $invalid.Error | Should -Match 'Invalid destination'
    }
    It 'persists service-specific identities without using shell state as RNS configuration' {
        $state = Join-Path $TestDrive 'rnsh'
        $first = Invoke-ShellCli @('-c', $state, '-l', '-s', 'test-service!', '-p')
        $first.Code | Should -Be 0 -Because $first.Error
        Test-Path (Join-Path $state 'identity.testservice') | Should -BeTrue
        $second = Invoke-ShellCli @('-c', $state, '-l', '-s', 'test-service!', '-p')
        $second.Code | Should -Be 0 -Because $second.Error
        $second.Output | Should -Be $first.Output
    }
}
