#Requires -Version 7.0

<#
.SYNOPSIS
Analyzes tracked PowerShell files and writes a SARIF report.
.DESCRIPTION
Requires PSScriptAnalyzer 1.24.0, installed explicitly before invocation.
Uses repository settings and scans tracked scripts, modules and data files,
including tests. Writes findings before returning exit code 1 for warnings/errors.
.EXAMPLE
.\tools\Invoke-PowerShellAnalysis.ps1 -Report TestResults\analysis\powershell.sarif
#>
[CmdletBinding()]
param(
    [string] $Repository = (Split-Path $PSScriptRoot -Parent),
    [string] $Settings = (Join-Path $PSScriptRoot '..\PSScriptAnalyzerSettings.psd1'),
    [string] $Report = (Join-Path $PSScriptRoot '..\TestResults\analysis\powershell.sarif')
)

$ErrorActionPreference = 'Stop'
Import-Module PSScriptAnalyzer -RequiredVersion 1.24.0 -ErrorAction Stop
$root = (Resolve-Path -LiteralPath $Repository).Path
$files = @(& git -C $root -c core.quotepath=false ls-files -- '*.ps1' '*.psm1' '*.psd1')
if ($LASTEXITCODE -ne 0) { throw 'Could not list tracked PowerShell files.' }
if ($files.Count -eq 0) { throw 'No tracked PowerShell files found.' }
$diagnostics = @(foreach ($file in $files) {
    Invoke-ScriptAnalyzer -Path (Join-Path $root $file) -Settings $Settings -ErrorAction Stop
})
$rules = @($diagnostics.RuleName | Sort-Object -Unique | ForEach-Object {
    @{ id = $_; shortDescription = @{ text = $_ } }
})
$results = @(foreach ($diagnostic in $diagnostics) {
    $path = [IO.Path]::GetRelativePath($root, $diagnostic.ScriptPath)
    $uri = (($path -split '[\\/]') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
    $level = switch ([string] $diagnostic.Severity) {
        'Error' { 'error' }
        'ParseError' { 'error' }
        'Warning' { 'warning' }
        'Information' { 'note' }
        default { throw "Unknown analyzer severity: $($diagnostic.Severity)" }
    }
    Write-Host "$($path):$($diagnostic.Line): $level $($diagnostic.RuleName): $($diagnostic.Message)"
    @{
        ruleId = $diagnostic.RuleName
        level = $level
        message = @{ text = $diagnostic.Message }
        locations = @(@{ physicalLocation = @{
            artifactLocation = @{ uri = $uri }
            region = @{ startLine = [Math]::Max(1, $diagnostic.Line) }
        } })
    }
})
$sarif = @{
    '$schema' = 'https://json.schemastore.org/sarif-2.1.0.json'
    version = '2.1.0'
    runs = @(@{
        tool = @{ driver = @{ name = 'PSScriptAnalyzer'; version = '1.24.0'; rules = $rules } }
        automationDetails = @{ id = '/language:powershell/' }
        invocations = @(@{ executionSuccessful = $true })
        results = $results
    })
}
$reportPath = [IO.Path]::GetFullPath($Report)
$null = New-Item -ItemType Directory -Force -Path (Split-Path $reportPath -Parent)
$sarif | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Host "Analyzed $($files.Count) tracked PowerShell files; $($results.Count) findings."
if (@($results | Where-Object { $_.level -in 'error', 'warning' }).Count -gt 0) { exit 1 }
