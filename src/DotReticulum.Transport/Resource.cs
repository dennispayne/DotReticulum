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

/// <summary>A file response whose readable stream is owned until transfer completion unless <see cref="LeaveOpen"/> is true.</summary>
public sealed record StreamFileResponse(Stream Data, object? Metadata = null, bool LeaveOpen = false);

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

        long Int(string key) => d.TryGetValue(key, out var value) && value is sbyte or byte or short or ushort or int or uint or long or ulong
            ? MsgPack.ToInt64(value) : throw new InvalidDataException($"Invalid integer {key}");
        byte[] Bytes(string key) => d.TryGetValue(key, out var value) && value is byte[] bytes ? bytes : throw new InvalidDataException($"Missing {key}");

        var adv = new ResourceAdvertisement
        {
            TransferSize = Int("t"),
            DataSize = Int("d"),
            Parts = checked((int)Int("n")),
            Hash = Bytes("h"),
            RandomHash = Bytes("r"),
            OriginalHash = Bytes("o"),
            Hashmap = Bytes("m"),
            Flags = checked((int)Int("f")),
            SegmentIndex = checked((int)Int("i")),
            TotalSegments = checked((int)Int("l")),
            RequestId = d.TryGetValue("q", out var request) && request is null or byte[] ? request as byte[] : throw new InvalidDataException("Invalid request ID"),
        };
        if (adv.TransferSize > (long)Resource.MaxEfficientSize * 3)
        {
            throw new InvalidDataException("Invalid transfer size");
        }

        if (adv.TransferSize <= 0 || adv.DataSize < 0 || adv.Hash.Length != 32 ||
            adv.RandomHash.Length != Resource.RandomHashSize || adv.OriginalHash.Length != 32 || adv.Parts <= 0 ||
            adv.Flags is < 0 or > 0x3f || adv.SegmentIndex < 1 || adv.TotalSegments < 1 || adv.SegmentIndex > adv.TotalSegments ||
            adv.Split != (adv.TotalSegments > 1) || adv.IsRequest && adv.IsResponse ||
            (adv.IsRequest || adv.IsResponse) != (adv.RequestId is not null) || adv.RequestId is { Length: not 16 } ||
            adv.Hashmap.Length != Math.Min(adv.Parts, HashmapMaxLength) * Resource.MapHashLength ||
            adv.SegmentIndex == 1 && !adv.Hash.AsSpan().SequenceEqual(adv.OriginalHash))
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
public sealed class Resource : IDisposable, IAsyncDisposable
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
    private ResourceInput? _source;
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
    private Resource? _nextSegment;
    private readonly CancellationTokenSource _workCancellation = new();
    private Task _assemblyWork = Task.CompletedTask;
    private int _terminalPosted;
    private bool _disposed;
    private ResourceStorage? _completedStorage;
    private byte[]? _data;
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
    private SegmentAccumulator? _accumulator;

    private double _lastActivity;
    private int _retriesLeft = MaxRetries;
    private double _timeoutFactor;

    internal sealed class SegmentAccumulator(ResourcePolicy policy) : IDisposable
    {
        public ResourceStorage Data { get; } = new(policy);
        public byte[]? PackedMetadata { get; set; }
        public int NextSegment { get; set; } = 1;
        public int TotalSegments { get; set; }
        public long TotalSize { get; set; }
        public long ReceivedSize { get; set; }
        public int Flags { get; set; }
        public byte[]? RequestId { get; set; }
        public double LastActivity { get; set; }
        public Resource? LastResource { get; set; }
        public void Dispose() => Data.Dispose();
    }

    private Resource(Link link)
    {
        _link = link;
        _sdu = link.Mtu - HeaderMaxSize - IfacMinSize;
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

    /// <summary>The completed payload. Disk-backed results are materialized on access for byte-array API compatibility.</summary>
    public byte[]? Data
    {
        get
        {
            lock (_link.Gate)
            {
                if (_data is null && Status == ResourceStatus.Complete && SegmentIndex == TotalSegments && _completedStorage is { } storage)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (storage.Length > int.MaxValue) throw new InvalidOperationException("Use OpenRead for resources larger than a byte array.");
                    using var input = storage.OpenRead();
                    var data = new byte[checked((int)storage.Length)];
                    input.ReadExactly(data);
                    _data = data;
                }
                return _data;
            }
        }
        private set => _data = value;
    }

    /// <summary>Whether payload bytes have been materialized; checking this does not read disk storage.</summary>
    public bool HasDataInMemory => _data is not null;

    /// <summary>True when the completed payload has disk storage; <see cref="OpenRead"/> avoids materializing a byte array.</summary>
    public bool IsFileBacked => _completedStorage?.IsFileBacked == true;

    /// <summary>Opens an independent read stream. Dispose the resource after consuming a disk-backed result.</summary>
    public Stream OpenRead()
    {
        lock (_link.Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Status != ResourceStatus.Complete || SegmentIndex != TotalSegments)
                throw new InvalidOperationException("The logical resource is not complete.");
            return _completedStorage?.OpenRead() ?? new MemoryStream(Data ?? [], writable: false);
        }
    }

    /// <summary>The unpacked metadata of a received resource, or null.</summary>
    public object? Metadata { get; private set; }

    public Action<Resource>? Concluded { get; set; }
    internal bool InternalCallback { get; set; }

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
        => CreateOutgoing(link, new ResourceInput(new MemoryStream(source, writable: false), leaveOpen: false),
            metadata, autoCompress, callback, progress, timeout, segmentIndex, originalHash, requestId, isResponse,
            sentMetadataSize, autoCompressLimit);

    internal static Resource CreateOutgoing(Link link, ResourceInput source, object? metadata, bool autoCompress, Action<Resource>? callback,
        Action<Resource>? progress, double? timeout, int segmentIndex = 1, byte[]? originalHash = null, byte[]? requestId = null,
        bool isResponse = false, int sentMetadataSize = 0, long autoCompressLimit = AutoCompressMaxSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        link.ResourcePolicy.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length < 0 || source.Length > link.ResourcePolicy.MaxDataSize)
            throw new InvalidDataException("Resource input exceeds the configured size limit.");
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
            if (packed.Length > Math.Min(link.ResourcePolicy.MaxMetadataSize, MaxEfficientSize - 4))
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
        byte[] slice;
        if (r.TotalSize <= MaxEfficientSize)
        {
            r.TotalSegments = 1;
            r.SegmentIndex = 1;
            r.Split = false;
            slice = source.Read(0, checked((int)dataSize), cancellationToken);
        }
        else
        {
            r.TotalSegments = (int)(((r.TotalSize - 1) / MaxEfficientSize) + 1);
            r.SegmentIndex = segmentIndex;
            r.Split = true;
            long firstRead = MaxEfficientSize - metadataSize;
            if (segmentIndex == 1)
            {
                slice = source.Read(0, (int)Math.Min(firstRead, dataSize), cancellationToken);
            }
            else
            {
                var seek = firstRead + ((segmentIndex - 2L) * MaxEfficientSize);
                slice = source.Read(seek, (int)Math.Min(MaxEfficientSize, dataSize - seek), cancellationToken);
            }
        }

        if (r.TotalSize > link.ResourcePolicy.MaxDataSize || r.TotalSegments > link.ResourcePolicy.MaxSegments)
            throw new InvalidDataException("Resource exceeds the configured multipart limit.");
        var data = r.HasMetadata ? [.. r._metadataBlock, .. slice] : slice;
        if (data.LongLength > link.ResourcePolicy.MaxSegmentSize)
            throw new InvalidDataException("Resource segment exceeds the configured size limit.");
        var timeoutValue = timeout ?? ((link.Rtt ?? 0) * Link.TrafficTimeoutFactor);
        r.Timeout = timeoutValue;

        byte[] stream;
        if (autoCompress && dataSize <= autoCompressLimit)
        {
            var compressed = Bz2.Compress(data);

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
        if (r.TotalParts > link.ResourcePolicy.MaxParts)
            throw new InvalidDataException("Resource exceeds the configured part limit.");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
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

                packets[i] = Packet.Create(PacketType.Data, DestinationType.Link, link.LinkId, partData, LinkContext.Resource, mtu: link.Mtu);
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
        if (Status >= ResourceStatus.Complete) return;
        if (_link.HasOutgoingResource)
        {
            Status = ResourceStatus.Queued;
            _link.QueueResource(this);
            return;
        }

        SendAdvertisement(first: true);
    }

    internal void StartQueued()
    {
        if (Status == ResourceStatus.Queued) SendAdvertisement(first: true);
    }

    internal void RegisterCancellation(CancellationToken cancellationToken)
    {
        lock (_link.Gate) _source?.RegisterCancellation(cancellationToken, this);
    }

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
        if (Status >= ResourceStatus.Complete)
        {
            return;
        }

        if (request.Length < 33 || request[0] is not (HashmapNotExhausted or HashmapExhausted))
        {
            CancelLocked();
            return;
        }

        var requestOffset = request[0] == HashmapExhausted ? 37 : 33;
        if (request.Length < requestOffset || (request.Length - requestOffset) % MapHashLength != 0 ||
            (request.Length - requestOffset) / MapHashLength > WindowMax ||
            !request.AsSpan(requestOffset - 32, 32).SequenceEqual(Hash))
        {
            CancelLocked();
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
            var found = false;
            for (var i = searchStart; i < searchEnd; i++)
            {
                partIndex++;
                if (_partMapHashes[i].AsSpan().SequenceEqual(lastMapHash))
                {
                    found = true;
                    break;
                }
            }

            _receiverMinConsecutiveHeight = Math.Max(partIndex - 1 - WindowMax, 0);
            if (!found || partIndex % ResourceAdvertisement.HashmapMaxLength != 0)
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

    internal bool HandleRequestPacketOnce(string packetHash)
    {
        if (_requestHashes.Count >= (long)_link.ResourcePolicy.MaxParts * (MaxRetries + 1))
        {
            CancelLocked();
            return false;
        }
        return _requestHashes.Add(packetHash);
    }

    /// <summary>Upstream <c>validate_proof</c>.</summary>
    internal void ValidateProof(ReadOnlySpan<byte> proof)
    {
        if (Status >= ResourceStatus.Complete || proof.Length != 64 || !proof[32..].SequenceEqual(_expectedProof))
        {
            return;
        }

        Status = ResourceStatus.Complete;
        _partPackets = [];
        _partMapHashes = [];
        _partSent = [];
        _hashmapFlat = [];
        _metadataBlock = [];
        _requestHashes.Clear();
        if (SegmentIndex == TotalSegments)
        {
            _link.ResourceConcluded(this);
            ReleaseSource();
            PostTerminal();
            return;
        }

        try
        {
            // Bounded preparation is synchronous: no detached prefetch worker can
            // outlive cancellation, close the shared input, or fault unobserved.
            _nextSegment = CreateOutgoing(_link, _source!, null, _autoCompressOption, Concluded, _progressCallback, null,
                SegmentIndex + 1, OriginalHash, RequestId, IsResponse, _sentMetadataSize, _autoCompressLimit,
                _workCancellation.Token);
            _nextSegment.InternalCallback = InternalCallback;
            _link.ResourceConcluded(this);
            _nextSegment.Advertise();
        }
        catch (Exception ex)
        {
            _link.Log("Preparing the next resource segment failed: " + ex.Message);
            CancelLocked();
        }
    }

    /// <summary>Upstream <c>_rejected</c>: the receiver refused the advertisement.</summary>
    internal void Rejected()
    {
        if (Status >= ResourceStatus.Complete || !IsInitiator)
        {
            return;
        }

        Status = ResourceStatus.Rejected;
        _link.ResourceConcluded(this);
        _workCancellation.Cancel();
        ReleaseSource();
        _partPackets = [];
        _partMapHashes = [];
        _partSent = [];
        _hashmapFlat = [];
        _metadataBlock = [];
        PostTerminal();
    }

    // ---------------------------------------------------------------- receiver

    /// <summary>Upstream <c>Resource.reject</c>: refuses an advertisement.</summary>
    internal static void Reject(Link link, ResourceAdvertisement adv)
    {
        link.SendWithContext(adv.Hash, LinkContext.ResourceRcl, createReceipt: false, encrypt: true);
    }

    /// <summary>Upstream <c>Resource.accept</c>: starts receiving an advertised resource. Returns null when it is a duplicate or invalid.</summary>
    internal static Resource? Accept(Link link, ResourceAdvertisement adv, Action<Resource>? callback, Action<Resource>? progress, byte[]? requestId,
        bool internalCallback = false)
    {
        Resource? resource = null;
        try
        {
            if (link.HasIncomingResource(adv.Hash)) return null;
            var policy = link.ResourcePolicy;
            policy.Validate();
            if (adv.DataSize > policy.MaxDataSize || adv.TransferSize <= 0 || adv.Parts > policy.MaxParts ||
                adv.TotalSegments > policy.MaxSegments || adv.TotalSegments != Math.Max(1L, ((adv.DataSize - 1) / MaxEfficientSize) + 1) ||
                !link.CanAcceptResource(adv))
            {
                Reject(link, adv);
                return null;
            }

            var r = resource = new Resource(link)
            {
                Status = ResourceStatus.Transferring,
                IsInitiator = false,
                Concluded = callback,
                InternalCallback = internalCallback,
                _progressCallback = progress,
                RequestId = requestId ?? adv.RequestId,
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
                r._accumulator = link.SegmentAccumulatorFor(adv);
                if (r._accumulator.NextSegment != r.SegmentIndex || r._accumulator.TotalSegments != r.TotalSegments ||
                    r._accumulator.TotalSize != r.TotalSize || r._accumulator.Flags != (adv.Flags & ~0x02) ||
                    !(r._accumulator.RequestId ?? []).AsSpan().SequenceEqual(adv.RequestId ?? []))
                {
                    Reject(link, adv);
                    return null;
                }
                r._accumulator.LastActivity = link.Now;
                r._accumulator.LastResource = r;
            }

            link.RegisterIncoming(r);
            link.NotifyResourceStarted(r);
            r.HashmapUpdate(0, adv.Hashmap);
            return r;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException or OverflowException or FormatException)
        {
            link.Log($"Could not decode resource advertisement, dropping resource: {ex.Message}");
            resource?.CancelLocked();
            return null;
        }
    }

    internal void HashmapUpdatePacket(ReadOnlySpan<byte> plaintext)
    {
        if (Status >= ResourceStatus.Assembling || !_waitingForHmu)
        {
            return;
        }

        if (plaintext.Length <= 32 || !plaintext[..32].SequenceEqual(Hash) ||
            MsgPack.Unpack(plaintext[32..]) is not object?[] { Length: 2 } update ||
            update[0] is not (byte or sbyte or short or ushort or int or uint or long or ulong) ||
            update[1] is not byte[] hashmap)
        {
            throw new InvalidDataException("Invalid resource hashmap update.");
        }

        HashmapUpdate(checked((int)MsgPack.ToInt64(update[0])), hashmap);
        _lastActivity = _link.Now;
        _retriesLeft = MaxRetries;
    }

    private void HashmapUpdate(int segment, byte[] hashmap)
    {
        if (Status >= ResourceStatus.Assembling)
        {
            return;
        }

        Status = ResourceStatus.Transferring;
        var hashes = hashmap.Length / MapHashLength;
        var start = (long)segment * ResourceAdvertisement.HashmapMaxLength;
        if (segment < 0 || hashes < 1 || hashmap.Length % MapHashLength != 0 ||
            start != _hashmapHeight || hashes != Math.Min(ResourceAdvertisement.HashmapMaxLength, _hashmap.Length - start))
        {
            throw new InvalidDataException("Noncontiguous or incorrectly sized resource hashmap update.");
        }

        for (var i = 0; i < hashes; i++)
        {
            var index = i + (segment * ResourceAdvertisement.HashmapMaxLength);
            var hash = hashmap.AsSpan(i * MapHashLength, MapHashLength);
            var guardStart = Math.Max(0, index - ResourceAdvertisement.CollisionGuardSize);
            for (var previous = guardStart; previous < index; previous++)
            {
                if (_hashmap[previous] is { } earlier && earlier.AsSpan().SequenceEqual(hash))
                    throw new InvalidDataException("Resource hashmap collision in the active guard window.");
            }
            if (_hashmap[index] is null)
            {
                _hashmapHeight++;
            }

            _hashmap[index] = hash.ToArray();
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

        if (packet.Payload.Length == 0 || packet.Payload.Length > _sdu) return;
        var now = _link.Now;
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
                var expectedLength = i == TotalParts - 1 ? Size - ((long)i * _sdu) : _sdu;
                if (partData.Length != expectedLength)
                {
                    CancelLocked();
                    return;
                }
                _parts[i] = partData;
                _lastActivity = now;
                _retriesLeft = MaxRetries;
                if (_accumulator is { } accumulator) accumulator.LastActivity = now;
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
            _assemblyWork = Task.Run(Assemble);
            _link.TrackResourceWork(_assemblyWork);
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
        byte[]? payload = null;
        byte[]? packedMetadata = null;
        object? metadata = null;
        try
        {
            var parts = _parts;
            var cancellationToken = _workCancellation.Token;
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new byte[checked((int)Size)];
            var offset = 0;
            foreach (var p in parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                p!.CopyTo(stream, offset);
                offset += p.Length;
            }

            if (offset != Size) throw new InvalidDataException("Resource parts do not match the advertised size.");
            var plain = Encrypted ? _link.Decrypt(stream) : stream;
            if (plain is null || plain.Length < RandomHashSize)
            {
                throw new InvalidDataException("Resource decryption failed.");
            }

            var body = plain.AsSpan(RandomHashSize).ToArray();
            var segmentLimit = Math.Min(_link.ResourcePolicy.MaxSegmentSize,
                Math.Min(MaxEfficientSize, TotalSize - ((SegmentIndex - 1L) * MaxEfficientSize)));
            if (Compressed)
            {
                body = Bz2.Decompress(body, segmentLimit, cancellationToken);
            }

            if (body.LongLength != segmentLimit || !ComputeHash(body, RandomHash).AsSpan().SequenceEqual(Hash))
                throw new InvalidDataException("Resource size or hash differs from its advertisement.");
            result = body;
            payload = result;
            if (HasMetadata && SegmentIndex == 1)
            {
                if (result.Length < 3) throw new InvalidDataException("Truncated resource metadata header.");
                var metadataSize = (result[0] << 16) | (result[1] << 8) | result[2];
                if (metadataSize > _link.ResourcePolicy.MaxMetadataSize || metadataSize > result.Length - 3)
                    throw new InvalidDataException("Invalid resource metadata length.");
                packedMetadata = result.AsSpan(3, metadataSize).ToArray();
                metadata = MsgPack.Unpack(packedMetadata);
                payload = result.AsSpan(3 + metadataSize).ToArray();
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (_workCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _link.Log("Error while assembling received resource: " + ex.Message);
            lock (_link.Gate)
            {
                if (Status < ResourceStatus.Complete)
                {
                    Status = ResourceStatus.Corrupt;
                    CancelLocked();
                }
            }
            return;
        }

        lock (_link.Gate)
        {
            if (_workCancellation.IsCancellationRequested || Status != ResourceStatus.Assembling)
            {
                return;
            }

            try
            {
                if (_accumulator is { } accumulator)
                {
                    accumulator.Data.Write(payload!);
                    accumulator.ReceivedSize += result!.Length;
                    accumulator.NextSegment++;
                    accumulator.LastActivity = _link.Now;
                    if (packedMetadata is not null) accumulator.PackedMetadata = packedMetadata;
                    if (SegmentIndex == TotalSegments)
                    {
                        if (accumulator.ReceivedSize != TotalSize)
                            throw new InvalidDataException("Multipart resource length mismatch.");
                        Metadata = accumulator.PackedMetadata is { } packed ? MsgPack.Unpack(packed) : null;
                        _completedStorage = accumulator.Data;
                        Data = _completedStorage.ToMemory();
                        if (_data is not null)
                        {
                            _completedStorage.Dispose();
                            _completedStorage = null;
                        }
                        _link.DetachSegments(OriginalHash);
                        _accumulator = null;
                    }
                }
                else
                {
                    _completedStorage = new ResourceStorage(_link.ResourcePolicy);
                    _completedStorage.Write(payload!);
                    Data = _completedStorage.ToMemory();
                    if (_data is not null)
                    {
                        _completedStorage.Dispose();
                        _completedStorage = null;
                    }
                    Metadata = metadata;
                }

                byte[] proof = [.. Hash, .. ComputeExpectedProof(result!, Hash)];
                _link.SendResourceProof(Packet.Create(PacketType.Proof, DestinationType.Link, _link.LinkId, proof,
                    LinkContext.ResourcePrf, mtu: _link.Mtu));
                Status = ResourceStatus.Complete;
                _parts = [];
                _hashmap = [];
                _link.ResourceConcluded(this);
                if (SegmentIndex == TotalSegments) PostTerminal();
            }
            catch (Exception ex)
            {
                _link.Log("Completing received resource failed: " + ex.Message);
                Status = ResourceStatus.Corrupt;
                CancelLocked();
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
        if (_accumulator?.LastResource is { } current && !ReferenceEquals(current, this))
        {
            current.CancelLocked();
            if (current.Status is ResourceStatus.Failed or ResourceStatus.Corrupt or ResourceStatus.Rejected)
                Status = current.Status;
            return;
        }
        if (_nextSegment is { } next && Status == ResourceStatus.Complete)
        {
            next.CancelLocked();
            if (next.Status is ResourceStatus.Failed or ResourceStatus.Corrupt or ResourceStatus.Rejected)
                Status = next.Status;
            return;
        }
        if (Volatile.Read(ref _terminalPosted) != 0 || Status is ResourceStatus.Failed or ResourceStatus.Rejected ||
            Status == ResourceStatus.Complete && SegmentIndex == TotalSegments)
        {
            return;
        }

        var wasQueued = Status is ResourceStatus.Queued or ResourceStatus.None;
        if (Status != ResourceStatus.Corrupt) Status = ResourceStatus.Failed;
        _workCancellation.Cancel();
        ReleaseSource();
        _partPackets = [];
        _partMapHashes = [];
        _partSent = [];
        _hashmapFlat = [];
        _metadataBlock = [];
        _parts = [];
        _hashmap = [];
        _requestHashes.Clear();
        ReleaseReceiveStorage();
        try
        {
            if (!wasQueued && _link.Status == LinkStatus.Active && Hash.Length == 32)
            {
                _link.SendWithContext(Hash, IsInitiator ? LinkContext.ResourceIcl : LinkContext.ResourceRcl, createReceipt: false, encrypt: true);
            }
        }
        catch (Exception ex)
        {
            _link.Log("Sending resource cancellation failed: " + ex.Message);
        }
        finally
        {
            _link.ResourceConcluded(this);
            PostTerminal();
        }
    }

    private void PostTerminal()
    {
        if (Interlocked.Exchange(ref _terminalPosted, 1) == 0 && Concluded is { } callback)
        {
            if (InternalCallback) callback(this);
            else _link.Post(() => callback(this));
        }
    }

    private void ReleaseSource()
    {
        try { _source?.Dispose(); }
        catch (Exception ex) { _link.Log("Disposing resource input failed: " + ex.Message); }
    }

    private void ReleaseReceiveStorage()
    {
        if (_accumulator is not null)
        {
            try { _link.ForgetSegments(OriginalHash); }
            catch (Exception ex) { _link.Log("Disposing multipart resource storage failed: " + ex.Message); }
            finally { _accumulator = null; }
        }
        if (_completedStorage is { } storage)
        {
            try { storage.Dispose(); }
            catch (Exception ex) { _link.Log("Disposing completed resource storage failed: " + ex.Message); }
            finally { _completedStorage = null; }
        }
    }

    public void Dispose()
    {
        lock (_link.Gate)
        {
            if (_disposed) return;
            CancelLocked();
            ReleaseReceiveStorage();
            _link.ForgetCompletedResource(this);
            _disposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _assemblyWork.ConfigureAwait(false);
        if (_nextSegment is { } next) await next.DisposeAsync().ConfigureAwait(false);
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
            if (InternalCallback) cb(this);
            else _link.Post(() => cb(this));
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
