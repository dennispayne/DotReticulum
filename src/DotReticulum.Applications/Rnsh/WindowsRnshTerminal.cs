using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DotReticulum.Applications.Rnsh;

// ConPTY does not have independent stderr or POSIX termios. Its UTF-8 terminal
// surface is deliberately distinct from the lossless three-pipe execution path.
internal sealed class WindowsRnshTerminal : IAsyncDisposable
{
    private IntPtr _console;
    private readonly SafeFileHandle _job;
    private readonly Process _process;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Stream _error;
    private readonly Task<int> _exit;
    private readonly Stream? _terminalInput;
    private readonly Stream? _terminalOutput;
    private readonly Task _discardTerminal;
    private bool _closed;
    private WindowsRnshTerminal(IntPtr console, SafeFileHandle job, Process process, Stream input, Stream output, Stream error,
        Stream? terminalInput, Stream? terminalOutput)
    {
        _console = console; _job = job; _process = process; _input = input; _output = output; _error = error;
        _terminalInput = terminalInput; _terminalOutput = terminalOutput;
        _discardTerminal = terminalOutput is not null && !ReferenceEquals(output, terminalOutput) && !ReferenceEquals(error, terminalOutput)
            ? terminalOutput.CopyToAsync(Stream.Null) : Task.CompletedTask;
        _exit = WaitCoreAsync();
    }
    public Stream Input => _input;
    public Stream Output => _output;
    public Stream Error => _error;

