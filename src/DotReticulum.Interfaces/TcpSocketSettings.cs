using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DotReticulum.Interfaces;

internal static class TcpSocketSettings
{
    public static void Apply(Socket socket, bool i2pTunneled = false)
    {
        socket.NoDelay = true;
        if (OperatingSystem.IsLinux())
        {
            var timeout = i2pTunneled ? 45_000 : 24_000;
            // TCP_USER_TIMEOUT is a Linux option without a portable SocketOptionName.
            if (SetSocketOption(socket.Handle.ToInt32(), 6, 18, ref timeout, sizeof(int)) != 0)
                throw new SocketException(Marshal.GetLastPInvokeError());
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, i2pTunneled ? 10 : 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, i2pTunneled ? 9 : 2);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, i2pTunneled ? 5 : 12);
        }
        else if (OperatingSystem.IsMacOS())
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, i2pTunneled ? 10 : 5);
        }
    }

    [DllImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static extern int SetSocketOption(int socket, int level, int option, ref int value, uint length);
}
