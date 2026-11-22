function Get-CompatibilityReferenceRevision {
    $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\Interop\reference_environment.py') -Raw
    $match = [regex]::Match($source, '(?m)^PINNED_RNS_SHA\s*=\s*"([a-f0-9]{40})"')
    if (!$match.Success) { throw 'The canonical Python reference pin was not found.' }
    $match.Groups[1].Value
}

function New-ReferenceGatewayConfiguration {
    param(
        [Parameter(Mandatory)] [string] $Gateway,
        [AllowEmptyString()] [string] $AdditionalGateway = '',
        [ValidateRange(1, 65535)] [int] $Port = 4242
    )
    $gateways = @($Gateway)
    if ($AdditionalGateway) { $gateways += $AdditionalGateway }
    foreach ($hostName in $gateways) {
        if ($hostName -notmatch '^[a-zA-Z0-9.-]+$') {
            throw 'Gateway must be a DNS hostname or IPv4 address.'
        }
    }
    if ($gateways.Count -eq 2 -and $gateways[0] -ieq $gateways[1]) {
        throw 'Reference gateways must be distinct.'
    }
    $lines = @('[reticulum]', 'share_instance = no', 'enable_transport = no', '[interfaces]')
    for ($index = 0; $index -lt $gateways.Count; $index++) {
        $lines += "  [[CI gateway $($index + 1)]]", '    type = TCPClientInterface',
            '    enabled = yes', "    target_host = $($gateways[$index])", "    target_port = $Port"
    }
    [pscustomobject]@{
        Configuration = ($lines -join "`n") + "`n"
        Gateways = @($gateways | ForEach-Object { [pscustomobject]@{ host = $_; port = $Port } })
    }
}
