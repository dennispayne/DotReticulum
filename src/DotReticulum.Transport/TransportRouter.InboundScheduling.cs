using System.Threading.Channels;
using DotReticulum.Core;

namespace DotReticulum.Transport;

public enum TransportTrafficClass : byte
{
    Data,
    Announce,
    PathRequest,
    IngressLimited,
}

public sealed record InboundQueueInfo(int Total, IReadOnlyList<int> Heights, IReadOnlyList<long> Dropped);

public sealed partial class TransportRouter
{
    private readonly object _inboundGate = new();
    private readonly Queue<QueuedInbound>[] _inboundQueues = [new(), new(), new(), new()];
    private readonly int[] _inboundCapacities;
    private readonly long[] _inboundDropped = new long[4];
    private readonly Dictionary<string, InflightPathRequest> _inflightPathRequests = new();
    private readonly SemaphoreSlim _inboundDispatcher = new(1, 1);
    private volatile ChannelWriter<bool>? _inboundWake;

    public int InflightPathRequests
    {
        get { lock (_inboundGate) return _inflightPathRequests.Count; }
    }

    public InboundQueueInfo InboundQueues
    {
        get
        {
            lock (_inboundGate)
            {
                var heights = _inboundQueues.Select(queue => queue.Count).ToArray();
                return new InboundQueueInfo(heights.Sum(), heights, _inboundDropped.ToArray());
            }
        }
    }

    public IReadOnlyList<int> InboundQueueCapacities => _inboundCapacities.ToArray();

    /// <summary>Nonblocking bounded ingress, ordered DATA, ANNOUNCE, PATH_REQUEST, INGRESS_LIMITED as upstream.</summary>
    public bool TryQueueInbound(ReceivedPacket received)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(received);
        var trafficClass = TransportTrafficClass.Data;
        string? requestKey = null;
        if (received.Packet.PacketType == PacketType.Announce)
        {
            if (!PrepareAnnounceIngress(received)) return false;
            trafficClass = TransportTrafficClass.Announce;
        }
        else if (received.Packet.DestinationType == DestinationType.Plain && received.Packet.DestinationHash.Span.SequenceEqual(PathRequestHash))
        {
            var data = received.Packet.Payload.Span;
            if (data.Length <= Destination.HashLength) return false;
            var tag = data[(data.Length > 32 ? 32 : 16)..];
            if (tag.Length > Destination.HashLength) tag = tag[..Destination.HashLength];
            requestKey = Convert.ToHexStringLower(data[..Destination.HashLength]);
            if (!RememberTag(requestKey + Convert.ToHexStringLower(tag))) return false;
            TrafficController(received.Source).ReceivedPathRequest(received.Raw.Length);
            trafficClass = ShouldIngressLimitPathRequests(received.Source) ? TransportTrafficClass.IngressLimited : TransportTrafficClass.PathRequest;
        }
        var index = (int)trafficClass;
        if (trafficClass == TransportTrafficClass.Data) _manager.NotifyIngressQueuePressure();
        lock (_inboundGate)
        {
            if (requestKey is not null && _inflightPathRequests.TryGetValue(requestKey, out var inflight))
            {
                if (trafficClass != TransportTrafficClass.IngressLimited && !inflight.Served.Contains(received.Source)
                    && (inflight.Waiting.ContainsKey(received.Source) || inflight.Waiting.Count + inflight.Served.Count < _options.ControlCapacity))
                    inflight.Waiting[received.Source] = received;
                return true;
            }
            if (_inboundQueues[index].Count >= _inboundCapacities[index])
            {
                _inboundDropped[index]++;
                return false;
            }
            if (requestKey is not null)
            {
                if (_inflightPathRequests.Count >= _options.ControlCapacity) { _inboundDropped[index]++; return false; }
                _inflightPathRequests[requestKey] = new InflightPathRequest(received.Source, _time.GetUtcNow());
            }
            _inboundQueues[index].Enqueue(new QueuedInbound(received, requestKey, index));
            _inboundWake?.TryWrite(true);
            return true;
        }
    }

    /// <summary>Drains queued traffic in priority order. The receive loop invokes this from its single owned dispatcher.</summary>
    public async ValueTask ProcessQueuedInboundAsync(CancellationToken cancellationToken = default)
    {
        await _inboundDispatcher.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                QueuedInbound? queued = null;
                lock (_inboundGate)
                {
                    foreach (var queue in _inboundQueues)
                    {
                        if (!queue.TryDequeue(out queued)) continue;
                        break;
                    }
                }
                if (queued is null) return;
                try
                {
                    await HandleCoreAsync(queued.Received, cancellationToken, queued.RequestKey is not null, ingressPrepared: true,
                        ingressLimitedPathRequest: queued.RequestKey is null ? null : queued.TrafficClass == (int)TransportTrafficClass.IngressLimited).ConfigureAwait(false);
                    if (queued.RequestKey is { } key) await CompleteInflightPathRequestAsync(key, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lock (_inboundGate)
                    {
                        var queue = _inboundQueues[queued.TrafficClass];
                        var pending = queue.ToArray();
                        if (pending.Length >= _inboundCapacities[queued.TrafficClass]) _inboundDropped[queued.TrafficClass]++;
                        queue.Clear();
                        queue.Enqueue(queued);
                        foreach (var entry in pending.Take(_inboundCapacities[queued.TrafficClass] - 1)) queue.Enqueue(entry);
                    }
                    throw;
                }
                catch (Exception ex)
                {
                    if (queued.RequestKey is { } key)
                    {
                        lock (_inboundGate) _inflightPathRequests.Remove(key);
                    }
                    Log?.Invoke($"Error while processing a packet: {ex.Message}");
                }
            }
        }
        finally { _inboundDispatcher.Release(); }
    }

    private async Task DrainInboundAsync(ChannelReader<bool> wake, CancellationToken cancellationToken)
    {
        await foreach (var _ in wake.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            await ProcessQueuedInboundAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CompleteInflightPathRequestAsync(string key, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReceivedPacket received;
            InflightPathRequest entry;
            lock (_inboundGate)
            {
                if (!_inflightPathRequests.TryGetValue(key, out entry!)) return;
                if (entry.Waiting.Count == 0) { _inflightPathRequests.Remove(key); return; }
                received = entry.Waiting.First().Value;
            }
            await HandlePathRequestAsync(received, cancellationToken, registeredPathRequest: true, ingressLimited: false).ConfigureAwait(false);
            lock (_inboundGate)
            {
                entry.Served.Add(received.Source);
                entry.Waiting.Remove(received.Source);
            }
        }
    }

    private sealed record QueuedInbound(ReceivedPacket Received, string? RequestKey, int TrafficClass);

    private void QueueHeldIngressRelease(ReceivedPacket received)
    {
        if (!PrepareAnnounceIngress(received)) return;
        const int trafficClass = (int)TransportTrafficClass.IngressLimited;
        lock (_inboundGate)
        {
            if (_inboundQueues[trafficClass].Count >= _inboundCapacities[trafficClass])
            {
                _inboundDropped[trafficClass]++;
                return;
            }
            _inboundQueues[trafficClass].Enqueue(new QueuedInbound(received, null, trafficClass));
            _inboundWake?.TryWrite(true);
        }
    }
    private sealed class InflightPathRequest(IPacketInterface source, DateTimeOffset created)
    {
        public IPacketInterface Source { get; } = source;
        public DateTimeOffset Created { get; } = created;
        public HashSet<IPacketInterface> Served { get; } = [source];
        public Dictionary<IPacketInterface, ReceivedPacket> Waiting { get; } = new();
    }
}
