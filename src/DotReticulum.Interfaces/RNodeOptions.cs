using System.Text;

namespace DotReticulum.Interfaces;

public record RNodeRadioOptions
{
    public long Frequency { get; init; }
    public int Bandwidth { get; init; }
    public int TxPower { get; init; }
    public int SpreadingFactor { get; init; }
    public int CodingRate { get; init; }
    public bool FlowControl { get; init; }
    public double? AirtimeLimitShort { get; init; }
    public double? AirtimeLimitLong { get; init; }

    internal void Validate(bool multi)
    {
        if (Frequency < 137_000_000 || Frequency > 3_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(Frequency));
        if (Bandwidth is < 7800 or > 1625000) throw new ArgumentOutOfRangeException(nameof(Bandwidth));
        if (TxPower < (multi ? -9 : 0) || TxPower > 37) throw new ArgumentOutOfRangeException(nameof(TxPower));
        if (SpreadingFactor is < 5 or > 12) throw new ArgumentOutOfRangeException(nameof(SpreadingFactor));
        if (CodingRate is < 5 or > 8) throw new ArgumentOutOfRangeException(nameof(CodingRate));
        foreach (var value in new[] { AirtimeLimitShort, AirtimeLimitLong })
            if (value is { } v && (!double.IsFinite(v) || v < 0 || v > 100))
                throw new ArgumentOutOfRangeException(nameof(AirtimeLimitShort), "Airtime limits must be finite percentages.");
    }
}

public sealed record RNodeSubinterfaceOptions : RNodeRadioOptions
{
    public string Name { get; init; } = "";
    public int VirtualPort { get; init; }
    public bool Enabled { get; init; }
    public bool Outgoing { get; init; } = true;
}

public abstract record RNodeDeviceOptions
{
    public long? ConfiguredBitrate { get; init; }
    public TimeSpan? IdInterval { get; init; }
    public string? IdCallsign { get; init; }
    public int SendQueueCapacity { get; init; } = 256;
    public int ReceiveQueueCapacity { get; init; } = 256;
    public bool Reconnect { get; init; } = true;
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan DeviceSettleDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan PartialFrameTimeout { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        if (SendQueueCapacity < 1 || ReceiveQueueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(SendQueueCapacity));
        if (ReconnectDelay <= TimeSpan.Zero || DeviceSettleDelay < TimeSpan.Zero ||
            ResponseTimeout <= TimeSpan.Zero || PartialFrameTimeout <= TimeSpan.Zero ||
            ShutdownTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ResponseTimeout));
        ArgumentNullException.ThrowIfNull(TimeProvider);
        if (ConfiguredBitrate is < 5) throw new ArgumentOutOfRangeException(nameof(ConfiguredBitrate));
        if (IdInterval is { } interval && interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(IdInterval));
        if (IdCallsign is { } callsign && Encoding.UTF8.GetByteCount(callsign) > 32)
            throw new ArgumentOutOfRangeException(nameof(IdCallsign));
    }
}

public sealed record RNodeOptions : RNodeDeviceOptions
{
    public RNodeRadioOptions Radio { get; init; } = new();
    public RNodeTcpOptions Tcp { get; init; } = new();

    internal void ValidateSingle()
    {
        Validate();
        ArgumentNullException.ThrowIfNull(Radio);
        Radio.Validate(multi: false);
        ArgumentNullException.ThrowIfNull(Tcp);
        Tcp.Validate();
    }

    public sealed record RNodeTcpOptions
    {
        public int Port { get; init; } = 7633;
        public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
        public TimeSpan DetectionTimeout { get; init; } = TimeSpan.FromSeconds(5);
        public TimeSpan PartialFrameTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);
        public TimeSpan ActivityKeepalive { get; init; } = TimeSpan.FromMilliseconds(3500);

        internal void Validate()
        {
            if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
            if (ConnectTimeout <= TimeSpan.Zero || DetectionTimeout <= TimeSpan.Zero ||
                PartialFrameTimeout <= TimeSpan.Zero || ActivityKeepalive <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        }
    }
}

public sealed record RNodeMultiOptions : RNodeDeviceOptions
{
    /// <summary>Upstream's parent "enabled" setting also enables every nested child.</summary>
    public bool EnableAllChildren { get; init; }
    public IReadOnlyList<RNodeSubinterfaceOptions> Subinterfaces { get; init; } = [];

    internal RNodeSubinterfaceOptions[] ValidateMulti()
    {
        Validate();
        ArgumentNullException.ThrowIfNull(Subinterfaces);
        var children = Subinterfaces.Where(c => c.Enabled || EnableAllChildren).ToArray();
        if (children.Length is < 1 or > 11) throw new ArgumentException("Configure between one and eleven enabled radios.");
        if (children.Select(c => c.VirtualPort).Distinct().Count() != children.Length)
            throw new ArgumentException("Enabled virtual ports must be unique.");
        foreach (var child in children)
        {
            child.Validate(multi: true);
            ArgumentException.ThrowIfNullOrWhiteSpace(child.Name);
            if (child.VirtualPort is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(child.VirtualPort));
        }
        return children;
    }
}
