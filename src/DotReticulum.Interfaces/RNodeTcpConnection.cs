using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DotReticulum.Interfaces;

internal static class RNodeTcpConnection
{
    internal static Func<CancellationToken, ValueTask<RNodeDevice.Connection>> Factory(string port, RNodeOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        if (!port.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            return RNodeDevice.SerialFactory(port);
        var host = port[6..];
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return async cancellationToken =>
        {
            var client = new TcpClient();
            using var deadline = new CancellationTokenSource(options.Tcp.ConnectTimeout, options.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                await client.ConnectAsync(host, options.Tcp.Port, linked.Token).ConfigureAwait(false);
                client.NoDelay = true;
                if (OperatingSystem.IsLinux())
                {
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
                    client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);
                    client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 12);
                    var milliseconds = 24000;
                    if (SetLinuxSocketOption(client.Client.SafeHandle, 6, 18, ref milliseconds, sizeof(int)) != 0)
                        throw new SocketException(Marshal.GetLastPInvokeError());
                }
                else if (OperatingSystem.IsMacOS())
                {
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
                }
                return new RNodeDevice.Connection(client.GetStream(), client, Tcp: true);
            }
            catch { client.Dispose(); throw; }
        };
    }

    // Linux IPPROTO_TCP / TCP_USER_TIMEOUT has no portable SocketOptionName mapping.
    [DllImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static extern int SetLinuxSocketOption(SafeSocketHandle socket, int level, int option, ref int value, uint length);
}
