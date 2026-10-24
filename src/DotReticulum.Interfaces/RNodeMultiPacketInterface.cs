using System.Runtime.CompilerServices;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>Owns one RNode device and exposes each configured radio as an independently routed child.</summary>
public sealed class RNodeMultiPacketInterface : ISpawningPacketInterface, IPacketInterfaceDiagnostics
{
    private readonly RNodeDevice _device;
    private int _started;
    public RNodeMultiPacketInterface(Stream duplexStream, RNodeMultiOptions options)
    {
        ArgumentNullException.ThrowIfNull(duplexStream);
        ArgumentNullException.ThrowIfNull(options);
        _device = new(_ => ValueTask.FromResult(new RNodeDevice.Connection(duplexStream)), options, true, false, duplexStream);
    }

    public RNodeMultiPacketInterface(Func<CancellationToken, ValueTask<Stream>> openStream, RNodeMultiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _device = new(RNodeDevice.StreamFactory(openStream), options, true, true);
    }

    public RNodeMultiPacketInterface(string portName, RNodeMultiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _device = new(RNodeDevice.SerialFactory(portName), options, true, true);
    }

    public IReadOnlyList<RNodeSubinterface> Children => _device.Radios;
    public long Bitrate => _device.ParentBitrate;
    public int HardwareMtu => RNodeProtocol.HardwareMtu;
    public bool SendsThroughChildren => true;
    public event Action<IPacketInterface>? ChildConnected;
    public event Action<Exception>? Faulted { add => _device.Faulted += value; remove => _device.Faulted -= value; }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The interface has already been started.");
        await _device.StartAsync(cancellationToken).ConfigureAwait(false);
        if (ChildConnected is not { } observers) return;
        foreach (var child in Children)
            foreach (Action<IPacketInterface> observer in observers.GetInvocationList())
                try { observer(child); }
                catch (Exception exception) { _device.ReportObserver(exception); }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Send on the selected RNode child, not the shared device.");

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _device.StopToken);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_device.StopToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
        yield break;
    }

    public IReadOnlyDictionary<string, object?> GetDiagnostics() => _device.GetDiagnostics();
    public ValueTask DisposeAsync() => _device.DisposeAsync();
    public ValueTask RefreshDeviceInformationAsync(CancellationToken cancellationToken = default) =>
        _device.RefreshDeviceInformationAsync(cancellationToken);
    public bool DisplayAvailable => _device.DisplayAvailable;
    public ValueTask EnableExternalFramebufferAsync(CancellationToken cancellationToken = default) =>
        _device.ExternalFramebufferAsync(true, cancellationToken);
    public ValueTask DisableExternalFramebufferAsync(CancellationToken cancellationToken = default) =>
        _device.ExternalFramebufferAsync(false, cancellationToken);
    public ValueTask WriteFramebufferLineAsync(int line, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        _device.WriteFramebufferLineAsync(line, data, cancellationToken);
    public ValueTask DisplayImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default) =>
        _device.DisplayImageAsync(image, cancellationToken);
    public ValueTask HardResetAsync(CancellationToken cancellationToken = default) => _device.HardResetAsync(cancellationToken);
}
