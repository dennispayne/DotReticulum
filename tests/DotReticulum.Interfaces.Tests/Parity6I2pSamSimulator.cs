using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace DotReticulum.Interfaces.Tests;

/// <summary>Bounded controlled SAM socket simulator, NOT an I2P router or network-interoperability oracle.</summary>
internal sealed class Parity6I2pSamSimulator : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _handlers = [];
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _sessions = new();
    private readonly ConcurrentDictionary<string, TcpClient> _sessionOwners = new(StringComparer.Ordinal);
    private readonly Channel<(string SessionId, NetworkStream Stream)> _accepts =
        Channel.CreateBounded<(string, NetworkStream)>(64);
    private readonly Channel<NetworkStream> _connects = Channel.CreateBounded<NetworkStream>(64);
    private readonly Task _worker;
    private int _generated;
    private int _sessionCount;
    public int Port { get; }
    public bool FragmentReplies { get; init; }
    public string? HelloReply { get; init; }
    public string? ConnectResult { get; init; }
    public bool HangHello { get; init; }
    public bool HangConnect { get; init; }
    public int FailConnectAttempts { get; init; }
    public int HangConnectAttempts { get; init; }
    public int InvalidIdConnectAttempts { get; init; }
    public string? SessionResult { get; init; }
    public int Generated => Volatile.Read(ref _generated);
    public int SessionCount => Volatile.Read(ref _sessionCount);
    public int ActiveSessionCount => _sessions.Count;
    public int ActiveConnections => _clients.Count;
    public int NamingLookups;
    public int ConnectAttempts;
    public ConcurrentQueue<string> SessionDestinations { get; } = new();
    public ConcurrentQueue<string> SessionIds { get; } = new();
    public ConcurrentQueue<string> StreamSessionIds { get; } = new();

    // Synthetic test structure only: these bytes are not a usable I2P private identity.
    public string PrivateDestination { get; } = Convert.ToBase64String(new byte[420]);
    public string PublicDestination { get; } = Convert.ToBase64String(new byte[387]);

    public Parity6I2pSamSimulator()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _worker = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                client.NoDelay = true;
                _clients.TryAdd(client, 0);
                // Total socket admissions are bounded as well as concurrently pending stream channels.
                if (_handlers.Count >= 512) { client.Dispose(); _clients.TryRemove(client, out _); continue; }
                _handlers.Add(HandleAsync(client));
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        string? sessionId = null;
        try
        {
            var stream = client.GetStream();
            Assert.Equal("HELLO VERSION MIN=3.1 MAX=3.1", await LineAsync(stream, _lifetime.Token));
            if (HangHello) { await Task.Delay(Timeout.Infinite, _lifetime.Token); return; }
            await ReplyAsync(stream, HelloReply ?? "HELLO REPLY RESULT=OK VERSION=3.1\n");
            var command = await LineAsync(stream, _lifetime.Token);
            if (command == "DEST GENERATE SIGNATURE_TYPE=7")
            {
                Interlocked.Increment(ref _generated);
                await ReplyAsync(stream, $"DEST REPLY PUB={PublicDestination} PRIV={PrivateDestination}\n");
            }
            else if (command.StartsWith("SESSION CREATE STYLE=STREAM ID=", StringComparison.Ordinal))
            {
                var destination = command.Split("DESTINATION=", StringSplitOptions.None)[1].Trim();
                sessionId = command.Split(' ').Single(part => part.StartsWith("ID=", StringComparison.Ordinal))[3..];
                SessionIds.Enqueue(sessionId);
                SessionDestinations.Enqueue(destination);
                Interlocked.Increment(ref _sessionCount);
                if (SessionResult is null)
                {
                    Assert.True(_sessionOwners.TryAdd(sessionId, client));
                    _sessions.TryAdd(client, 0);
                }
                await ReplyAsync(stream, $"SESSION STATUS RESULT={SessionResult ?? "OK"} DESTINATION={PrivateDestination}\n");
                if (SessionResult is not null) return;
                await UntilClosedAsync(stream);
            }
            else if (command.StartsWith("NAMING LOOKUP NAME=", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref NamingLookups);
                await ReplyAsync(stream, $"NAMING REPLY RESULT=OK NAME=peer.i2p VALUE={PublicDestination}\n");
            }
            else if (command.StartsWith("STREAM CONNECT ID=", StringComparison.Ordinal))
            {
                Assert.Contains($"DESTINATION={PublicDestination} SILENT=false", command);
                var id = command.Split(' ').Single(part => part.StartsWith("ID=", StringComparison.Ordinal))[3..];
                StreamSessionIds.Enqueue(id);
                var attempt = Interlocked.Increment(ref ConnectAttempts);
                Assert.True(_sessionOwners.ContainsKey(id), "STREAM CONNECT must reference a live owned SAM session.");
                if (HangConnect || attempt <= HangConnectAttempts) { await UntilClosedAsync(stream); return; }
                var result = attempt <= InvalidIdConnectAttempts ? "INVALID_ID" :
                    attempt <= FailConnectAttempts ? "CANT_REACH_PEER" : ConnectResult ?? "OK";
                await ReplyAsync(stream, $"STREAM STATUS RESULT={result}\n");
                if (result != "OK") return;
                await _connects.Writer.WriteAsync(stream, _lifetime.Token);
                await Task.Delay(Timeout.Infinite, _lifetime.Token);
            }
            else if (command.StartsWith("STREAM ACCEPT ID=", StringComparison.Ordinal))
            {
                Assert.EndsWith("SILENT=false", command);
                var id = command.Split(' ').Single(part => part.StartsWith("ID=", StringComparison.Ordinal))[3..];
                if (!_sessionOwners.ContainsKey(id))
                {
                    await ReplyAsync(stream, "STREAM STATUS RESULT=INVALID_ID\n");
                    return;
                }
                await ReplyAsync(stream, "STREAM STATUS RESULT=OK\n");
                await _accepts.Writer.WriteAsync((id, stream), _lifetime.Token);
                await Task.Delay(Timeout.Infinite, _lifetime.Token);
            }
            else throw new InvalidDataException("Unexpected simulator command.");
        }
        catch (Exception error) when (_lifetime.IsCancellationRequested ||
            error is IOException or SocketException or ObjectDisposedException) { }
        finally
        {
            if (sessionId is not null) _sessionOwners.TryRemove(sessionId, out _);
            _sessions.TryRemove(client, out _);
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }

    private async Task ReplyAsync(NetworkStream stream, string reply)
    {
        var bytes = Encoding.ASCII.GetBytes(reply);
        if (!FragmentReplies) { await stream.WriteAsync(bytes, _lifetime.Token); return; }
        foreach (var value in bytes) await stream.WriteAsync(new byte[] { value }, _lifetime.Token);
    }

    private async Task UntilClosedAsync(NetworkStream stream)
    {
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, _lifetime.Token) != 0) { }
    }

    internal static async Task<string> LineAsync(NetworkStream stream, CancellationToken token)
    {
        var result = new StringBuilder();
        var one = new byte[1];
        while (result.Length < 16384)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new EndOfStreamException();
            if (one[0] == '\n') return result.ToString();
            result.Append((char)one[0]);
        }
        throw new InvalidDataException("Simulator line bound.");
    }

    public async Task<NetworkStream> OutgoingAsync(CancellationToken token) => await _connects.Reader.ReadAsync(token);

    public async Task<NetworkStream> IncomingAsync(ReadOnlyMemory<byte> initialBytes, CancellationToken token,
        string? peerDestination = null, string? sessionId = null)
    {
        (string SessionId, NetworkStream Stream) pending;
        do { pending = await _accepts.Reader.ReadAsync(token); }
        while (sessionId is not null && pending.SessionId != sessionId);
        var stream = pending.Stream;
        var combined = Encoding.ASCII.GetBytes((peerDestination ?? PublicDestination) + "\n").Concat(initialBytes.ToArray()).ToArray();
        if (FragmentReplies)
            foreach (var value in combined) await stream.WriteAsync(new byte[] { value }, token);
        else await stream.WriteAsync(combined, token);
        return stream;
    }

    public void DropSessions()
    {
        foreach (var client in _sessions.Keys) client.Dispose();
    }

    public static async Task<byte[]> PacketAsync(NetworkStream stream, CancellationToken token)
    {
        var decoder = new HdlcFrameDecoder(1064, validatePackets: false);
        var frames = new List<byte[]>();
        var one = new byte[1];
        while (frames.Count == 0)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new EndOfStreamException();
            decoder.Feed(one, frames);
        }
        return frames[0];
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _listener.Stop();
        foreach (var client in _clients.Keys) client.Dispose();
        await _worker;
        await Task.WhenAll(_handlers);
    }
}
