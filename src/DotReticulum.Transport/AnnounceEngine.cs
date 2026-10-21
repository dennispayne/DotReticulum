using System.Security.Cryptography;
using DotReticulum.Core;

namespace DotReticulum.Transport;

public enum AnnounceOutcome
{
    /// <summary>Valid, newer or new: remembered, path state updated, handlers called, queued for rebroadcast when enabled.</summary>
    Accepted,

    /// <summary>As <see cref="Accepted"/> but the destination exceeded the announce rate limit, so it was not queued for rebroadcast.</summary>
    AcceptedRateLimited,

    /// <summary>Valid but not new (duplicate random hash, older emission or longer path): identity remembered, nothing else changed.</summary>
    Duplicate,

    /// <summary>The destination is registered locally.</summary>
    Local,

    /// <summary>Valid, but the hop count exceeds the maximum.</summary>
    HopLimit,

    /// <summary>Bad signature, destination mismatch, malformed payload or invalid key.</summary>
    Invalid,

    /// <summary>Valid signature and hash, but the public key differs from the one already known for this destination hash.</summary>
    KeyConflict,

    /// <summary>Announce packets must use the SINGLE destination type.</summary>
    WrongDestinationType,

    NotAnnounce,
}

public sealed record AnnounceResult(AnnounceOutcome Outcome, AnnounceStatus? Validation = null, int Hops = 0);

/// <summary>Upstream per-interface <c>announce_rate_target/grace/penalty</c>.</summary>
public sealed record AnnounceRateLimit(TimeSpan Target, int Grace, TimeSpan Penalty);

/// <summary>A queued rebroadcast produced by <see cref="AnnounceEngine.CollectDueRebroadcasts"/>.</summary>
/// <param name="Packet">The packet to transmit.</param>
/// <param name="ExcludedInterface">The interface the announce arrived on; do not send it back there.</param>
/// <param name="TargetInterface">When set (path responses), transmit only on this interface.</param>
public sealed record AnnounceRebroadcast(Packet Packet, IPacketInterface? ExcludedInterface, IPacketInterface? TargetInterface = null);

/// <summary>Read-only view of the best known announce path for a destination.</summary>
public sealed record AnnouncePathInfo(long ReceivedUnix, int Hops, long ExpiresUnix, byte[]? TransportId, IPacketInterface? Interface, byte[] PacketHash);

public sealed class AnnounceEngineOptions
{
    /// <summary>When true, accepted announces are queued for rebroadcast (upstream <c>enable_transport</c>). Default false.</summary>
    public bool TransportEnabled { get; init; }

    /// <summary>
    /// The transport identity hash. When set, rebroadcasts are HEADER_2/TRANSPORT packets carrying it, exactly like upstream;
    /// when null they are HEADER_1 broadcasts (full transport rewriting belongs to the routing layer).
    /// </summary>
    public byte[]? TransportId { get; init; }

    /// <summary>Applied to every receiving interface (upstream configures this per interface). Null disables rate limiting.</summary>
    public AnnounceRateLimit? RateLimit { get; init; }

    /// <summary>Capacity of each internal table (paths, announce table, rate table); the oldest entries are evicted beyond it.</summary>
    public int TableCapacity { get; init; } = 65536;

    public int KnownDestinationCapacity { get; init; } = KnownDestinations.DefaultCapacity;
}

/// <summary>
/// The announce half of upstream <c>Transport.inbound</c>: validation, identity/ratchet remembering, duplicate and freshness decisions
/// by random hash, rate limiting, the announce (rebroadcast) table with hop/retry rules, and announce handler dispatch.
/// Pure state machine: no I/O; <see cref="AnnounceProcessor"/> connects it to interfaces.
/// </summary>
public sealed partial class AnnounceEngine
{
    /// <summary>Upstream <c>PATHFINDER_M</c>.</summary>
    public const int MaxHops = 128;
    public const double RetransmitGraceSeconds = 5;            // PATHFINDER_G
    public const double RandomWindowSeconds = 0.5;             // PATHFINDER_RW
    public const int MaxRetransmitRetries = 1;                 // PATHFINDER_R
    public const int LocalRebroadcastsMax = 2;                 // LOCAL_REBROADCASTS_MAX
    public const int MaxRateTimestamps = 16;                   // MAX_RATE_TIMESTAMPS
    public const int MaxRandomBlobs = 64;                      // MAX_RANDOM_BLOBS
    public static readonly TimeSpan PathExpiry = TimeSpan.FromDays(7); // PATHFINDER_E

