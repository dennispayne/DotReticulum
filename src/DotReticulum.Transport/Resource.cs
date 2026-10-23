using System.Security.Cryptography;
using DotReticulum.Core;

namespace DotReticulum.Transport;

public enum ResourceStatus : byte
{
    None = 0x00,
    Queued = 0x01,
    Advertised = 0x02,
    Transferring = 0x03,
    AwaitingProof = 0x04,
    Assembling = 0x05,
    Complete = 0x06,
    Failed = 0x07,
    Corrupt = 0x08,
    Rejected = 0x09,
}

/// <summary>A response that is sent as a Resource with metadata (upstream file responses).</summary>
public sealed record FileResponse(byte[] Data, object? Metadata = null);

/// <summary>Upstream <c>ResourceAdvertisement</c>: the msgpack dictionary that announces a Resource (and its first hashmap segment).</summary>
public sealed class ResourceAdvertisement
{
    public const int Overhead = 134;
    public static readonly int HashmapMaxLength = (Link.ComputeMdu(Packet.Mtu) - Overhead) / Resource.MapHashLength;
    public static readonly int CollisionGuardSize = (2 * Resource.WindowMax) + HashmapMaxLength;

    public long TransferSize { get; init; }
    public long DataSize { get; init; }
    public int Parts { get; init; }
    public byte[] Hash { get; init; } = [];
    public byte[] RandomHash { get; init; } = [];
    public byte[] OriginalHash { get; init; } = [];
    public byte[] Hashmap { get; init; } = [];
    public int Flags { get; init; }
    public int SegmentIndex { get; init; }
    public int TotalSegments { get; init; }
    public byte[]? RequestId { get; init; }

    public bool Encrypted => (Flags & 0x01) != 0;
    public bool Compressed => (Flags & 0x02) != 0;
    public bool Split => (Flags & 0x04) != 0;
    public bool IsRequest => (Flags & 0x08) != 0;
    public bool IsResponse => (Flags & 0x10) != 0;
    public bool HasMetadata => (Flags & 0x20) != 0;

    /// <summary>The link the advertisement arrived on (set for the accept-app callback).</summary>
    public Link? Link { get; internal set; }

    public static int BuildFlags(bool encrypted, bool compressed, bool split, bool isRequest, bool isResponse, bool hasMetadata) =>
        (hasMetadata ? 0x20 : 0) | (isResponse ? 0x10 : 0) | (isRequest ? 0x08 : 0) | (split ? 0x04 : 0) | (compressed ? 0x02 : 0) | (encrypted ? 0x01 : 0);

    /// <summary>Packs the advertisement with the given hashmap segment (upstream <c>pack(segment)</c>); <paramref name="fullHashmap"/> holds every map hash.</summary>
    public byte[] Pack(ReadOnlySpan<byte> fullHashmap, int segment = 0)
    {
        var start = segment * HashmapMaxLength;
        var end = Math.Min((segment + 1) * HashmapMaxLength, Parts);
        var slice = start < end ? fullHashmap[(start * Resource.MapHashLength)..(end * Resource.MapHashLength)].ToArray() : [];
        return MsgPack.Pack(new List<KeyValuePair<object, object?>>
        {
            new("t", TransferSize),
            new("d", DataSize),
            new("n", (long)Parts),
            new("h", Hash),
            new("r", RandomHash),
            new("o", OriginalHash),
            new("i", (long)SegmentIndex),
            new("l", (long)TotalSegments),
            new("q", RequestId),
            new("f", (long)Flags),
            new("m", slice),
        });
    }

    public static ResourceAdvertisement Unpack(ReadOnlySpan<byte> data)
    {
        if (MsgPack.Unpack(data) is not Dictionary<object, object?> d)
        {
            throw new InvalidDataException("Resource advertisement is not a map.");
        }

        long Int(string key) => MsgPack.ToInt64(d[key] ?? throw new InvalidDataException($"Missing {key}"));
        byte[] Bytes(string key) => d[key] as byte[] ?? throw new InvalidDataException($"Missing {key}");

        var adv = new ResourceAdvertisement
        {
            TransferSize = Int("t"),
            DataSize = Int("d"),
            Parts = checked((int)Int("n")),
            Hash = Bytes("h"),
            RandomHash = Bytes("r"),
            OriginalHash = Bytes("o"),
            Hashmap = Bytes("m"),
            Flags = (int)Int("f"),
            SegmentIndex = (int)Int("i"),
            TotalSegments = (int)Int("l"),
            RequestId = d["q"] as byte[],
        };
        if (adv.TransferSize > (long)Resource.MaxEfficientSize * 3)
        {
            throw new InvalidDataException("Invalid transfer size");
        }

        if (adv.Hash.Length != 32 || adv.RandomHash.Length != Resource.RandomHashSize || adv.OriginalHash.Length != 32 || adv.Parts < 0)
        {
            throw new InvalidDataException("Invalid resource advertisement fields");
        }

        return adv;
    }
}

