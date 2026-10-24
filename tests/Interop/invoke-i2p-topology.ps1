#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $RouterBinary,
    [string] $SamEndpoint = '127.0.0.1:17656',
    [ValidateRange(0, 65535)][int] $TransportPort = 0,
    [string] $CertificatesDirectory,
    [string] $DotnetExecutable = 'dotnet',
    [string] $PythonExecutable = $env:DOTRETICULUM_PYTHON,
    [string] $UpstreamPath = $env:DOTRETICULUM_UPSTREAM,
    [switch] $AllowPublicNetwork,
    [switch] $RunStreamTests,
    [switch] $NoBuild,
    [switch] $KeepArtifacts,
    [ValidateRange(5, 180)][int] $StartupTimeoutSeconds = 30,
    [ValidateRange(60, 1800)][int] $TestTimeoutSeconds = 1200
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($RunStreamTests -and -not $AllowPublicNetwork) {
    throw 'Stock RNS I2P sessions need ready tunnels and lease-set discovery. Same-router zero-hop alone is insufficient. Stream tests require explicit -AllowPublicNetwork; isolated mode only tests real SAM destination persistence.'
}
if (-not (Test-Path -LiteralPath $RouterBinary -PathType Leaf)) { throw "Router binary does not exist: $RouterBinary" }
$endpoint = $null
if (-not [System.Net.IPEndPoint]::TryParse($SamEndpoint, [ref] $endpoint) -or
    -not [System.Net.IPAddress]::IsLoopback($endpoint.Address) -or
    $endpoint.Address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or
    $endpoint.Port -eq 0) {
    throw 'SamEndpoint must be an explicitly owned IPv4 loopback address:port.'
}
if ($RunStreamTests -and ([string]::IsNullOrWhiteSpace($PythonExecutable) -or [string]::IsNullOrWhiteSpace($UpstreamPath))) {
    throw 'Stream tests require PythonExecutable and UpstreamPath for the verified pinned reference.'
}
if ($AllowPublicNetwork -and ([string]::IsNullOrWhiteSpace($CertificatesDirectory) -or
    -not (Test-Path -LiteralPath $CertificatesDirectory -PathType Container))) {
    throw 'Public bootstrap requires CertificatesDirectory pointing at the extracted package public certificates; no verification bypass is applied.'
}
if (-not [string]::IsNullOrWhiteSpace($CertificatesDirectory) -and
    -not (Test-Path -LiteralPath $CertificatesDirectory -PathType Container)) {
    throw 'CertificatesDirectory does not exist.'
}

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$owned = Join-Path $root ('TestResults\i2p-topology-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $owned 'router'
$config = Join-Path $owned 'i2pd.conf'
$tunnels = Join-Path $owned 'tunnels.conf'
$tunnelsDirectory = Join-Path $owned 'tunnels.d'
$result = Join-Path $owned 'results'
$router = $null
$runner = $null
$success = $false

function Start-OwnedProcess {
    param([string] $FileName, [string[]] $Arguments, [string] $Prefix, [hashtable] $EnvironmentVariables = @{})
    $start = [System.Diagnostics.ProcessStartInfo]::new($FileName)
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in $EnvironmentVariables.Keys) { $start.Environment[$key] = $EnvironmentVariables[$key] }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    $stdout = [System.IO.File]::Open((Join-Path $owned "$Prefix.stdout.log"), 'CreateNew', 'Write', 'Read')
    $stderr = [System.IO.File]::Open((Join-Path $owned "$Prefix.stderr.log"), 'CreateNew', 'Write', 'Read')
    try {
        if (-not $process.Start()) { throw "Failed to start owned process $FileName" }
        return @{
            Process = $process; Stdout = $stdout; Stderr = $stderr
            OutputTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
            ErrorTask = $process.StandardError.BaseStream.CopyToAsync($stderr)
        }
    }
    catch {
        $stdout.Dispose()
        $stderr.Dispose()
        $process.Dispose()
        throw
    }
}

function Stop-OwnedProcess {
    param($OwnedProcess)
    if ($null -eq $OwnedProcess) { return }
    $process = $OwnedProcess.Process
    try {
        if (-not $process.HasExited) {
            # Only this process object and its descendants are owned; never stop by executable name.
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) { throw "Owned process $($process.Id) did not exit." }
        }
        [System.Threading.Tasks.Task]::WhenAll(
            [System.Threading.Tasks.Task[]]@($OwnedProcess.OutputTask, $OwnedProcess.ErrorTask)
        ).WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
    }
    finally {
        $OwnedProcess.Stdout.Dispose()
        $OwnedProcess.Stderr.Dispose()
        $process.Dispose()
    }
}

