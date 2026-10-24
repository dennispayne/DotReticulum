using System.Net.Sockets;

namespace DotReticulum.Interfaces;

public sealed record UdpPacketInterfaceOptions
{
    public string? ListenAddress { get; init; }
    public int? ListenPort { get; init; }
    public string? ForwardAddress { get; init; }
    public int? ForwardPort { get; init; }
    public int? Port { get; init; }
    public string? Device { get; init; }
    public AddressFamily AddressFamily { get; init; } = AddressFamily.InterNetwork;
    public long NominalBitrate { get; init; } = 10_000_000;
}
