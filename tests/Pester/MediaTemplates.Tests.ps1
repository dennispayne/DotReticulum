param(
    [string] $Fixture = (Join-Path $PSScriptRoot ('..\Fixtures\RngitPresentationBackend\bin\Release\net10.0\RngitPresentationBackend' + $(if ($IsWindows) { '.exe' } else { '' }))),
    [string] $NativeEncoder = $env:DOTRETICULUM_MEDIA_BACKEND
)

# Separate owned-process acceptance of public helpers. This is NOT a claim of
# shipped rngit CLI/page routing parity or real image-encoder interoperability.
# First build tests\Fixtures\RngitPresentationBackend\RngitPresentationBackend.csproj.
BeforeAll {
    $root = Join-Path $PSScriptRoot "..\..\TestResults\presentation-pester-$([guid]::NewGuid().ToString('N'))"
    $null = New-Item -ItemType Directory -Path $root
    $root = (Resolve-Path -LiteralPath $root).Path
    $sourceFixture = (Resolve-Path -LiteralPath $Fixture).Path
    Copy-Item -LiteralPath (Split-Path $sourceFixture) -Destination (Join-Path $root 'backend') -Recurse
    $backend = Join-Path $root ('backend\' + (Split-Path $sourceFixture -Leaf))
    $editorExecutablePath = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $editorScriptPath = Join-Path $root 'editor.ps1'
    [IO.File]::WriteAllText($editorScriptPath, @'
param([string] $Mode, [string] $Path)
if ($Mode -eq 'success') { [IO.File]::WriteAllText($Path, "edited π`n", [Text.UTF8Encoding]::new($false)); exit 0 }
if ($Mode -eq 'failure') { exit 9 }
Start-Sleep -Seconds 60
'@, [Text.UTF8Encoding]::new($false))
    if (!(Test-Path -LiteralPath $editorExecutablePath -PathType Leaf) -or !(Test-Path -LiteralPath $editorScriptPath -PathType Leaf)) {
        throw "Owned editor fixture paths are missing: executable='$editorExecutablePath', script='$editorScriptPath'."
    }
    $reference = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\DotReticulum.Applications.Tests\Fixtures\rngit-media-templates.json') -Raw | ConvertFrom-Json
    $webp = [Convert]::FromBase64String($reference.webp_headers[0].data)
    [IO.File]::WriteAllText((Join-Path $root 'backend\rngit-backend-output'), [Convert]::ToBase64String($webp))

    function Invoke-PresentationProcess {
        param([string[]] $Arguments)
        $info = [Diagnostics.ProcessStartInfo]::new($backend)
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.WorkingDirectory = $root
        foreach ($arg in $Arguments) { $info.ArgumentList.Add($arg) }
        $process = [Diagnostics.Process]::Start($info)
        try {
            $process.StandardInput.Close()
            $out = $process.StandardOutput.ReadToEndAsync()
            $err = $process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit(20000)) { $process.Kill($true); throw 'Owned presentation harness exceeded 20 seconds.' }
            @{ Code = $process.ExitCode; Output = $out.GetAwaiter().GetResult(); Error = $err.GetAwaiter().GetResult() }
        }
        finally { $process.Dispose() }
    }
}

AfterAll {
    if ($root -and (Test-Path -LiteralPath $root)) { Remove-Item -LiteralPath $root -Recurse -Force }
}

Describe 'Bounded presentation helper subprocesses' {
    It 'renders UTF-8 static .mu substitutions and preserves unknown tokens' {
        [IO.File]::WriteAllText((Join-Path $root 'static.mu'), "π {PAGE_CONTENT} {UNKNOWN}`n", [Text.UTF8Encoding]::new($false))
        $result = Invoke-PresentationProcess @('--helper-render', $root, 'static', '♥ content')
        $result.Code | Should -Be 0
        $result.Output | Should -Be 'π ♥ content {UNKNOWN}'
        $result.Error | Should -BeNullOrEmpty
    }

    It 'denies editor execution before creating or changing owned input' {
        $sourceInputPath = Join-Path $root 'denied.txt'
        [IO.File]::WriteAllText($sourceInputPath, 'original')
        $result = Invoke-PresentationProcess @('--helper-editor', $root, $sourceInputPath, $editorExecutablePath, $editorScriptPath, 'success', 'false', '10000')
        $result.Code | Should -Be 2 -Because "stderr='$($result.Error)', stdout='$($result.Output)'"
        $result.Error | Should -Match 'Editor denied by policy\.' -Because "stderr='$($result.Error)'"
        [IO.File]::ReadAllText($sourceInputPath) | Should -Be 'original'
        Test-Path -LiteralPath ($sourceInputPath + '.edited') | Should -BeFalse
        @(Get-ChildItem -LiteralPath $root -Filter '.rngit-edit-*').Count | Should -Be 0
    }

    It 'executes explicitly authorized editor and persists its UTF-8 output with cleanup' {
        $sourceInputPath = Join-Path $root 'authorized.txt'
        [IO.File]::WriteAllText($sourceInputPath, 'original')
        $result = Invoke-PresentationProcess @('--helper-editor', $root, $sourceInputPath, $editorExecutablePath, $editorScriptPath, 'success', 'true', '10000')
        $result.Code | Should -Be 0 -Because "stderr='$($result.Error)', stdout='$($result.Output)'"
        $result.Error | Should -BeNullOrEmpty -Because "successful authorized editing must not report stderr='$($result.Error)'"
        $result.Output | Should -Be 'edited'
        [IO.File]::ReadAllText($sourceInputPath + '.edited') | Should -Be "edited π`n"
        [IO.File]::ReadAllText($sourceInputPath) | Should -Be 'original'
        @(Get-ChildItem -LiteralPath $root -Filter '.rngit-edit-*').Count | Should -Be 0
    }

    It 'reports editor failure and timeout without false success or leftover scratch files' {
        foreach ($mode in @('failure', 'timeout')) {
            $sourceInputPath = Join-Path $root ($mode + '.txt')
            [IO.File]::WriteAllText($sourceInputPath, 'original')
            $timeout = if ($mode -eq 'timeout') { '500' } else { '10000' }
            $result = Invoke-PresentationProcess @('--helper-editor', $root, $sourceInputPath, $editorExecutablePath, $editorScriptPath, $mode, 'true', $timeout)
            $result.Code | Should -Be 2 -Because "mode='$mode', stderr='$($result.Error)', stdout='$($result.Output)'"
            $expectedError = if ($mode -eq 'timeout') { '(?s)Editor failed \(-2\):.*Process timed out after 0\.5 seconds\.' } else { 'Editor failed \(9\):' }
            $result.Error | Should -Match $expectedError -Because "mode='$mode' must exercise its exact failure path; stderr='$($result.Error)'"
            [IO.File]::ReadAllText($sourceInputPath) | Should -Be 'original'
            Test-Path -LiteralPath ($sourceInputPath + '.edited') | Should -BeFalse
            @(Get-ChildItem -LiteralPath $root -Filter '.rngit-edit-*').Count | Should -Be 0
        }
    }

    It 'preserves WebP bypass bytes and distinguishes converted output from fallback failure' {
        $existing = Join-Path $root 'existing.webp'
        [IO.File]::WriteAllBytes($existing, $webp)
        $bypass = Invoke-PresentationProcess @('--helper-media', $existing, $backend, '10000', '4096')
        $bypass.Code | Should -Be 0
        $bypass.Output | Should -Be 'Bypassed'
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($existing)) | Should -Be ([Convert]::ToBase64String($webp))
        $source = Join-Path $root 'image.png'
        [IO.File]::WriteAllBytes($source, [byte[]] @(1, 2, 3))
        [IO.File]::WriteAllText((Join-Path $root 'backend\rngit-backend-mode'), 'success')
        $result = Invoke-PresentationProcess @('--helper-media', $source, $backend, '10000', '4096')
        $result.Code | Should -Be 0
        $result.Output | Should -Be 'Converted'
        [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root 'image.webp'))) | Should -Be ([Convert]::ToBase64String($webp))
        Remove-Item -LiteralPath (Join-Path $root 'image.webp')
        [IO.File]::WriteAllText((Join-Path $root 'backend\rngit-backend-mode'), 'fail')
        $failed = Invoke-PresentationProcess @('--helper-media', $source, $backend, '10000', '4096')
        $failed.Code | Should -Be 2
        $failed.Output | Should -Be 'Failed'
        $failed.Error | Should -Match 'owned encoder failure'
        Test-Path -LiteralPath (Join-Path $root 'image.webp') | Should -BeFalse
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($source)) | Should -Be 'AQID'
    }

    It 'bounds supported-backend timeout and excessive output without saving invalid media' {
        $source = Join-Path $root 'bounded.png'
        [IO.File]::WriteAllBytes($source, [byte[]] @(1, 2, 3))
        foreach ($mode in @('timeout', 'overflow')) {
            [IO.File]::WriteAllText((Join-Path $root 'backend\rngit-backend-mode'), $mode)
            $result = Invoke-PresentationProcess @('--helper-media', $source, $backend, '500', '512')
            $result.Code | Should -Be 2
            $result.Output | Should -Be 'Failed'
            $result.Error | Should -Match 'timed out|byte combined stdout/stderr limit'
            Test-Path -LiteralPath (Join-Path $root 'bounded.webp') | Should -BeFalse
        }
    }

    It 'executes actual pinned FFmpeg and persists the exact upstream WebP bytes' -Skip:([string]::IsNullOrWhiteSpace($NativeEncoder)) {
            $nativePath = (Resolve-Path -LiteralPath $NativeEncoder).Path
            (Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant() | Should -Be $reference.native.encoder_sha256
            $source = Join-Path $root 'native.png'
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\..\assets\branding\dotreticulum.png') -Destination $source
            $result = Invoke-PresentationProcess @('--helper-native-media', $source, $nativePath)
            $result.Code | Should -Be 0
            $result.Output | Should -Be 'Converted'
            $outputPath = Join-Path $root 'native.webp'
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($outputPath)) | Should -Be $reference.native.cases[0].output
            Remove-Item -LiteralPath $outputPath
            [IO.File]::WriteAllText($source, 'not an image')
            $failure = Invoke-PresentationProcess @('--helper-native-media', $source, $nativePath)
            $failure.Code | Should -Be 2
            $failure.Output | Should -Be 'Failed'
            $failure.Error | Should -Match 'failed'
            Test-Path -LiteralPath $outputPath | Should -BeFalse
    }
}
