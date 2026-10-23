using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>The upstream local HDLC bearer (loopback TCP or Linux abstract Unix socket), with reconnect for initiators.</summary>
public sealed class LocalSharedPacketInterface : IPacketInterface, IPacketInterfaceDiagnostics
{
    public const int HardwareMtu = 262144;
    int IPacketInterface.HardwareMtu => HardwareMtu;
    public long Bitrate => 1_000_000_000;
    public bool SupportsMtuDiscovery => true;
    private readonly EndPoint endpoint;
    private readonly TimeSpan reconnectDelay;
    private readonly TimeSpan connectTimeout;
    private readonly Action<Exception>? diagnostic;
    private readonly Action? closed;
    private readonly bool reconnect;
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private Connection? connection;
    private Task? keepalive;
    private Task? disposal;
    private bool started;
    private int receiving;
    private long receivedBytes;
    private long sentBytes;

    public LocalSharedPacketInterface(EndPoint endpoint, TimeSpan? reconnectDelay = null, TimeSpan? connectTimeout = null,
        Action<Exception>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        this.endpoint = endpoint;
        this.reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(8);
        this.connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
        if (this.reconnectDelay <= TimeSpan.Zero || this.connectTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(reconnectDelay));
        this.diagnostic = diagnostic;
        reconnect = true;
    }

    internal LocalSharedPacketInterface(Socket socket, Action closed, Action<Exception>? diagnostic)
        : this(socket.RemoteEndPoint!, diagnostic: diagnostic)
    {
        connection = NewConnection(socket);
        this.closed = closed;
        reconnect = false;
        started = true;
    }

    public bool IsConnected { get { lock (gate) return connection is not null; } }
    internal long ReceivedBytes => Interlocked.Read(ref receivedBytes);
    internal long SentBytes => Interlocked.Read(ref sentBytes);
    public event Action<bool>? ConnectionChanged;
    public override string ToString() => $"LocalInterface[{endpoint}]";
    public IReadOnlyDictionary<string, object?> GetDiagnostics() => new Dictionary<string, object?>
    {
        ["rxb"] = ReceivedBytes, ["txb"] = SentBytes, ["status"] = IsConnected,
        ["peer"] = endpoint.ToString(), ["bitrate"] = 1_000_000_000L, ["mtu"] = HardwareMtu,
    };
    private Connection NewConnection(Socket socket) => new(socket,
        count => Interlocked.Add(ref receivedBytes, count), count => Interlocked.Add(ref sentBytes, count));

