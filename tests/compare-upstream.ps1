#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Application,
    [Parameter(Mandatory)] [string] $Python,
    [Parameter(Mandatory)] [string] $Upstream,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $Revision,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $Label = 'Upstream-head comparison'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Pester\TestProcess.ps1')
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Comparison directory must be fresh.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory
$root = (Resolve-Path -LiteralPath $OutputDirectory).Path
$native = (Resolve-Path -LiteralPath $Application).Path
$pythonPath = (Resolve-Path -LiteralPath $Python).Path
$reference = (Resolve-Path -LiteralPath $Upstream).Path
$adapter = Join-Path $PSScriptRoot 'Interop\reference_cli.py'
$fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'DotReticulum.Applications.Tests\Fixtures\parity7-rnid.json') -Raw | ConvertFrom-Json
$key = [Convert]::ToHexString([byte[]](0..63)).ToLowerInvariant()
$rows = [Collections.Generic.List[object]]::new()
$cases = @(
    @{ Id = 'UTIL-RNID-002'; Title = 'Import and persist exact private key bytes'; Operation = 'import' },
    @{ Id = 'UTIL-RNID-003'; Title = 'Produce the expected deterministic detached signature'; Operation = 'sign' },
    @{ Id = 'UTIL-RNID-006'; Title = 'Decrypt the independent pinned ciphertext'; Operation = 'decrypt' },
    @{ Id = 'UTIL-RNID-009'; Title = 'Derive the expected rns.id destination hash'; Operation = 'hash' }
)
foreach ($case in $cases) {
    $row = [ordered]@{ Id = $case.Id; Behavior = $case.Title; Upstream = ''; DotReticulum = ''; Detail = @() }
    foreach ($implementation in 'Upstream', 'DotReticulum') {
        $directory = Join-Path $root "$($case.Operation)-$implementation"
        $null = New-Item -ItemType Directory -Path $directory
        $inputPath = Join-Path $directory $(if ($case.Operation -eq 'decrypt') { 'input.rfe' } else { 'input' })
        $outputPath = Join-Path $directory 'output'
        $arguments = switch ($case.Operation) {
            'import' { @('-M', $key, '-X', '-w', $outputPath) }
            'sign' {
                [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($fixture.message))
                @('-M', $key, '-s', $inputPath)
            }
            'decrypt' {
                [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($fixture.encrypted))
                @('-M', $key, '-d', $inputPath, '-w', $outputPath)
            }
            'hash' { @('-M', $key, '-H', 'rns.id') }
        }
        if ($implementation -eq 'Upstream') {
            $result = Invoke-TestProcess $pythonPath (@($adapter, '--revision', $Revision, '--tool', 'rnid', '--') + $arguments) -Environment @{
                DOTRETICULUM_UPSTREAM = $reference
                PYTHONPATH = $reference
                PYTHONIOENCODING = 'utf-8'
            }
        }
        else {
            $executable = if ([IO.Path]::GetExtension($native) -eq '.dll') { 'dotnet' } else { $native }
            $prefix = @(if ($executable -eq 'dotnet') { $native })
            $result = Invoke-TestProcess $executable ($prefix + @('rnid') + $arguments)
        }
        $matchesContract = $false
        if ($result.Code -eq 0) {
            $matchesContract = switch ($case.Operation) {
                'import' { (Test-Path -LiteralPath $outputPath) -and [Convert]::ToHexString([IO.File]::ReadAllBytes($outputPath)).ToLowerInvariant() -eq $key }
                'sign' { (Test-Path -LiteralPath "$inputPath.rsg") -and [Convert]::ToHexString([IO.File]::ReadAllBytes("$inputPath.rsg")).ToLowerInvariant() -eq $fixture.rsg }
                'decrypt' { (Test-Path -LiteralPath $outputPath) -and [Convert]::ToHexString([IO.File]::ReadAllBytes($outputPath)).ToLowerInvariant() -eq $fixture.message }
                'hash' { $result.Output.Contains($fixture.destination, [StringComparison]::OrdinalIgnoreCase) }
            }
        }
        $row[$implementation] = if ($matchesContract) { 'Pass' } else { 'Fail' }
        if (!$matchesContract) {
            $row.Detail += "$implementation exit $($result.Code): $($result.Error) $($result.Output)"
        }
    }
    $rows.Add([pscustomobject] $row)
}
$rows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'comparison.json') -Encoding utf8
$upstreamRows = @($rows | ForEach-Object {
    [pscustomobject]@{ Id = $_.Id; Status = $_.Upstream; Detail = ($_.Detail -join ' '); Revision = $Revision }
})
$upstreamRows += [pscustomobject]@{
    Id = 'FUNC-CORE-ADDRESSING'
    Status = ($rows | Where-Object Id -eq 'UTIL-RNID-009').Upstream
    Detail = 'Deterministic rns.id address derivation for the same public fixture identity.'
    Revision = $Revision
}
$crypto = @($rows | Where-Object { $_.Id -in 'UTIL-RNID-003', 'UTIL-RNID-006' })
$upstreamRows += [pscustomobject]@{
    Id = 'FUNC-CORE-CRYPTO'
    Status = if ($crypto | Where-Object Upstream -ne 'Pass') { 'Fail' } else { 'Pass' }
    Detail = 'Detached Ed25519 signature bytes and authenticated decryption of the independent fixture; not a full crypto audit.'
    Revision = $Revision
}
$upstreamRows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'upstream.json') -Encoding utf8
$lines = @(
    "### $Label"
    ''
    "Executed upstream revision: ``$Revision``. This does not move the compatibility pin."
    'These four CLI cases share the same inputs and pinned behavioral expectations on both implementations. Other contracts are not established by this comparison.'
    ''
    '| Contract | Behavior | Upstream Python | DotReticulum |'
    '| --- | --- | --- | --- |'
    foreach ($row in $rows) {
        "| ``$($row.Id)`` | $($row.Behavior) | $($row.Upstream) | $($row.DotReticulum) |"
    }
    foreach ($row in $rows | Where-Object Detail) {
        foreach ($detail in $row.Detail) { "- ``$($row.Id)``: $(($detail -replace '[\r\n]+', ' ') -replace '\|', '&#124;')" }
    }
)
$lines | Set-Content -LiteralPath (Join-Path $root 'comparison.md') -Encoding utf8
$lines
if ($env:GITHUB_STEP_SUMMARY) { $lines | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
if ($rows | Where-Object { $_.Upstream -eq 'Fail' -or $_.DotReticulum -eq 'Fail' }) { exit 1 }
