function Get-CompatibilityClaims {
    param([string] $Path = (Join-Path $PSScriptRoot '..\catalog.json'))
    $catalog = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($catalog.claims -isnot [array] -or !$catalog.claims.Count) {
        throw 'Compatibility catalog must contain a nonempty claims array.'
    }
    $ids = @{}
    foreach ($claim in $catalog.claims) {
        if ($claim.id -cnotmatch '^C[0-9]{3,}$' -or $ids.ContainsKey($claim.id)) {
            throw "Invalid or duplicate catalog claim: $($claim.id)"
        }
        if ($claim.claim -isnot [string] -or [string]::IsNullOrWhiteSpace($claim.claim)) {
            throw "Claim $($claim.id) requires a quoted upstream statement."
        }
        $citation = $claim.citation
        if ($citation -isnot [pscustomobject] -or $citation.path -isnot [string] -or
            [string]::IsNullOrWhiteSpace($citation.path) -or
            $citation.lines -cnotmatch '^[1-9][0-9]*(?:-[1-9][0-9]*)?$') {
            throw "Claim $($claim.id) requires one file/line citation object."
        }
        if ($citation.path -match '^[\\/]|^[A-Za-z]:|(^|[\\/])\.\.([\\/]|$)') {
            throw "Claim $($claim.id) citation must be relative to the upstream checkout."
        }
        $range = $citation.lines -split '-'
        if ($range.Count -eq 2 -and [int]$range[1] -lt [int]$range[0]) {
            throw "Claim $($claim.id) has a reversed citation line range."
        }
        $ids[$claim.id] = $true
        $claim
    }
}

function Assert-CompatibilitySourceCitations {
    param(
        [Parameter(Mandatory)] [string] $Upstream,
        [string] $Catalog = (Join-Path $PSScriptRoot '..\catalog.json')
    )
    foreach ($claim in @(Get-CompatibilityClaims $Catalog)) {
        $citation = $claim.citation
        $source = Get-Content -LiteralPath (Join-Path $Upstream $citation.path)
        $range = $citation.lines -split '-'
        $first = [int]$range[0]
        $last = if ($range.Count -eq 2) { [int]$range[1] } else { $first }
        if ($last -gt $source.Count) { throw "Citation for $($claim.id) extends past the upstream file." }
        $excerpt = $source[($first - 1)..($last - 1)] -join "`n"
        if (!$excerpt.Contains($claim.claim, [StringComparison]::Ordinal)) {
            throw "Claim $($claim.id) is not a verbatim excerpt at $($citation.path):$($citation.lines)."
        }
    }
}
