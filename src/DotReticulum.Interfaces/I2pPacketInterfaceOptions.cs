namespace DotReticulum.Interfaces;

/// <summary>Native SAM 3.1 stream bearer settings. IFAC and routing policy belong to the interface manager.</summary>
public sealed record I2pPacketInterfaceOptions
{
    public required string Name { get; init; }
    public required string StoragePath { get; init; }
    public ReadOnlyMemory<byte> TransportIdentityHash { get; init; }
    public bool Connectable { get; init; }
    /// <summary>Generic Reticulum bitrate override; values below five are ignored by the reference.</summary>
    public long? ConfiguredBitrate { get; init; }
    public IReadOnlyList<string> Peers { get; init; } = [];
    /// <summary>Null selects I2P_SAM_ADDRESS, otherwise 127.0.0.1:7656.</summary>
    public string? SamHost { get; init; }
    public int SamPort { get; init; } = 7656;
    public int MaxIncomingPeers { get; init; } = 64;
    public int MaxOutgoingPeers { get; init; } = 64;
    public int ReceiveQueueCapacity { get; init; } = 256;
    public int? MaxReconnectAttempts { get; init; }
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan SetupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(110);

    internal I2pPacketInterfaceOptions Snapshot()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(StoragePath);
        if (Connectable && TransportIdentityHash.Length != 16)
            throw new ArgumentException("A connectable I2P interface requires the 16-byte transport identity hash.");
        ArgumentOutOfRangeException.ThrowIfLessThan(SamPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SamPort, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxIncomingPeers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxIncomingPeers, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxOutgoingPeers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxOutgoingPeers, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReceiveQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ReceiveQueueCapacity, 65536);
        ArgumentNullException.ThrowIfNull(Peers);
        if (Peers.Count > MaxOutgoingPeers) throw new ArgumentException("Too many configured I2P peers.");
        foreach (var peer in Peers) I2pSamConnection.ValidateToken(peer);
        if (MaxReconnectAttempts is < 0) throw new ArgumentOutOfRangeException(nameof(MaxReconnectAttempts));
        if (ReconnectDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ReconnectDelay));
        if (SetupTimeout <= TimeSpan.Zero || SetupTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(SetupTimeout));
        if (KeepAliveInterval <= TimeSpan.Zero || StaleAfter <= TimeSpan.Zero || ReadTimeout <= StaleAfter)
            throw new ArgumentOutOfRangeException(nameof(ReadTimeout));
        var host = SamHost;
        var port = SamPort;
        if (host is null && Environment.GetEnvironmentVariable("I2P_SAM_ADDRESS") is { Length: > 0 } address)
        {
            var separator = address.LastIndexOf(':');
            if (separator < 1 || !int.TryParse(address.AsSpan(separator + 1), out port) || port is < 1 or > 65535)
                throw new ArgumentException("I2P_SAM_ADDRESS must contain host:port.");
            host = address[..separator].Trim('[', ']');
        }
        host ??= "127.0.0.1";
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return this with { SamHost = host, SamPort = port, Peers = Peers.ToArray(), TransportIdentityHash = TransportIdentityHash.ToArray() };
    }
}
