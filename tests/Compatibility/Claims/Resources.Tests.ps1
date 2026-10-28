param([hashtable] $Context)

# Fixed pinned Python rncp peer, direct loopback TCP. Windows reference retention
# uses the disclosed rncp_win_shim workaround; no radio/multi-hop/loss claim.
BeforeAll {
    . (Join-Path $PSScriptRoot '..\Support\Candidate.ps1')
    . (Join-Path $PSScriptRoot '..\Support\TransferPeer.ps1')
    Initialize-CompatibilityCandidate $Context
}

Describe 'Bulk files and resources with a fixed upstream rncp peer' {
    foreach ($case in @(
        @{ Id = 'T030'; Claim = 'C020'; Direction = 'upload'; Compressible = $true },
        @{ Id = 'T031'; Claim = 'C006'; Direction = 'upload'; Compressible = $false },
        @{ Id = 'T032'; Claim = 'C020'; Direction = 'receive'; Compressible = $true },
        @{ Id = 'T033'; Claim = 'C006'; Direction = 'receive'; Compressible = $false }
    )) {
        It "persists exact multi-megabyte $($case.Direction) bytes (compressible=$($case.Compressible))" `
            -Tag $case.Id, $case.Claim -TestCases @($case) {
            param($Id, $Direction, $Compressible)
            $directory = New-CompatibilityCaseDirectory $Context $Id
            $pair = New-CompatibilityTransferPair $Context $directory
            $candidateId = Get-CompatibilityTransferIdentity $pair.Candidate $pair.CandidateConfig $pair.Root 'candidate-id'
            $referenceId = Get-CompatibilityTransferIdentity $pair.Reference $pair.ReferenceConfig $pair.Root 'reference-id'
            $bytes = [byte[]]::new(3211282)
            if ($Compressible) {
                $block = [Text.Encoding]::UTF8.GetBytes("Independent compatibility payload`n")
                for ($offset = 0; $offset -lt $bytes.Length; $offset += $block.Length) {
                    [Array]::Copy($block, 0, $bytes, $offset, [Math]::Min($block.Length, $bytes.Length - $offset))
                }
            }
            else { [Security.Cryptography.RandomNumberGenerator]::Fill($bytes) }
            $source = Join-Path $pair.Root 'bulk.bin'
            [IO.File]::WriteAllBytes($source, $bytes)
            $save = (New-Item -ItemType Directory -Path (Join-Path $pair.Root 'received')).FullName
            $sender = if ($Direction -eq 'upload') { $pair.Candidate } else { $pair.Reference }
            $receiver = if ($Direction -eq 'upload') { $pair.Reference } else { $pair.Candidate }
            $sendConfig = if ($Direction -eq 'upload') { $pair.CandidateConfig } else { $pair.ReferenceConfig }
            $receiveConfig = if ($Direction -eq 'upload') { $pair.ReferenceConfig } else { $pair.CandidateConfig }
            $allowed = if ($Direction -eq 'upload') { $candidateId.Identity } else { $referenceId.Identity }
            $destination = if ($Direction -eq 'upload') { $referenceId.Destination } else { $candidateId.Destination }
            $listener = $null
            try {
                $listener = Start-CompatibilityTransferListener $receiver $receiveConfig $allowed $save
                $sent = Invoke-CompatibilityTransferCommand $sender @('--config', $sendConfig, '-w', '30',
                    $source, $destination) $pair.Root 'send'
                $sent.Code | Should -Be 0 -Because "$($sent.Output) $($sent.Error)"
                $sent.Output | Should -Match 'copied to'
                $received = Join-Path $save 'bulk.bin'
                Wait-CompatibilityTransferFile $received $bytes.Length
                (Get-Item -LiteralPath $received).Length | Should -Be $bytes.Length
                (Get-FileHash -LiteralPath $received).Hash | Should -Be (Get-FileHash -LiteralPath $source).Hash
                [Convert]::ToBase64String([IO.File]::ReadAllBytes($received)) |
                    Should -BeExactly ([Convert]::ToBase64String($bytes))
                $listener.Process.HasExited | Should -BeFalse
            }
            finally { Stop-CompatibilityTransferListener $listener $save }
            $observations = if ($Direction -eq 'upload') {
                Get-Content -LiteralPath (Join-Path $save 'listener-stderr.txt')
            } else { $sent.Error -split '\r?\n' }
            $resources = @($observations | Where-Object { $_ -match '^\{"event": "resource"' } |
                ForEach-Object { $_ | ConvertFrom-Json })
            $resources.Count | Should -BeGreaterThan 0
            @($resources | Where-Object { $_.segments -gt 1 }).Count | Should -Be $resources.Count
            @($resources | Where-Object { $_.compressed -eq $Compressible }).Count | Should -Be $resources.Count
            ($resources | Measure-Object -Property total_size -Sum).Sum | Should -BeGreaterOrEqual $bytes.Length
        }
    }

    It 'fetches both compressible and incompressible multi-megabyte files from the same authorized upstream listener' -Tag 'T034', 'C022' {
        $directory = New-CompatibilityCaseDirectory $Context 'T034'
        $pair = New-CompatibilityTransferPair $Context $directory
        $candidateId = Get-CompatibilityTransferIdentity $pair.Candidate $pair.CandidateConfig $pair.Root 'candidate-id'
        $referenceId = Get-CompatibilityTransferIdentity $pair.Reference $pair.ReferenceConfig $pair.Root 'reference-id'
        $jail = (New-Item -ItemType Directory -Path (Join-Path $pair.Root 'jail')).FullName
        $save = (New-Item -ItemType Directory -Path (Join-Path $pair.Root 'fetched')).FullName
        $listener = $null
        try {
            $listener = Start-CompatibilityTransferListener $pair.Reference $pair.ReferenceConfig $candidateId.Identity $jail
            foreach ($kind in 'compressible', 'random') {
                $bytes = [byte[]]::new(2200017)
                if ($kind -eq 'random') { [Security.Cryptography.RandomNumberGenerator]::Fill($bytes) }
                $source = Join-Path $jail "$kind.bin"
                [IO.File]::WriteAllBytes($source, $bytes)
                $fetched = Invoke-CompatibilityTransferCommand $pair.Candidate @('--config', $pair.CandidateConfig,
                    '-f', $source, $referenceId.Destination, '-w', '30') $save $kind
                $fetched.Code | Should -Be 0 -Because "$($fetched.Output) $($fetched.Error)"
                $fetched.Output | Should -Match 'fetched from'
                $received = Join-Path $save "$kind.bin"
                Wait-CompatibilityTransferFile $received $bytes.Length
                (Get-FileHash -LiteralPath $received).Hash | Should -Be (Get-FileHash -LiteralPath $source).Hash
                [Convert]::ToBase64String([IO.File]::ReadAllBytes($received)) |
                    Should -BeExactly ([Convert]::ToBase64String($bytes))
                $listener.Process.HasExited | Should -BeFalse
            }
        }
        finally { Stop-CompatibilityTransferListener $listener $jail }
    }

    It 'rejects an unauthorized candidate identity without writing a file then accepts a fresh authorized connection' -Tag 'T035', 'C021' {
        $directory = New-CompatibilityCaseDirectory $Context 'T035'
        $pair = New-CompatibilityTransferPair $Context $directory
        $candidateId = Get-CompatibilityTransferIdentity $pair.Candidate $pair.CandidateConfig $pair.Root 'candidate-id'
        $referenceId = Get-CompatibilityTransferIdentity $pair.Reference $pair.ReferenceConfig $pair.Root 'reference-id'
        $rogueConfig = New-CompatibilityTransferConfig (Join-Path $pair.Root 'unauthorized') $pair.Port
        $rogueId = Get-CompatibilityTransferIdentity $pair.Candidate $rogueConfig $pair.Root 'unauthorized-id'
        $rogueId.Identity | Should -Not -Be $candidateId.Identity
        $save = (New-Item -ItemType Directory -Path (Join-Path $pair.Root 'received')).FullName
        $bytes = [byte[]]::new(3211282)
        [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        $source = Join-Path $pair.Root 'authorized.bin'
        [IO.File]::WriteAllBytes($source, $bytes)
        $listener = $null
        try {
            $listener = Start-CompatibilityTransferListener $pair.Reference $pair.ReferenceConfig $candidateId.Identity $save
            $denied = Invoke-CompatibilityTransferCommand $pair.Candidate @('--config', $rogueConfig,
                $source, $referenceId.Destination, '-w', '10') $pair.Root 'unauthorized'
            @(Get-ChildItem -LiteralPath $save -Filter '*.bin').Count | Should -Be 0
            $listener.Process.HasExited | Should -BeFalse
            $accepted = Invoke-CompatibilityTransferCommand $pair.Candidate @('--config', $pair.CandidateConfig,
                $source, $referenceId.Destination, '-w', '20') $pair.Root 'authorized'
            $accepted.Code | Should -Be 0 -Because "$($accepted.Output) $($accepted.Error)"
            Wait-CompatibilityTransferFile (Join-Path $save 'authorized.bin') $bytes.Length
            (Get-FileHash -LiteralPath (Join-Path $save 'authorized.bin')).Hash |
                Should -Be (Get-FileHash -LiteralPath $source).Hash
            $denied.Code | Should -Be 1 -Because "$($denied.Output) $($denied.Error)"
            $denied.Output | Should -Match 'transfer failed|not accepted'
        }
        finally { Stop-CompatibilityTransferListener $listener $save }
        Get-Content -LiteralPath (Join-Path $save 'listener-stdout.txt') -Raw | Should -Match 'Sender not allowed'
    }

    It 'reports an absent upstream file without writing a file and recovers with a valid fetch on a fresh link' -Tag 'T036', 'C022' {
        $directory = New-CompatibilityCaseDirectory $Context 'T036'
        $pair = New-CompatibilityTransferPair $Context $directory
        $candidateId = Get-CompatibilityTransferIdentity $pair.Candidate $pair.CandidateConfig $pair.Root 'candidate-id'
        $referenceId = Get-CompatibilityTransferIdentity $pair.Reference $pair.ReferenceConfig $pair.Root 'reference-id'
        $jail = (New-Item -ItemType Directory -Path (Join-Path $pair.Root 'jail')).FullName
        $save = (New-Item -ItemType Directory -Path (Join-Path $pair.Root 'fetched')).FullName
        $bytes = [Text.Encoding]::UTF8.GetBytes('owned jail acceptance payload')
        [IO.File]::WriteAllBytes((Join-Path $jail 'inside.bin'), $bytes)
        $listener = $null
        try {
            $listener = Start-CompatibilityTransferListener $pair.Reference $pair.ReferenceConfig $candidateId.Identity $jail
            $denied = Invoke-CompatibilityTransferCommand $pair.Candidate @('--config', $pair.CandidateConfig,
                '-f', (Join-Path $jail 'absent.bin'), $referenceId.Destination, '-w', '20') $save 'absent'
            $denied.Code | Should -Be 0
            $denied.Output | Should -Match 'not found'
            @(Get-ChildItem -LiteralPath $save -Filter '*.bin').Count | Should -Be 0
            $listener.Process.HasExited | Should -BeFalse
            $accepted = Invoke-CompatibilityTransferCommand $pair.Candidate @('--config', $pair.CandidateConfig,
                '-f', (Join-Path $jail 'inside.bin'), $referenceId.Destination, '-w', '20') $save 'inside'
            $accepted.Code | Should -Be 0 -Because "$($accepted.Output) $($accepted.Error)"
            Wait-CompatibilityTransferFile (Join-Path $save 'inside.bin') $bytes.Length
            [IO.File]::ReadAllText((Join-Path $save 'inside.bin')) | Should -BeExactly ([Text.Encoding]::UTF8.GetString($bytes))
            Test-Path -LiteralPath (Join-Path $jail 'absent.bin') | Should -BeFalse
        }
        finally { Stop-CompatibilityTransferListener $listener $jail }
    }
}
