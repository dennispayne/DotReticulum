. (Join-Path $PSScriptRoot '..\..\Pester\TestProcess.ps1')

function Publish-CompatibilityApplication {
    param([string] $Directory)

    $architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
    $rid = if ($IsWindows -and $architecture -eq 'X64') { 'win-x64' }
        elseif ($IsLinux -and $architecture -eq 'X64') { 'linux-x64' }
        elseif ($IsMacOS -and $architecture -eq 'Arm64') { 'osx-arm64' }
        else { throw 'Automatic preparation supports Windows x64, Linux x64 and macOS ARM64 only; supply an explicit candidate/reference on other platforms.' }
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $arguments = @('publish', (Join-Path $repository 'src\DotReticulum.Applications\DotReticulum.Applications.csproj'),
        '-c', 'Release', '-r', $rid, '-o', $Directory,
        '-p:ILLinkTreatWarningsAsErrors=true', '-p:IlcTreatWarningsAsErrors=true', '--nologo', '-v', 'quiet')
    $executable = $dotnet
    $environment = @{}
    $rawArguments = $null
    if ($IsWindows) {
        $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
        $vswhere = Join-Path $installer 'vswhere.exe'
        if (!(Test-Path -LiteralPath $vswhere)) { throw 'NativeAOT preparation requires Visual Studio Build Tools with the C++ toolchain (vswhere.exe is missing).' }
        $installation = Invoke-TestProcess $vswhere @('-latest', '-products', '*',
            '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-property', 'installationPath')
        if ($installation.Code -ne 0 -or !$installation.Output.Trim()) {
            throw "MSVC NativeAOT tools are unavailable. Install the Visual Studio C++ build tools. $($installation.Error)"
        }
        $setup = Join-Path $installation.Output.Trim() 'VC\Auxiliary\Build\vcvars64.bat'
        if (!(Test-Path -LiteralPath $setup)) { throw "MSVC environment setup is missing: $setup" }
        $command = '"' + $dotnet + '" ' + (($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' ')
        $rawArguments = "/d /s /c `"call `"$setup`" >nul && $command`""
        $arguments = @()
        $executable = $env:ComSpec
        $environment.PATH = "$installer;$env:PATH"
    }
    Write-Host "Publishing the $rid NativeAOT candidate (requires the .NET SDK and native build tools)..."
    $result = Invoke-TestProcess $executable $arguments -RawArguments $rawArguments `
        -WorkingDirectory $repository -Environment $environment -TimeoutSeconds 900
    if ($result.Code -ne 0) { throw "Native candidate publication failed (exit $($result.Code)).`n$($result.Output)`n$($result.Error)" }
    $application = Join-Path $Directory $(if ($IsWindows) { 'DotReticulum.Applications.exe' } else { 'DotReticulum.Applications' })
    if (!(Test-Path -LiteralPath $application -PathType Leaf)) { throw "Native publication produced no executable: $application" }
    $smoke = Invoke-TestProcess $application @('--help')
    if ($smoke.Code -ne 0) { throw "Published candidate failed its help smoke test.`n$($smoke.Output)`n$($smoke.Error)" }
    return $application
}

function Initialize-CompatibilityLocalRun {
    param(
        [string] $Application,
        [string] $Python,
        [string] $OutputDirectory,
        [string] $ReferenceDirectory,
        [string] $Implementation
    )

    if ($Implementation -eq 'Upstream') { throw '-Prepare requires a DotReticulum candidate to retrieve the reference; use Both or DotReticulum.' }
    $null = Get-Command git -CommandType Application -ErrorAction Stop
    if (!$Python) { $Python = if ($IsWindows) { 'python' } else { 'python3' } }
    $Python = (Get-Command $Python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    if (!$OutputDirectory) {
        $OutputDirectory = Join-Path $PSScriptRoot "..\..\..\TestResults\compatibility-local-$([guid]::NewGuid().ToString('N'))"
    }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Comparison directory must be fresh.' }
    if (!$ReferenceDirectory) { $ReferenceDirectory = Join-Path $OutputDirectory 'reference' }
    $ReferenceDirectory = [IO.Path]::GetFullPath($ReferenceDirectory)
    if (Test-Path -LiteralPath $ReferenceDirectory) { throw 'Reference directory must be fresh.' }
    if ($Application) {
        $Application = (Resolve-Path -LiteralPath $Application).Path
        if ([IO.Path]::GetExtension($Application) -eq '.dll') { throw '-Prepare requires a native Application, not a managed DLL.' }
    }
    $null = New-Item -ItemType Directory -Path $OutputDirectory
    Write-Host "Compatibility results: $OutputDirectory"
    Write-Host 'This run retrieves upstream over Reticulum and installs pinned Python dependencies into an owned virtual environment.'
    if (!$Application) { $Application = Publish-CompatibilityApplication (Join-Path $OutputDirectory 'native') }
    return @{
        Application = $Application; Python = $Python
        OutputDirectory = $OutputDirectory; ReferenceDirectory = $ReferenceDirectory
    }
}
