using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>
/// One packet per UDP datagram with independent receive and forwarding endpoints.
/// </summary>
public sealed class UdpPacketInterface : IPacketInterface, IPacketInterfaceDiagnostics
{
    private const int ReceiveBufferSize = 1064 + 64 + 1;

    private IPEndPoint? _local;
    private IPEndPoint? _peer;
    private readonly UdpPacketInterfaceOptions? _options;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _gate = new();
    private Socket? _socket;
    private bool _started;
    private bool _ready;
    private bool _disposed;
    private Task? _disposeTask;
    private int _receiving;
    private long _receivedBytes;
    private long _sentBytes;
    private long _oversizedDatagrams;
    public long Bitrate { get; }
    public int HardwareMtu => 1064;

    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (_gate) return new Dictionary<string, object?>
        {
            ["rxb"] = Interlocked.Read(ref _receivedBytes),
            ["txb"] = Interlocked.Read(ref _sentBytes),
            ["bitrate"] = Bitrate,
            ["hardware_mtu"] = HardwareMtu,
            ["dotreticulum_bitrate_kind"] = "configured_estimate",
            ["listen_address"] = LocalEndpoint?.ToString(),
            ["forward_address"] = _peer?.ToString(),
            ["receives"] = _local is not null,
            ["forwards"] = _peer is not null,
            ["oversized_datagrams"] = Interlocked.Read(ref _oversizedDatagrams),
            ["status"] = _ready && !_disposed,
        };
    }

    public UdpPacketInterface(IPEndPoint localEndpoint, IPEndPoint peerEndpoint, long nominalBitrate = 10_000_000)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        ArgumentNullException.ThrowIfNull(peerEndpoint);
        ArgumentOutOfRangeException.ThrowIfLessThan(nominalBitrate, 1);
        if (localEndpoint.AddressFamily != peerEndpoint.AddressFamily)
            throw new ArgumentException("UDP receive and forwarding endpoints must use the same address family.");
        Bitrate = nominalBitrate;
        _local = new IPEndPoint(localEndpoint.Address, localEndpoint.Port);
        _peer = new IPEndPoint(peerEndpoint.Address, peerEndpoint.Port);
    }

    public UdpPacketInterface(UdpPacketInterfaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.NominalBitrate, 1);
        if (options.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentException("UDP requires an IPv4 or IPv6 address family.", nameof(options));
        foreach (var port in new[] { options.Port, options.ListenPort, options.ForwardPort })
            if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(options), "UDP ports must be between 0 and 65535.");
        _options = options;
        Bitrate = options.NominalBitrate;
    }

    /// <summary>The bound local endpoint (resolves an ephemeral port 0); null before start.</summary>
    public IPEndPoint? LocalEndpoint { get; private set; }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The interface has already been started.");
            }

            _started = true;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        IPEndPoint? local = _local, peer = _peer;
        if (_options is { } options)
        {
            var listen = options.ListenAddress;
            var forward = options.ForwardAddress;
            if (options.Device is not null && (listen is null || forward is null))
            {
                if (options.AddressFamily != AddressFamily.InterNetwork)
                    throw new ArgumentException("UDP device broadcast resolution is IPv4-only.");
                var broadcast = GetDeviceBroadcastAddress(options.Device).ToString();
                listen ??= broadcast;
                forward ??= broadcast;
            }
            var listenPort = options.ListenPort ?? options.Port;
            var forwardPort = options.ForwardPort ?? options.Port;
            if (listen is not null && listenPort is { } lp)
                local = new IPEndPoint(await ResolveAsync(listen, options.AddressFamily, linked.Token).ConfigureAwait(false), lp);
            else if (listen is not null || listenPort is not null)
                System.Diagnostics.Trace.TraceWarning("UDP receive address/port pair is incomplete; reception is disabled.");
            if (forward is not null && forwardPort is { } fp)
                peer = new IPEndPoint(await ResolveAsync(forward, options.AddressFamily, linked.Token).ConfigureAwait(false), fp);
            else if (forward is not null || forwardPort is not null)
                System.Diagnostics.Trace.TraceWarning("UDP forwarding address/port pair is incomplete; forwarding is disabled.");
            if (local is null && peer is null)
                throw new ArgumentException("UDP requires at least one complete receive or forwarding address/port pair.");
        }
        linked.Token.ThrowIfCancellationRequested();
        Socket? socket = null;
        try
        {
            if (local is not null)
            {
                socket = new Socket(local.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(local);
            }
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _local = local;
                _peer = peer;
                LocalEndpoint = socket?.LocalEndPoint as IPEndPoint;
                _socket = socket;
                _ready = true;
            }
        }
        catch
        {
            socket?.Dispose();
            throw;
        }
    }

    public static IPAddress GetDeviceBroadcastAddress(string device)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device);
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.Name.Equals(device, StringComparison.Ordinal) || n.Id.Equals(device, StringComparison.Ordinal));
        if (adapter is null) throw new IOException($"UDP network device '{device}' was not found.");
        var address = adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new IOException($"UDP network device '{device}' has no IPv4 address.");
        var bytes = address.Address.GetAddressBytes();
        var mask = address.IPv4Mask.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++) bytes[i] |= (byte)~mask[i];
        return new IPAddress(bytes);
    }

    private static async ValueTask<IPAddress> ResolveAsync(string host, AddressFamily family, CancellationToken cancellationToken)
    {
        if (host.Length == 0) return family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
        if (IPAddress.TryParse(host, out var literal))
            return literal.AddressFamily == family ? literal
                : throw new ArgumentException($"UDP address '{host}' does not use the configured address family.");
        var addresses = await Dns.GetHostAddressesAsync(host, family, cancellationToken).ConfigureAwait(false);
        return addresses.FirstOrDefault() ?? throw new IOException($"UDP host '{host}' has no address in the configured family.");
    }

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        Socket? socket;
        lock (_gate)
        {
            EnsureReady();
            socket = _socket;
        }
        if (Interlocked.Exchange(ref _receiving, 1) != 0)
        {
            throw new InvalidOperationException("ReceiveAsync supports a single enumerator.");
        }

        return ReceiveCoreAsync(socket, cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveCoreAsync(
        Socket? socket,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        if (socket is null)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
            yield break;
        }
        var buffer = new byte[ReceiveBufferSize];
        EndPoint remote = socket.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            int length;
            try
            {
                var result = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, remote, linked.Token).ConfigureAwait(false);
                if ((result.SocketFlags & SocketFlags.Truncated) != 0)
                {
                    Interlocked.Increment(ref _oversizedDatagrams);
                    continue;
                }
                length = result.ReceivedBytes;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows reports an earlier ICMP port-unreachable (our send had no listener) on the next receive.
                continue;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
            {
                Interlocked.Increment(ref _oversizedDatagrams);
                continue;
            }
            catch (Exception) when (_disposeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (length > HardwareMtu + (length > 0 && (buffer[0] & 0x80) != 0 ? 64 : 0))
            {
                Interlocked.Increment(ref _oversizedDatagrams);
                continue;
            }
            if (length > 0)
            {
                Interlocked.Add(ref _receivedBytes, length);
                yield return buffer.AsMemory(0, length).ToArray();
            }
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IPEndPoint peer;
        lock (_gate)
        {
            EnsureReady();
            peer = _peer ?? throw new InvalidOperationException("UDP forwarding is not configured.");
        }
        if (packet.IsEmpty || packet.Length > HardwareMtu + ((packet.Span[0] & 0x80) != 0 ? 64 : 0))
        {
            throw new ArgumentException("Packet exceeds the bearer frame bound.", nameof(packet));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        await _send.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            using var socket = new Socket(peer.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            if (peer.AddressFamily == AddressFamily.InterNetwork) socket.EnableBroadcast = true;
            var sent = await socket.SendToAsync(packet, SocketFlags.None, peer, linked.Token).ConfigureAwait(false);
            Interlocked.Add(ref _sentBytes, sent);
            if (sent != packet.Length) throw new IOException($"Incomplete UDP datagram send: {sent} of {packet.Length} bytes.");
        }
        finally { _send.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            _disposeTask = DisposeCoreAsync(_socket);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Socket? socket)
    {
        _disposeCts.Cancel();
        socket?.Dispose();
        await _send.WaitAsync().ConfigureAwait(false);
        _send.Release();
    }

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ready) throw new InvalidOperationException("The interface has not been started.");
    }
}
