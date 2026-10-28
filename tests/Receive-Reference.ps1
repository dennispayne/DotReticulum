#Requires -Version 7.0

<#
.SYNOPSIS
Fetches pinned upstream Git objects over Reticulum and records provenance.
.DESCRIPTION
Requires a fresh destination and either a native application or verified Python
reference. Seeds older history from the GitHub mirror, then requires missing
objects and nonzero bundle bytes from the hosted Reticulum repository.
.PARAMETER Application
Published native executable whose rngit client performs the fetch.
Required in the Native parameter set; managed DLLs are rejected.
.PARAMETER Python
Python interpreter for the actual upstream Git helper.
Required in the Python parameter set instead of Application.
.PARAMETER Upstream
Verified pinned Python reference checkout. Required in the Python parameter set.
.PARAMETER Revision
Python reference revision. Defaults to the canonical compatibility pin and must match it.
.PARAMETER Directory
Fresh destination for the fetched repository, bundle, configuration and provenance.
.PARAMETER Gateway
Reticulum TCP gateway hostname or IPv4 address.
Defaults to reticulum.thecryptosquid.com.
.PARAMETER Port
Gateway TCP port, from 1 through 65535. Defaults to 4242.
.PARAMETER TimeoutSeconds
Timeout for Git subprocesses and the Reticulum fetch, from 1 through 3600 seconds.
Defaults to 900; native fetch process cleanup is allowed an additional 30 seconds.
.EXAMPLE
.\Receive-Reference.ps1 -Application ..\artifacts\native\DotReticulum.Applications.exe -Directory ..\TestResults\reference-fetch
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'Native')] [string] $Application,
    [Parameter(Mandatory, ParameterSetName = 'Python')] [string] $Python,
    [Parameter(Mandatory, ParameterSetName = 'Python')] [string] $Upstream,
    [Parameter(ParameterSetName = 'Python')] [string] $Revision,
    [Parameter(Mandatory)] [string] $Directory,
    [string] $Gateway = 'reticulum.thecryptosquid.com',
    [ValidateRange(1, 65535)] [int] $Port = 4242,
    [ValidateRange(1, 3600)] [int] $TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
if ($Gateway -notmatch '^[a-zA-Z0-9.-]+$') { throw 'Gateway must be a DNS hostname or IPv4 address.' }
if (Test-Path -LiteralPath $Directory) { throw 'Fetch directory must be fresh.' }
. (Join-Path $PSScriptRoot 'Pester\TestProcess.ps1')
. (Join-Path $PSScriptRoot 'Compatibility\Support\Reference.ps1')
$pin = Get-CompatibilityReferenceRevision
if (!$Revision) { $Revision = $pin }
if ($Revision -ne $pin) { throw 'Reference retrieval revision must match the canonical pin.' }
$applicationPath = if ($Application) { (Resolve-Path -LiteralPath $Application).Path }
if ($Application -and [IO.Path]::GetExtension($applicationPath) -eq '.dll') { throw 'Hosted reference retrieval requires the published native executable.' }
$null = New-Item -ItemType Directory -Path $Directory
$root = (Resolve-Path -LiteralPath $Directory).Path
$repository = Join-Path $root 'repository.git'
$config = Join-Path $root 'config'
$null = New-Item -ItemType Directory -Path $config
$url = 'rns://7649a50d84610232d1416b41d2896aff/reticulum/reticulum'
$seed = '7f2b3b9b524c9386316379af1313b43a5e4f7a5d'

function Invoke-Git([string[]] $Arguments) {
    $result = Invoke-TestProcess git $Arguments -TimeoutSeconds $TimeoutSeconds
    if ($result.Code -ne 0) { throw "Git failed: $($result.Error)" }
    return $result.Output.Trim()
}

$null = Invoke-Git @('init', '--bare', '--initial-branch=main', $repository)
$null = Invoke-Git @('--git-dir', $repository, 'fetch', '--no-tags',
    'https://github.com/markqvist/Reticulum.git', $seed)
$null = Invoke-Git @('--git-dir', $repository, 'update-ref', 'refs/heads/main', $seed)
$missing = Invoke-TestProcess git @('--git-dir', $repository, 'cat-file', '-e', "$pin^{commit}")
if ($missing.Code -eq 0) { throw 'The target pin must be absent before the Reticulum fetch.' }
@"
[reticulum]
share_instance = no
enable_transport = no
[interfaces]
  [[CI gateway]]
    type = TCPClientInterface
    enabled = yes
    target_host = $Gateway
    target_port = $Port
