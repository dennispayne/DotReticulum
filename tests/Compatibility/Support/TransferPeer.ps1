function New-CompatibilityTransferEndpoint {
    param([hashtable] $Context, [switch] $Reference)
    if ($Reference -or $Context.Implementation -eq 'Upstream') {
        return @{
            Executable = $Context.Python
            Prefix = @('-u', (Join-Path $PSScriptRoot '..\..\Interop\CompatibilityTransferReference.py'),
                '--revision', $Context.Revision, '--')
            Environment = $Context.PeerEnvironment
        }
    }
    @{ Executable = $Context.Executable; Prefix = $Context.Prefix + @('rncp'); Environment = $Context.Environment }
}

function New-CompatibilityTransferConfig {
    param([string] $Directory, [int] $Port, [switch] $Server,
        [string] $NetworkName, [string] $Passphrase)
    $null = New-Item -ItemType Directory -Path $Directory -Force
    $endpoint = if ($Server) { "listen_ip = 127.0.0.1`nlisten_port = $Port" }
        else { "target_host = 127.0.0.1`ntarget_port = $Port" }
    $type = if ($Server) { 'TCPServerInterface' } else { 'TCPClientInterface' }
    $authentication = if ($NetworkName -or $Passphrase) {
        "ifac_size = 128`nnetwork_name = $NetworkName`npassphrase = $Passphrase"
    } else { '' }
    @"
[reticulum]
share_instance = no
[logging]
loglevel = 6
[interfaces]
[[Owned compatibility peer]]
type = $type
enabled = yes
$endpoint
$authentication
"@ | Set-Content -LiteralPath (Join-Path $Directory 'config') -Encoding utf8
    $Directory
}

function New-CompatibilityTransferPair {
    param([hashtable] $Context, [string] $Directory, [string] $NetworkName, [string] $Passphrase)
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { $port = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
    $root = Join-Path $Directory ([guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $root
    @{
        Root = $root; Port = $port
        Candidate = New-CompatibilityTransferEndpoint $Context
        Reference = New-CompatibilityTransferEndpoint $Context -Reference
        CandidateConfig = New-CompatibilityTransferConfig (Join-Path $root 'candidate') $port `
            -NetworkName $NetworkName -Passphrase $Passphrase
        ReferenceConfig = New-CompatibilityTransferConfig (Join-Path $root 'reference') $port -Server `
            -NetworkName $NetworkName -Passphrase $Passphrase
    }
}

function Invoke-CompatibilityTransferCommand {
    param([hashtable] $Endpoint, [string[]] $Arguments, [string] $Directory,
        [string] $LogName, [int] $TimeoutSeconds = 120)
    $peer = Start-TestProcess $Endpoint.Executable ($Endpoint.Prefix + $Arguments) `
        -Environment $Endpoint.Environment -WorkingDirectory $Directory
    try {
        $result = Complete-TestProcess $peer $TimeoutSeconds
        $result.Output | Set-Content -LiteralPath (Join-Path $Directory "$LogName-stdout.txt") -Encoding utf8
        $result.Error | Set-Content -LiteralPath (Join-Path $Directory "$LogName-stderr.txt") -Encoding utf8
        # Preserve raw logs, but avoid putting terminal backspaces into NUnit XML.
        $result.Output = $result.Output -replace '[\x00-\x08\x0B\x0C\x0E-\x1F]', ''
        $result.Error = $result.Error -replace '[\x00-\x08\x0B\x0C\x0E-\x1F]', ''
        $result
    }
    catch {
        $_ | Out-String | Set-Content -LiteralPath (Join-Path $Directory "$LogName-failure.txt") -Encoding utf8
        throw
    }
}

function Get-CompatibilityTransferIdentity {
    param([hashtable] $Endpoint, [string] $Config, [string] $Directory, [string] $Name)
    $result = Invoke-CompatibilityTransferCommand $Endpoint @('--config', $Config, '-p') $Directory $Name
    if ($result.Code -ne 0) { throw "Identity setup failed: $($result.Output) $($result.Error)" }
    $identity = [regex]::Match($result.Output, 'Identity\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
    $destination = [regex]::Match($result.Output, 'Listening on\s*:\s*<([0-9a-f]{32})>').Groups[1].Value
    if (!$identity -or !$destination) { throw "Identity output was incomplete: $($result.Output)" }
    @{ Identity = $identity; Destination = $destination }
}

function Start-CompatibilityTransferListener {
    param([hashtable] $Endpoint, [string] $Config, [string] $AllowedIdentity,
        [string] $Directory, [string] $Jail)
    $arguments = @('--config', $Config, '-l', '-a', $AllowedIdentity, '-F', '-b', '1', '-vv')
    if ($Jail) { $arguments += @('-j', $Jail) }
    $peer = Start-TestProcess $Endpoint.Executable ($Endpoint.Prefix + $arguments) `
        -Environment $Endpoint.Environment -WorkingDirectory $Directory
    Start-Sleep -Milliseconds 500
    if ($peer.Process.HasExited) {
        $result = Complete-TestProcess $peer
        throw "Transfer listener exited: $($result.Output) $($result.Error)"
    }
    $peer
}

function Stop-CompatibilityTransferListener {
    param([hashtable] $Peer, [string] $Directory)
    if (!$Peer) { return }
    try {
        if (!$Peer.Process.HasExited) { $Peer.Process.Kill($true); $Peer.Process.WaitForExit() }
        $Peer.Output.GetAwaiter().GetResult() |
            Set-Content -LiteralPath (Join-Path $Directory 'listener-stdout.txt') -Encoding utf8
        $Peer.Error.GetAwaiter().GetResult() |
            Set-Content -LiteralPath (Join-Path $Directory 'listener-stderr.txt') -Encoding utf8
    }
    finally { $Peer.Process.Dispose() }
}

function Wait-CompatibilityTransferFile {
    param([string] $Path, [long] $Length)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (!(Test-Path -LiteralPath $Path) -or (Get-Item -LiteralPath $Path).Length -ne $Length) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Transferred file did not persist at expected length: $Path ($Length bytes)" }
        Start-Sleep -Milliseconds 50
    }
}
