using System.Security.Cryptography;
using DotReticulum.Core;
using DotReticulum.Crypto;

namespace DotReticulum.Transport;

/// <summary>
/// An established (or establishing) encrypted link between two peers (upstream <c>RNS.Link</c>): X25519 key agreement bound to the destination
/// identity by an Ed25519 proof, Token (AES-256-CBC + HMAC) encryption keyed by HKDF(shared, salt = link id), RTT measurement, keep-alives,
/// stale detection, teardown, initiator identification, packet proofs and the request/response API. Resources are layered on top
/// (<see cref="Resource"/>). Link MTU upgrades are constrained by every bearer on the negotiated path.
/// </summary>
public sealed partial class Link
{
    public const int EcPubSize = 64;
    public const int SignallingSize = 3;
    /// <summary>Bounded native receive ceiling, matching the largest pinned bitrate-optimized bearer.</summary>
    public const int MaximumOperationalMtu = InterfaceAccessCode.MaximumPacketSize;
    public const byte ModeAes256Cbc = 0x01;
    public const double KeepaliveMax = 360;
    public const double KeepaliveMin = 5;
    public const double KeepaliveMaxRtt = 1.75;
    public const double StaleFactor = 2;
    public const double StaleGrace = 5;
    public const double KeepaliveTimeoutFactor = 4;
    public const double TrafficTimeoutFactor = 6;
    public const double EstablishmentTimeoutPerHop = 6;
    public const double ResponseMaxGraceTime = 10;
    private const int MtuMask = 0x1FFFFF;

    private readonly LinkManager _mgr;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _closedCancellation = new();
    private readonly List<PacketReceipt> _receipts = [];
    private readonly List<RequestReceipt> _pendingRequests = [];
    private byte[]? _prv;
    private byte[]? _sigSeed;
    private byte[] _pub = [];
    private byte[] _sigPub = [];
    private byte[] _peerPub = [];
    private byte[] _peerSigPub = [];
    private byte[]? _derivedKey;
    private Token? _token;
    private readonly LocalDestination? _owner;
    private readonly Identity? _destinationIdentity;
    private double _staleDeadline;
    private Channel? _channel;
    private bool _trackPhysicalLayer;
    private PacketPhysicalLayer _physicalLayer;
    internal bool TracksPhysicalLayer { get { lock (_gate) return _trackPhysicalLayer; } }

    /// <summary>Last available local ingress measurements while tracking is enabled; never a remote-end RF report.</summary>
    public PacketPhysicalLayer PhysicalLayer
    {
        get { lock (_gate) return _trackPhysicalLayer ? _physicalLayer : default; }
    }

    /// <summary>Opt-in PHY tracking. Disabling clears retained readings immediately.</summary>
    public void TrackPhysicalLayer(bool enabled)
    {
        lock (_gate)
        {
            _trackPhysicalLayer = enabled;
            if (!enabled) _physicalLayer = default;
        }
    }

    private void UpdatePhysicalLayer(Packet packet, bool force = false)
    {
        if (!_trackPhysicalLayer && !force) return;
        var value = packet.PhysicalLayer;
        _physicalLayer = new(value.Rssi ?? _physicalLayer.Rssi, value.Snr ?? _physicalLayer.Snr,
            value.Quality ?? _physicalLayer.Quality);
    }

    /// <summary>The reliable message <see cref="Channel"/> of this link, created on first use (upstream <c>get_channel</c>).</summary>
    public Channel GetChannel()
    {
        lock (_gate)
        {
            if (Status == LinkStatus.Closed) throw new InvalidOperationException("The link is closed.");
            return _channel ??= new Channel(this);
        }
    }