"@ | Set-Content -LiteralPath (Join-Path $config 'config') -Encoding utf8
if ($Application) {
    $fetch = Invoke-TestProcess $applicationPath @('rngit', 'fetch', $url, $repository,
        '--config', $config, '--timeout', "$TimeoutSeconds", '--single-branch') -TimeoutSeconds ($TimeoutSeconds + 30)
    $fetch.Output | Write-Host
    $fetch.Error | Write-Host
    if ($fetch.Code -ne 0) { throw "Native rngit fetch failed with exit $($fetch.Code)." }
    if ($fetch.Output -notmatch 'Received (\d+) bundle bytes over Reticulum') {
        throw 'Native rngit did not report received bundle bytes.'
    }
    $bytes = [long] $Matches[1]
}
else {
    $info = [Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $Python).Path)
    $info.UseShellExecute = $false
    $info.WorkingDirectory = $repository
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $observations = Join-Path $root 'bundle-observations.jsonl'
    foreach ($argument in @('-u', (Join-Path $PSScriptRoot 'Interop\reference_git_remote.py'),
            '--revision', $Revision, '--url', $url, '--observations', $observations)) {
        $info.ArgumentList.Add($argument)
    }
    $reference = (Resolve-Path -LiteralPath $Upstream).Path
    $info.Environment['DOTRETICULUM_UPSTREAM'] = $reference
    $info.Environment['PYTHONPATH'] = $reference
    $info.Environment['PYTHONIOENCODING'] = 'utf-8'
    foreach ($key in 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES') {
        $null = $info.Environment.Remove($key)
    }
    $info.Environment['RNS_CONFIG'] = $config
    $info.Environment['RNGIT_CONFIG'] = Join-Path $root 'rngit'
    $process = [Diagnostics.Process]::Start($info)
    $errorTask = $process.StandardError.ReadToEndAsync()
    try {
        $process.StandardInput.WriteLine('list')
        $process.StandardInput.Flush()
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $headRef = $null
        $remoteRefs = @{}
        $advertisement = [Collections.Generic.List[string]]::new()
        $listed = $false
        for ($count = 0; $count -lt 10000; $count++) {
            $remaining = $deadline - [DateTime]::UtcNow
            if ($remaining.TotalSeconds -le 0) { throw 'Upstream Git helper timed out listing refs.' }
            $line = $process.StandardOutput.ReadLineAsync().WaitAsync($remaining).GetAwaiter().GetResult()
            if ($null -eq $line) { throw "Upstream Git helper exited before listing refs: $($errorTask.GetAwaiter().GetResult())" }
            if ($line -eq '') { $listed = $true; break }
            $advertisement.Add($line)
            if ($line -match '^@(refs/heads/\S+) HEAD$') { $headRef = $Matches[1] }
            elseif ($line -match '^([0-9a-f]{40}) (refs/heads/\S+)$') { $remoteRefs[$Matches[2]] = $Matches[1] }
        }
        $advertisement | Set-Content -LiteralPath (Join-Path $root 'advertised-refs.txt') -Encoding utf8
        if (!$listed -or !$headRef -or !$remoteRefs.ContainsKey($headRef)) { throw 'Upstream Git helper did not advertise a valid default branch.' }
        $remoteHead = $remoteRefs[$headRef]
        $null = Invoke-Git @('--git-dir', $repository, 'update-ref', $headRef, $seed)
        $process.StandardInput.WriteLine("fetch $remoteHead $headRef")
        $process.StandardInput.WriteLine('')
        $process.StandardInput.Close()
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $remaining = [Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        if (!$process.WaitForExit([int] $remaining)) { throw 'Upstream Git helper timed out fetching objects.' }
        if ($process.ExitCode -ne 0) { throw "Upstream Git helper failed: $($errorTask.GetAwaiter().GetResult())" }
        $outputTask.GetAwaiter().GetResult() | Write-Host
        $null = Invoke-Git @('--git-dir', $repository, 'update-ref', $headRef, $remoteHead)
        $null = Invoke-Git @('--git-dir', $repository, 'symbolic-ref', 'HEAD', $headRef)
    }
    finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $errorTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $root 'helper-stderr.txt') -Encoding utf8
        $process.Dispose()
    }
    $bytes = [long] ((Get-Content -LiteralPath $observations | ForEach-Object {
        ($_ | ConvertFrom-Json).bundle_bytes
    } | Measure-Object -Sum).Sum)
}
if ($bytes -le 0) {
    throw 'Reference retrieval requires nonzero Git bundle bytes over Reticulum.'
}
$null = Invoke-Git @('--git-dir', $repository, 'fsck', '--full', '--strict')
$headCommit = Invoke-Git @('--git-dir', $repository, 'rev-parse', 'HEAD')
$headTree = Invoke-Git @('--git-dir', $repository, 'rev-parse', 'HEAD^{tree}')
$null = Invoke-Git @('--git-dir', $repository, 'cat-file', '-e', "$pin^{commit}")
$pinTree = Invoke-Git @('--git-dir', $repository, 'rev-parse', "$pin^{tree}")
$null = Invoke-Git @('--git-dir', $repository, 'update-ref', 'refs/heads/pinned-reference', $pin)
$bundle = Join-Path $root 'reference.bundle'
$null = Invoke-Git @('--git-dir', $repository, 'bundle', 'create', $bundle, '--all')
[ordered]@{
    url = $url
    gateway = $Gateway
    seed = $seed
    seedSource = 'GitHub comparison mirror; only history through the parent of the pin'
    pin = $pin
    pinTree = $pinTree
    pinInitiallyMissing = ($missing.Code -ne 0)
    head = $headCommit
    tree = $headTree
    networkBundleBytes = $bytes
    bundleSha256 = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash
    publisherAuthenticated = $false
    candidate = if ($Application) { 'DotReticulum' } else { 'Upstream' }
    applicationSha256 = if ($Application) { (Get-FileHash -LiteralPath $applicationPath -Algorithm SHA256).Hash } else { $null }
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'reference-provenance.json') -Encoding utf8
