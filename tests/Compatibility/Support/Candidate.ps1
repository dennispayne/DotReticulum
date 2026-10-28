. (Join-Path $PSScriptRoot '..\..\Pester\TestProcess.ps1')
. (Join-Path $PSScriptRoot 'Catalog.ps1')

function Initialize-CompatibilityCandidate {
    param([hashtable] $Context)
    if (!$Context.Python -or !$Context.Upstream) {
        throw 'Core compatibility tests require a verified pinned Python reference; retrieval/provisioning did not complete.'
    }
    $Context.Python = (Resolve-Path -LiteralPath $Context.Python).Path
    $Context.Upstream = (Resolve-Path -LiteralPath $Context.Upstream).Path
    $Context.PeerEnvironment = @{
        DOTRETICULUM_UPSTREAM = $Context.Upstream
        PYTHONPATH = $Context.Upstream
        PYTHONIOENCODING = 'utf-8'
    }
    $verification = Invoke-TestProcess $Context.Python @((Join-Path $PSScriptRoot '..\..\Interop\verify_reference.py')) `
        -Environment $Context.PeerEnvironment
    if ($verification.Code -ne 0) { throw "Reference verification failed: $($verification.Error) $($verification.Output)" }
    Assert-CompatibilitySourceCitations $Context.Upstream
    $Context.Executable = if ($Context.Implementation -eq 'Upstream') { $Context.Python }
        elseif ([IO.Path]::GetExtension($Context.Application) -eq '.dll') { 'dotnet' }
        else { $Context.Application }
    $Context.Prefix = @(if ($Context.Implementation -eq 'Upstream') {
        @((Join-Path $PSScriptRoot '..\..\Interop\reference_cli.py'), '--revision', $Context.Revision, '--tool')
    } elseif ($Context.Executable -eq 'dotnet') { $Context.Application })
    $Context.ToolSeparator = @(if ($Context.Implementation -eq 'Upstream') { '--' })
    $Context.Environment = if ($Context.Implementation -eq 'Upstream') { $Context.PeerEnvironment } else { @{} }
}

function New-CompatibilityCaseDirectory {
    param([hashtable] $Context, [string] $Id)
    $directory = Join-Path $Context.OutputDirectory $Id
    $null = New-Item -ItemType Directory -Path $directory -ErrorAction Stop
    $directory
}

function Invoke-CompatibilityTool {
    param([hashtable] $Context, [string] $Tool, [string[]] $Arguments)
    Invoke-TestProcess $Context.Executable ($Context.Prefix + @($Tool) + $Context.ToolSeparator + $Arguments) `
        -Environment $Context.Environment
}
