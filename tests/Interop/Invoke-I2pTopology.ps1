#Requires -Version 7.3

<#
.SYNOPSIS
Starts an owned I2P router and runs bounded SAM or stream acceptance.
.DESCRIPTION
Supports i2pd or an extracted Java I2P distribution. Public networking requires
-AllowPublicNetwork; mixed-peer stream tests also require the verified Python reference.
Owned processes are stopped on exit. -KeepArtifacts retains diagnostic files.
.PARAMETER RouterBinary
Path to the i2pd executable. Required in the default I2pd parameter set.
.PARAMETER JavaExecutable
Path to the Java executable. Required in the Java parameter set.
.PARAMETER JavaRouterDirectory
Extracted Java I2P distribution containing hosts.txt and the required libraries.
Required with JavaExecutable.
.PARAMETER SamEndpoint
Owned IPv4 loopback address and port for SAM. Defaults to 127.0.0.1:17656.
Java mode requires the host address 127.0.0.1.
.PARAMETER TransportPort
Router transport port, from 0 through 65535. Defaults to 0 to select a free port.
.PARAMETER CertificatesDirectory
Public router certificate directory required for public bootstrap.
Java mode defaults to the certificates directory in JavaRouterDirectory.
.PARAMETER SeedRouterInfoDirectory
Optional public netDb seed directory containing bounded routerInfo files in their
standard hash subdirectories. Requires AllowPublicNetwork.
.PARAMETER DotnetExecutable
Command or executable path for .NET used to run tests. Defaults to dotnet.
.PARAMETER PythonExecutable
Python reference interpreter. Defaults to DOTRETICULUM_PYTHON.
Required for RunStreamTests or ReferenceRestartOnly.
.PARAMETER UpstreamPath
Verified pinned reference checkout. Defaults to DOTRETICULUM_UPSTREAM.
Required for RunStreamTests or ReferenceRestartOnly.
.PARAMETER AllowPublicNetwork
Explicitly permits public I2P bootstrap/networking. Required for Java mode and
stream/restart tests; without it, i2pd runs isolated SAM persistence acceptance.
.PARAMETER RunStreamTests
Runs actual-router stream tests in addition to SAM persistence acceptance.
Requires public networking and the verified reference.
.PARAMETER ReferenceRestartOnly
Selects the upstream-only restart diagnostic test group.
Requires public networking and the reference; cannot be combined with RunStreamTests.
.PARAMETER NativeRestartOnly
Selects only the native persisted-bearer restart and bidirectional DATA test.
Requires public networking; cannot be combined with the other test selections.
.PARAMETER PublishedFeatureOnly
Runs the published-native rnprobe fresh/persisted startup DATA/proof flow using the Applications
test project. Requires AllowPublicNetwork and an existing DOTRETICULUM_NATIVE_EXE;
cannot be combined with other test selections. This is native integration,
not a Python baseline or a public-overlay claim.
.PARAMETER JavaLocalDestinationRouting
Retains Java I2P's default local-to-local destination delivery instead of forcing
same-router traffic through public tunnels. This is actual SAM/stream delivery,
not public-overlay or remote-router acceptance. Available only in Java mode.
.PARAMETER SingleThreadedSam
Enables i2pd's single-threaded SAM mode for diagnostics. Available only in I2pd mode.
.PARAMETER NoBuild
Uses the existing Release test build rather than building before execution.
.PARAMETER TestAssemblyPath
Runs an existing test assembly with dotnet vstest instead of building/testing the
project. Copy its complete output directory first to isolate assembly locks from
other builds. Cannot be combined with NoBuild.
.PARAMETER KeepArtifacts
Retains owned router configuration, logs and test results after success.
Failed runs retain artifacts regardless of this switch.
.PARAMETER StartupTimeoutSeconds
Router SAM startup deadline, from 5 through 180 seconds. Defaults to 30.
.PARAMETER TestTimeoutSeconds
Overall test-process deadline, from 60 through 1800 seconds. Defaults to 1200.
.EXAMPLE
.\Invoke-I2pTopology.ps1 -RouterBinary C:\Tools\i2pd.exe
.EXAMPLE
.\Invoke-I2pTopology.ps1 -JavaExecutable C:\Java\bin\java.exe -JavaRouterDirectory C:\I2P -AllowPublicNetwork -NativeRestartOnly -JavaLocalDestinationRouting -TestAssemblyPath C:\Git\DotReticulum\TestResults\isolated\DotReticulum.Interfaces.Tests.dll
.EXAMPLE
$env:DOTRETICULUM_NATIVE_EXE = 'C:\Git\DotReticulum\artifacts\native\DotReticulum.Applications.exe'
.\Invoke-I2pTopology.ps1 -JavaExecutable C:\Java\bin\java.exe -JavaRouterDirectory C:\I2P -AllowPublicNetwork -JavaLocalDestinationRouting -PublishedFeatureOnly -NoBuild
#>
[CmdletBinding(DefaultParameterSetName = 'I2pd')]
param(
    [Parameter(Mandatory, ParameterSetName = 'I2pd')][string] $RouterBinary,
    [Parameter(Mandatory, ParameterSetName = 'Java')][string] $JavaExecutable,
    [Parameter(Mandatory, ParameterSetName = 'Java')][string] $JavaRouterDirectory,
    [string] $SamEndpoint = '127.0.0.1:17656',
    [ValidateRange(0, 65535)][int] $TransportPort = 0,
    [string] $CertificatesDirectory,
    [string] $SeedRouterInfoDirectory,
    [string] $DotnetExecutable = 'dotnet',
    [string] $PythonExecutable = $env:DOTRETICULUM_PYTHON,
    [string] $UpstreamPath = $env:DOTRETICULUM_UPSTREAM,
    [switch] $AllowPublicNetwork,
    [switch] $RunStreamTests,
    [switch] $ReferenceRestartOnly,
    [switch] $NativeRestartOnly,
    [switch] $PublishedFeatureOnly,
    [Parameter(ParameterSetName = 'Java')][switch] $JavaLocalDestinationRouting,
    [Parameter(ParameterSetName = 'I2pd')][switch] $SingleThreadedSam,
    [switch] $NoBuild,
    [string] $TestAssemblyPath,
    [switch] $KeepArtifacts,
    [ValidateRange(5, 180)][int] $StartupTimeoutSeconds = 30,
    [ValidateRange(60, 1800)][int] $TestTimeoutSeconds = 1200
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$javaRouter = $PSCmdlet.ParameterSetName -eq 'Java'
if ($javaRouter) {
    if (-not $AllowPublicNetwork) { throw 'Java router requires explicit -AllowPublicNetwork; no zero-hop/isolated Java mode is configured.' }
    if (-not (Test-Path -LiteralPath $JavaExecutable -PathType Leaf)) { throw 'JavaExecutable does not exist.' }
    $JavaExecutable = (Resolve-Path -LiteralPath $JavaExecutable).ProviderPath
    if (-not (Test-Path -LiteralPath $JavaRouterDirectory -PathType Container)) { throw 'JavaRouterDirectory does not exist.' }
    $JavaRouterDirectory = (Resolve-Path -LiteralPath $JavaRouterDirectory).ProviderPath
    foreach ($asset in @('hosts.txt', 'lib\i2p.jar', 'lib\router.jar', 'lib\sam.jar', 'lib\mstreaming.jar', 'lib\streaming.jar')) {
        if (-not (Test-Path -LiteralPath (Join-Path $JavaRouterDirectory $asset) -PathType Leaf)) {
            throw "JavaRouterDirectory is not a complete extracted distribution: missing $asset"
        }
    }
    if ([string]::IsNullOrWhiteSpace($CertificatesDirectory)) {
        $CertificatesDirectory = Join-Path $JavaRouterDirectory 'certificates'
    }
} elseif (-not (Test-Path -LiteralPath $RouterBinary -PathType Leaf)) {
    throw "Router binary does not exist: $RouterBinary"
}

if ($ReferenceRestartOnly) {
    if (-not $AllowPublicNetwork) { throw 'ReferenceRestartOnly requires explicit -AllowPublicNetwork.' }
    if ($RunStreamTests) { throw 'ReferenceRestartOnly and RunStreamTests select separate test groups.' }
}
if ($NativeRestartOnly) {
    if (-not $AllowPublicNetwork) { throw 'NativeRestartOnly requires explicit -AllowPublicNetwork.' }
    if ($RunStreamTests -or $ReferenceRestartOnly) { throw 'NativeRestartOnly selects a separate test group.' }
}
if ($PublishedFeatureOnly) {
    if (-not $AllowPublicNetwork) { throw 'PublishedFeatureOnly requires explicit -AllowPublicNetwork.' }
    if ($RunStreamTests -or $ReferenceRestartOnly -or $NativeRestartOnly) { throw 'PublishedFeatureOnly selects a separate test group.' }
    if ([string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NATIVE_EXE) -or
        -not (Test-Path -LiteralPath $env:DOTRETICULUM_NATIVE_EXE -PathType Leaf)) {
        throw 'PublishedFeatureOnly requires DOTRETICULUM_NATIVE_EXE pointing at an existing published executable.'
    }
}
if (-not [string]::IsNullOrWhiteSpace($TestAssemblyPath)) {
    if ($NoBuild) { throw 'TestAssemblyPath and NoBuild select separate execution modes.' }
    if (-not (Test-Path -LiteralPath $TestAssemblyPath -PathType Leaf)) { throw 'TestAssemblyPath does not exist.' }
    $TestAssemblyPath = (Resolve-Path -LiteralPath $TestAssemblyPath).ProviderPath
}
if ($RunStreamTests -and -not $AllowPublicNetwork) {
    throw 'Stock RNS I2P sessions need ready tunnels and lease-set discovery. Same-router zero-hop alone is insufficient. Stream tests require explicit -AllowPublicNetwork; isolated mode only tests real SAM destination persistence.'
}
$endpoint = $null
if (-not [System.Net.IPEndPoint]::TryParse($SamEndpoint, [ref] $endpoint) -or
    -not [System.Net.IPAddress]::IsLoopback($endpoint.Address) -or
    $endpoint.Address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or
    $endpoint.Port -eq 0) {
    throw 'SamEndpoint must be an explicitly owned IPv4 loopback address:port.'
}
if ($javaRouter -and -not $endpoint.Address.Equals([System.Net.IPAddress]::Loopback)) {
    throw 'Java SAM requires SamEndpoint host 127.0.0.1.'
}
if (($RunStreamTests -or $ReferenceRestartOnly) -and ([string]::IsNullOrWhiteSpace($PythonExecutable) -or [string]::IsNullOrWhiteSpace($UpstreamPath))) {
    throw 'Stream tests require PythonExecutable and UpstreamPath for the verified pinned reference.'
}
if (-not [string]::IsNullOrWhiteSpace($PythonExecutable)) {
    if (-not (Test-Path -LiteralPath $PythonExecutable -PathType Leaf)) { throw 'PythonExecutable does not exist.' }
    $PythonExecutable = (Resolve-Path -LiteralPath $PythonExecutable).ProviderPath
}
if (-not [string]::IsNullOrWhiteSpace($UpstreamPath)) {
    if (-not (Test-Path -LiteralPath $UpstreamPath -PathType Container)) { throw 'UpstreamPath does not exist.' }
    $UpstreamPath = (Resolve-Path -LiteralPath $UpstreamPath).ProviderPath
}
if ($AllowPublicNetwork -and ([string]::IsNullOrWhiteSpace($CertificatesDirectory) -or
    -not (Test-Path -LiteralPath $CertificatesDirectory -PathType Container))) {
    throw 'Public bootstrap requires CertificatesDirectory pointing at the extracted package public certificates; no verification bypass is applied.'
}
if (-not [string]::IsNullOrWhiteSpace($CertificatesDirectory) -and
    -not (Test-Path -LiteralPath $CertificatesDirectory -PathType Container)) {
    throw 'CertificatesDirectory does not exist.'
}
if ($javaRouter -and -not (Test-Path -LiteralPath (Join-Path $CertificatesDirectory 'reseed') -PathType Container)) {
    throw 'Java router requires a public certificates root containing reseed certificates.'
}
$seedRouterInfos = @()
if (-not [string]::IsNullOrWhiteSpace($SeedRouterInfoDirectory)) {
    if (-not $AllowPublicNetwork) { throw 'SeedRouterInfoDirectory requires explicit -AllowPublicNetwork.' }
    if (-not (Test-Path -LiteralPath $SeedRouterInfoDirectory -PathType Container)) {
        throw 'SeedRouterInfoDirectory does not exist.'
    }
    $SeedRouterInfoDirectory = (Resolve-Path -LiteralPath $SeedRouterInfoDirectory).ProviderPath
    $seedRoot = Get-Item -LiteralPath $SeedRouterInfoDirectory
    if ($seedRoot.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw 'SeedRouterInfoDirectory must not be a symbolic link/reparse point.'
    }
    $seedRouterInfos = @(Get-ChildItem -LiteralPath $SeedRouterInfoDirectory -Recurse -File -Filter 'routerInfo-*.dat' |
        Where-Object { $_.Name -cmatch '^routerInfo-[A-Za-z0-9~-]{43}=\.dat$' })
    if ($seedRouterInfos.Count -eq 0 -or $seedRouterInfos.Count -gt 10000) {
        throw 'SeedRouterInfoDirectory must contain 1..10000 public routerInfo-<I2P-hash>.dat files.'
    }
    foreach ($file in $seedRouterInfos) {
        $relative = [System.IO.Path]::GetRelativePath($SeedRouterInfoDirectory, $file.FullName)
        $expected = Join-Path ('r' + $file.Name[11]) $file.Name
        if ($relative -cne $expected -or $file.Length -lt 1 -or $file.Length -gt 65536 -or
            ($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or
            ($file.Directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw 'Seed RouterInfos must be bounded regular files in their standard netDb hash subdirectory.'
        }
    }
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
    $null = $start.Environment.Remove('GITHUB_STEP_SUMMARY')
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
    $transportAddress = if ($javaRouter) { [System.Net.IPAddress]::Any } else { $endpoint.Address }
    $transportProbe = [System.Net.Sockets.TcpListener]::new($transportAddress, $TransportPort)
    try {
        $transportProbe.Start()
        $TransportPort = $transportProbe.LocalEndpoint.Port
        if ($TransportPort -eq $endpoint.Port) { throw 'SAM and NTCP2 ports must differ.' }
        if ($javaRouter) {
            $udpProbe = [System.Net.Sockets.UdpClient]::new([System.Net.Sockets.AddressFamily]::InterNetwork)
            try {
                $udpProbe.ExclusiveAddressUse = $true
                $udpProbe.Client.Bind([System.Net.IPEndPoint]::new($transportAddress, $TransportPort))
            }
            finally { $udpProbe.Dispose() }
        }
    }
    finally { $transportProbe.Stop() }
    $null = New-Item -ItemType Directory -Path $data, $tunnelsDirectory, $result -Force
    foreach ($file in $seedRouterInfos) {
        $seedTarget = Join-Path (Join-Path $data 'netDb') ('r' + $file.Name[11])
        $null = New-Item -ItemType Directory -Path $seedTarget -Force
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $seedTarget $file.Name)
    }
    if ($seedRouterInfos.Count -gt 0) {
        Write-Host "Seeded public NetDb: $($seedRouterInfos.Count) RouterInfo files from $SeedRouterInfoDirectory; no router keys, private destinations or lease sets copied. Router validates RouterInfos."
    }
    if ($javaRouter) {
        $config = Join-Path $data 'router.config'
        $javaBase = Join-Path $owned 'java-base'
        $javaScratch = Join-Path $owned 'java-scratch'
        $null = New-Item -ItemType Directory -Path $javaBase, $javaScratch
        Copy-Item -LiteralPath (Join-Path $JavaRouterDirectory 'hosts.txt') -Destination $javaBase
        Copy-Item -LiteralPath $CertificatesDirectory -Destination (Join-Path $javaBase 'certificates') -Recurse
        $disableLoopback = (-not $JavaLocalDestinationRouting).ToString().ToLowerInvariant()
        @"
i2cp.disableInterface=true
i2cp.disableLoopback=$disableLoopback
i2np.upnp.enable=false
router.sharePercentage=0
router.maxParticipatingTunnels=0
i2np.ntcp.port=$TransportPort
i2np.ntcp.autoport=false
i2np.udp.internalPort=$TransportPort
i2np.udp.port=$TransportPort
i2np.udp.fixedPort=true
"@ | Set-Content -LiteralPath $config -Encoding utf8NoBOM
        @"
clientApp.0.main=net.i2p.sam.SAMBridge
clientApp.0.name=Owned SAM
clientApp.0.args=sam.keys 127.0.0.1 $($endpoint.Port)
clientApp.0.onBoot=true
clientApp.0.startOnLoad=true
"@ | Set-Content -LiteralPath (Join-Path $data 'clients.config') -Encoding utf8NoBOM
        $routerArguments = @(
            '-Xmx512m', '-Djava.awt.headless=true', '-DI2P_DISABLE_OUTPUT_OVERRIDE=true',
            # Explicit non-portable config: 2.14.0 portable mode recurses during Linux temp setup.
            "-Djava.io.tmpdir=$javaScratch",
            "-Di2p.dir.base=$javaBase", "-Di2p.dir.config=$data",
            "-Di2p.dir.router=$data", "-Di2p.dir.app=$data", "-Di2p.dir.log=$data", "-Di2p.dir.pid=$data",
            "-Drouter.configLocation=$config", '-cp', (Join-Path $JavaRouterDirectory 'lib\*'),
            'net.i2p.router.Router'
        )
        $routerExecutable = $JavaExecutable
        Write-Host "Java I2P distribution $JavaRouterDirectory; owned SAM 127.0.0.1:$($endpoint.Port); TCP/UDP transport port $TransportPort; public network; local destination routing $($JavaLocalDestinationRouting.IsPresent); transit disabled; source client tunnel defaults unchanged."
        if ($JavaLocalDestinationRouting) {
            Write-Host 'Java default local-to-local I2P delivery enabled: this run does not establish traffic through public tunnels or a remote router.'
        }
    } else {
    Set-Content -LiteralPath $tunnels -Value '' -Encoding utf8NoBOM
    $network = $AllowPublicNetwork.IsPresent.ToString().ToLowerInvariant()
    $samSingleThread = $SingleThreadedSam.IsPresent.ToString().ToLowerInvariant()
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
singlethread = $samSingleThread
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
    $routerExecutable = [System.IO.Path]::GetFullPath($RouterBinary)
    Write-Host "i2pd SAM singlethread: $samSingleThread; public network allowed: $($AllowPublicNetwork.IsPresent)"
    if ($SingleThreadedSam) {
        Write-Warning 'i2pd 2.59.0 single-threaded SAM can delete a replacement persisted destination during delayed cleanup of the previous session. This mode preserves the router-default failure reproducer.'
    }
    }
    $router = Start-OwnedProcess -FileName $routerExecutable -Arguments $routerArguments -Prefix 'router'
    Write-Host "Owned router PID $($router.Process.Id); SAM $SamEndpoint; transport port $TransportPort."
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
    if (-not $javaRouter) {
    $transportCheck = [System.Net.Sockets.TcpClient]::new()
    try {
        $transportCheck.ConnectAsync($endpoint.Address, $TransportPort).WaitAsync([TimeSpan]::FromSeconds(2)).GetAwaiter().GetResult()
    }
    finally { $transportCheck.Dispose() }
    }
    $filter = if ($PublishedFeatureOnly) {
        'FullyQualifiedName~I2pNativeTests.PublishedProbeExchangesEncryptedDataAndValidatedProofsAcrossFreshAndPersistedProcesses'
    } elseif ($ReferenceRestartOnly) {
        'FullyQualifiedName~Parity6I2pReferenceRecoveryTests'
    } elseif ($NativeRestartOnly) {
        'FullyQualifiedName~Parity6I2pLiveTests.ActualRouterNativeStreamsAreBidirectionalAndPersistAcrossBearerRestart'
    } elseif ($RunStreamTests) {
        'FullyQualifiedName~Parity6I2pLiveTests'
    } else {
        'FullyQualifiedName~Parity6I2pLiveTests.ActualRouterSamDestinationPersistenceWithoutOverlay'
    }
    $arguments = if ([string]::IsNullOrWhiteSpace($TestAssemblyPath)) {
        $project = if ($PublishedFeatureOnly) { 'DotReticulum.Applications.Tests' } else { 'DotReticulum.Interfaces.Tests' }
        @(
            'test', (Join-Path $root "tests\$project\$project.csproj"),
            '-c', 'Release', '--filter', $filter, '--results-directory', $result,
            '--logger', 'trx;LogFileName=i2p-live.trx'
        )
    } else {
        @('vstest', $TestAssemblyPath, "--TestCaseFilter:$filter", "--ResultsDirectory:$result",
            '--Logger:trx;LogFileName=i2p-live.trx')
    }
    if ($NoBuild) { $arguments += '--no-build' }
    $environment = @{ DOTRETICULUM_I2P_SAM = $SamEndpoint; I2P_SAM_ADDRESS = $SamEndpoint }
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
    Write-Host 'Selected actual-router cases passed; consult the TRX packet assertions and topology. SAM persistence alone is not stream evidence; local destination routing is not public-overlay evidence.'
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
