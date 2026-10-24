#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Dotnet,
    [Parameter(Mandatory)] [string] $Python,
    [Parameter(Mandatory)] [string] $Upstream,
    [string] $Repository = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [string] $ResultsDirectory,
    [switch] $Build,
    [switch] $MulticastSmoke,
    [ValidateRange(90, 900)] [int] $TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
if (!$IsLinux) { throw 'This bounded topology runner requires Linux; on Windows invoke it inside WSL as root.' }
if ((& id -u) -ne '0') { throw 'Network namespace setup requires root (WSL: wsl -u root); sudo is not assumed.' }
$ip = (Get-Command ip -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$Dotnet = (Resolve-Path -LiteralPath $Dotnet).Path
$Python = (Resolve-Path -LiteralPath $Python).Path
$Upstream = (Resolve-Path -LiteralPath $Upstream).Path
$Upstream = (& readlink -f -- $Upstream).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Upstream)) { throw 'Could not canonicalize the pinned reference checkout.' }
$Repository = (Resolve-Path -LiteralPath $Repository).Path
$project = Join-Path $Repository 'tests/DotReticulum.Interfaces.Tests/DotReticulum.Interfaces.Tests.csproj'
if (!(Test-Path -LiteralPath $project -PathType Leaf)) { throw "Test project not found: $project" }
if (!(Test-Path -LiteralPath (Join-Path $Upstream 'RNS/Interfaces/AutoInterface.py') -PathType Leaf)) {
    throw '-Upstream must be the verified pinned checkout root containing RNS/Interfaces/AutoInterface.py.'
}
if (!(Test-Path -LiteralPath (Join-Path $Repository 'tests/Interop/live_auto_node.py') -PathType Leaf)) {
    throw 'The dedicated live_auto_node.py adapter is missing.'
}
$token = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$nativeNamespace = "dr-auto-$token-net"
$pythonNamespace = "dr-auto-$token-py"
$nativeDevice = "dra${token}a"
$pythonDevice = "dra${token}b"
$createdNamespaces = [System.Collections.Generic.List[string]]::new()
$vethCreated = $false
$nativeMoved = $false
$process = $null
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $Repository "TestResults/auto-live-$token"
}
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
$repoPrefix = $Repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (!$ResultsDirectory.StartsWith($repoPrefix, [StringComparison]::Ordinal)) {
    throw 'ResultsDirectory must be a new directory inside the repository.'
}
if (Test-Path -LiteralPath $ResultsDirectory) { throw 'Use a fresh results directory; existing artifacts are not owned by this run.' }
$null = New-Item -ItemType Directory -Path $ResultsDirectory
$scratch = Join-Path $ResultsDirectory 'work'
$null = New-Item -ItemType Directory -Path $scratch

function Invoke-OwnedIp {
    param([Parameter(Mandatory)] [string[]] $Arguments)
    & $ip @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Owned topology command failed ($LASTEXITCODE): ip $($Arguments -join ' ')" }
}

function Stop-OwnedProcess {
    param([Parameter(Mandatory)] [Diagnostics.Process] $OwnedProcess)
    if (!$OwnedProcess.HasExited) {
        try { $OwnedProcess.Kill($true) }
        catch [InvalidOperationException] { if (!$OwnedProcess.HasExited) { throw } }
        if (!$OwnedProcess.WaitForExit(10000)) { throw "Owned PID $($OwnedProcess.Id) did not exit after termination." }
    }
}