    /// <summary>Reserves a connection during election; StartAsync still belongs to the interface manager.</summary>
    public async ValueTask PrepareAsync(CancellationToken token)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (connection is not null) return;
        }
        var next = await ConnectAsync(token).ConfigureAwait(false);
        lock (gate)
        {
            if (disposal is null && connection is null) { connection = next; return; }
        }
        await next.DisposeAsync().ConfigureAwait(false);
        lock (gate) ObjectDisposedException.ThrowIf(disposal is not null, this);
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (started) throw new InvalidOperationException("Local shared interface already started.");
            started = true;
        }
        await PrepareAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            keepalive = KeepAliveAsync();
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (!started) throw new InvalidOperationException("Local shared interface not started.");
        }
        if (Interlocked.Exchange(ref receiving, 1) != 0) throw new InvalidOperationException("Single receive enumerator required.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        while (!linked.IsCancellationRequested)
        {
            Connection? current;
            lock (gate) current = connection;
            if (current is null)
            {
                if (!reconnect) yield break;
                try
                {
                    await Task.Delay(reconnectDelay, linked.Token).ConfigureAwait(false);
                    await PrepareAsync(linked.Token).ConfigureAwait(false);
                    Notify(true);
                }
                catch (Exception ex) when (!linked.IsCancellationRequested && ex is IOException or SocketException or OperationCanceledException) { Report(ex); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { yield break; }
                continue;
            }
            await using var frames = current.ReceiveAsync(linked.Token).GetAsyncEnumerator(linked.Token);
            while (true)
            {
                bool next;
                try { next = await frames.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    if (!stop.IsCancellationRequested) Report(ex);
                    next = false;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { yield break; }
                catch (OperationCanceledException) when (current.IsDisposed && !linked.IsCancellationRequested) { next = false; }
                if (!next) break;
                yield return frames.Current;
            }
            await DropAsync(current).ConfigureAwait(false);
            if (!reconnect) yield break;
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        Connection current;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (!started) throw new InvalidOperationException("Local shared interface not started.");
            current = connection ?? throw new IOException("Local shared instance is disconnected.");
        }
        try { await current.SendAsync(packet, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            await DropAsync(current).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<Connection> ConnectAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
        linked.CancelAfter(connectTimeout);
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, endpoint.AddressFamily == AddressFamily.Unix ? ProtocolType.Unspecified : ProtocolType.Tcp);
        try
        {
            if (endpoint.AddressFamily != AddressFamily.Unix) socket.NoDelay = true;
            await socket.ConnectAsync(endpoint, linked.Token).ConfigureAwait(false);
            return NewConnection(socket);
        }
        catch { socket.Dispose(); throw; }
    }

    private async Task KeepAliveAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                Connection? current;
                lock (gate) current = connection;
                if (current is null) continue;
                try { await current.SendKeepaliveAsync(stop.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    if (!stop.IsCancellationRequested) Report(ex);
                    await DropAsync(current).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async ValueTask DropAsync(Connection current)
    {
        bool removed;
        lock (gate) { removed = ReferenceEquals(current, connection); if (removed) connection = null; }
        await current.DisposeAsync().ConfigureAwait(false);
        if (removed) Notify(false);
    }
    private void Notify(bool online)
    {
        foreach (var callback in ConnectionChanged?.GetInvocationList() ?? [])
            try { ((Action<bool>)callback)(online); } catch (Exception ex) { Report(ex); }
    }
    private void Report(Exception error)
    {
        try
        {
            if (diagnostic is null) System.Diagnostics.Trace.TraceError("Local shared bearer failure: {0}", error);
            else diagnostic(error);
        }
        catch (Exception observer) { System.Diagnostics.Trace.TraceError("Local shared observer failed: {0}; original: {1}", observer, error); }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) return new ValueTask(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        Connection? current;
        lock (gate) { current = connection; connection = null; }
        if (current is not null) await current.DisposeAsync().ConfigureAwait(false);
        if (keepalive is not null) await keepalive.ConfigureAwait(false);
        closed?.Invoke();
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly NetworkStream stream;
        private readonly SemaphoreSlim writes = new(1);
        private readonly CancellationTokenSource stop = new();
        private int disposed;
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;
        private readonly Action<int> received;
        private readonly Action<int> sent;
        public Connection(Socket socket, Action<int> received, Action<int> sent)
        {
            stream = new NetworkStream(socket, ownsSocket: true);
            this.received = received;
            this.sent = sent;
        }
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
            var decoder = new HdlcFrameDecoder(HardwareMtu);
            var buffer = new byte[4096];
            var frames = new List<byte[]>();
            while (true)
            {
                var count = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (count == 0) yield break;
                received(count);
                frames.Clear();
                decoder.Feed(buffer.AsSpan(0, count), frames);
                foreach (var frame in frames) yield return frame;
            }
        }
        public ValueTask SendKeepaliveAsync(CancellationToken token) => WriteAsync(new byte[] { 0x7e, 0x7e }, token);
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken token)
        {
            if (!Packet.TryParse(packet, out _, HardwareMtu)) throw new ArgumentException("Invalid local packet.", nameof(packet));
            var encoded = new byte[2 + packet.Length * 2];
            var count = 0;
            encoded[count++] = 0x7e;
            foreach (var b in packet.Span)
            {
                if (b is 0x7e or 0x7d) { encoded[count++] = 0x7d; encoded[count++] = (byte)(b ^ 0x20); }
                else encoded[count++] = b;
            }
            encoded[count++] = 0x7e;
            return WriteAsync(encoded.AsMemory(0, count), token);
        }
        private async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token);
            await writes.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(data, linked.Token).ConfigureAwait(false);
                sent(data.Length);
            }
            finally { writes.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await stop.CancelAsync().ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Exclusive-bind local instance election and independently owned accepted peers.</summary>
public sealed class LocalSharedServerInterface : ISpawningPacketInterface, IPacketInterfaceDiagnostics
{
    public int HardwareMtu => LocalSharedPacketInterface.HardwareMtu;
    public long Bitrate => 1_000_000_000;
    public bool SupportsMtuDiscovery => true;
    private readonly EndPoint endpoint;
    private readonly int maximumClients;
    private readonly Action<Exception>? diagnostic;
    private readonly object gate = new();
    private readonly HashSet<LocalSharedPacketInterface> clients = [];
    private readonly CancellationTokenSource stop = new();
    private Socket? listener;
    private Task? accept;
    private Task? disposal;
    private bool started;
    private long closedReceivedBytes;
    private long closedSentBytes;

    public LocalSharedServerInterface(EndPoint endpoint, int maximumClients = 64, Action<Exception>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (maximumClients < 1) throw new ArgumentOutOfRangeException(nameof(maximumClients));
        if (endpoint is IPEndPoint ip && !IPAddress.IsLoopback(ip.Address)) throw new ArgumentException("Shared bearer must be loopback.", nameof(endpoint));
        this.endpoint = endpoint;
        this.maximumClients = maximumClients;
        this.diagnostic = diagnostic;
    }
    public event Action<IPacketInterface>? ChildConnected;
    public EndPoint? LocalEndpoint { get; private set; }
    public override string ToString() => $"Shared Instance[{LocalEndpoint ?? endpoint}]";
    public IReadOnlyDictionary<string, object?> GetDiagnostics()
    {
        lock (gate) return new Dictionary<string, object?>
        {
            ["rxb"] = closedReceivedBytes + clients.Sum(client => client.ReceivedBytes),
            ["txb"] = closedSentBytes + clients.Sum(client => client.SentBytes),
            ["status"] = listener is not null && !stop.IsCancellationRequested,
            ["clients"] = clients.Count, ["bitrate"] = 1_000_000_000L, ["mtu"] = LocalSharedPacketInterface.HardwareMtu,
        };
    }

    /// <summary>Bind without accepting yet, allowing the coordinator to choose host/client before building its router.</summary>
    public void Reserve()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (listener is not null) return;
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, endpoint.AddressFamily == AddressFamily.Unix ? ProtocolType.Unspecified : ProtocolType.Tcp);
            try
            {
                if (endpoint.AddressFamily != AddressFamily.Unix) socket.ExclusiveAddressUse = true;
                socket.Bind(endpoint);
                socket.Listen(maximumClients);
                LocalEndpoint = socket.LocalEndPoint;
                listener = socket;
            }
            catch { socket.Dispose(); throw; }
        }
    }
    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            Reserve();
            if (started) throw new InvalidOperationException("Local server already started.");
            started = true;
            accept = AcceptAsync();
        }
        return ValueTask.CompletedTask;
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var socket = await listener!.AcceptAsync(stop.Token).ConfigureAwait(false);
                LocalSharedPacketInterface child;
                lock (gate)
                {
                    if (stop.IsCancellationRequested || clients.Count >= maximumClients) { socket.Dispose(); continue; }
                    if (socket.AddressFamily != AddressFamily.Unix) socket.NoDelay = true;
                    LocalSharedPacketInterface? captured = null;
                    child = new LocalSharedPacketInterface(socket, () =>
                    {
                        lock (gate)
                            if (clients.Remove(captured!))
                            {
                                closedReceivedBytes += captured!.ReceivedBytes;
                                closedSentBytes += captured.SentBytes;
                            }
                    }, diagnostic);
                    captured = child;
                    clients.Add(child);
                }
                var handler = ChildConnected;
                if (handler is null) { await child.DisposeAsync().ConfigureAwait(false); continue; }
                try { handler(child); }
                catch (Exception ex)
                {
                    Report(ex);
                    await child.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { Report(ex); }
    }
    private void Report(Exception error)
    {
        try
        {
            if (diagnostic is null) System.Diagnostics.Trace.TraceError("Local shared listener failure: {0}", error);
            else diagnostic(error);
        }
        catch (Exception observer) { System.Diagnostics.Trace.TraceError("Shared listener observer failed: {0}; original: {1}", observer, error); }
    }
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        try { await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
        yield break;
    }
    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync()
    {
        lock (gate) return new ValueTask(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        listener?.Dispose();
        if (accept is not null) await accept.ConfigureAwait(false);
        LocalSharedPacketInterface[] snapshot;
        lock (gate) snapshot = [.. clients];
        foreach (var child in snapshot) await child.DisposeAsync().ConfigureAwait(false);
    }
}