    private readonly object _gate = new();
    private readonly AnnounceEngineOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, PathState> _paths = new();
    private readonly Dictionary<string, TableEntry> _table = new();
    private readonly Dictionary<string, TableEntry> _responses = new();
    private readonly Dictionary<string, RateEntry> _rates = new();
    private readonly HashSet<string> _local = new();
    private readonly List<IAnnounceHandler> _handlers = [];

    public AnnounceEngine(AnnounceEngineOptions? options = null, KnownDestinations? known = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new AnnounceEngineOptions();
        if (_options.TransportId is { Length: not Destination.HashLength }) throw new ArgumentException("Transport id must be 16 bytes.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.TableCapacity, 1);
        _time = timeProvider ?? TimeProvider.System;
        Known = known ?? new KnownDestinations(_options.KnownDestinationCapacity, _time);
    }

    public KnownDestinations Known { get; }

    public Action<string>? Log { get; set; }

    public int PendingRebroadcasts
    {
        get { lock (_gate) return _table.Count; }
    }

    private double Now => _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    private static string Key(ReadOnlySpan<byte> hash) => Convert.ToHexStringLower(hash);

    public void RegisterLocalDestination(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate) _local.Add(Key(destinationHash));
    }

    public void DeregisterLocalDestination(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate) _local.Remove(Key(destinationHash));
    }

    public void RegisterHandler(IAnnounceHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate) _handlers.Add(handler);
    }

    public void DeregisterHandler(IAnnounceHandler handler)
    {
        lock (_gate) _handlers.RemoveAll(h => ReferenceEquals(h, handler));
    }

    public AnnouncePathInfo? GetPath(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate)
        {
            return _paths.TryGetValue(Key(destinationHash), out var p)
                ? new AnnouncePathInfo((long)p.Timestamp, p.Hops, (long)p.Expires, p.TransportId, p.Source, p.PacketHash)
                : null;
        }
    }

    /// <summary>Processes one received packet. <paramref name="packet"/> has the hop count as received on the wire; this adds the hop for this node.</summary>
    public AnnounceResult Process(Packet packet, IPacketInterface? source, int hopIncrement = 1, bool queueRebroadcast = true)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.PacketType != PacketType.Announce) return new(AnnounceOutcome.NotAnnounce);
        if (packet.DestinationType != DestinationType.Single) return new(AnnounceOutcome.WrongDestinationType);

        var validation = Announce.Validate(packet);
        if (!validation.IsValid) return new(AnnounceOutcome.Invalid, validation.Status);
        var data = validation.Data!;
        ArgumentOutOfRangeException.ThrowIfNegative(hopIncrement);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hopIncrement, 1);
        var hops = packet.Hops + hopIncrement;
        var destinationHash = packet.DestinationHash.ToArray();
        var key = Key(destinationHash);
        var packetHash = packet.CalculateHash();
        using var identity = validation.Identity!;

        var known = Known.GetPublicKey(destinationHash);
        if (known is not null && !CryptographicOperations.FixedTimeEquals(known, data.PublicKey))
            return new(AnnounceOutcome.KeyConflict, AnnounceStatus.Valid, hops);

        Known.Remember(destinationHash, packetHash, data.PublicKey, data.AppData, data.Ratchet);

        AnnounceReceived? delivery = null;
        List<IAnnounceHandler>? handlers = null;
        AnnounceResult result;

        lock (_gate)
        {
            if (_local.Contains(key)) return new(AnnounceOutcome.Local, AnnounceStatus.Valid, hops);

            var transportId = packet.HeaderType == HeaderType.Header2 ? packet.TransportId.ToArray() : null;
            var receivedFrom = transportId ?? destinationHash;
            var now = Now;

            if (transportId is not null && _options.TransportEnabled && _table.TryGetValue(key, out var queued))
            {
                if (hops - 1 == queued.Hops)
                {
                    queued.LocalRebroadcasts++;
                    if (queued.Retries > 0 && queued.LocalRebroadcasts >= LocalRebroadcastsMax) _table.Remove(key);
                }

                if (hops - 1 == queued.Hops + 1 && queued.Retries > 0 && now < queued.RetransmitAt) _table.Remove(key);
            }

            if (hops > MaxHops) return new(AnnounceOutcome.HopLimit, AnnounceStatus.Valid, hops);

            var blob = data.RandomHash;
            var emitted = data.Emitted;
            List<byte[]> blobs = [];
            var shouldAdd = false;
            if (!_paths.TryGetValue(key, out var path))
            {
                shouldAdd = true;
            }
            else
            {
                blobs = path.RandomBlobs;
                var known1 = blobs.Any(b => b.AsSpan().SequenceEqual(blob));
                if (hops <= path.Hops)
                {
                    shouldAdd = !known1 && emitted > Timebase(blobs);
                }
                else
                {
                    long pathEmitted = 0;
                    foreach (var b in blobs)
                    {
                        pathEmitted = Math.Max(pathEmitted, Announce.EmittedFromRandomHash(b));
                        if (pathEmitted >= emitted) break;
                    }

                    if (now >= path.Expires) shouldAdd = !known1;
                    else if (emitted > pathEmitted) shouldAdd = !known1;
                }
            }

            if (!shouldAdd) return new(AnnounceOutcome.Duplicate, AnnounceStatus.Valid, hops);

            var isPathResponse = packet.Context == Announce.PathResponseContext;
            var rateBlocked = !isPathResponse && _options.RateLimit is { } limit && ApplyRateLimit(key, limit, now);

            if (!blobs.Any(b => b.AsSpan().SequenceEqual(blob)))
            {
                blobs.Add(blob);
                if (blobs.Count > MaxRandomBlobs) blobs.RemoveRange(0, blobs.Count - MaxRandomBlobs);
            }

            if (_options.TransportEnabled && queueRebroadcast && !isPathResponse && !rateBlocked)
            {
                _table[key] = new TableEntry(now, now + Random.Shared.NextDouble() * RandomWindowSeconds, receivedFrom, hops, packet, source);
                Trim(_table, e => e.Timestamp, key);
            }

            _paths[key] = new PathState(now, receivedFrom, transportId, hops, now + PathExpiry.TotalSeconds, blobs, source, packetHash, packet);
            Trim(_paths, p => p.Timestamp, key);

            handlers = [.. _handlers];
            delivery = new AnnounceReceived(destinationHash, identity, data.AppData, packetHash, isPathResponse, hops, data.Ratchet);
            result = new(rateBlocked ? AnnounceOutcome.AcceptedRateLimited : AnnounceOutcome.Accepted, AnnounceStatus.Valid, hops);
        }

        Dispatch(handlers, delivery, destinationHash, identity);
        return result;
    }

    /// <summary>
    /// Advances the announce table (upstream's 1 s job): returns the rebroadcasts that are due and retires entries that reached the
    /// retry or local-rebroadcast limits. Callers invoke this roughly once a second.
    /// </summary>
    public IReadOnlyList<AnnounceRebroadcast> CollectDueRebroadcasts()
    {
        var due = new List<AnnounceRebroadcast>();
        var now = Now;
        lock (_gate)
        {
            Collect(_table, now, due);
            Collect(_responses, now, due);
        }

        return due;
    }

    private void Collect(Dictionary<string, TableEntry> table, double now, List<AnnounceRebroadcast> due)
    {
        var completed = new List<string>();
        foreach (var (key, entry) in table)
        {
            if (entry.Retries > 0 && entry.Retries >= LocalRebroadcastsMax) { completed.Add(key); continue; }
            if (entry.Retries > MaxRetransmitRetries) { completed.Add(key); continue; }
            if (now <= entry.RetransmitAt) continue;

            entry.RetransmitAt = now + RetransmitGraceSeconds + RandomWindowSeconds;
            entry.Retries++;
            var original = entry.Packet;
            if (Known.Get(original.DestinationHash.Span) is null) { completed.Add(key); continue; }

            var context = entry.Blocked ? Announce.PathResponseContext : (byte)0;
            var rebroadcast = _options.TransportId is { } transportId
                ? Packet.Create(PacketType.Announce, DestinationType.Single, original.DestinationHash, original.Payload,
                    context: context, headerType: HeaderType.Header2, transportId: transportId, transportType: TransportType.Transport,
                    contextFlag: original.ContextFlag, hops: (byte)entry.Hops)
                : Packet.Create(PacketType.Announce, DestinationType.Single, original.DestinationHash, original.Payload,
                    context: context, contextFlag: original.ContextFlag, hops: (byte)entry.Hops);
            due.Add(new AnnounceRebroadcast(rebroadcast, entry.Source, entry.Attached));
        }

        foreach (var key in completed) table.Remove(key);
    }

    /// <summary>
    /// Upstream <c>Transport.path_request</c> answer for a known path: replays the cached announce as a PATH_RESPONSE, sent once and only on
    /// <paramref name="attached"/> after <paramref name="delay"/>. Returns false when no path is known.
    /// </summary>
    public bool QueuePathResponse(ReadOnlySpan<byte> destinationHash, IPacketInterface? attached, TimeSpan delay)
    {
        var key = Key(destinationHash);
        lock (_gate)
        {
            if (!_paths.TryGetValue(key, out var path)) return false;
            var now = Now;
            _responses[key] = new TableEntry(now, now + delay.TotalSeconds, path.ReceivedFrom, path.Hops, path.Packet, path.Source)
            {
                Attached = attached,
                Blocked = true,
                Retries = MaxRetransmitRetries,
            };
            return true;
        }
    }

    /// <summary>The next hop towards the destination: the transport id of the last announce hop, or the destination itself when directly attached.</summary>
    public byte[]? GetNextHop(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate) return _paths.TryGetValue(Key(destinationHash), out var p) ? p.ReceivedFrom : null;
    }

    public int PathCount
    {
        get { lock (_gate) return _paths.Count; }
    }

    /// <summary>Refreshes the path timestamp; upstream does this whenever a packet is forwarded along the path.</summary>
    public void TouchPath(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate)
        {
            if (_paths.TryGetValue(Key(destinationHash), out var p)) p.Timestamp = Now;
        }
    }

    public bool RemovePath(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate)
        {
            var key = Key(destinationHash);
            _table.Remove(key);
            _responses.Remove(key);
            return _paths.Remove(key);
        }
    }

    /// <summary>
    /// Removes paths whose lifetime (from their last use) has passed or whose interface is gone. <paramref name="lifetimeFor"/> maps the path's
    /// interface to its lifetime, or null when that interface no longer exists. Returns the number removed.
    /// </summary>
    public int CullPaths(Func<IPacketInterface?, TimeSpan?> lifetimeFor)
    {
        ArgumentNullException.ThrowIfNull(lifetimeFor);
        var now = Now;
        lock (_gate)
        {
            var stale = _paths.Where(kv => lifetimeFor(kv.Value.Source) is not { } life || now > kv.Value.Timestamp + life.TotalSeconds)
                .Select(kv => kv.Key).ToArray();
            foreach (var key in stale)
            {
                _paths.Remove(key);
                _table.Remove(key);
                _responses.Remove(key);
            }

            return stale.Length;
        }
    }

    private bool ApplyRateLimit(string key, AnnounceRateLimit limit, double now)
    {
        if (!_rates.TryGetValue(key, out var rate))
        {
            _rates[key] = new RateEntry { Last = now, Timestamps = [now] };
            Trim(_rates, r => r.Last, key);
            return false;
        }

        rate.Timestamps.Add(now);
        while (rate.Timestamps.Count > MaxRateTimestamps) rate.Timestamps.RemoveAt(0);
        var currentRate = now - rate.Last;
        if (now <= rate.BlockedUntil) return true;

        if (currentRate < limit.Target.TotalSeconds) rate.Violations++;
        else rate.Violations = Math.Max(0, rate.Violations - 1);

        if (rate.Violations > limit.Grace)
        {
            rate.BlockedUntil = rate.Last + limit.Target.TotalSeconds + limit.Penalty.TotalSeconds;
            return true;
        }

        rate.Last = now;
        return false;
    }

    private void Dispatch(List<IAnnounceHandler> handlers, AnnounceReceived delivery, byte[] destinationHash, Identity identity)
    {
        foreach (var handler in handlers)
        {
            try
            {
                if (handler.AspectFilter is { } filter
                    && !Destination.ComputeHashFromNameAndIdentity(filter, identity.Hash).AsSpan().SequenceEqual(destinationHash)) continue;
                if (delivery.IsPathResponse && !handler.ReceivePathResponses) continue;
                // Each handler gets its own identity copy so it may dispose it.
                handler.ReceivedAnnounce(delivery with { Identity = Identity.FromPublicKey(identity.ExportPublicKey()) });
            }
            catch (Exception ex)
            {
                ReportCallbackError(handler.GetType().Name, destinationHash, ex);
            }
        }
    }

    internal void ReportCallbackError(string callback, ReadOnlySpan<byte> destinationHash, Exception error)
    {
        var message = $"Announce callback {callback} failed for {Key(destinationHash)}: {error}";
        if (Log is { } log) log(message);
        else System.Diagnostics.Trace.TraceError(message);
    }

    private static long Timebase(List<byte[]> blobs) =>
        blobs.Count == 0 ? 0 : blobs.Max(b => Announce.EmittedFromRandomHash(b));

    private void Trim<T>(Dictionary<string, T> table, Func<T, double> age, string keep)
    {
        if (table.Count <= _options.TableCapacity) return;
        var drop = table.Where(kv => kv.Key != keep).OrderBy(kv => age(kv.Value))
            .Take(Math.Max(table.Count - _options.TableCapacity, _options.TableCapacity / 10)).Select(kv => kv.Key).ToArray();
        foreach (var k in drop) table.Remove(k);
    }

    private sealed class PathState(double timestamp, byte[] receivedFrom, byte[]? transportId, int hops, double expires, List<byte[]> blobs, IPacketInterface? source, byte[] packetHash, Packet packet)
    {
        public double Timestamp { get; set; } = timestamp;
        public Packet Packet { get; } = packet;
        public byte[] ReceivedFrom { get; } = receivedFrom;
        public byte[]? TransportId { get; } = transportId;
        public int Hops { get; } = hops;
        public double Expires { get; } = expires;
        public List<byte[]> RandomBlobs { get; } = blobs;
        public IPacketInterface? Source { get; } = source;
        public byte[] PacketHash { get; } = packetHash;
    }

    private sealed class TableEntry(double timestamp, double retransmitAt, byte[] receivedFrom, int hops, Packet packet, IPacketInterface? source)
    {
        public IPacketInterface? Attached { get; init; }
        public bool Blocked { get; init; }
        public double Timestamp { get; } = timestamp;
        public double RetransmitAt { get; set; } = retransmitAt;
        public int Retries { get; set; }
        public byte[] ReceivedFrom { get; } = receivedFrom;
        public int Hops { get; } = hops;
        public Packet Packet { get; } = packet;
        public int LocalRebroadcasts { get; set; }
        public IPacketInterface? Source { get; } = source;
    }

    private sealed class RateEntry
    {
        public double Last { get; set; }
        public int Violations { get; set; }
        public double BlockedUntil { get; set; }
        public List<double> Timestamps { get; init; } = [];
    }
}
