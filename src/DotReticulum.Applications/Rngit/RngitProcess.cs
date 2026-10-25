using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DotReticulum.Applications.Rngit;

public sealed record RngitProcessResult(int Code, byte[] Output, string Error);

/// <summary>Runs an explicitly selected executable, without a command shell.</summary>
public static class RngitProcess
{
    public const int StartFailed = -1;
    public const int TimedOut = -2;
    public const int OutputLimitExceeded = -3;
    public const int InputWriteFailed = -4;

    public static async Task<RngitProcessResult> RunAsync(string file, IReadOnlyList<string> args,
        string? workingDirectory = null, byte[]? stdin = null, TimeSpan? timeout = null,
        int maxOutputBytes = 4 * 1024 * 1024, CancellationToken stop = default,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(args);
        if (maxOutputBytes is < 1 or > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        var duration = timeout ?? TimeSpan.FromSeconds(8);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        stop.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        foreach (var argument in args) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment)
                if (value is null) info.Environment.Remove(key);
                else info.Environment[key] = value;
        using var process = new Process { StartInfo = info };
        WindowsProcessTree? ownedTree = null;
        try
        {
            if (!process.Start()) return new(StartFailed, [], "Could not start executable.");
            if (OperatingSystem.IsWindows()) ownedTree = WindowsProcessTree.Create(process);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception killError) when (killError is InvalidOperationException or Win32Exception) { }
            return new(StartFailed, [], error.Message);
        }
        using var tree = ownedTree;

        using var deadline = new CancellationTokenSource(duration);
        using var exceeded = new CancellationTokenSource();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(stop, deadline.Token, exceeded.Token);
        using var drain = new CancellationTokenSource();
        using var output = new MemoryStream();
        using var errors = new MemoryStream();
        long seen = 0;
        var outputExceeded = 0;
        string? inputFailure = null;
        string? streamFailure = null;

        async Task ReadAsync(Stream source, MemoryStream target)
        {
            var buffer = new byte[8192];
            try
            {
                while (true)
                {
                    var count = await source.ReadAsync(buffer, drain.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    var total = Interlocked.Add(ref seen, count);
                    var retain = (int)Math.Clamp(maxOutputBytes - (total - count), 0, count);
                    target.Write(buffer, 0, retain);
                    if (total > maxOutputBytes)
                    {
                        Interlocked.Exchange(ref outputExceeded, 1);
                        exceeded.Cancel();
                    }
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (!operation.IsCancellationRequested && !drain.IsCancellationRequested) throw;
            }
        }

        async Task WriteAsync()
        {
            try
            {
                if (stdin is not null)
                    await process.StandardInput.BaseStream.WriteAsync(stdin, operation.Token).ConfigureAwait(false);
            }
            catch (IOException error) { inputFailure = $"Process did not accept all stdin bytes: {error.Message}"; }
            finally
            {
                try { process.StandardInput.Close(); }
                catch (IOException error) { inputFailure ??= $"Process did not accept all stdin bytes: {error.Message}"; }
            }
        }

        var tasks = Task.WhenAll(ReadAsync(process.StandardOutput.BaseStream, output),
            ReadAsync(process.StandardError.BaseStream, errors), WriteAsync(), process.WaitForExitAsync(drain.Token));
        var code = 0;
        try
        {
            await tasks.WaitAsync(operation.Token).ConfigureAwait(false);
            code = outputExceeded != 0 ? OutputLimitExceeded : process.ExitCode;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            if (error is IOException) streamFailure = error.Message;
            try
            {
                if (OperatingSystem.IsWindows() && ownedTree is not null) ownedTree.Terminate();
                else process.Kill(entireProcessTree: true);
            }
            catch (Exception killError) when (killError is InvalidOperationException or Win32Exception) { }
            // Descendants can inherit pipe handles. Never await their EOF indefinitely.
            drain.CancelAfter(TimeSpan.FromSeconds(2));
            try { await tasks.ConfigureAwait(false); }
            catch (Exception drainError) when (drainError is OperationCanceledException or IOException) { }
            stop.ThrowIfCancellationRequested();
            code = outputExceeded != 0 ? OutputLimitExceeded : deadline.IsCancellationRequested ? TimedOut : StartFailed;
        }
        var detail = Encoding.UTF8.GetString(errors.ToArray()).Trim();
        if (inputFailure is not null)
        {
            if (code == 0) code = InputWriteFailed;
            detail = Append(detail, inputFailure);
        }
        if (streamFailure is not null) detail = Append(detail, $"Process pipe failure: {streamFailure}");
        if (code == TimedOut) detail = Append(detail, $"Process timed out after {duration.TotalSeconds:g} seconds.");
        if (code == OutputLimitExceeded) detail = Append(detail, $"Process exceeded the {maxOutputBytes} byte combined stdout/stderr limit.");
        return new(code, output.ToArray(), detail);
    }

    private static string Append(string detail, string message) => detail.Length == 0 ? message : detail + "\n" + message;

    // A job retains descendant ownership even after the root exits and children
    // retain inherited pipes. Kill(entireProcessTree) alone cannot do that.
    [SupportedOSPlatform("windows")]
    private sealed class WindowsProcessTree : IDisposable
    {
        private readonly SafeFileHandle job;
        private WindowsProcessTree(SafeFileHandle job) => this.job = job;

        public static WindowsProcessTree Create(Process process)
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } };
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
                    || !AssignProcessToJobObject(job, process.Handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return new(job);
            }
            catch { job.Dispose(); throw; }
        }

        public void Terminate()
        {
            if (!TerminateJobObject(job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => job.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessUserTime;
            public long JobUserTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet;
            public UIntPtr MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    }
}
