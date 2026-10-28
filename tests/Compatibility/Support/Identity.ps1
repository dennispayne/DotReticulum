function New-CompatibilitySignedInput {
    param([hashtable] $Context, [string] $Id, [object] $Fixture)
    $directory = New-CompatibilityCaseDirectory $Context $Id
    $inputPath = Join-Path $directory 'message'
    [IO.File]::WriteAllBytes($inputPath, [Convert]::FromHexString($Fixture.message))
    [IO.File]::WriteAllBytes("$inputPath.rsg", [Convert]::FromHexString($Fixture.rsg))
    $inputPath
}

function Assert-CompatibilityNoPlaintext {
    param([string] $Path)
    # Upstream rnid opens the output before authentication, leaving an empty file on failure.
    if (Test-Path -LiteralPath $Path) {
        (Get-Item -LiteralPath $Path).Length | Should -Be 0 -Because 'unauthenticated plaintext must not be released'
    }
}

function Assert-CompatibilityDecryptRejected {
    param([hashtable] $Context, [string] $Directory, [string] $Name, [byte[]] $Ciphertext, [string] $Key)
    $inputPath = Join-Path $Directory "$Name.rfe"
    $outputPath = Join-Path $Directory "$Name.plaintext"
    [IO.File]::WriteAllBytes($inputPath, $Ciphertext)
    $result = Invoke-CompatibilityTool $Context rnid @('-M', $Key, '-d', $inputPath, '-w', $outputPath)
    $result.Code | Should -Not -Be 0 -Because "$($result.Error) $($result.Output)"
    Assert-CompatibilityNoPlaintext $outputPath
}
