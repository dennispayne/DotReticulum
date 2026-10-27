#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Application,
    [Parameter(Mandatory)] [string] $Directory,
    [string] $Gateway = 'reticulum.thecryptosquid.com',
    [ValidateRange(1, 65535)] [int] $Port = 4242,
    [ValidateRange(1, 3600)] [int] $TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
if ($Gateway -notmatch '^[a-zA-Z0-9.-]+$') { throw 'Gateway must be a DNS hostname or IPv4 address.' }
if (Test-Path -LiteralPath $Directory) { throw 'Fetch directory must be fresh.' }
. (Join-Path $PSScriptRoot 'Pester\TestProcess.ps1')
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Compatibility\catalog.json') -Raw | ConvertFrom-Json
$applicationPath = (Resolve-Path -LiteralPath $Application).Path
if ([IO.Path]::GetExtension($applicationPath) -eq '.dll') { throw 'Hosted reference retrieval requires the published native executable.' }
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
$missing = Invoke-TestProcess git @('--git-dir', $repository, 'cat-file', '-e', "$($catalog.reference.revision)^{commit}")
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
$fetch = Invoke-TestProcess $applicationPath @('rngit', 'fetch', $url, $repository,
    '--config', $config, '--timeout', "$TimeoutSeconds", '--single-branch') -TimeoutSeconds ($TimeoutSeconds + 30)
$fetch.Output
$fetch.Error | Write-Host
if ($fetch.Code -ne 0) { throw "Native rngit fetch failed with exit $($fetch.Code)." }
if ($fetch.Output -notmatch 'Received (\d+) bundle bytes over Reticulum' -or [long] $Matches[1] -le 0) {
    throw 'Reference retrieval requires nonzero Git bundle bytes over Reticulum.'
}
$bytes = [long] $Matches[1]
$null = Invoke-Git @('--git-dir', $repository, 'fsck', '--full', '--strict')
$headCommit = Invoke-Git @('--git-dir', $repository, 'rev-parse', 'HEAD')
$headTree = Invoke-Git @('--git-dir', $repository, 'rev-parse', 'HEAD^{tree}')
$null = Invoke-Git @('--git-dir', $repository, 'cat-file', '-e', "$($catalog.reference.revision)^{commit}")
$null = Invoke-Git @('--git-dir', $repository, 'update-ref', 'refs/heads/pinned-reference', $catalog.reference.revision)
$bundle = Join-Path $root 'reference.bundle'
$null = Invoke-Git @('--git-dir', $repository, 'bundle', 'create', $bundle, '--all')
[ordered]@{
    url = $url
    gateway = $Gateway
    seed = $seed
    seedSource = 'GitHub comparison mirror; only history through the parent of the pin'
    pin = $catalog.reference.revision
    head = $headCommit
    tree = $headTree
    networkBundleBytes = $bytes
    bundleSha256 = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash
    publisherAuthenticated = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'reference-provenance.json') -Encoding utf8
