using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotReticulum.Applications.Rnsh;

// Linux and Darwin libc ABIs. Unsupported platforms are rejected rather than using pipes as a PTY.
internal sealed class UnixRnshTerminal : IAsyncDisposable
{
    private readonly int _pid;
    private readonly FileStream _master;
    private readonly bool _stdinPipe;
    private readonly Task<int> _exit;
    private readonly object _disposeGate = new();
    private Task? _dispose;
    private UnixRnshTerminal(int pid, FileStream master, Stream input, Stream output, Stream error, bool stdinPipe)
    {
        _pid = pid;
        _master = master;
        Input = input; Output = output; Error = error; _stdinPipe = stdinPipe;
        _exit = Task.Run(() =>
        {
            int result, status;
            do { result = waitpid(pid, out status, 0); } while (result < 0 && Marshal.GetLastPInvokeError() == 4);
            if (result < 0) throw ErrorFor("waitpid");
            // Pinned CallbackSubprocess.start.poll returns raw wait status & 0xff.
            return status & 0xff;
        });
    }
    public Stream Input { get; }
    public Stream Output { get; }
    public Stream Error { get; }
    private static FileStream StreamFor(int fd) => new(new SafeFileHandle(fd, true), FileAccess.ReadWrite, 4096, false);

    public static UnixRnshTerminal Start(string[] argv, RnshExecute command, string identity)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new IOException("Native Unix rnsh PTY sessions require Linux or macOS; use redirected streams on this platform.");
        var darwin = OperatingSystem.IsMacOS();
        var master = posix_openpt(2 | (darwin ? DarwinRnshNative.NoControllingTerminal : 0x100));
        if (master < 0) throw ErrorFor("posix_openpt");
        var owned = new HashSet<int> { master };
        var allocations = new List<IntPtr>();
        // Darwin's opaque spawn types are pointers; Linux uses inline structs.
        var actions = Marshal.AllocHGlobal(darwin ? IntPtr.Size : 512);
        var attr = Marshal.AllocHGlobal(darwin ? IntPtr.Size : 512);
        var actionsReady = false;
        var attrReady = false;
        try
        {
            if (grantpt(master) != 0 || unlockpt(master) != 0) throw ErrorFor("grantpt/unlockpt");
            var slave = Marshal.PtrToStringUTF8(ptsname(master)) ?? throw ErrorFor("ptsname");
            Check(posix_spawn_file_actions_init(actions)); actionsReady = true;
            Check(posix_spawnattr_init(attr)); attrReady = true;
            Check(posix_spawnattr_setflags(attr, darwin
                ? (short)(DarwinRnshNative.SpawnNewSession | DarwinRnshNative.SpawnCloseOnExecDefault) : (short)0x80));
            var sources = new int[3];
            var parents = new int[3];
            var pipes = new[] { command.PipeStdin, command.PipeStdout, command.PipeStderr };
            for (var i = 0; i < 3; i++)
            {
                if (pipes[i])
                {
                    var fds = new int[2];
                    if (pipe(fds) != 0) throw ErrorFor("pipe");
                    owned.UnionWith(fds);
                    sources[i] = fds[i == 0 ? 0 : 1];
                    parents[i] = fds[i == 0 ? 1 : 0];
                    Check(posix_spawn_file_actions_adddup2(actions, sources[i], i));
                }
                else
                {
                    // Linux opens after setsid. Darwin needs a second open in the bootstrap below.
                    Check(posix_spawn_file_actions_addopen(actions, i, slave, 2, 0));
                    sources[i] = -1; parents[i] = master;
                }
            }
            foreach (var fd in owned.Where(fd => fd > 2)) Check(posix_spawn_file_actions_addclose(actions, fd));
            var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
            environment["TERM"] = command.Terminal ?? environment.GetValueOrDefault("TERM", "xterm");
            environment["RNS_REMOTE_IDENTITY"] = identity;
            IntPtr Strings(IEnumerable<string> values)
            {
                var strings = values.Select(value =>
                {
                    var ptr = Marshal.StringToCoTaskMemUTF8(value); allocations.Add(ptr); return ptr;
                }).ToArray();
                var array = Marshal.AllocCoTaskMem((strings.Length + 1) * IntPtr.Size);
                allocations.Add(array);
                for (var i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(array, i * IntPtr.Size, strings[i]);
                Marshal.WriteIntPtr(array, strings.Length * IntPtr.Size, IntPtr.Zero);
                return array;
            }
            var spawnArgv = argv;
            if (darwin && pipes.Any(value => !value))
            {
                var terminalFd = Array.FindIndex(pipes, value => !value);
                // XNU kern_exec.c applies file actions before POSIX_SPAWN_SETSID.
                // Reopen one terminal fd after the new session exists: open1() in
                // vfs_syscalls.c then assigns its controlling tty and foreground pgrp.
                // The script is fixed; user command arguments never become shell code.
                spawnArgv = ["/bin/sh", "-c", $"exec {terminalFd}<> \"$1\"; shift; exec -- \"$@\"", "rnsh-pty", slave, .. argv];
            }
            var av = Strings(spawnArgv);
            var ev = Strings(environment.Select(e => e.Key + "=" + e.Value));
            if (!command.PipeStdin || !command.PipeStdout || !command.PipeStderr)
            {
                var size = new ushort[] { checked((ushort)(command.Window.Rows ?? 0)), checked((ushort)(command.Window.Columns ?? 0)),
                    checked((ushort)(command.Window.HorizontalPixels ?? 0)), checked((ushort)(command.Window.VerticalPixels ?? 0)) };
                if (SetWindow(master, size) != 0) throw ErrorFor("TIOCSWINSZ");
            }
            Check(posix_spawnp(out var pid, spawnArgv[0], actions, attr, av, ev));
            foreach (var fd in sources.Where(fd => fd >= 0)) { close(fd); owned.Remove(fd); }
            var masterStream = StreamFor(master); owned.Remove(master);
            Stream Parent(int index)
            {
                if (!pipes[index])
                {
                    var duplicate = dup(master);
                    if (duplicate < 0) throw ErrorFor("dup");
                    return index == 0 ? StreamFor(duplicate) : new RnshNativeReader(new SafeFileHandle(duplicate, true), true, true);
                }
                var fd = parents[index]; owned.Remove(fd);
                return index == 0 ? StreamFor(fd) : new RnshNativeReader(new SafeFileHandle(fd, true), true);
            }
            var terminal = new UnixRnshTerminal(pid, masterStream, Parent(0), Parent(1),
                pipes[2] ? Parent(2) : command.PipeStdout ? Parent(2) : Stream.Null, command.PipeStdin);
            return terminal;
        }
        finally
        {
            if (actionsReady) posix_spawn_file_actions_destroy(actions);
            if (attrReady) posix_spawnattr_destroy(attr);
            Marshal.FreeHGlobal(actions); Marshal.FreeHGlobal(attr);
            foreach (var ptr in allocations) Marshal.FreeCoTaskMem(ptr);
            foreach (var fd in owned) close(fd);
        }
    }
    public void Resize(RnshWindow size)
    {
        var window = new ushort[] { checked((ushort)(size.Rows ?? 0)), checked((ushort)(size.Columns ?? 0)),
            checked((ushort)(size.HorizontalPixels ?? 0)), checked((ushort)(size.VerticalPixels ?? 0)) };
        if (SetWindow(_master.SafeFileHandle.DangerousGetHandle().ToInt32(), window) != 0) throw ErrorFor("TIOCSWINSZ");
    }
    public void CloseInput()
    {
        if (_stdinPipe) Input.Close();
        else { Input.WriteByte(4); Input.Flush(); }
    }
    public Task<int> WaitAsync(CancellationToken token) => _exit.WaitAsync(token);
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_dispose ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        // The leader may have exited while descendants still own output pipes.
        kill(-_pid, 15);
        try { await _exit.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { kill(-_pid, 9); await _exit.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        finally
        {
            kill(-_pid, 9);
            Input.Dispose(); Output.Dispose(); Error.Dispose(); _master.Dispose();
        }
    }
    private static IOException ErrorFor(string operation) => new($"{operation}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    private static int SetWindow(int fd, ushort[] size) => OperatingSystem.IsMacOS()
        ? DarwinRnshNative.Window(fd, DarwinRnshNative.SetWindow, size) : ioctl(fd, 0x5414, size);
    private static void Check(int code) { if (code != 0) throw new IOException(new Win32Exception(code).Message); }
    [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
    [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr ptsname(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int dup(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int pipe([Out] int[] fds);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
    [DllImport("libc", SetLastError = true)] private static extern int waitpid(int pid, out int status, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, uint request, ushort[] value);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_init(IntPtr actions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_destroy(IntPtr actions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addclose(IntPtr actions, int fd);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int target);
    [DllImport("libc", CharSet = CharSet.Ansi)] private static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd, string path, int flags, uint mode);
    [DllImport("libc")] private static extern int posix_spawnattr_init(IntPtr attr);
    [DllImport("libc")] private static extern int posix_spawnattr_destroy(IntPtr attr);
    [DllImport("libc")] private static extern int posix_spawnattr_setflags(IntPtr attr, short flags);
    [DllImport("libc", CharSet = CharSet.Ansi)] private static extern int posix_spawnp(out int pid, string file, IntPtr actions, IntPtr attr, IntPtr argv, IntPtr env);
}
