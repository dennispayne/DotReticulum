function Start-TestProcess {
    param([string] $Executable, [string[]] $Arguments)
    $info = [System.Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
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
    param([string] $Executable, [string[]] $Arguments, [int] $TimeoutSeconds = 60)
    Complete-TestProcess (Start-TestProcess $Executable $Arguments) $TimeoutSeconds
}