    public static WindowsRnshTerminal Start(string[] argv, RnshExecute command, string identity)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var pipes = command.PipeStdin && command.PipeStdout && command.PipeStderr;
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null, errorRead = null, errorWrite = null, job = null;
        SafeFileHandle? terminalRead = null, terminalWrite = null, terminalOutputRead = null, terminalOutputWrite = null;
        var console = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var handles = IntPtr.Zero;
        ProcessInfo process = default;
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
            if (command.PipeStdin)
            {
                if (!CreatePipeInherited(out inputRead, out inputWrite, ref security, 0) ||
                    !SetHandleInformation(inputWrite, 1, 0)) throw ErrorFor("CreatePipe(stdin)");
            }
            if (command.PipeStdout)
            {
                if (!CreatePipeInherited(out outputRead, out outputWrite, ref security, 0) ||
                    !SetHandleInformation(outputRead, 1, 0)) throw ErrorFor("CreatePipe(stdout)");
            }
            if (command.PipeStderr)
            {
                if (!CreatePipeInherited(out errorRead, out errorWrite, ref security, 0) ||
                    !SetHandleInformation(errorRead, 1, 0)) throw ErrorFor("CreatePipe(stderr)");
            }
            if (!pipes)
            {
                if (!CreatePipe(out terminalRead, out terminalWrite, IntPtr.Zero, 0) ||
                    !CreatePipe(out terminalOutputRead, out terminalOutputWrite, IntPtr.Zero, 0)) throw ErrorFor("CreatePipe(terminal)");
                Check(CreatePseudoConsole(Size(command.Window), terminalRead, terminalOutputWrite, 0, out console));
            }
            var inherited = new[] { inputRead, outputWrite, errorWrite }.OfType<SafeFileHandle>().ToArray();
            var attributeCount = (pipes ? 0 : 1) + (inherited.Length == 0 ? 0 : 1);
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref size))
                throw ErrorFor("Pseudoconsole process attributes");
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                Input = inputRead?.DangerousGetHandle() ?? IntPtr.Zero,
                Output = outputWrite?.DangerousGetHandle() ?? IntPtr.Zero,
                Error = errorWrite?.DangerousGetHandle() ?? IntPtr.Zero,
            }, Attributes = attributes };
            if (inherited.Length > 0)
            {
                handles = Marshal.AllocHGlobal(inherited.Length * IntPtr.Size);
                for (var index = 0; index < inherited.Length; index++)
                    Marshal.WriteIntPtr(handles, index * IntPtr.Size, inherited[index].DangerousGetHandle());
                if (!UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(inherited.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                    throw ErrorFor("Pipe inheritance attributes");
            }
            if (!pipes && !UpdateProcThreadAttribute(attributes, 0, 0x20016, console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw ErrorFor("Pseudoconsole attributes");
            var values = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(v => (string)v.Key, v => (string?)v.Value ?? "", StringComparer.OrdinalIgnoreCase);
            values["TERM"] = command.Terminal ?? values.GetValueOrDefault("TERM", "xterm");
            values["RNS_REMOTE_IDENTITY"] = identity;
            environment = Marshal.StringToHGlobalUni(string.Join('\0', values.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
                .Select(v => v.Key + "=" + v.Value)) + "\0\0");
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw ErrorFor("CreateJobObject");
            var limits = new JobExtendedLimits { Basic = new JobBasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimits>())) throw ErrorFor("SetInformationJobObject");
            // Suspend until the process tree belongs to the job, preventing a child escape race.
            if (!CreateProcessW(null, new StringBuilder(string.Join(' ', argv.Select(Quote))), IntPtr.Zero, IntPtr.Zero,
                inherited.Length > 0, 0x80000u | 0x400u | 4u | (pipes ? 0x08000000u : 0u), environment, null, ref startup, out process)) throw ErrorFor("CreateProcess");
            if (!AssignProcessToJobObject(job, process.Process)) throw ErrorFor("AssignProcessToJobObject");
            if (ResumeThread(process.Thread) == uint.MaxValue) throw ErrorFor("ResumeThread");
            var managedProcess = Process.GetProcessById(process.ProcessId);
            inputRead?.Dispose(); inputRead = null; outputWrite?.Dispose(); outputWrite = null;
            errorWrite?.Dispose(); errorWrite = null;
            terminalRead?.Dispose(); terminalRead = null; terminalOutputWrite?.Dispose(); terminalOutputWrite = null;
            Stream? terminalInput = terminalWrite is null ? null : new FileStream(terminalWrite, FileAccess.Write, 4096, false); terminalWrite = null;
            Stream? terminalOutput = terminalOutputRead is null ? null : new RnshNativeReader(terminalOutputRead, true); terminalOutputRead = null;
            Stream inputStream = inputWrite is null ? terminalInput! : new FileStream(inputWrite, FileAccess.Write, 4096, false); inputWrite = null;
            Stream outputStream = outputRead is null ? terminalOutput! : new RnshNativeReader(outputRead, true); outputRead = null;
            Stream errorStream = errorRead is null ? command.PipeStdout ? terminalOutput! : Stream.Null : new RnshNativeReader(errorRead, true); errorRead = null;
            var terminal = new WindowsRnshTerminal(console, job, managedProcess, inputStream, outputStream, errorStream, terminalInput, terminalOutput);
            console = IntPtr.Zero; job = null;
            return terminal;
        }
        catch
        {
            if (process.Process != IntPtr.Zero) TerminateProcess(process.Process, 255);
            throw;
        }
        finally
        {
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (console != IntPtr.Zero) ClosePseudoConsole(console);
            inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose(); errorRead?.Dispose(); errorWrite?.Dispose(); job?.Dispose();
            terminalRead?.Dispose(); terminalWrite?.Dispose(); terminalOutputRead?.Dispose(); terminalOutputWrite?.Dispose();
        }
    }
    internal static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { backslashes++; continue; }
            result.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(c); backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }
    private async Task<int> WaitCoreAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        var result = _process.ExitCode;
        if (_console != IntPtr.Zero)
        {
            // Process exit precedes conhost's asynchronous terminal renderer. Closing
            // HPCON immediately can discard the child's final screen update.
            var started = Environment.TickCount64;
            while (Environment.TickCount64 - started < 1000)
            {
                await Task.Delay(25).ConfigureAwait(false);
                var lastRead = (_terminalOutput as RnshNativeReader)?.LastRead ?? started;
                if (Environment.TickCount64 - started >= 150 && Environment.TickCount64 - lastRead >= 100) break;
            }
        }
        // Closing may emit a final frame: callers must keep the output pump alive.
        await Task.Run(CloseConsole).ConfigureAwait(false);
        return result;
    }
    private void CloseConsole()
    {
        var console = Interlocked.Exchange(ref _console, IntPtr.Zero);
        if (console != IntPtr.Zero) ClosePseudoConsole(console);
    }
    public Task<int> WaitAsync(CancellationToken token) => _exit.WaitAsync(token);
    public void CloseInput() => _input.Dispose();
    public void Resize(RnshWindow window)
    {
        var console = _console;
        if (console != IntPtr.Zero) Check(ResizePseudoConsole(console, Size(window)));
    }
    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        // Job closure is scoped to this session, including descendants.
        _job.Dispose();
        _input.Dispose();
        // Ensure ClosePseudoConsole cannot block on an undrained final frame after cancellation.
        var drain = Task.Run(async () =>
        {
            try { await _output.CopyToAsync(Stream.Null).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
        try { await _exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        finally { _output.Dispose(); _error.Dispose(); _terminalInput?.Dispose(); _terminalOutput?.Dispose(); _process.Dispose(); }
        try { await _discardTerminal.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        try { await drain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { throw new IOException("ConPTY output did not settle during shutdown."); }
    }
    private static Coord Size(RnshWindow window) => new()
    {
        X = checked((short)Math.Clamp(window.Columns ?? 80, 1, short.MaxValue)),
        Y = checked((short)Math.Clamp(window.Rows ?? 24, 1, short.MaxValue)),
    };
    private static IOException ErrorFor(string operation) => new($"{operation}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    private static void Check(int result) { if (result < 0) throw new IOException($"ConPTY HRESULT 0x{result:x8}"); }
    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedBytes; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimits
    {
        public JobBasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
    [DllImport("kernel32", EntryPoint = "CreatePipe", SetLastError = true)] private static extern bool CreatePipeInherited(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32")] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string? application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int information, ref JobExtendedLimits value, uint length);
    [DllImport("kernel32", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);
}
