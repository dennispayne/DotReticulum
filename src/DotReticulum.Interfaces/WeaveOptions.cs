namespace DotReticulum.Interfaces;

public sealed record WeaveOptions
{
    /// <summary>Effective estimate after generic bitrate overrides the type-specific configured_bitrate. Not serial baud.</summary>
    public long ConfiguredBitrate { get; init; } = 250_000;
    public bool Reconnect { get; init; } = true;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PeeringTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan PeerJobInterval { get; init; } = TimeSpan.FromSeconds(22);
    public int MaximumPeers { get; init; } = 256;
    public int ReceiveQueueCapacity { get; init; } = 256;
    public int MaximumQueuedTransmitFrames { get; init; } = 256;
    public int MaximumQueuedTransmitBytes { get; init; } = 256 * WeaveProtocol.MaximumPacketLength;
    public int MaximumDisplayBytes { get; init; } = 32768;

    internal WeaveOptions Validate()
    {
        if (ConfiguredBitrate < 1 || HandshakeTimeout <= TimeSpan.Zero || ReconnectDelay <= TimeSpan.Zero ||
            PeeringTimeout <= TimeSpan.Zero || PeerJobInterval <= TimeSpan.Zero || MaximumPeers < 1 ||
            ReceiveQueueCapacity < 1 || MaximumQueuedTransmitFrames < 1 ||
            MaximumQueuedTransmitBytes < WeaveProtocol.MaximumPacketLength || MaximumDisplayBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(WeaveOptions));
        return this;
    }
}

public sealed record WeaveLogFrame(uint TimestampMilliseconds, byte Level, ushort Event, ReadOnlyMemory<byte> Data);