try {
    Invoke-OwnedIp -Arguments @('netns', 'add', $nativeNamespace)
    $createdNamespaces.Add($nativeNamespace)
    Invoke-OwnedIp -Arguments @('netns', 'add', $pythonNamespace)
    $createdNamespaces.Add($pythonNamespace)
    Invoke-OwnedIp -Arguments @('link', 'add', $nativeDevice, 'type', 'veth', 'peer', 'name', $pythonDevice)
    $vethCreated = $true
    Invoke-OwnedIp -Arguments @('link', 'set', $nativeDevice, 'netns', $nativeNamespace)
    $nativeMoved = $true
    Invoke-OwnedIp -Arguments @('link', 'set', $pythonDevice, 'netns', $pythonNamespace)
    foreach ($endpoint in @(
        @{ Namespace = $nativeNamespace; Device = $nativeDevice; Address = 'fe80::1/64' },
        @{ Namespace = $pythonNamespace; Device = $pythonDevice; Address = 'fe80::2/64' }
    )) {
        Invoke-OwnedIp -Arguments @('netns', 'exec', $endpoint.Namespace, 'sysctl', '-w', "net.ipv6.conf.$($endpoint.Device).disable_ipv6=0")
        Invoke-OwnedIp -Arguments @('-n', $endpoint.Namespace, 'link', 'set', 'dev', $endpoint.Device, 'addrgenmode', 'none')
        Invoke-OwnedIp -Arguments @('-n', $endpoint.Namespace, '-6', 'addr', 'add', $endpoint.Address, 'dev', $endpoint.Device, 'nodad')
        Invoke-OwnedIp -Arguments @('-n', $endpoint.Namespace, 'link', 'set', 'dev', $endpoint.Device, 'up')
        Invoke-OwnedIp -Arguments @('-n', $endpoint.Namespace, 'link', 'set', 'dev', 'lo', 'up')
        Invoke-OwnedIp -Arguments @('-n', $endpoint.Namespace, '-6', 'addr', 'show', 'dev', $endpoint.Device)
    }
    Write-Host "Owned topology: $nativeNamespace/$nativeDevice fe80::1 <-> $pythonNamespace/$pythonDevice fe80::2"
    Write-Host 'Only newly created namespaces/veth are configured; no physical NIC, existing route or serial device is touched.'
    $start = [Diagnostics.ProcessStartInfo]::new($ip)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $Repository
    foreach ($argument in @('netns', 'exec', $nativeNamespace, $Dotnet, 'test', $project, '-c', 'Release', '--no-restore')) {
        $start.ArgumentList.Add($argument)
    }
    if (!$Build) { $start.ArgumentList.Add('--no-build') }
    $filter = 'FullyQualifiedName~Parity6AutoLiveTests'
    if ($MulticastSmoke) { $filter += '|FullyQualifiedName~Parity6AutoMulticastTests' }
    foreach ($argument in @('--filter', $filter, '--logger', 'trx;LogFileName=auto-live.trx', '--results-directory', $ResultsDirectory)) {
        $start.ArgumentList.Add($argument)
    }
    $variables = @{
        DOTNET_ROOT = (Split-Path $Dotnet -Parent)
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_CLI_HOME = $scratch
        TMPDIR = $scratch
        DOTRETICULUM_AUTO_LIVE = '1'
        DOTRETICULUM_AUTO_MULTICAST = $(if ($MulticastSmoke) { '1' } else { '0' })
        DOTRETICULUM_AUTO_DEVICE = $nativeDevice
        DOTRETICULUM_AUTO_NATIVE_ADDRESS = 'fe80::1'
        DOTRETICULUM_AUTO_EXPECTED_PEER = 'fe80::2'
        DOTRETICULUM_AUTO_PYTHON_DEVICE = $pythonDevice
        DOTRETICULUM_AUTO_PYTHON_NAMESPACE = $pythonNamespace
        DOTRETICULUM_AUTO_IP = $ip
        DOTRETICULUM_AUTO_ARTIFACTS = $scratch
        DOTRETICULUM_PYTHON = $Python
        DOTRETICULUM_UPSTREAM = $Upstream
        PYTHONPATH = $Upstream
        PYTHONDONTWRITEBYTECODE = '1'
        GIT_CONFIG_COUNT = '1'
        GIT_CONFIG_KEY_0 = 'safe.directory'
        GIT_CONFIG_VALUE_0 = $Upstream
    }
    foreach ($entry in $variables.GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Could not start the owned namespace test process.' }
    $wait = $process.WaitForExitAsync()
    if (!$wait.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) {
        Stop-OwnedProcess -OwnedProcess $process
        throw "Owned Auto test process exceeded $TimeoutSeconds seconds and was terminated by its specific process handle."
    }
    if ($process.ExitCode -ne 0) { throw "Auto acceptance failed with test-runner exit code $($process.ExitCode). See $ResultsDirectory." }
    $report = Join-Path $ResultsDirectory 'auto-live.trx'
    if (!(Test-Path -LiteralPath $report -PathType Leaf)) { throw 'The test runner emitted no TRX evidence.' }
    [xml] $trx = Get-Content -LiteralPath $report -Raw
    $outcomes = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    $live = @($outcomes | Where-Object { $_.testName -like '*RealPythonDiscoveryBidirectionalPacketsIfacAndPeerExpiry*' })
    if ($live.Count -ne 2 -or @($live | Where-Object { $_.outcome -ne 'Passed' }).Count -gt 0) {
        throw "Real Python Auto acceptance requires two passed raw/IFAC cases, not skips or an empty filter. See $report."
    }
    if ($MulticastSmoke) {
        $smoke = @($outcomes | Where-Object { $_.testName -like '*RealIpv6MulticastReceivesOwnDiscoveryEcho*' })
        if ($smoke.Count -ne 1 -or $smoke[0].outcome -ne 'Passed') { throw 'Requested actual multicast smoke did not pass; a skip is not acceptance evidence.' }
    }
    Write-Host "Passed real pinned Python/.NET Auto raw and IFAC cases. Evidence: $report"
}
finally {
    $cleanupProblems = [System.Collections.Generic.List[string]]::new()
    if ($null -ne $process) {
        try { Stop-OwnedProcess -OwnedProcess $process }
        catch { $cleanupProblems.Add("Owned test-process cleanup failed: $_") }
        $process.Dispose()
    }
    foreach ($namespace in $createdNamespaces) {
        $ownedPids = @(& $ip netns pids $namespace)
        foreach ($ownedPid in $ownedPids) {
            if ("$ownedPid" -match '^\d+$') {
                Stop-Process -Id ([int] $ownedPid) -Force -ErrorAction SilentlyContinue
            }
        }
        & $ip netns delete $namespace
        if ($LASTEXITCODE -ne 0) { $cleanupProblems.Add("Could not remove owned namespace $namespace.") }
    }
    if ($vethCreated -and !$nativeMoved) {
        & $ip link delete $nativeDevice
        if ($LASTEXITCODE -ne 0) { $cleanupProblems.Add("Could not remove owned veth $nativeDevice.") }
    }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    if ($cleanupProblems.Count -gt 0) { throw ($cleanupProblems -join ' ') }
}
