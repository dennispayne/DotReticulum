namespace DotReticulum.Interfaces;

internal sealed record RNodeBleSelector(string? Name, string? Address)
{
    internal static readonly Guid UartService = new("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
    internal static readonly Guid UartReceive = new("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
    internal static readonly Guid UartTransmit = new("6e400003-b5a3-f393-e0a9-e50e24dcca9e");

    internal static RNodeBleSelector Parse(string port)
    {
        if (!port.StartsWith("ble://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected an RNode ble:// selector.", nameof(port));
        var target = port[6..];
        return target.Length == 0 ? new(null, null)
            : target.Length == 17 && target.Split(':').Length == 6 ? new(null, target)
            : new(target, null);
    }

    // RNS 1.5.7's default RNode-name preference falls through to the unrestricted
    // bonded UART match. Do not silently narrow that observable selection rule.
    internal bool Matches(string? name, string? address, bool bonded, bool uart) =>
        bonded && uart && (Address is null || address == Address) && (Name is null || name == Name);
}

internal interface IRNodeBleGatt : IAsyncDisposable
{
    int MaxWritePayload { get; }
    event Action<ReadOnlyMemory<byte>>? Received;
    event Action<Exception>? Disconnected;
    ValueTask StartNotificationsAsync(CancellationToken cancellationToken);
    ValueTask WriteWithoutResponseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

internal static class RNodeBleConnection
{
    internal static Func<CancellationToken, ValueTask<RNodeDevice.Connection>> Factory(string port, RNodeOptions options)
    {
        var selector = RNodeBleSelector.Parse(port);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native RNode BLE currently requires Windows. Linux, macOS and Android BLE providers are not implemented.");
        return async cancellationToken =>
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Native RNode BLE currently requires Windows.");
            using var timeout = new CancellationTokenSource(options.Ble.ConnectTimeout, options.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var gatt = await WindowsRNodeBleGatt.OpenAsync(selector, linked.Token).ConfigureAwait(false);
            var stream = await RNodeBleStream.OpenAsync(gatt, options.Ble.ReceiveBufferBytes, linked.Token).ConfigureAwait(false);
            return new RNodeDevice.Connection(stream, Ble: true);
        };
    }
}