/// <summary>
/// Upstream <c>Resource</c>: reliable transfer of arbitrarily large data over a <see cref="Link"/>: optional bz2 compression, whole-stream link encryption,
/// per-part map hashes, a sliding request window, hashmap updates, a proof, cancel/reject and segmentation above <see cref="MaxEfficientSize"/>.
/// All state is guarded by the owning link's lock; callbacks are delivered in order through the link manager.
/// </summary>
public sealed class Resource
{
    public const int Window = 4;
    public const int WindowMin = 2;
    public const int WindowMaxSlow = 10;
    public const int WindowMaxVerySlow = 4;
    public const int WindowMaxFast = 75;
    public const int WindowMax = WindowMaxFast;
    public const int FastRateThreshold = WindowMaxSlow - Window - 2;
    public const int VerySlowRateThreshold = 2;
    public const double RateFast = 50 * 1000 / 8.0;
    public const double RateVerySlow = 2 * 1000 / 8.0;
    public const int WindowFlexibility = 4;
    public const int MapHashLength = 4;
    public const int RandomHashSize = 4;
    public const int MaxEfficientSize = (1024 * 1024) - 1;
    public const int MetadataMaxSize = (16 * 1024 * 1024) - 1;
    public const long AutoCompressMaxSize = 64L * 1024 * 1024;
    public const double PartTimeoutFactor = 4;
    public const double PartTimeoutFactorAfterRtt = 2;
    public const double ProofTimeoutFactor = 3;
    public const double HmuWaitFactor = 3.5;
    public const int MaxRetries = 16;
    public const int MaxAdvRetries = 4;
    public const double SenderGraceTime = 10;
    public const double ProcessingGrace = 1;
    public const double RetryGraceTime = 0.25;
    public const double PerRetryDelay = 0.5;
    private const byte HashmapNotExhausted = 0x00;
    private const byte HashmapExhausted = 0xFF;
    private const int HeaderMaxSize = 35;
    private const int IfacMinSize = 1;

    private readonly Link _link;
    private readonly int _sdu;

    // sender
    private byte[] _source = [];
    private byte[] _metadataBlock = [];
    private int _sentMetadataSize;
    private bool _autoCompressOption;
    private long _autoCompressLimit;
    private byte[] _expectedProof = [];
    private byte[] _hashmapFlat = [];
    private Packet[] _partPackets = [];
    private byte[][] _partMapHashes = [];
    private bool[] _partSent = [];
    private int _sentParts;
    private int _receiverMinConsecutiveHeight;
    private double _advSent;
    private double _lastPartSent;
    private readonly HashSet<string> _requestHashes = [];
    private Task<Resource>? _nextSegment;
    private Action<Resource>? _progressCallback;

    // receiver
    private byte[]?[] _parts = [];
    private byte[]?[] _hashmap = [];
    private int _hashmapHeight;
    private int _consecutiveCompleted = -1;
    private int _receivedCount;
    private int _outstanding;
    private bool _waitingForHmu;
    private bool _assembling;
    private int _window = Window;
    private int _windowMax = WindowMaxSlow;
    private int _windowMin = WindowMin;
    private int _windowFlexibility = WindowFlexibility;
    private double _reqSent;
    private int _reqSentBytes;
    private double? _reqResp;
    private long _rttRxdBytes;
    private long _rttRxdBytesAtPartReq;
    private double _reqRespRttRate;
    private double _reqDataRttRate;
    private double? _previousEifr;
    private int _fastRateRounds;
    private int _verySlowRateRounds;
    private double _partTimeoutFactor = PartTimeoutFactor;
    private ResourceAdvertisement? _advertisement;
    private SegmentAccumulator? _accumulator;

    private double _lastActivity;
    private int _retriesLeft = MaxRetries;
    private double _timeoutFactor;

    internal sealed class SegmentAccumulator
    {
        public MemoryStream Data { get; } = new();
        public byte[]? PackedMetadata { get; set; }
    }

    private Resource(Link link)
    {
        _link = link;
        _sdu = Math.Min(link.Mtu, Packet.Mtu) - HeaderMaxSize - IfacMinSize;
        _timeoutFactor = Link.TrafficTimeoutFactor;
    }

    public Link Link => _link;
    public ResourceStatus Status { get; private set; } = ResourceStatus.None;
    public bool IsInitiator { get; private set; }
    public byte[] Hash { get; private set; } = [];
    public byte[] OriginalHash { get; private set; } = [];
    public byte[] RandomHash { get; private set; } = [];
    public byte[]? RequestId { get; private set; }
    public bool IsResponse { get; private set; }

    /// <summary>Bytes on the wire for this segment (encrypted stream size).</summary>
    public long Size { get; private set; }

    /// <summary>Total uncompressed size of all segments including metadata.</summary>
    public long TotalSize { get; private set; }

    public int TotalParts { get; private set; }
    public int SegmentIndex { get; private set; } = 1;
    public int TotalSegments { get; private set; } = 1;
    public bool Compressed { get; private set; }
    public bool Encrypted { get; private set; } = true;
    public bool Split { get; private set; }
    public bool HasMetadata { get; private set; }
    public double? Rtt { get; private set; }
    public double? Eifr { get; private set; }

    /// <summary>The received payload once <see cref="Status"/> is Complete (all segments joined; metadata stripped).</summary>
    public byte[]? Data { get; private set; }

    /// <summary>The unpacked metadata of a received resource, or null.</summary>
    public object? Metadata { get; private set; }

    public Action<Resource>? Concluded { get; set; }

    public Action<Resource>? ProgressChanged
    {
        get => _progressCallback;
        set => _progressCallback = value;
    }

