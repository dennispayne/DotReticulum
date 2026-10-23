namespace DotReticulum.Core;

/// <summary>RNS Interfaces/Interface.py::optimise_mtu bitrate thresholds for autoconfigurable bearers.</summary>
public static class PacketInterfaceCapabilities
{
    public static int MtuForBitrate(long bitrate) => bitrate switch
    {
        >= 1_000_000_000 => 524288,
        >= 750_000_000 => 262144,
        >= 400_000_000 => 131072,
        >= 200_000_000 => 65536,
        >= 100_000_000 => 32768,
        >= 10_000_000 => 16384,
        >= 5_000_000 => 8192,
        >= 2_000_000 => 4096,
        >= 1_000_000 => 2048,
        >= 62_500 => 1024,
        _ => Packet.Mtu,
    };
}
