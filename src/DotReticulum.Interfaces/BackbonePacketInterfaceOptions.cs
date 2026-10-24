namespace DotReticulum.Interfaces;

public sealed record BackbonePacketInterfaceOptions
{
    public long NominalBitrate { get; init; } = 100_000_000;
    public int HardwareMtu { get; init; } = 1_048_576;
    public int InterfaceAccessCodeSize { get; init; } = 16;
    public int MaximumClients { get; init; } = 64;
    public int ReceiveQueueFrames { get; init; } = 8;
    public int TransmitQueueFrames { get; init; } = 256;
    public int TransmitQueueBytes { get; init; } = 4 * 1024 * 1024;
    public bool BlockFastFlapping { get; init; } = true;
    public TimeSpan FastFlapThreshold { get; init; } = TimeSpan.FromSeconds(20);
    public int FastFlapGrace { get; init; } = 5;
    public TimeSpan FastFlapBlockTime { get; init; } = TimeSpan.FromHours(12);
    public int MaximumFlapEntries { get; init; } = 4096;
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int? MaximumReconnectAttempts { get; init; }
    public bool PreferIPv6 { get; init; }
    public string? Device { get; init; }

    internal void Validate()
    {
        if (NominalBitrate < 1 || HardwareMtu is < 500 or > 1_048_576 ||
            InterfaceAccessCodeSize is < 0 or > 64 || MaximumClients < 1 ||
            ReceiveQueueFrames < 1 || TransmitQueueFrames < 1 || TransmitQueueBytes < 1 ||
            FastFlapThreshold <= TimeSpan.Zero || FastFlapGrace < 0 || FastFlapBlockTime <= TimeSpan.Zero ||
            MaximumFlapEntries < 1 || ReconnectDelay <= TimeSpan.Zero || ConnectTimeout <= TimeSpan.Zero ||
            MaximumReconnectAttempts is < 0 || (Device is not null && string.IsNullOrWhiteSpace(Device)))
            throw new ArgumentOutOfRangeException(nameof(BackbonePacketInterfaceOptions));
    }

    internal static void RequirePlatform()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
            throw new PlatformNotSupportedException("BackboneInterface is supported only on Linux and Android, matching the pinned reference.");
    }
}