    public int WindowSize => _window;
    public int WindowMaxSize => _windowMax;
    internal double StartedTransferring { get; private set; }

    public double Progress
    {
        get
        {
            lock (_link.Gate) return ComputeProgress();
        }
    }

    // ------------------------------------------------------------------ sender

    /// <summary>Builds an outgoing resource segment (upstream <c>Resource.__init__</c> with data) without advertising it.</summary>
    internal static Resource CreateOutgoing(Link link, byte[] source, object? metadata, bool autoCompress, Action<Resource>? callback,
        Action<Resource>? progress, double? timeout, int segmentIndex = 1, byte[]? originalHash = null, byte[]? requestId = null,
        bool isResponse = false, int sentMetadataSize = 0, long autoCompressLimit = AutoCompressMaxSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (segmentIndex == 1 && callback is { } concluded)
        {
            var completionPosted = 0;
            callback = resource =>
            {
                if (Interlocked.Exchange(ref completionPosted, 1) == 0) concluded(resource);
            };
        }
        var r = new Resource(link)
        {
            IsInitiator = true,
            RequestId = requestId,
            IsResponse = isResponse,
            Concluded = callback,
            _progressCallback = progress,
            _source = source,
            _autoCompressOption = autoCompress,
            _autoCompressLimit = autoCompressLimit,
            _sentMetadataSize = sentMetadataSize,
        };

        var metadataSize = sentMetadataSize;
        if (metadata is not null)
        {
            var packed = MsgPack.Pack(metadata);
            if (packed.Length > MetadataMaxSize)
            {
                throw new InvalidOperationException("Resource metadata size exceeded");
            }

            r._metadataBlock = [(byte)(packed.Length >> 16), (byte)(packed.Length >> 8), (byte)packed.Length, .. packed];
            metadataSize = r._metadataBlock.Length;
            r._sentMetadataSize = metadataSize;
            r.HasMetadata = true;
        }
        else if (sentMetadataSize > 0)
        {
            r.HasMetadata = true;
        }

        long dataSize = source.Length;
        r.TotalSize = dataSize + metadataSize;
        ReadOnlySpan<byte> slice;
        if (r.TotalSize <= MaxEfficientSize)
        {
            r.TotalSegments = 1;
            r.SegmentIndex = 1;
            r.Split = false;
            slice = source;
        }
        else
        {
            r.TotalSegments = (int)(((r.TotalSize - 1) / MaxEfficientSize) + 1);
            r.SegmentIndex = segmentIndex;
            r.Split = true;
            long firstRead = MaxEfficientSize - metadataSize;
            if (segmentIndex == 1)
            {
                slice = source.AsSpan(0, (int)Math.Min(firstRead, dataSize));
            }
            else
            {
                var seek = firstRead + ((segmentIndex - 2L) * MaxEfficientSize);
                slice = source.AsSpan((int)seek, (int)Math.Min(MaxEfficientSize, dataSize - seek));
            }
        }

        var data = r.HasMetadata ? [.. r._metadataBlock, .. slice] : slice.ToArray();
        var timeoutValue = timeout ?? ((link.Rtt ?? 0) * Link.TrafficTimeoutFactor);
        r.Timeout = timeoutValue;

        byte[] stream;
        if (autoCompress && dataSize <= autoCompressLimit)
        {
            byte[] compressed;
            try
            {
                compressed = Bz2.Compress(data);
            }
            catch (Exception ex)
            {
                link.Log("Resource compression failed; sending uncompressed: " + ex);
                compressed = data;
            }

            if (compressed.Length < data.Length)
            {
                stream = [.. RandomNumberGenerator.GetBytes(RandomHashSize), .. compressed];
                r.Compressed = true;
            }
            else
            {
                stream = [.. RandomNumberGenerator.GetBytes(RandomHashSize), .. data];
            }
        }
        else
        {
            stream = [.. RandomNumberGenerator.GetBytes(RandomHashSize), .. data];
        }

        var encrypted = link.Encrypt(stream);
        r.Encrypted = true;
        r.Size = encrypted.Length;
        r.TotalParts = (int)Math.Ceiling(encrypted.Length / (double)r._sdu);

        while (true)
        {
            r.RandomHash = RandomNumberGenerator.GetBytes(RandomHashSize);
            r.Hash = SHA256.HashData([.. data, .. r.RandomHash]);
            r._expectedProof = SHA256.HashData([.. data, .. r.Hash]);
            r.OriginalHash = originalHash ?? r.Hash;

            var packets = new Packet[r.TotalParts];
            var hashes = new byte[r.TotalParts][];
            var guard = new List<byte[]>();
            var collision = false;
            for (var i = 0; i < r.TotalParts; i++)
            {
                var partData = encrypted.AsSpan(i * r._sdu, Math.Min(r._sdu, encrypted.Length - (i * r._sdu))).ToArray();
                var mapHash = r.MapHash(partData);
                if (guard.Any(g => g.AsSpan().SequenceEqual(mapHash)))
                {
                    collision = true;
                    break;
                }

                guard.Add(mapHash);
                if (guard.Count > ResourceAdvertisement.CollisionGuardSize)
                {
                    guard.RemoveAt(0);
                }

                packets[i] = Packet.Create(PacketType.Data, DestinationType.Link, link.LinkId, partData, LinkContext.Resource);
                hashes[i] = mapHash;
            }

            if (collision)
            {
                continue;
            }

            r._partPackets = packets;
            r._partMapHashes = hashes;
            r._partSent = new bool[r.TotalParts];
            r._hashmapFlat = [.. hashes.SelectMany(h => h)];
            break;
        }

        return r;
    }

