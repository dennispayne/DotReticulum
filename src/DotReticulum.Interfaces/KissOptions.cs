using System.IO.Ports;
using System.Text;

namespace DotReticulum.Interfaces;

/// <summary>TNC settings and explicitly bounded native worker policy. Construction does not open a device.</summary>
public record KissOptions
{
    public int BaudRate { get; init; } = 9600;
    public long? ConfiguredBitrate { get; init; }
    public int DataBits { get; init; } = 8;
    public Parity Parity { get; init; } = Parity.None;
    public StopBits StopBits { get; init; } = StopBits.One;
    public int PreambleMilliseconds { get; init; } = 350;
    public int TxTailMilliseconds { get; init; } = 20;
    public int Persistence { get; init; } = 64;
    public int SlotTimeMilliseconds { get; init; } = 20;
    public bool FlowControl { get; init; }
    public int SendQueueCapacity { get; init; } = 16;
    public int ReceiveQueueCapacity { get; init; } = 256;
    public bool Reconnect { get; init; } = true;
    public TimeSpan DeviceSettleDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan FlowControlTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PartialFrameTimeout { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan? BeaconInterval { get; init; }
    public string BeaconCallsign { get; init; } = "";
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal virtual void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BaudRate, 1);
        if (ConfiguredBitrate is < 5) throw new ArgumentOutOfRangeException(nameof(ConfiguredBitrate));
        if (DataBits is < 5 or > 8) throw new ArgumentOutOfRangeException(nameof(DataBits));
        if (Parity is not (Parity.None or Parity.Even or Parity.Odd)) throw new ArgumentOutOfRangeException(nameof(Parity));
        if (StopBits is not (StopBits.One or StopBits.Two)) throw new ArgumentOutOfRangeException(nameof(StopBits));
        if (SendQueueCapacity is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(SendQueueCapacity));
        if (ReceiveQueueCapacity is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(ReceiveQueueCapacity));
        if (DeviceSettleDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(DeviceSettleDelay));
        if (ReconnectDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ReconnectDelay));
        if (FlowControlTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(FlowControlTimeout));
        if (PartialFrameTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(PartialFrameTimeout));
        if (MaintenanceInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MaintenanceInterval));
        if (BeaconInterval is { } interval && interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(BeaconInterval));
        ArgumentNullException.ThrowIfNull(BeaconCallsign);
        ArgumentNullException.ThrowIfNull(TimeProvider);
        if (new UTF8Encoding(false, true).GetByteCount(BeaconCallsign) > 564) throw new ArgumentException("Beacon exceeds hardware MTU.", nameof(BeaconCallsign));
    }
}

public sealed record Ax25KissOptions : KissOptions
{
    public required string Callsign { get; init; }
    public required int Ssid { get; init; }

    internal override void Validate()
    {
        base.Validate();
        Ax25KissFraming.ValidateAddress(Callsign, Ssid);
        if (BeaconInterval is not null) throw new ArgumentException("AX.25 KISS has no upstream beacon setting.", nameof(BeaconInterval));
    }
}
