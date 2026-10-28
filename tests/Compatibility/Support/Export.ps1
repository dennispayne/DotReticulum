function Export-CompatibilityResults {
    param([object] $Result, [string] $Revision, [string] $Path)
    $rows = @(
        foreach ($test in @(Get-CompatibilityTestMetadata $Result.Tests)) {
            [pscustomobject]@{
                Id = $test.Id; ClaimId = $test.ClaimId; Name = $test.Name
                Result = [string] $test.Test.Result; File = $test.File; Line = $test.Line; Revision = $Revision
            }
        }
    )
    ConvertTo-Json -InputObject $rows -Depth 5 | Set-Content -LiteralPath $Path -Encoding utf8
}