    public double Timeout { get; private set; }

    internal byte[] MapHash(ReadOnlySpan<byte> partData) => MapHash(partData, RandomHash);

    internal static byte[] MapHash(ReadOnlySpan<byte> partData, ReadOnlySpan<byte> randomHash) => SHA256.HashData([.. partData, .. randomHash])[..MapHashLength];

    internal static byte[] ComputeHash(ReadOnlySpan<byte> data, ReadOnlySpan<byte> randomHash) => SHA256.HashData([.. data, .. randomHash]);

    internal static byte[] ComputeExpectedProof(ReadOnlySpan<byte> data, ReadOnlySpan<byte> hash) => SHA256.HashData([.. data, .. hash]);

    private ResourceAdvertisement BuildAdvertisement() => new()
    {
        TransferSize = Size,
        DataSize = TotalSize,
        Parts = TotalParts,
        Hash = Hash,
        RandomHash = RandomHash,
        OriginalHash = OriginalHash,
        Flags = ResourceAdvertisement.BuildFlags(Encrypted, Compressed, Split, RequestId is not null && !IsResponse, RequestId is not null && IsResponse, HasMetadata),
        SegmentIndex = SegmentIndex,
        TotalSegments = TotalSegments,
        RequestId = RequestId,
    };

    /// <summary>Advertises the resource, or queues it behind the link's current outgoing resource.</summary>
    internal void Advertise()
    {
        if (_link.HasOutgoingResource)
        {
            Status = ResourceStatus.Queued;
            _link.QueueResource(this);
            return;
        }

        SendAdvertisement(first: true);
        if (SegmentIndex < TotalSegments)
        {
            _nextSegment = Task.Run(() => CreateOutgoing(_link, _source, null, _autoCompressOption, Concluded, _progressCallback, null,
                SegmentIndex + 1, OriginalHash, RequestId, IsResponse, _sentMetadataSize, _autoCompressLimit));
        }
    }

    internal void StartQueued() => SendAdvertisement(first: true);

    private void SendAdvertisement(bool first)
    {
        try
        {
            if (_link.Status != LinkStatus.Active)
            {
                CancelLocked();
                return;
            }

            var payload = BuildAdvertisement().Pack(_hashmapFlat);
            _link.SendWithContext(payload, LinkContext.ResourceAdv, createReceipt: false, encrypt: true);
            _lastActivity = _link.Now;
            _advSent = _lastActivity;
            if (first)
            {
                StartedTransferring = _lastActivity;
                Rtt = null;
                Status = ResourceStatus.Advertised;
                _retriesLeft = MaxAdvRetries;
                _link.RegisterOutgoing(this);
            }
        }
        catch (Exception ex)
        {
            _link.Log($"Could not advertise resource: {ex.Message}");
            CancelLocked();
        }
    }

    /// <summary>Upstream <c>Resource.request</c>: the receiver asked for more parts (and possibly a hashmap update).</summary>
    internal void HandleRequest(byte[] request)
    {
        if (Status == ResourceStatus.Failed)
        {
            return;
        }

        var now = _link.Now;
        Rtt ??= now - _advSent;
        if (Status != ResourceStatus.Transferring)
        {
            Status = ResourceStatus.Transferring;
        }

        _retriesLeft = MaxRetries;
        var wantsMore = request[0] == HashmapExhausted;
        var pad = wantsMore ? 1 + MapHashLength : 1;
        if (request.Length < pad + 32)
        {
            return;
        }

        var requested = request.AsSpan(pad + 32);
        var wanted = new List<byte[]>();
        for (var i = 0; i < requested.Length / MapHashLength; i++)
        {
            wanted.Add(requested.Slice(i * MapHashLength, MapHashLength).ToArray());
        }

        var searchStart = _receiverMinConsecutiveHeight;
        var searchEnd = Math.Min(_receiverMinConsecutiveHeight + ResourceAdvertisement.CollisionGuardSize, _partPackets.Length);
        for (var i = searchStart; i < searchEnd; i++)
        {
            if (!wanted.Any(w => w.AsSpan().SequenceEqual(_partMapHashes[i])))
            {
                continue;
            }

            if (_link.Status != LinkStatus.Active)
            {
                CancelLocked();
                return;
            }

            _link.SendPrebuilt(_partPackets[i]);
            if (!_partSent[i])
            {
                _partSent[i] = true;
                _sentParts++;
            }

            _lastActivity = _link.Now;
            _lastPartSent = _lastActivity;
        }

        if (wantsMore)
        {
            var lastMapHash = request.AsSpan(1, MapHashLength);
            var partIndex = _receiverMinConsecutiveHeight;
            for (var i = searchStart; i < searchEnd; i++)
            {
                partIndex++;
                if (_partMapHashes[i].AsSpan().SequenceEqual(lastMapHash))
                {
                    break;
                }
            }

            _receiverMinConsecutiveHeight = Math.Max(partIndex - 1 - WindowMax, 0);
            if (partIndex % ResourceAdvertisement.HashmapMaxLength != 0)
            {
                _link.Log("Resource sequencing error, cancelling transfer!");
                CancelLocked();
                return;
            }

            var segment = partIndex / ResourceAdvertisement.HashmapMaxLength;
            var start = segment * ResourceAdvertisement.HashmapMaxLength;
            var end = Math.Min((segment + 1) * ResourceAdvertisement.HashmapMaxLength, _partPackets.Length);
            if (end <= start)
            {
                _link.Log("Resource HMU error, cancelling transfer!");
                CancelLocked();
                return;
            }

            var hashmap = _hashmapFlat[(start * MapHashLength)..(end * MapHashLength)];
            byte[] hmu = [.. Hash, .. MsgPack.Pack(new object?[] { (long)segment, hashmap })];
            _link.SendWithContext(hmu, LinkContext.ResourceHmu, createReceipt: false, encrypt: true);
            _lastActivity = _link.Now;
        }

        if (_sentParts == _partPackets.Length)
        {
            Status = ResourceStatus.AwaitingProof;
            _retriesLeft = 3;
        }

        PostProgress();
    }

