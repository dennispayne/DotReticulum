using System.Runtime.CompilerServices;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>Single-radio RNode. Opening and all device writes are deferred until explicit start.</summary>
public sealed class RNodePacketInterface : IReconnectingPacketInterface, IPacketInterfaceDiagnostics, IPacketInterfacePhysicalLayer
{
    private readonly RNodeDevice _device;
    private RNodeSubinterface Radio => _device.Radios[0];

    public RNodePacketInterface(Stream duplexStream, RNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(duplexStream);
        ArgumentNullException.ThrowIfNull(options);
        _device = new(_ => ValueTask.FromResult(new RNodeDevice.Connection(duplexStream)), options, false, false, duplexStream);
    }

    /// <summary>Every returned stream transfers ownership. Reconnection uses a fresh stream from the factory.</summary>
    public RNodePacketInterface(Func<CancellationToken, ValueTask<Stream>> openStream, RNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _device = new(RNodeDevice.StreamFactory(openStream), options, false, true);
    }

    public RNodePacketInterface(string portName, RNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _device = new(RNodeTcpConnection.Factory(portName, options), options, false, true);
    }

    public long Bitrate => Radio.Bitrate;
    public int HardwareMtu => RNodeProtocol.HardwareMtu;
    public bool IsConnected => Radio.IsConnected;
    public event Action? Connected { add => _device.Connected += value; remove => _device.Connected -= value; }
    public event Action? Disconnected { add => _device.Disconnected += value; remove => _device.Disconnected -= value; }
    public event Action<Exception>? Faulted { add => _device.Faulted += value; remove => _device.Faulted -= value; }
    public ValueTask StartAsync(CancellationToken cancellationToken) => _device.StartAsync(cancellationToken);
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken) => Radio.ReceiveAsync(cancellationToken);
    public IAsyncEnumerable<ReceivedFrame> ReceiveFramesAsync(CancellationToken cancellationToken) => Radio.ReceiveFramesAsync(cancellationToken);
    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => Radio.SendAsync(packet, cancellationToken);
    public ValueTask DisposeAsync() => _device.DisposeAsync();
    public ValueTask ConfigureRadioAsync(RNodeRadioOptions options, CancellationToken cancellationToken = default) =>
        Radio.ConfigureRadioAsync(options, cancellationToken);
    public ValueTask SetRadioStateAsync(bool enabled, CancellationToken cancellationToken = default) =>
        Radio.SetRadioStateAsync(enabled, cancellationToken);
    public ValueTask RefreshDeviceInformationAsync(CancellationToken cancellationToken = default) =>
        _device.RefreshDeviceInformationAsync(cancellationToken);
    public bool DisplayAvailable => _device.DisplayAvailable;
    public ReadOnlyMemory<byte> LastFramebuffer => _device.LastFramebuffer;
    public ReadOnlyMemory<byte> LastDisplay => _device.LastDisplay;
    public byte? BatteryState => Radio.GetDiagnostics().TryGetValue("battery_state", out var state) ? (byte?)state : null;
    public int? BatteryPercent => Radio.GetDiagnostics().TryGetValue("battery_percent", out var percent) ? (int?)percent : null;
    public string BatteryStateDescription => BatteryState switch { 1 => "discharging", 2 => "charging", 3 => "charged", _ => "unknown" };
    public ValueTask EnableExternalFramebufferAsync(CancellationToken cancellationToken = default) =>
        _device.ExternalFramebufferAsync(true, cancellationToken);
    public ValueTask DisableExternalFramebufferAsync(CancellationToken cancellationToken = default) =>
        _device.ExternalFramebufferAsync(false, cancellationToken);
    public ValueTask WriteFramebufferLineAsync(int line, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        _device.WriteFramebufferLineAsync(line, data, cancellationToken);
    public ValueTask DisplayImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default) =>
        _device.DisplayImageAsync(image, cancellationToken);
    public ValueTask<ReadOnlyMemory<byte>> ReadFramebufferAsync(CancellationToken cancellationToken = default) =>
        _device.ReadDisplayAsync(true, cancellationToken);
    public ValueTask<ReadOnlyMemory<byte>> ReadDisplayAsync(CancellationToken cancellationToken = default) =>
        _device.ReadDisplayAsync(false, cancellationToken);
    public ValueTask HardResetAsync(CancellationToken cancellationToken = default) => _device.HardResetAsync(cancellationToken);

    /// <summary>Cancellation/disposal replaces upstream's start/stop background display-polling job.</summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadDisplayUpdatesAsync(
        TimeSpan? interval = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var delay = interval ?? TimeSpan.FromSeconds(1);
        if (delay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await ReadDisplayAsync(cancellationToken).ConfigureAwait(false);
            await _device.DisplayDelayAsync(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        var result = new Dictionary<string, object?>(Radio.GetDiagnostics());
        foreach (var (key, value) in _device.GetDiagnostics()) result[key] = value;
        return result;
    }
}
