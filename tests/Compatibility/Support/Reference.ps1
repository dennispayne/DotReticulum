function Get-CompatibilityReferenceRevision {
    $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\Interop\reference_environment.py') -Raw
    $match = [regex]::Match($source, '(?m)^PINNED_RNS_SHA\s*=\s*"([a-f0-9]{40})"')
    if (!$match.Success) { throw 'The canonical Python reference pin was not found.' }
    $match.Groups[1].Value
}