    internal bool HandleRequestPacketOnce(string packetHash) => _requestHashes.Add(packetHash);

    /// <summary>Upstream <c>validate_proof</c>.</summary>
    internal void ValidateProof(ReadOnlySpan<byte> proof)
    {
        if (Status >= ResourceStatus.Complete || proof.Length != 64 || !proof[32..].SequenceEqual(_expectedProof))
        {
            return;
        }

        Status = ResourceStatus.Complete;
        if (SegmentIndex == TotalSegments)
        {
            _link.ResourceConcluded(this);
            var callback = Concluded;
            if (callback is not null)
            {
                _link.Post(() => callback(this));
            }

            return;
        }

        var next = _nextSegment ?? Task.Run(() => CreateOutgoing(_link, _source, null, _autoCompressOption, Concluded, _progressCallback, null,
            SegmentIndex + 1, OriginalHash, RequestId, IsResponse, _sentMetadataSize, _autoCompressLimit));
        _ = next.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                lock (_link.Gate) CancelLocked();
                _link.Log("Preparing the next resource segment failed: " + t.Exception?.GetBaseException().Message);
                return;
            }

            lock (_link.Gate)
            {
                _link.ResourceConcluded(this);
                if (_link.Status == LinkStatus.Active && Status == ResourceStatus.Complete)
                {
                    t.Result.Advertise();
                }
                else t.Result.CancelLocked();
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Upstream <c>_rejected</c>: the receiver refused the advertisement.</summary>
    internal void Rejected()
    {
        if (Status >= ResourceStatus.Complete || !IsInitiator)
        {
            return;
        }

        Status = ResourceStatus.Rejected;
        _link.CancelOutgoing(this);
        _link.ResourceConcluded(this);
        var cb = Concluded;
        if (cb is not null)
        {
            _link.Post(() => cb(this));
        }
    }

    // ---------------------------------------------------------------- receiver

    /// <summary>Upstream <c>Resource.reject</c>: refuses an advertisement.</summary>
    internal static void Reject(Link link, ResourceAdvertisement adv)
    {
        link.SendWithContext(adv.Hash, LinkContext.ResourceRcl, createReceipt: false, encrypt: true);
    }

    /// <summary>Upstream <c>Resource.accept</c>: starts receiving an advertised resource. Returns null when it is a duplicate or invalid.</summary>
    internal static Resource? Accept(Link link, ResourceAdvertisement adv, Action<Resource>? callback, Action<Resource>? progress, byte[]? requestId)
    {
        try
        {
            var r = new Resource(link)
            {
                Status = ResourceStatus.Transferring,
                IsInitiator = false,
                Concluded = callback,
                _progressCallback = progress,
                RequestId = requestId,
                _advertisement = adv,
                Size = adv.TransferSize,
                TotalSize = adv.DataSize,
                Hash = adv.Hash,
                OriginalHash = adv.OriginalHash,
                RandomHash = adv.RandomHash,
                Encrypted = adv.Encrypted,
                Compressed = adv.Compressed,
                HasMetadata = adv.HasMetadata,
                SegmentIndex = adv.SegmentIndex,
                TotalSegments = adv.TotalSegments,
                Split = adv.TotalSegments > 1,
                IsResponse = adv.IsResponse,
            };
            r.TotalParts = (int)Math.Ceiling(r.Size / (double)r._sdu);
            if (r.TotalParts != adv.Parts || r.TotalParts <= 0)
            {
                return null;
            }

            r._parts = new byte[]?[r.TotalParts];
            r._hashmap = new byte[]?[r.TotalParts];
            r._lastActivity = link.Now;
            r.StartedTransferring = r._lastActivity;
            if (link.LastResourceWindow is { } w)
            {
                r._window = w;
            }

            r._previousEifr = link.LastResourceEifr;
            if (link.HasIncomingResource(r.Hash))
            {
                link.Log("Ignoring resource advertisement, resource already transferring");
                return null;
            }

            if (r.TotalSegments > 1)
            {
                r._accumulator = link.SegmentAccumulatorFor(r.OriginalHash);
            }

            link.RegisterIncoming(r);
            link.NotifyResourceStarted(r);
            r.HashmapUpdate(0, adv.Hashmap);
            return r;
        }
        catch (Exception ex)
        {
            link.Log($"Could not decode resource advertisement, dropping resource: {ex.Message}");
            return null;
        }
    }

    internal void HashmapUpdatePacket(ReadOnlySpan<byte> plaintext)
    {
        if (Status == ResourceStatus.Failed || !_waitingForHmu)
        {
            return;
        }

        _lastActivity = _link.Now;
        _retriesLeft = MaxRetries;
        if (MsgPack.Unpack(plaintext[32..]) is object?[] { Length: >= 2 } update && update[1] is byte[] hashmap)
        {
            HashmapUpdate((int)MsgPack.ToInt64(update[0]), hashmap);
        }
    }

    private void HashmapUpdate(int segment, byte[] hashmap)
    {
        if (Status == ResourceStatus.Failed)
        {
            return;
        }

        Status = ResourceStatus.Transferring;
        var hashes = hashmap.Length / MapHashLength;
        if (hashes < 1 || ((long)segment * ResourceAdvertisement.HashmapMaxLength) + hashes > _hashmap.Length)
        {
            _link.Log("Invalid HMU received, cancelling transfer");
            CancelLocked();
            return;
        }

        for (var i = 0; i < hashes; i++)
        {
            var index = i + (segment * ResourceAdvertisement.HashmapMaxLength);
            if (_hashmap[index] is null)
            {
                _hashmapHeight++;
            }

            _hashmap[index] = hashmap.AsSpan(i * MapHashLength, MapHashLength).ToArray();
        }

        _waitingForHmu = false;
        RequestNext();
    }

    private void UpdateEifr()
    {
        var rtt = Rtt ?? _link.Rtt ?? 0.5;
        double inflight;
        if (_reqDataRttRate != 0)
        {
            inflight = _reqDataRttRate * 8;
        }
        else if (_previousEifr is { } prev)
        {
            inflight = prev;
        }
        else
        {
            inflight = _link.EstablishmentCost * 8 / Math.Max(rtt, 0.0001);
        }

        Eifr = Math.Max(inflight, 1);
        _link.ExpectedRate = Eifr;
    }

    /// <summary>Upstream <c>receive_part</c>.</summary>
    internal void ReceivePart(Packet packet, int rawLength)
    {
        if (Status >= ResourceStatus.Assembling)
        {
            return;
        }

        var now = _link.Now;
        _lastActivity = now;
        _retriesLeft = MaxRetries;
        if (_reqResp is null)
        {
            _reqResp = now;
            var rtt = now - _reqSent;
            _partTimeoutFactor = PartTimeoutFactorAfterRtt;
            if (Rtt is null)
            {
                Rtt = _link.Rtt;
            }
            else if (rtt < Rtt)
            {
                Rtt = Math.Max(Rtt.Value - (Rtt.Value * 0.05), rtt);
            }
            else if (rtt > Rtt)
            {
                Rtt = Math.Min(Rtt.Value + (Rtt.Value * 0.05), rtt);
            }

            if (rtt > 0)
            {
                var cost = rawLength + _reqSentBytes;
                _reqRespRttRate = cost / rtt;
                if (_reqRespRttRate > RateFast && _fastRateRounds < FastRateThreshold)
                {
                    _fastRateRounds++;
                    if (_fastRateRounds == FastRateThreshold)
                    {
                        _windowMax = WindowMaxFast;
                    }
                }
            }
        }

        var partData = packet.Payload.ToArray();
        var partHash = MapHash(partData);
        var searchStart = _consecutiveCompleted + 1;
        var searchEnd = Math.Min(searchStart + _window, _hashmap.Length);
        for (var i = searchStart; i < searchEnd; i++)
        {
            if (_hashmap[i] is { } h && h.AsSpan().SequenceEqual(partHash) && _parts[i] is null)
            {
                _parts[i] = partData;
                _rttRxdBytes += partData.Length;
                _receivedCount++;
                _outstanding--;
                var cp = _consecutiveCompleted + 1;
                while (cp < _parts.Length && _parts[cp] is not null)
                {
                    _consecutiveCompleted = cp;
                    cp++;
                }

                PostProgress();
            }
        }

        if (_receivedCount == TotalParts && !_assembling)
        {
            _assembling = true;
            Status = ResourceStatus.Assembling;
            _ = Task.Run(Assemble);
        }
        else if (_outstanding == 0)
        {
            if (_window < _windowMax)
            {
                _window++;
                if (_window - _windowMin > WindowFlexibility - 1)
                {
                    _windowMin++;
                }
            }

            if (_reqSent != 0)
            {
                var rtt = now - _reqSent;
                var transferred = _rttRxdBytes - _rttRxdBytesAtPartReq;
                if (rtt != 0)
                {
                    _reqDataRttRate = transferred / rtt;
                    UpdateEifr();
                    _rttRxdBytesAtPartReq = _rttRxdBytes;
                    if (_reqDataRttRate > RateFast && _fastRateRounds < FastRateThreshold)
                    {
                        _fastRateRounds++;
                        if (_fastRateRounds == FastRateThreshold)
                        {
                            _windowMax = WindowMaxFast;
                        }
                    }

                    if (_fastRateRounds == 0 && _reqDataRttRate < RateVerySlow && _verySlowRateRounds < VerySlowRateThreshold)
                    {
                        _verySlowRateRounds++;
                        if (_verySlowRateRounds == VerySlowRateThreshold)
                        {
                            _windowMax = WindowMaxVerySlow;
                        }
                    }
                }
            }

            RequestNext();
        }
    }

    /// <summary>Upstream <c>request_next</c>: asks the sender for the next window of parts.</summary>
    private void RequestNext()
    {
        if (Status == ResourceStatus.Failed || _waitingForHmu)
        {
            return;
        }

        _outstanding = 0;
        var exhausted = HashmapNotExhausted;
        var requested = new List<byte>();
        var i = 0;
        var pn = _consecutiveCompleted + 1;
        var end = Math.Min(pn + _window, _parts.Length);
        for (var index = pn; index < end; index++)
        {
            if (_parts[index] is null)
            {
                if (_hashmap[index] is { } hash)
                {
                    requested.AddRange(hash);
                    _outstanding++;
                    i++;
                }
                else
                {
                    exhausted = HashmapExhausted;
                }
            }

            if (i >= _window || exhausted == HashmapExhausted)
            {
                break;
            }
        }

        List<byte> payload = [exhausted];
        if (exhausted == HashmapExhausted)
        {
            payload.AddRange(_hashmap[_hashmapHeight - 1]!);
            _waitingForHmu = true;
        }

        payload.AddRange(Hash);
        payload.AddRange(requested);
        if (_link.Status != LinkStatus.Active)
        {
            CancelLocked();
            return;
        }

        _link.SendWithContext([.. payload], LinkContext.ResourceReq, createReceipt: false, encrypt: true);
        _lastActivity = _link.Now;
        _reqSent = _lastActivity;
        _reqSentBytes = Link.OverheadOfEncrypted(payload.Count);
        _rttRxdBytesAtPartReq = _rttRxdBytes;
        _reqResp = null;
    }

    /// <summary>Upstream <c>assemble</c>; runs off the link lock for the heavy work.</summary>
    private void Assemble()
    {
        byte[]? result = null;
        var corrupt = false;
        var oversize = false;
        try
        {
            var stream = new byte[Size];
            var offset = 0;
            foreach (var p in _parts)
            {
                p!.CopyTo(stream, offset);
                offset += p.Length;
            }

            var plain = Encrypted ? _link.Decrypt(stream) : stream;
            if (plain is null || plain.Length < RandomHashSize)
            {
                corrupt = true;
            }
            else
            {
                var body = plain.AsSpan(RandomHashSize).ToArray();
                if (Compressed)
                {
                    try
                    {
                        body = Bz2.Decompress(body, AutoCompressMaxSize);
                    }
                    catch (InvalidDataException)
                    {
                        oversize = true;
                    }
                }

                if (!oversize)
                {
                    if (SHA256.HashData([.. body, .. RandomHash]).AsSpan().SequenceEqual(Hash))
                    {
                        result = body;
                    }
                    else
                    {
                        corrupt = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _link.Log("Error while assembling received resource: " + ex.Message);
            corrupt = true;
        }

        lock (_link.Gate)
        {
            if (Status == ResourceStatus.Failed)
            {
                return;
            }

            if (oversize || corrupt || result is null)
            {
                Status = ResourceStatus.Corrupt;
                if (oversize)
                {
                    CancelLocked();
                    return;
                }
            }
            else
            {
                byte[] payload = result;
                if (HasMetadata && SegmentIndex == 1)
                {
                    var metadataSize = (result[0] << 16) | (result[1] << 8) | result[2];
                    var packed = result.AsSpan(3, metadataSize).ToArray();
                    if (_accumulator is not null)
                    {
                        _accumulator.PackedMetadata = packed;
                    }
                    else
                    {
                        Metadata = MsgPack.Unpack(packed);
                    }

                    payload = result.AsSpan(3 + metadataSize).ToArray();
                }

                Status = ResourceStatus.Complete;
                byte[] proof = [.. Hash, .. SHA256.HashData([.. result, .. Hash])];
                _link.SendResourceProof(Packet.Create(PacketType.Proof, DestinationType.Link, _link.LinkId, proof, LinkContext.ResourcePrf));

                if (_accumulator is not null)
                {
                    _accumulator.Data.Write(payload);
                    if (SegmentIndex == TotalSegments)
                    {
                        Data = _accumulator.Data.ToArray();
                        if (_accumulator.PackedMetadata is { } pm)
                        {
                            Metadata = MsgPack.Unpack(pm);
                        }

                        _link.ForgetSegments(OriginalHash);
                    }
                }
                else
                {
                    Data = payload;
                }
            }

            _link.ResourceConcluded(this);
            if (SegmentIndex == TotalSegments || Status == ResourceStatus.Corrupt)
            {
                var cb = Concluded;
                if (cb is not null)
                {
                    _link.Post(() => cb(this));
                }
            }
            else
            {
                _link.Log($"Resource segment {SegmentIndex} of {TotalSegments} received, waiting for next segment");
            }
        }
    }

    // --------------------------------------------------------------- lifecycle

    /// <summary>Cancels the transfer (upstream <c>cancel</c>).</summary>
    public void Cancel()
    {
        lock (_link.Gate) CancelLocked();
    }

    internal void CancelLocked()
    {
        if (_nextSegment is { } next)
        {
            _nextSegment = null;
            if (next.IsCompletedSuccessfully) next.Result.CancelLocked();
            else _ = next.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) t.Result.Cancel();
                else if (t.IsFaulted) _ = t.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        if (Status == ResourceStatus.Corrupt)
        {
            _link.CancelIncoming(this);
            if (_advertisement is not null && _link.Status == LinkStatus.Active)
            {
                Reject(_link, _advertisement);
            }

            _link.Teardown();
        }
        else if (Status < ResourceStatus.Complete || (IsInitiator && Status == ResourceStatus.Complete && SegmentIndex < TotalSegments))
        {
            Status = ResourceStatus.Failed;
            if (_accumulator is not null)
            {
                _link.ForgetSegments(OriginalHash);
                _accumulator = null;
            }
            if (_link.Status == LinkStatus.Active)
            {
                _link.SendWithContext(Hash, IsInitiator ? LinkContext.ResourceIcl : LinkContext.ResourceRcl, createReceipt: false, encrypt: true);
            }

            if (IsInitiator)
            {
                _link.CancelOutgoing(this);
            }
            else
            {
                _link.CancelIncoming(this);
            }

            _link.ResourceConcluded(this);
            var cb = Concluded;
            if (cb is not null)
            {
                _link.Post(() => cb(this));
            }
        }
    }

    /// <summary>The sender cancelled; the receiver echoes a reject like upstream.</summary>
    internal void CancelledByPeer() => CancelLocked();

    /// <summary>Upstream <c>__watchdog_job</c> body, evaluated on every link tick.</summary>
    internal void Tick(double now)
    {
        switch (Status)
        {
            case ResourceStatus.Advertised:
                if (now > _advSent + Timeout + ProcessingGrace)
                {
                    if (_retriesLeft <= 0)
                    {
                        CancelLocked();
                    }
                    else
                    {
                        _retriesLeft--;
                        SendAdvertisement(first: false);
                    }
                }

                break;

            case ResourceStatus.Transferring when !IsInitiator:
                {
                    var retriesUsed = MaxRetries - _retriesLeft;
                    var extraWait = retriesUsed * PerRetryDelay;
                    UpdateEifr();
                    var eifr = Eifr!.Value;
                    var hmuWait = _waitingForHmu || _outstanding == 0 ? _sdu * 8 * HmuWaitFactor / eifr : 0;
                    var tofRemaining = _outstanding * _sdu * 8 / eifr;
                    var deadline = _reqRespRttRate != 0
                        ? _lastActivity + (_partTimeoutFactor * tofRemaining) + hmuWait + RetryGraceTime + extraWait
                        : _lastActivity + (_partTimeoutFactor * (3 * _sdu / eifr)) + RetryGraceTime + extraWait;
                    if (now > deadline)
                    {
                        if (_retriesLeft > 0)
                        {
                            if (_window > _windowMin)
                            {
                                _window--;
                                if (_windowMax > _windowMin)
                                {
                                    _windowMax--;
                                    if (_windowMax - _window > _windowFlexibility - 1)
                                    {
                                        _windowMax--;
                                    }
                                }
                            }

                            _retriesLeft--;
                            _waitingForHmu = false;
                            RequestNext();
                        }
                        else
                        {
                            CancelLocked();
                        }
                    }

                    break;
                }

            case ResourceStatus.Transferring:
                {
                    var maxExtraWait = Enumerable.Range(0, MaxRetries).Sum(r => (r + 1) * PerRetryDelay);
                    var maxWait = ((Rtt ?? _link.Rtt ?? 1) * _timeoutFactor * MaxRetries) + SenderGraceTime + maxExtraWait;
                    if (now > _lastActivity + maxWait)
                    {
                        CancelLocked();
                    }

                    break;
                }

            case ResourceStatus.AwaitingProof:
                _timeoutFactor = ProofTimeoutFactor;
                if (now > _lastPartSent + ((Rtt ?? _link.Rtt ?? 1) * _timeoutFactor) + SenderGraceTime)
                {
                    if (_retriesLeft <= 0)
                    {
                        CancelLocked();
                    }
                    else
                    {
                        _retriesLeft--;
                        _link.RequestResourceProof(Hash, _expectedProof);
                        _lastPartSent = now;
                    }
                }

                break;
        }
    }

    private void PostProgress()
    {
        var cb = _progressCallback;
        if (cb is not null)
        {
            _link.Post(() => cb(this));
        }
    }

    private double ComputeProgress()
    {
        if (Status == ResourceStatus.Complete && SegmentIndex == TotalSegments)
        {
            return 1.0;
        }

        double processed = IsInitiator ? _sentParts : _receivedCount;
        double total = TotalParts;
        if (Split)
        {
            var maxParts = Math.Ceiling(MaxEfficientSize / (double)_sdu);
            var factor = TotalParts < maxParts ? maxParts / TotalParts : 1;
            processed = ((SegmentIndex - 1) * maxParts) + (processed * factor);
            total = TotalSegments * maxParts;
        }

        return Math.Min(1.0, processed / total);
    }

    public override string ToString() => $"<{Convert.ToHexStringLower(Hash)}/{_link.LinkIdHex}>";
}
