param([hashtable] $Context)

# Seeded incremental retrieval, not a full network clone or publisher authentication.
# The retrieved head may advance between candidates; the pinned commit/tree must agree.
BeforeAll {
    . (Join-Path $PSScriptRoot '..\Support\Candidate.ps1')
}

Describe 'Git over Reticulum reference retrieval' {
    It 'retrieves initially missing pinned objects using the candidate rngit' -Tag 'T001', 'C001' {
        if (!$Context.FetchReference -and !$Context.ReferenceDirectory) {
            Set-ItResult -Skipped -Because 'Hosted retrieval was not requested; supply -FetchReference or a candidate-owned -ReferenceDirectory.'
            return
        }
        $evidenceDirectory = $Context.ReferenceDirectory
        if ($Context.Implementation -eq 'Upstream') {
            Initialize-CompatibilityCandidate $Context
            $evidenceDirectory = Join-Path $Context.OutputDirectory 'upstream-retrieval'
            & (Join-Path $PSScriptRoot '..\..\fetch-reference.ps1') -Python $Context.Python -Upstream $Context.Upstream `
                -Revision $Context.Revision -Directory $evidenceDirectory -Gateway $Context.Gateway
        }
        elseif ($Context.FetchReference) {
            & (Join-Path $PSScriptRoot '..\..\fetch-reference.ps1') -Application $Context.Application `
                -Directory $evidenceDirectory -Gateway $Context.Gateway
        }
        $evidence = Get-Content -LiteralPath (Join-Path $evidenceDirectory 'reference-provenance.json') -Raw | ConvertFrom-Json
        $canonical = Get-Content -LiteralPath (Join-Path $Context.ReferenceDirectory 'reference-provenance.json') -Raw | ConvertFrom-Json
        $evidence.candidate | Should -BeExactly $Context.Implementation
        $evidence.pin | Should -BeExactly $Context.Revision
        $evidence.pinInitiallyMissing | Should -BeTrue
        $evidence.networkBundleBytes | Should -BeGreaterThan 0
        $evidence.pinTree | Should -Not -BeNullOrEmpty
        $evidence.pinTree | Should -BeExactly $canonical.pinTree
        $repository = Join-Path $evidenceDirectory 'repository.git'
        $integrity = Invoke-TestProcess git @('--git-dir', $repository, 'fsck', '--full', '--strict')
        $integrity.Code | Should -Be 0 -Because "$($integrity.Error) $($integrity.Output)"
        $pin = Invoke-TestProcess git @('--git-dir', $repository, 'rev-parse', "$($Context.Revision)^{commit}")
        $pin.Code | Should -Be 0
        $pin.Output.Trim() | Should -BeExactly $Context.Revision
        $tree = Invoke-TestProcess git @('--git-dir', $repository, 'rev-parse', "$($Context.Revision)^{tree}")
        $tree.Code | Should -Be 0
        $tree.Output.Trim() | Should -BeExactly $evidence.pinTree
        $evidence.bundleSha256 | Should -BeExactly (Get-FileHash -LiteralPath (Join-Path $evidenceDirectory 'reference.bundle') -Algorithm SHA256).Hash
        if ($Context.Implementation -eq 'DotReticulum') {
            $evidence.applicationSha256 | Should -BeExactly (Get-FileHash -LiteralPath $Context.Application -Algorithm SHA256).Hash
        }
        Copy-Item -LiteralPath (Join-Path $evidenceDirectory 'reference-provenance.json') `
            -Destination (Join-Path $Context.OutputDirectory 'retrieval-provenance.json')
        $Context.RetrievalPassed = $true
    }

    AfterAll {
        if ($Context.FetchReference -and $Context.RetrievalPassed) {
            $environmentDirectory = Join-Path $Context.ReferenceDirectory 'python'
            $bootstrapPython = if ($Context.Python) { $Context.Python } elseif ($IsWindows) { 'python' } else { 'python3' }
            $Context.Python = $null
            $Context.Upstream = $null
            & (Join-Path $PSScriptRoot '..\..\prepare-reference.ps1') `
                -Bundle (Join-Path $Context.ReferenceDirectory 'reference.bundle') `
                -Directory $environmentDirectory -Python $bootstrapPython | Out-Host
            $Context.Python = Join-Path $environmentDirectory $(if ($IsWindows) { 'venv\Scripts\python.exe' } else { 'venv\bin\python' })
            $Context.Upstream = Join-Path $environmentDirectory 'upstream'
        }
        if ($Context.FetchReference -and !$Context.RetrievalPassed) {
            $Context.Python = $null
            $Context.Upstream = $null
        }
    }
}