function Assert-OwnedLogsBounded {
    foreach ($file in Get-ChildItem -LiteralPath $owned -Filter '*.log' -File) {
        if ($file.Length -gt 16MB) { throw "Owned log exceeded its 16 MiB limit: $($file.Name)" }
    }
}

try {
    # Refuse to use an existing router or take over its listener.
    $probe = [System.Net.Sockets.TcpListener]::new($endpoint)
    try { $probe.Start() } finally { $probe.Stop() }
    $transportProbe = [System.Net.Sockets.TcpListener]::new($endpoint.Address, $TransportPort)
    try {
        $transportProbe.Start()
        $TransportPort = $transportProbe.LocalEndpoint.Port
        if ($TransportPort -eq $endpoint.Port) { throw 'SAM and NTCP2 ports must differ.' }
    }
    finally { $transportProbe.Stop() }
    $null = New-Item -ItemType Directory -Path $data, $tunnelsDirectory, $result -Force
    Set-Content -LiteralPath $tunnels -Value '' -Encoding utf8NoBOM
    $network = $AllowPublicNetwork.IsPresent.ToString().ToLowerInvariant()
    $networkId = if ($AllowPublicNetwork) { 2 } else { 222 }
    $binding = if ($AllowPublicNetwork) {
        'reservedrange = true'
    } else {
        # A real loopback NTCP2 listener requires a published RouterInfo address.
        # reservedrange=false permits that local-only address to survive parsing.
        "address4 = $($endpoint.Address)`nhost = $($endpoint.Address)`nreservedrange = false"
    }
    $text = @"
daemon = false
service = false
nat = false
notransit = true
share = 0
ipv4 = true
ipv6 = false
netid = $networkId
$binding
log = file
loglevel = warn
[sam]
enabled = true
address = $($endpoint.Address)
port = $($endpoint.Port)
portudp = 0
[ntcp2]
enabled = true
published = true
port = $TransportPort
proxy =
[ssu2]
enabled = $network
[http]
enabled = false
[httpproxy]
enabled = false
[socksproxy]
enabled = false
[bob]
enabled = false
[i2cp]
enabled = false
[i2pcontrol]
enabled = false
[upnp]
enabled = false
[addressbook]
enabled = false
[nettime]
enabled = false
frompeers = false
[meshnets]
yggdrasil = false
"@
    if (-not $AllowPublicNetwork) {
        # Threshold=0 alone does NOT disable reseeding: NetDb also checks floodfill count.
        $text += "`n[reseed]`nthreshold = 0`nurls =`nyggurls =`nfile =`nzipfile =`n"
    } else {
        $text += "`n[reseed]`nverify = true`n"
    }
    Set-Content -LiteralPath $config -Value $text -Encoding utf8NoBOM
    $routerArguments = @(
        "--conf=$config", "--datadir=$data", "--tunconf=$tunnels", "--tunnelsdir=$tunnelsDirectory",
        "--pidfile=$(Join-Path $owned 'i2pd.pid')", "--logfile=$(Join-Path $owned 'router.log')"
    )
    if (-not [string]::IsNullOrWhiteSpace($CertificatesDirectory)) {
        $routerArguments += "--certsdir=$([System.IO.Path]::GetFullPath($CertificatesDirectory))"
    }
    $router = Start-OwnedProcess -FileName ([System.IO.Path]::GetFullPath($RouterBinary)) -Arguments $routerArguments -Prefix 'router'
    Write-Host "Owned router PID $($router.Process.Id); SAM $SamEndpoint; NTCP2 port $TransportPort; public network allowed: $($AllowPublicNetwork.IsPresent)"
    $ready = $false
    $startup = [System.Diagnostics.Stopwatch]::StartNew()
    while ($startup.Elapsed.TotalSeconds -lt $StartupTimeoutSeconds) {
        if ($router.Process.HasExited) { throw "Owned router exited with code $($router.Process.ExitCode)." }
        Assert-OwnedLogsBounded
        $client = [System.Net.Sockets.TcpClient]::new()
        try {
            $client.ConnectAsync($endpoint.Address, $endpoint.Port).WaitAsync([TimeSpan]::FromMilliseconds(500)).GetAwaiter().GetResult()
            $stream = $client.GetStream()
            $stream.ReadTimeout = 1000
            $stream.WriteTimeout = 1000
            $hello = [System.Text.Encoding]::ASCII.GetBytes("HELLO VERSION MIN=3.1 MAX=3.1`n")
            $stream.Write($hello, 0, $hello.Length)
            $reader = [System.IO.StreamReader]::new($stream)
            try {
                $line = $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(1)).GetAwaiter().GetResult()
                if ($line -eq 'HELLO REPLY RESULT=OK VERSION=3.1') { $ready = $true; break }
                throw "Unexpected actual SAM handshake: $line"
            }
            finally { $reader.Dispose() }
        }
        catch [System.Net.Sockets.SocketException] { Start-Sleep -Milliseconds 100 }
        catch [System.TimeoutException] { Start-Sleep -Milliseconds 100 }
        finally { $client.Dispose() }
    }
    if (-not $ready) { throw 'Owned router did not answer SAM 3.1 within the startup deadline.' }
    $transportCheck = [System.Net.Sockets.TcpClient]::new()
    try {
        $transportCheck.ConnectAsync($endpoint.Address, $TransportPort).WaitAsync([TimeSpan]::FromSeconds(2)).GetAwaiter().GetResult()
    }
    finally { $transportCheck.Dispose() }
    $filter = if ($RunStreamTests) {
        'FullyQualifiedName~Parity6I2pLiveTests'
    } else {
        'FullyQualifiedName~Parity6I2pLiveTests.ActualRouterSamDestinationPersistenceWithoutOverlay'
    }
    $arguments = @(
        'test', (Join-Path $root 'tests\DotReticulum.Interfaces.Tests\DotReticulum.Interfaces.Tests.csproj'),
        '-c', 'Release', '--filter', $filter, '--results-directory', $result,
        '--logger', 'trx;LogFileName=i2p-live.trx'
    )
    if ($NoBuild) { $arguments += '--no-build' }
    $environment = @{ DOTRETICULUM_I2P_SAM = $SamEndpoint }
    if (-not [string]::IsNullOrWhiteSpace($PythonExecutable)) { $environment.DOTRETICULUM_PYTHON = $PythonExecutable }
    if (-not [string]::IsNullOrWhiteSpace($UpstreamPath)) { $environment.DOTRETICULUM_UPSTREAM = $UpstreamPath }
    $runner = Start-OwnedProcess -FileName $DotnetExecutable -Arguments $arguments -Prefix 'tests' -EnvironmentVariables $environment
    $execution = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $runner.Process.WaitForExit(500)) {
        if ($router.Process.HasExited) { throw 'Owned router exited while the actual-router test was running.' }
        if ($execution.Elapsed.TotalSeconds -gt $TestTimeoutSeconds) { throw 'Actual-router test process exceeded its overall deadline.' }
        Assert-OwnedLogsBounded
    }
    $exitCode = $runner.Process.ExitCode
    Stop-OwnedProcess $runner
    $runner = $null
    Get-Content -LiteralPath (Join-Path $owned 'tests.stdout.log') -Tail 160 | Write-Host
    if ($exitCode -ne 0) {
        Get-Content -LiteralPath (Join-Path $owned 'tests.stderr.log') -Tail 80 | Write-Host
        throw "Actual-router tests failed with exit code $exitCode."
    }
    $report = Join-Path $result 'i2p-live.trx'
    if (-not (Test-Path -LiteralPath $report -PathType Leaf)) { throw 'The selected actual-router test run produced no TRX evidence.' }
    [xml] $trx = Get-Content -LiteralPath $report -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $expected = if ($RunStreamTests) { 4 } else { 1 }
    if ([int] $counters.total -ne $expected -or [int] $counters.passed -ne $expected -or [int] $counters.failed -ne 0) {
        throw "Expected $expected executed/passed actual-router cases, got total=$($counters.total), passed=$($counters.passed), failed=$($counters.failed). A skipped/missing test is not acceptance."
    }
    $success = $true
    Write-Host 'Actual-router selected tests passed. Isolated SAM persistence is not streaming or remote-overlay evidence.'
}
finally {
    try { Stop-OwnedProcess $runner }
    finally { Stop-OwnedProcess $router }
    if ($success -and -not $KeepArtifacts) {
        if (Test-Path -LiteralPath $owned) { Remove-Item -LiteralPath $owned -Recurse -Force }
    } elseif (Test-Path -LiteralPath $owned) {
        Write-Host "Owned artifacts retained at $owned (contains private router/destination state; do not commit)."
    }
}
