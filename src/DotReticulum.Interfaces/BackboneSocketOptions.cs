using System.Net.Sockets;

namespace DotReticulum.Interfaces;

internal static class BackboneSocketOptions
{
    internal const int LinuxSocketLevel = 1;
    internal const int LinuxReuseAddress = 2;
    internal const int LinuxReusePort = 15;
    internal const int TcpLevel = 6;
    internal const int LinuxTcpUserTimeout = 18;

    public static void ConfigureListener(Socket socket)
    {
        // .NET's Unix ReuseAddress enables SO_REUSEPORT too; upstream enables only SO_REUSEADDR.
        SetNativeInt(socket, LinuxSocketLevel, LinuxReusePort, 0);
        SetNativeInt(socket, LinuxSocketLevel, LinuxReuseAddress, 1);
    }

    public static void ConfigureUserTimeout(Socket socket) =>
        SetNativeInt(socket, TcpLevel, LinuxTcpUserTimeout, 24000);

    private static void SetNativeInt(Socket socket, int level, int option, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BitConverter.TryWriteBytes(bytes, value);
        socket.SetRawSocketOption(level, option, bytes);
    }

    internal static int GetNativeInt(Socket socket, int level, int option)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        if (socket.GetRawSocketOption(level, option, bytes) != bytes.Length)
            throw new IOException("The Backbone native socket option did not return a complete integer.");
        return BitConverter.ToInt32(bytes);
    }
}
