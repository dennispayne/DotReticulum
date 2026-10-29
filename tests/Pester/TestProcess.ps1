function Start-TestProcess {
    param([string] $Executable, [string[]] $Arguments, [string] $WorkingDirectory, [hashtable] $Environment = @{},
        [string] $RawArguments)
    $info = [System.Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $true
    foreach ($key in 'GITHUB_STEP_SUMMARY', 'GITHUB_ENV', 'GITHUB_OUTPUT', 'GITHUB_PATH', 'GITHUB_STATE') {
        $null = $info.Environment.Remove($key)
    }
    if ($WorkingDirectory) { $info.WorkingDirectory = $WorkingDirectory }
    if ($RawArguments) {
        if ($Arguments) { throw 'Specify Arguments or RawArguments, not both.' }
        # cmd.exe /s /c requires a raw command line, not C runtime argv quote escaping.
        $info.Arguments = $RawArguments
    }
    else { foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) } }
    foreach ($key in $Environment.Keys) {
        if ($null -eq $Environment[$key]) { $null = $info.Environment.Remove($key) }
        else { $info.Environment[$key] = $Environment[$key] }
    }
    $process = [System.Diagnostics.Process]::Start($info)
    return @{
        Process = $process
        Output = $process.StandardOutput.ReadToEndAsync()
        Error = $process.StandardError.ReadToEndAsync()
    }
}

function Stop-TestProcess {
    param([hashtable] $Peer)
    if (!$Peer.Process.HasExited) {
        $Peer.Process.Kill($true)
        $Peer.Process.WaitForExit()
    }
    $Peer.Process.Dispose()
}

function Complete-TestProcess {
    param([hashtable] $Peer, [int] $TimeoutSeconds = 60)
    try {
        if (!$Peer.Process.WaitForExit($TimeoutSeconds * 1000)) {
            $Peer.Process.Kill($true)
            $Peer.Process.WaitForExit()
            throw "Process timed out after ${TimeoutSeconds}s.`n$($Peer.Output.GetAwaiter().GetResult())`n$($Peer.Error.GetAwaiter().GetResult())"
        }
        return @{
            Code = $Peer.Process.ExitCode
            Output = $Peer.Output.GetAwaiter().GetResult()
            Error = $Peer.Error.GetAwaiter().GetResult()
        }
    }
    finally { Stop-TestProcess $Peer }
}

function Invoke-TestProcess {
    param([string] $Executable, [string[]] $Arguments, [int] $TimeoutSeconds = 60,
        [string] $WorkingDirectory, [hashtable] $Environment = @{}, [string] $InputText = '',
        [string] $RawArguments)
    $peer = Start-TestProcess $Executable $Arguments $WorkingDirectory $Environment -RawArguments $RawArguments
    if ($InputText) { $peer.Process.StandardInput.Write($InputText) }
    $peer.Process.StandardInput.Close()
    Complete-TestProcess $peer $TimeoutSeconds
}

function Install-TestGitHelper {
    param([string] $Application, [string] $Directory)
    $suffix = if ($IsWindows) { '.exe' } else { '' }
    $helper = Join-Path $Directory "git-remote-rns$suffix"
    $source = Split-Path $Application
    $name = [IO.Path]::GetFileNameWithoutExtension($Application)
    if ([IO.Path]::GetExtension($Application) -eq '.dll' -or (Test-Path -LiteralPath (Join-Path $source "$name.runtimeconfig.json"))) {
        Copy-Item -LiteralPath (Join-Path $source "$name$suffix") -Destination $helper
        foreach ($extension in '.dll', '.deps.json', '.runtimeconfig.json') {
            Copy-Item -LiteralPath (Join-Path $source "$name$extension") -Destination $Directory
        }
        foreach ($item in Get-ChildItem -LiteralPath $source -File) {
            if ($item.Name -eq "$name.dll" -or $item.Extension -notin @('.dll', '.so', '.dylib')) { continue }
            Copy-Item -LiteralPath $item.FullName -Destination $Directory
        }
        $runtimes = Join-Path $source 'runtimes'
        if (Test-Path -LiteralPath $runtimes) { Copy-Item -LiteralPath $runtimes -Destination $Directory -Recurse }
    }
    else { Copy-Item -LiteralPath $Application -Destination $helper }
    return $helper
}
