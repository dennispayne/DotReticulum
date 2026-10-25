using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotReticulum.Applications.Rnsh;

// Polling readiness keeps cancellation independent of an uninterruptible native
// console/PTY read. At most one reader owns each endpoint.
internal sealed class RnshNativeReader(SafeFileHandle handle, bool ownsHandle, bool pty = false) : Stream
{
    private bool _disposed;
    private long _lastRead = Environment.TickCount64;
    public long LastRead => Interlocked.Read(ref _lastRead);
    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        while (true)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = new byte[Math.Min(buffer.Length, 4096)];
            if (OperatingSystem.IsWindows())
            {
                if (!PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is 109 or 232) return 0;
                    if (error == 1)
                    {
                        // Redirected regular files do not implement PeekNamedPipe.
                        if (!ReadFile(handle, bytes, (uint)bytes.Length, out var fileCount, IntPtr.Zero))
                            throw new IOException($"ReadFile failed ({Marshal.GetLastPInvokeError()}).");
                        bytes.AsMemory(0, (int)fileCount).CopyTo(buffer);
                        return (int)fileCount;
                    }
                    throw new IOException($"PeekNamedPipe failed ({error}).");
                }
                if (available > 0)
                {
                    if (!ReadFile(handle, bytes, (uint)Math.Min(bytes.Length, available), out var count, IntPtr.Zero))
                        throw new IOException($"ReadFile failed ({Marshal.GetLastPInvokeError()}).");
                    bytes.AsMemory(0, checked((int)count)).CopyTo(buffer);
                    Interlocked.Exchange(ref _lastRead, Environment.TickCount64);
                    return (int)count;
                }
            }
            else
            {
                var pollFd = new[] { new PollFd { Fd = handle.DangerousGetHandle().ToInt32(), Events = 1 } };
                var ready = poll(pollFd, 1, 0);
                if (ready < 0 && Marshal.GetLastPInvokeError() != 4) throw new IOException("PTY poll failed.");
                if (ready > 0)
                {
                    var count = read(pollFd[0].Fd, bytes, (nuint)bytes.Length);
                    if (count < 0)
                    {
                        var error = Marshal.GetLastPInvokeError();
                        if (pty && error == 5) return 0;
                        if (error is not 4 and not 11) throw new IOException($"Native read failed ({error}).");
                    }
                    else { bytes.AsMemory(0, (int)count).CopyTo(buffer); return (int)count; }
                }
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }
    protected override void Dispose(bool disposing) { _disposed = true; if (ownsHandle) handle.Dispose(); base.Dispose(disposing); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    [StructLayout(LayoutKind.Sequential)] private struct PollFd { public int Fd; public short Events, Returned; }
    [DllImport("libc", SetLastError = true)] private static extern int poll([In, Out] PollFd[] fds, nuint count, int timeout);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, [Out] byte[] data, nuint count);
    [DllImport("kernel32", SetLastError = true)] private static extern bool PeekNamedPipe(SafeFileHandle handle, IntPtr data, uint size, IntPtr read, out uint available, IntPtr remaining);
    [DllImport("kernel32", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle handle, [Out] byte[] data, uint size, out uint read, IntPtr overlapped);
}