    /// <summary>Creates a channel with synchronous type/handler registration before ingress can acknowledge data.
    /// Requires that no channel has previously been created.</summary>
    public Channel GetChannel(Action<Channel> initialize)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        lock (_gate)
        {
            if (Status == LinkStatus.Closed) throw new InvalidOperationException("The link is closed.");
            if (_channel is not null) throw new InvalidOperationException("The channel has already been created.");
            var channel = new Channel(this);
            initialize(channel);
            _channel = channel;
            return channel;
        }
    }


    internal Link(LinkManager mgr, bool initiator, byte[] linkId, byte[] destinationHash, LocalDestination? owner, Identity? destinationIdentity)
    {
        _mgr = mgr;
        ResourcePolicy = mgr.DefaultResourcePolicy;
        IsInitiator = initiator;
        LinkId = linkId;
        DestinationHash = destinationHash;
        _owner = owner;
        _destinationIdentity = destinationIdentity;
        Mtu = Packet.Mtu;
        Mdu = ComputeMdu(Mtu);
        KeepaliveInterval = KeepaliveMax;
        StaleTime = StaleFactor * KeepaliveMax;
        if (owner is not null)
        {
            ProofStrategy = owner.ProofStrategy;
        }
    }

    public byte[] LinkId { get; }

    public string LinkIdHex => Convert.ToHexStringLower(LinkId);

    public byte[] DestinationHash { get; }

    public bool IsInitiator { get; }

    public LinkStatus Status { get; private set; } = LinkStatus.Pending;

    public LinkTeardownReason TeardownReason { get; private set; }

    /// <summary>Round-trip time in seconds (null until measured).</summary>
    public double? Rtt { get; private set; }

    public int Mtu { get; private set; }

    /// <summary>Maximum plaintext data unit of one link packet.</summary>
    public int Mdu { get; private set; }

    public int Hops { get; internal set; }

    public double KeepaliveInterval { get; private set; }

    public double StaleTime { get; private set; }

    public IPacketInterface? AttachedInterface { get; internal set; }

    /// <summary>The peer identity: the destination identity (initiator side) or the identity the initiator identified with (responder side).</summary>
    public Identity? RemoteIdentity { get; private set; }

    public LocalDestination? Owner => _owner;

    public double RequestTime { get; internal set; }

    public double ActivatedAt { get; private set; }

    public double LastInbound { get; private set; }

    public double LastOutbound { get; private set; }

    public double LastData { get; private set; }

    public double LastProof { get; private set; }

    public double LastKeepalive { get; private set; }

    public double? Age => Rtt is null ? null : Now - ActivatedAt;

    public double NoInboundFor => Now - Math.Max(LastInbound, ActivatedAt);

    public double NoOutboundFor => Now - LastOutbound;

    public double NoDataFor => Now - LastData;

    public double InactiveFor => Math.Min(NoInboundFor, NoOutboundFor);

    /// <summary>Measured handshake rate in bits per second, when a positive RTT is available.</summary>
    public double? EstablishmentRate => Rtt is > 0 && EstablishmentCost > 0 ? EstablishmentCost * 8 / Rtt : null;

    public double EstablishmentTimeout { get; internal set; }

    public long KeepalivesSent { get; private set; }

    public long KeepalivesReceived { get; private set; }

    public long PacketsReceived { get; private set; }

    public ProofStrategy ProofStrategy { get; set; } = ProofStrategy.None;

    public Func<Packet, bool>? ProofRequested { get; set; }

    /// <summary>Fires when the link becomes active (initiator: proof validated; responder: RTT packet received).</summary>
    public Action<Link>? Established { get; set; }

    public Action<Link>? Closed { get; set; }

    /// <summary>Fires for each received link data packet (decrypted payload, packet).</summary>
    public Action<byte[], Packet>? PacketReceived { get; set; }

    public Action<Link, Identity>? RemoteIdentified { get; set; }

    internal Packet? RequestPacket { get; private set; }

    internal double Now => _mgr.Now;

    public static int ComputeMdu(int mtu) => (((mtu - 1 - Packet.Header1Size - Token.Overhead) / 16) * 16) - 1;

    public static byte[] SignallingBytes(int mtu, byte mode)
    {
        if (mode != ModeAes256Cbc) throw new ArgumentOutOfRangeException(nameof(mode), "Unsupported link mode.");
        var value = (mtu & MtuMask) + (((mode << 5) & 0xE0) << 16);
        return [(byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    public static int MtuFromSignalling(ReadOnlySpan<byte> signalling)
    {
        if (signalling.Length != SignallingSize) throw new ArgumentException("Link signalling must contain three bytes.", nameof(signalling));
        return ((signalling[0] << 16) + (signalling[1] << 8) + signalling[2]) & MtuMask;
    }

    public static byte ModeFromSignalling(ReadOnlySpan<byte> signalling)
    {
        if (signalling.Length != SignallingSize) throw new ArgumentException("Link signalling must contain three bytes.", nameof(signalling));
        return (byte)((signalling[0] & 0xE0) >> 5);
    }

    internal static int InterfaceMtu(IPacketInterface? iface) =>
        iface is { SupportsMtuDiscovery: true, HardwareMtu: > 0 } ? Math.Min(iface.HardwareMtu, MtuMask) : Packet.Mtu;

    /// <summary>Upstream <c>link_id_from_lr_packet</c>: the hash of the request's hashable part with any trailing signalling bytes removed.</summary>
    public static byte[] LinkIdFromRequest(Packet request)
    {
        var payload = request.Payload.Span;
        if (payload.Length > EcPubSize)
        {
            payload = payload[..EcPubSize];
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData([(byte)(request.Flags & 0x0F)]);
        hash.AppendData(request.DestinationHash.Span);
        hash.AppendData([request.Context]);
        hash.AppendData(payload);
        return hash.GetHashAndReset()[..Packet.HashLength];
    }

    /// <summary>Builds an initiator link and its LINKREQUEST packet (not yet sent).</summary>
    internal static Link CreateInitiator(LinkManager mgr, Identity destinationIdentity, byte[] destinationHash, int hops, double establishmentTimeout,
        byte[]? fixedX25519Private = null, byte[]? fixedSigSeed = null)
    {
        var prv = fixedX25519Private ?? X25519.GeneratePrivateKey();
        var seed = fixedSigSeed ?? Ed25519.GenerateSeed();
        var pub = X25519.GetPublicKey(prv);
        var sigPub = Ed25519.GetPublicKey(seed);
        var mtu = Math.Min(InterfaceMtu(mgr.Router.Engine.GetPath(destinationHash)?.Interface), MaximumOperationalMtu);
        byte[] requestData = [.. pub, .. sigPub, .. SignallingBytes(mtu, ModeAes256Cbc)];
        var request = Packet.Create(PacketType.LinkRequest, DestinationType.Single, destinationHash, requestData);
        var link = new Link(mgr, true, LinkIdFromRequest(request), destinationHash, null, destinationIdentity)
        {
            _prv = prv,
            _sigSeed = seed,
            _pub = pub,
            _sigPub = sigPub,
            RequestPacket = request,
            Hops = hops,
            EstablishmentTimeout = establishmentTimeout,
            RemoteIdentity = destinationIdentity,
            Mtu = mtu,
            Mdu = ComputeMdu(mtu),
        };
        return link;
    }

    /// <summary>Upstream <c>Link.validate_request</c>: validates a LINKREQUEST for a local destination, answers with the proof and registers the link.</summary>
    internal static Link? ValidateRequest(LinkManager mgr, LocalDestination owner, ReceivedPacket received, byte[]? fixedX25519Private = null)
    {
        var packet = received.Packet;
        var data = packet.Payload.Span;
        if (data.Length != EcPubSize && data.Length != EcPubSize + SignallingSize)
        {
            return null;
        }

        var mtu = Packet.Mtu;
        var mode = ModeAes256Cbc;
        if (data.Length == EcPubSize + SignallingSize)
        {
            mode = ModeFromSignalling(data[EcPubSize..]);
            var requested = MtuFromSignalling(data[EcPubSize..]);
            mtu = Math.Min(requested == 0 ? Packet.Mtu : requested, Math.Min(InterfaceMtu(received.Source), MaximumOperationalMtu));
        }

        if (mode != ModeAes256Cbc || ComputeMdu(mtu) <= 0 || !X25519.IsValidPublicKey(data[..32]) || !Ed25519.IsValidPublicKey(data[32..64]))
        {
            return null;
        }

        var prv = fixedX25519Private ?? X25519.GeneratePrivateKey();
        var link = new Link(mgr, false, LinkIdFromRequest(packet), packet.DestinationHash.ToArray(), owner, null)
        {
            _prv = prv,
            _pub = X25519.GetPublicKey(prv),
            _sigPub = owner.Identity.ExportPublicKey()[32..],
            _peerPub = data[..32].ToArray(),
            _peerSigPub = data[32..64].ToArray(),
            Mtu = mtu,
            Mdu = ComputeMdu(mtu),
            Hops = packet.Hops + mgr.Router.InboundHopIncrement(received.Source),
            AttachedInterface = received.Source,
        };
        link.EstablishmentTimeout = (EstablishmentTimeoutPerHop * Math.Max(1, link.Hops)) + KeepaliveMax;
        link.Handshake();
        link.UpdatePhysicalLayer(packet, force: true);
        link.LastInbound = link.Now;
        link.EstablishmentCost = received.Packet.Length;
        link.Prove();
        link.RequestTime = link.Now;
        return link;
    }

    private void Handshake()
    {
        var shared = X25519.Agree(_prv!, _peerPub);
        try
        {
            _derivedKey = KeyDerivation.HkdfSha256(shared, 64, LinkId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }

        _token = new Token(_derivedKey);
        Status = LinkStatus.Handshake;
    }

    private void Prove()
    {
        var proof = BuildProofPacket(LinkId, _pub, _owner!.Identity, Mtu);
        _mgr.Enqueue(proof, AttachedInterface);
        EstablishmentCost += proof.Length;
        HadOutbound();
    }

    internal static Packet BuildProofPacket(ReadOnlyMemory<byte> linkId, ReadOnlySpan<byte> ephemeralPublicKey, Identity identity, int mtu)
    {
        if (ephemeralPublicKey.Length != 32) throw new ArgumentException("An ephemeral public key must contain 32 bytes.", nameof(ephemeralPublicKey));
        var signalling = SignallingBytes(mtu, ModeAes256Cbc);
        byte[] signed = [.. linkId.Span, .. ephemeralPublicKey, .. identity.ExportPublicKey().AsSpan(32), .. signalling];
        return Packet.Create(PacketType.Proof, DestinationType.Link, linkId,
            (byte[])[.. identity.Sign(signed), .. ephemeralPublicKey, .. signalling], LinkContext.LrProof);
    }

    internal static bool TryValidateProofData(ReadOnlySpan<byte> linkId, ReadOnlySpan<byte> data, Identity identity,
        out int mtu, out byte[] peerPublicKey)
    {
        mtu = Packet.Mtu;
        peerPublicKey = [];
        ReadOnlySpan<byte> signalling = [];
        if (linkId.Length != Packet.HashLength) return false;
        if (data.Length == 96 + SignallingSize)
        {
            signalling = data[96..];
            if (ModeFromSignalling(signalling) != ModeAes256Cbc) return false;
            mtu = MtuFromSignalling(signalling);
            if (mtu == 0) mtu = Packet.Mtu;
        }
        else if (data.Length != 96) return false;
        if (ComputeMdu(mtu) <= 0 || !X25519.IsValidPublicKey(data.Slice(64, 32))) return false;
        byte[] signed = [.. linkId, .. data.Slice(64, 32), .. identity.ExportPublicKey().AsSpan(32), .. signalling];
        if (!identity.Verify(data[..64], signed)) return false;
        peerPublicKey = data.Slice(64, 32).ToArray();
        return true;
    }

    private byte[] Sign(ReadOnlySpan<byte> message) => IsInitiator ? Ed25519.Sign(_sigSeed!, message) : _owner!.Identity.Sign(message);

    private bool ValidatePeer(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> message) => Ed25519.Verify(_peerSigPub, message, signature);

    /// <summary>Encrypts with the link key (throws when the link is closed).</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        lock (_gate)
        {
            return (_token ?? throw new InvalidOperationException("Link has no key material.")).Encrypt(plaintext);
        }
    }

    /// <summary>Decrypts with the link key; null when the token is invalid or the link keys were purged.</summary>
    public byte[]? Decrypt(ReadOnlySpan<byte> ciphertext)
    {
        lock (_gate)
        {
            return _token is not null && _token.TryDecrypt(ciphertext, out var plain) ? plain : null;
        }
    }

    internal byte[] DerivedKeyForTests => (byte[])(_derivedKey ?? throw new InvalidOperationException()).Clone();

    internal void HadOutbound(bool keepalive = false)
    {
        LastOutbound = Now;
        if (!keepalive)
        {
            LastData = LastOutbound;
        }
    }

    private void UpdateKeepalive()
    {
        var rtt = Rtt ?? 0;
        KeepaliveInterval = Math.Max(Math.Min(rtt * (KeepaliveMax / KeepaliveMaxRtt), KeepaliveMax), KeepaliveMin);
        StaleTime = KeepaliveInterval * StaleFactor;
    }

    /// <summary>Sends link data (encrypted) with an optional receipt. Returns null when the link is not usable.</summary>
    public PacketReceipt? Send(ReadOnlySpan<byte> data, bool createReceipt = true) => SendWithContext(data, LinkContext.None, createReceipt, encrypt: true);

    internal PacketReceipt? SendWithContext(ReadOnlySpan<byte> data, byte context, bool createReceipt, bool encrypt,
        Action<PacketReceipt?>? beforeSend = null, bool updateActivity = true)
    {
        lock (_gate)
        {
            if (Status is LinkStatus.Closed or LinkStatus.Pending)
            {
                return null;
            }

            var payload = encrypt ? Encrypt(data) : data.ToArray();
            if (payload.Length > Mtu - Packet.Header1Size)
            {
                throw new ArgumentException("Link packet payload exceeds the link MTU.", nameof(data));
            }

            var packet = Packet.Create(PacketType.Data, DestinationType.Link, LinkId, payload, context, mtu: Mtu);
            PacketReceipt? receipt = null;
            if (createReceipt && PacketReceipt.IsEligible(packet))
            {
                var timeout = Math.Max((Rtt ?? 0) * TrafficTimeoutFactor, 0.005);
                receipt = new PacketReceipt(packet.CalculateHash(), _mgr.UnixNow, timeout)
                { TimeoutOrigin = Now, SentPacket = packet, CoreCallbacks = context == LinkContext.Channel,
                    ChannelOwner = context == LinkContext.Channel ? this : null };
                _receipts.Add(receipt);
                _mgr.Router.TrackReceipt(receipt);
                if (_receipts.Count > TransportRouter.MaximumReceipts) _receipts.RemoveAll(item => item.Status != PacketReceiptStatus.Sent);
            }

            beforeSend?.Invoke(receipt);
            if (!_mgr.Enqueue(packet, AttachedInterface))
            {
                if (receipt is not null)
                {
                    _receipts.Remove(receipt);
                    receipt.Conclude(PacketReceiptStatus.Failed, _mgr.UnixNow);
                    if (receipt.TimedOut is { } failed) _mgr.Dispatch(() => failed(receipt));
                }
                return null;
            }
            if (updateActivity) HadOutbound(keepalive: context == LinkContext.Keepalive);
            return receipt;
        }
    }

    internal PacketReceipt? ResendChannelPacket(PacketReceipt previous)
    {
        lock (_gate)
        {
            if (Status is not (LinkStatus.Active or LinkStatus.Stale)) return null;
            if (!ReferenceEquals(previous.ChannelOwner, this))
                throw new ArgumentException("Receipt does not belong to this link channel.", nameof(previous));
            if (previous.Status == PacketReceiptStatus.Delivered) return previous;
            var packet = previous.SentPacket ?? throw new ArgumentException("Receipt has no original packet.", nameof(previous));
            if (packet.Context != LinkContext.Channel || !packet.DestinationHash.Span.SequenceEqual(LinkId))
                throw new ArgumentException("Receipt does not belong to this link channel.", nameof(previous));
            _receipts.Remove(previous);
            var envelope = Decrypt(packet.Payload.Span) ??
                throw new InvalidOperationException("The retained channel packet cannot be decrypted.");
            try
            {
                return SendWithContext(envelope, LinkContext.Channel, createReceipt: true, encrypt: true,
                    beforeSend: receipt => receipt!.Timeout = previous.Timeout, updateActivity: false);
            }
            finally { CryptographicOperations.ZeroMemory(envelope); }
        }
    }

    internal void SendProofPacket(Packet proof)
    {
        _mgr.Enqueue(proof, AttachedInterface);
        HadOutbound();
    }

    /// <summary>Initiator only: validates the LRPROOF, activates the link and sends the RTT packet.</summary>
    internal bool ValidateProof(ReceivedPacket received)
    {
        List<Action> after = [];
        try
        {
            lock (_gate)
            {
                if (Status != LinkStatus.Pending || !IsInitiator)
                {
                    return false;
                }

                var data = received.Packet.Payload.Span;
                EstablishmentCost = (RequestPacket?.Length ?? 0) + received.Packet.Length;
                if (!TryValidateProofData(LinkId, data, _destinationIdentity!, out var confirmedMtu, out var peerPub)
                    || confirmedMtu > Math.Min(Mtu, MaximumOperationalMtu))
                {
                    return false;
                }
                _peerPub = peerPub;
                _peerSigPub = _destinationIdentity!.ExportPublicKey()[32..];
                Handshake();

                var now = Now;
                Rtt = now - RequestTime;
                AttachedInterface = received.Source;
                Mtu = confirmedMtu == 0 ? Packet.Mtu : confirmedMtu;
                Mdu = ComputeMdu(Mtu);
                Hops = received.Packet.Hops + _mgr.Router.InboundHopIncrement(received.Source);
                _mgr.Router.NotifyLinkProofValidated(DestinationHash, Hops);
                Status = LinkStatus.Active;
                ActivatedAt = now;
                LastProof = now;
                LastInbound = now;
                UpdatePhysicalLayer(received.Packet);
                UpdateKeepalive();
                _mgr.Activate(this);
                var rttPacket = Packet.Create(PacketType.Data, DestinationType.Link, LinkId, Encrypt(MsgPack.Pack(Rtt.Value)), LinkContext.LrRtt);
                _mgr.Enqueue(rttPacket, AttachedInterface);
                HadOutbound();
                var established = Established;
                if (established is not null)
                {
                    after.Add(() => established(this));
                }
            }

            return true;
        }
        finally
        {
            foreach (var a in after)
            {
                _mgr.Dispatch(a);
            }
        }
    }

    /// <summary>Processes a packet addressed to this link (upstream <c>Link.receive</c>).</summary>
    internal void Receive(ReceivedPacket received)
    {
        var packet = received.Packet;
        List<Action> after = [];
        List<Action> coreAfter = [];
        lock (_gate)
        {
            if (Status == LinkStatus.Closed)
            {
                return;
            }
            if (packet.Length > Mtu)
            {
                _mgr.Log?.Invoke($"Link {LinkIdHex} packet exceeds negotiated MTU {Mtu}");
                return;
            }

            if (packet.PacketType == PacketType.Data && packet.Context == LinkContext.CacheRequest) return;

            if (IsInitiator && packet.Context == LinkContext.Keepalive && packet.Payload.Length == 1 && packet.Payload.Span[0] == 0xFF)
            {
                return;
            }

            if (!ReferenceEquals(received.Source, AttachedInterface))
            {
                _mgr.Log?.Invoke($"Link {LinkIdHex} packet arrived on an unexpected interface, ignoring");
                return;
            }

            LastInbound = Now;
            if (packet.Context != LinkContext.Keepalive)
            {
                LastData = LastInbound;
            }

            PacketsReceived++;
            if (Status == LinkStatus.Stale)
            {
                Status = LinkStatus.Active;
            }

            if (packet.PacketType == PacketType.Data)
            {
                ReceiveData(received, after, coreAfter);
            }
            else if (packet.PacketType == PacketType.Proof)
            {
                ReceiveProof(received, after, coreAfter);
            }
        }

        foreach (var action in coreAfter)
        {
            try { action(); }
            catch (Exception ex) { _mgr.Log?.Invoke($"Link {LinkIdHex} channel processing failed: {ex.Message}"); }
        }
        foreach (var a in after)
        {
            _mgr.Dispatch(a);
        }
    }

    private void ReceiveData(ReceivedPacket received, List<Action> after, List<Action> coreAfter)
    {
        var packet = received.Packet;
        switch (packet.Context)
        {
            case LinkContext.None:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is null)
                    {
                        break;
                    }

                    UpdatePhysicalLayer(packet);
                    var callback = PacketReceived;
                    if (callback is not null)
                    {
                        after.Add(() => callback(plaintext, packet));
                    }

                    ProvePacketIfRequired(packet, after);
                    break;
                }

            case LinkContext.Channel:
                {
                    if (_channel is { } channel && Status == LinkStatus.Active)
                    {
                        var plaintext = Decrypt(packet.Payload.Span);
                        if (plaintext is not null)
                        {
                            UpdatePhysicalLayer(packet);
                            ProvePacket(packet);
                            coreAfter.Add(() => channel.Receive(plaintext));
                        }
                    }

                    break;
                }

            case LinkContext.LinkIdentify:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is { Length: Identity.KeySize + 64 } && !IsInitiator)
                    {
                        var publicKey = plaintext[..Identity.KeySize];
                        byte[] signed = [.. LinkId, .. publicKey];
                        Identity? identity = null;
                        try
                        {
                            identity = Identity.FromPublicKey(publicKey);
                        }
                        catch (ArgumentException)
                        {
                        }

                        if (identity is not null && identity.Verify(plaintext.AsSpan(Identity.KeySize, 64), signed))
                        {
                            if (_mgr.Router.IsIdentityBlocked(identity.Hash))
                            {
                                SendTeardownPacket();
                                CloseLocked(LinkTeardownReason.DestinationClosed, after);
                                break;
                            }
                            if (RemoteIdentity is not null) break;
                            RemoteIdentity = identity;
                            UpdatePhysicalLayer(packet);
                            var cb = RemoteIdentified;
                            if (cb is not null)
                            {
                                after.Add(() => cb(this, identity));
                            }
                        }
                    }

                    break;
                }

            case LinkContext.Request:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is not null && !IsInitiator)
                    {
                        UpdatePhysicalLayer(packet);
                        var requestId = packet.CalculateTruncatedHash();
                        if (_owner is { } owner && (owner.MaxRequestSize is null || plaintext.Length <= owner.MaxRequestSize))
                        {
                            _mgr.DispatchRequestAsync(ct => HandleRequestAsync(requestId, plaintext, ct));
                        }
                        else _mgr.Log?.Invoke($"Ignored request with excessive size {plaintext.Length} on {LinkIdHex}");
                    }

                    break;
                }

            case LinkContext.Response:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is not null)
                    {
                        UpdatePhysicalLayer(packet);
                        HandlePackedResponse(plaintext, smallPacket: true);
                    }

                    break;
                }

            case LinkContext.LrRtt:
                if (!IsInitiator && Status == LinkStatus.Handshake)
                {
                    var measured = Now - RequestTime;
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is not null && MsgPack.TryUnpack(plaintext, out var v) && v is double or long)
                    {
                        var reported = MsgPack.ToDouble(v);
                        if (!double.IsFinite(reported) || reported < 0)
                        {
                            _mgr.Log?.Invoke($"Invalid RTT on link {LinkIdHex}");
                            CloseLocked(LinkTeardownReason.Timeout, after);
                            break;
                        }
                        Rtt = Math.Max(measured, reported);
                        UpdatePhysicalLayer(packet);
                        Status = LinkStatus.Active;
                        ActivatedAt = Now;
                        UpdateKeepalive();
                        var owner = _owner;
                        var cb = Established;
                        if (owner?.LinkEstablished is { } accepted) after.Add(() => accepted(this));
                        if (cb is not null) after.Add(() => cb(this));
                    }
                }

                break;

            case LinkContext.LinkClose:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is not null && plaintext.AsSpan().SequenceEqual(LinkId))
                    {
                        UpdatePhysicalLayer(packet);
                        CloseLocked(IsInitiator ? LinkTeardownReason.DestinationClosed : LinkTeardownReason.InitiatorClosed, after);
                    }

                    break;
                }

            case LinkContext.Keepalive:
                KeepalivesReceived++;
                if (!IsInitiator && packet.Payload.Length == 1 && packet.Payload.Span[0] == 0xFF && Now >= LastOutbound + KeepaliveInterval)
                {
                    SendWithContext([0xFE], LinkContext.Keepalive, createReceipt: false, encrypt: false);
                    KeepalivesSent++;
                }

                break;
            default:
                ReceiveExtended(received, after);
                break;
        }
    }

    private void ReceiveProof(ReceivedPacket received, List<Action> after, List<Action> coreAfter)
    {
        var packet = received.Packet;
        if (packet.Context == LinkContext.None)
        {
            var data = packet.Payload.Span;
            if (data.Length < 32 + 64)
            {
                return;
            }

            for (var i = _receipts.Count - 1; i >= 0; i--)
            {
                var receipt = _receipts[i];
                if (receipt.Status == PacketReceiptStatus.Sent && data[..32].SequenceEqual(receipt.Hash) && ValidatePeer(data.Slice(32, 64), receipt.Hash))
                {
                    if (!receipt.Conclude(PacketReceiptStatus.Delivered, _mgr.UnixNow, packet)) continue;
                    LastProof = Now;
                    _receipts.RemoveAt(i);
                    var cb = receipt.Delivered;
                    (receipt.CoreCallbacks ? coreAfter : after).Add(() =>
                    {
                        cb?.Invoke(receipt);
                    });
                }
            }
        }
        else
        {
            ReceiveExtendedProof(received, after);
        }
    }





    private void ProvePacketIfRequired(Packet packet, List<Action> after)
    {
        var prove = ProofStrategy == ProofStrategy.All;
        if (ProofStrategy == ProofStrategy.App)
        {
            var callback = ProofRequested ?? _owner?.ProofRequested;
            if (callback is not null)
            {
                after.Add(() =>
                {
                    try
                    {
                        if (callback(packet))
                            lock (_gate) { if (Status == LinkStatus.Active) ProvePacket(packet); }
                    }
                    catch (Exception ex) { _mgr.Log?.Invoke("Proof request callback failed: " + ex.Message); }
                });
            }
            return;
        }

        if (prove)
        {
            ProvePacket(packet);
        }
    }

    internal void ProvePacket(Packet packet)
    {
        var hash = packet.CalculateHash();
        var signature = Sign(hash);
        var proof = Packet.Create(PacketType.Proof, DestinationType.Link, LinkId, (byte[])[.. hash, .. signature]);
        SendProofPacket(proof);
    }

    /// <summary>Upstream <c>Link.identify</c>: reveals the initiator identity to the responder over the encrypted link.</summary>
    public void Identify(Identity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            if (!IsInitiator || Status != LinkStatus.Active)
            {
                return;
            }

            var publicKey = identity.ExportPublicKey();
            var signature = identity.Sign([.. LinkId, .. publicKey]);
            SendWithContext([.. publicKey, .. signature], LinkContext.LinkIdentify, createReceipt: false, encrypt: true);
        }
    }

    /// <summary>Upstream <c>Link.teardown</c>: notifies the peer and closes the link.</summary>
    public void Teardown()
    {
        List<Action> after = [];
        lock (_gate)
        {
            if (Status is LinkStatus.Closed)
            {
                return;
            }

            if (Status != LinkStatus.Pending)
            {
                SendTeardownPacket();
            }

            CloseLocked(IsInitiator ? LinkTeardownReason.InitiatorClosed : LinkTeardownReason.DestinationClosed, after);
        }

        foreach (var a in after)
        {
            _mgr.Dispatch(a);
        }
    }

    private void SendTeardownPacket()
    {
        if (_token is null)
        {
            return;
        }

        var packet = Packet.Create(PacketType.Data, DestinationType.Link, LinkId, Encrypt(LinkId), LinkContext.LinkClose);
        _mgr.Enqueue(packet, AttachedInterface);
        HadOutbound();
    }

    internal void FailEstablishment()
    {
        List<Action> after = [];
        lock (_gate)
        {
            if (!IsInitiator || Status is not (LinkStatus.Pending or LinkStatus.Handshake)) return;
            _mgr.Router.NotifyLinkEstablishmentFailed(DestinationHash, AttachedInterface);
            CloseLocked(LinkTeardownReason.Timeout, after);
        }
        foreach (var action in after) _mgr.Dispatch(action);
    }

    private void CloseLocked(LinkTeardownReason reason, List<Action> after)
    {
        if (Status == LinkStatus.Closed)
        {
            return;
        }

        Status = LinkStatus.Closed;
        _mgr.CancelOwnedWork(_closedCancellation);
        TeardownReason = reason;
        CancelResources(after);
        _mgr.ObserveRequiredCleanup(DrainResourceWorkAsync(), "resource cleanup");
        foreach (var r in _receipts.ToArray())
        {
            if (r.Conclude(PacketReceiptStatus.Failed, _mgr.UnixNow) && r.TimedOut is { } timedOut) after.Add(() => timedOut(r));
        }

        _receipts.Clear();
        if (_channel is { } closingChannel)
        {
            closingChannel.Shutdown();
        }
        foreach (var req in _pendingRequests.ToArray())
        {
            req.Fail(notify: false);
            if (req.Failed is { } failed) after.Add(() => failed(req));
        }

        _pendingRequests.Clear();
        _token?.Dispose();
        _token = null;
        if (_derivedKey is not null)
        {
            CryptographicOperations.ZeroMemory(_derivedKey);
            _derivedKey = null;
        }

        if (_prv is not null)
        {
            CryptographicOperations.ZeroMemory(_prv);
            _prv = null;
        }

        if (_sigSeed is not null)
        {
            CryptographicOperations.ZeroMemory(_sigSeed);
            _sigSeed = null;
        }

        _mgr.Remove(this);
        var closed = Closed;
        if (closed is not null)
        {
            after.Add(() => closed(this));
        }
    }



    /// <summary>Periodic watchdog (upstream <c>__watchdog_job</c>) driven by <see cref="LinkManager"/>.</summary>
    internal void Tick(double now)
    {
        List<Action> after = [];
        List<Action> coreAfter = [];
        lock (_gate)
        {
            if (!_trackPhysicalLayer) _physicalLayer = default;
            switch (Status)
            {
                case LinkStatus.Pending:
                case LinkStatus.Handshake:
                    if (now >= RequestTime + EstablishmentTimeout)
                    {
                        if (IsInitiator) _mgr.Router.NotifyLinkEstablishmentFailed(DestinationHash, AttachedInterface);
                        CloseLocked(LinkTeardownReason.Timeout, after);
                    }

                    break;
                case LinkStatus.Active:
                    {
                        var lastInbound = Math.Max(Math.Max(LastInbound, LastProof), ActivatedAt);
                        if (now >= lastInbound + KeepaliveInterval || now >= LastOutbound + KeepaliveInterval)
                        {
                            if (IsInitiator && now >= LastKeepalive + KeepaliveInterval)
                            {
                                LastKeepalive = now;
                                SendWithContext([0xFF], LinkContext.Keepalive, createReceipt: false, encrypt: false);
                                KeepalivesSent++;
                            }

                            if (now >= lastInbound + StaleTime)
                            {
                                Status = LinkStatus.Stale;
                                _staleDeadline = now + ((Rtt ?? 0) * KeepaliveTimeoutFactor) + StaleGrace;
                            }
                        }

                        break;
                    }

                case LinkStatus.Stale:
                    if (now >= _staleDeadline)
                    {
                        SendTeardownPacket();
                        CloseLocked(LinkTeardownReason.Timeout, after);
                    }

                    break;
            }

            if (Status != LinkStatus.Closed)
            {
                for (var i = _receipts.Count - 1; i >= 0; i--)
                {
                    var receipt = _receipts[i];
                    if (receipt.Status != PacketReceiptStatus.Sent)
                    {
                        _receipts.RemoveAt(i);
                    }
                    else if (now > receipt.TimeoutOrigin + receipt.Timeout)
                    {
                        _receipts.RemoveAt(i);
                        receipt.Conclude(PacketReceiptStatus.Failed, _mgr.UnixNow + Math.Max(0, now - Now));
                        var cb = receipt.TimedOut;
                        (receipt.CoreCallbacks ? coreAfter : after).Add(() =>
                        {
                            cb?.Invoke(receipt);
                        });
                    }
                }

                for (var i = _pendingRequests.Count - 1; i >= 0; i--)
                {
                    var req = _pendingRequests[i];
                    if (now >= req.Deadline && req.Status != RequestStatus.Receiving)
                    {
                        _pendingRequests.RemoveAt(i);
                        after.Add(() => req.Fail());
                    }
                }

                TickResources(now, after);
            }
        }

        foreach (var action in coreAfter)
        {
            try { action(); }
            catch (Exception ex) { _mgr.Log?.Invoke($"Link {LinkIdHex} channel receipt failed: {ex.Message}"); }
        }
        foreach (var a in after)
        {
            _mgr.Dispatch(a);
        }
    }


}
