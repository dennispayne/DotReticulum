#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Application = (Join-Path $PSScriptRoot '..\src\DotReticulum.Applications\bin\Release\net10.0\DotReticulum.Applications.dll'),
    [switch] $ToolingOnly,
    [switch] $GitRemote,
    [switch] $GitHosting,
    [string] $Python = $env:DOTRETICULUM_PYTHON,
    [string] $Upstream = $env:DOTRETICULUM_UPSTREAM
)

$ErrorActionPreference = 'Stop'
if (($GitRemote -or $GitHosting) -and $ToolingOnly) { throw 'Git suites cannot be combined with -ToolingOnly.' }
if ($GitRemote -and ([string]::IsNullOrWhiteSpace($Python) -or [string]::IsNullOrWhiteSpace($Upstream))) {
    throw '-GitRemote requires -Python and -Upstream, or their DOTRETICULUM_* environment variables.'
}
Import-Module Pester -MinimumVersion 5.7.1 -ErrorAction Stop
$containers = @(New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\Coverage.Tests.ps1'))
if (!$ToolingOnly) {
    $resolved = (Resolve-Path -LiteralPath $Application).Path
    $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\Cli.Tests.ps1') -Data @{ Application = $resolved }
    if ($GitHosting) {
        $presentationProject = Join-Path $PSScriptRoot 'Fixtures\RngitPresentationBackend\RngitPresentationBackend.csproj'
        & dotnet build $presentationProject -c Release --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Presentation subprocess fixture build failed.' }
        $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\RngitHosting.Tests.ps1') -Data @{
            Application = $resolved
            Python = $Python
            Upstream = $Upstream
        }
        $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\Rngcs.Tests.ps1') -Data @{
            Application = $resolved
            Python = $Python
            Upstream = $Upstream
        }
        $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\MediaTemplates.Tests.ps1')
    }
    if ($GitRemote) {
        $containers += New-PesterContainer -Path (Join-Path $PSScriptRoot 'Pester\GitRemote.Tests.ps1') -Data @{
            Application = $resolved
            Python = (Resolve-Path -LiteralPath $Python).Path
            Upstream = (Resolve-Path -LiteralPath $Upstream).Path
        }
    }
}
$configuration = New-PesterConfiguration
$configuration.Run.Container = $containers
$configuration.Run.PassThru = $true
$configuration.Output.Verbosity = 'Detailed'
$result = Invoke-Pester -Configuration $configuration
if ($result.FailedCount -gt 0 -or $result.Result -ne 'Passed' -or $result.PassedCount -eq 0) { exit 1 }
