param(
    [ValidateSet('echo', 'exit-immediately', 'emit-hex-and-wait', 'stderr-and-echo')]
    [string] $Mode = 'echo',
    [string] $Hex = '',
    [int] $ExitCode = 23,
    [string] $ExpectedArgument = '',
    [string] $PidFile = ''
)

$ErrorActionPreference = 'Stop'
if ($PidFile -ne '') { [IO.File]::WriteAllText($PidFile, $PID.ToString([Globalization.CultureInfo]::InvariantCulture)) }
if ($ExpectedArgument -ne '' -and $ExpectedArgument -cne 'a b "c"') { exit 24 }
if ($Mode -eq 'exit-immediately') { exit $ExitCode }
$inputStream = [Console]::OpenStandardInput()
$outputStream = [Console]::OpenStandardOutput()
if ($Mode -eq 'emit-hex-and-wait') {
    $bytes = [Convert]::FromHexString($Hex)
    $outputStream.Write($bytes, 0, $bytes.Length)
    $outputStream.Flush()
    $null = [Threading.Tasks.Task]::Delay(20000).GetAwaiter().GetResult()
    exit 0
}
if ($Mode -eq 'stderr-and-echo') { [Console]::Error.WriteLine('owned Pipe fixture diagnostic') }
$buffer = [byte[]]::new(4096)
$deadline = [DateTime]::UtcNow.AddSeconds(20)
while ([DateTime]::UtcNow -lt $deadline) {
    $remaining = $deadline - [DateTime]::UtcNow
    try {
        $read = $inputStream.ReadAsync($buffer, 0, $buffer.Length)
        $count = $read.WaitAsync($remaining).GetAwaiter().GetResult()
    }
    catch [TimeoutException] { exit 124 }
    if ($count -eq 0) { break }
    $outputStream.Write($buffer, 0, $count)
    $outputStream.Flush()
}
