using System.Security.Cryptography;
using DotReticulum.Core;
using DotReticulum.Crypto;

namespace DotReticulum.Transport;

/// <summary>
/// An established (or establishing) encrypted link between two peers (upstream <c>RNS.Link</c>): X25519 key agreement bound to the destination
/// identity by an Ed25519 proof, Token (AES-256-CBC + HMAC) encryption keyed by HKDF(shared, salt = link id), RTT measurement, keep-alives,
/// stale detection, teardown, initiator identification, packet proofs and the request/response API. Resources are layered on top
/// (<see cref="Resource"/>). The link MTU is fixed at <see cref="Packet.Mtu"/> (no MTU discovery); see plan.md for the deviation record.
/// </summary>
public sealed partial class Link
{
    public const int EcPubSize = 64;
    public const int SignallingSize = 3;
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

    /// <summary>The reliable message <see cref="Channel"/> of this link, created on first use (upstream <c>get_channel</c>).</summary>
    public Channel GetChannel()
    {
        lock (_gate)
        {
            return _channel ??= new Channel(this);
        }
    }


    internal Link(LinkManager mgr, bool initiator, byte[] linkId, byte[] destinationHash, LocalDestination? owner, Identity? destinationIdentity)
    {
        _mgr = mgr;
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
        var value = (mtu & MtuMask) + (((mode << 5) & 0xE0) << 16);
        return [(byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    public static int MtuFromSignalling(ReadOnlySpan<byte> signalling) => ((signalling[0] << 16) + (signalling[1] << 8) + signalling[2]) & MtuMask;

    public static byte ModeFromSignalling(ReadOnlySpan<byte> signalling) => (byte)((signalling[0] & 0xE0) >> 5);

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
        byte[] requestData = [.. pub, .. sigPub, .. SignallingBytes(Packet.Mtu, ModeAes256Cbc)];
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
            mtu = requested == 0 ? Packet.Mtu : Math.Min(requested, Packet.Mtu);
        }

        if (mode != ModeAes256Cbc || !X25519.IsValidPublicKey(data[..32]) || !Ed25519.IsValidPublicKey(data[32..64]))
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
            Hops = packet.Hops + 1,
            AttachedInterface = received.Source,
        };
        link.EstablishmentTimeout = (EstablishmentTimeoutPerHop * Math.Max(1, link.Hops)) + KeepaliveMax;
        link.Handshake();
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
        var signalling = SignallingBytes(Mtu, ModeAes256Cbc);
        byte[] signedData = [.. LinkId, .. _pub, .. _sigPub, .. signalling];
        var signature = Sign(signedData);
        byte[] proofData = [.. signature, .. _pub, .. signalling];
        var proof = Packet.Create(PacketType.Proof, DestinationType.Link, LinkId, proofData, LinkContext.LrProof);
        _mgr.Enqueue(proof, AttachedInterface);
        HadOutbound();
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

    internal PacketReceipt? SendWithContext(ReadOnlySpan<byte> data, byte context, bool createReceipt, bool encrypt)
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

            var packet = Packet.Create(PacketType.Data, DestinationType.Link, LinkId, payload, context);
            PacketReceipt? receipt = null;
            if (createReceipt)
            {
                receipt = new PacketReceipt(packet.CalculateHash(), Now, Math.Max((Rtt ?? 0) * TrafficTimeoutFactor, 0.005) + 0.5);
                _receipts.Add(receipt);
            }

            _mgr.Enqueue(packet, AttachedInterface);
            HadOutbound(keepalive: context == LinkContext.Keepalive);
            return receipt;
        }
    }

    internal void SendProofPacket(Packet proof)
    {
        _mgr.Enqueue(proof, AttachedInterface);
        EstablishmentCost += proof.Length;
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
                var confirmedMtu = Packet.Mtu;
                byte[] signalling = [];
                if (data.Length == 64 + 32 + SignallingSize)
                {
                    signalling = data[96..].ToArray();
                    if (ModeFromSignalling(signalling) != ModeAes256Cbc)
                    {
                        return false;
                    }

                    confirmedMtu = MtuFromSignalling(signalling);
                    data = data[..96];
                }
                else if (data.Length != 96)
                {
                    return false;
                }

                var peerPub = data[64..96].ToArray();
                if (!X25519.IsValidPublicKey(peerPub))
                {
                    return false;
                }

                _peerPub = peerPub;
                _peerSigPub = _destinationIdentity!.ExportPublicKey()[32..];
                Handshake();
                byte[] signedData = [.. LinkId, .. _peerPub, .. _peerSigPub, .. signalling];
                if (!ValidatePeer(data[..64], signedData))
                {
                    return false;
                }

                var now = Now;
                Rtt = now - RequestTime;
                AttachedInterface = received.Source;
                Mtu = Math.Min(confirmedMtu == 0 ? Packet.Mtu : confirmedMtu, Packet.Mtu);
                Mdu = ComputeMdu(Mtu);
                Status = LinkStatus.Active;
                ActivatedAt = now;
                LastProof = now;
                LastInbound = now;
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
        lock (_gate)
        {
            if (Status == LinkStatus.Closed)
            {
                return;
            }

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
                ReceiveData(received, after);
            }
            else if (packet.PacketType == PacketType.Proof)
            {
                ReceiveProof(received, after);
            }
        }

        foreach (var a in after)
        {
            _mgr.Dispatch(a);
        }
    }

    private void ReceiveData(ReceivedPacket received, List<Action> after)
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
                        ProvePacket(packet);
                        var plaintext = Decrypt(packet.Payload.Span);
                        if (plaintext is not null)
                        {
                            after.Add(() => channel.Receive(plaintext));
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

                        if (identity is not null && identity.Verify(plaintext.AsSpan(Identity.KeySize, 64), signed) && RemoteIdentity is null)
                        {
                            RemoteIdentity = identity;
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
                        var requestId = packet.CalculateTruncatedHash();
                        if (_owner is { } owner && (owner.MaxRequestSize is null || plaintext.Length <= owner.MaxRequestSize))
                        {
                            _ = Task.Run(() => HandleRequest(requestId, plaintext));
                        }
                    }

                    break;
                }

            case LinkContext.Response:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is not null && MsgPack.TryUnpack(plaintext, out var unpacked) && unpacked is object?[] { Length: >= 2 } arr && arr[0] is byte[] requestId)
                    {
                        _ = Task.Run(() => HandleResponse(requestId, arr[1]));
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
                        Rtt = Math.Max(measured, MsgPack.ToDouble(v));
                        Status = LinkStatus.Active;
                        ActivatedAt = Now;
                        UpdateKeepalive();
                        var owner = _owner;
                        var cb = Established;
                        after.Add(() =>
                        {
                            owner?.LinkEstablished?.Invoke(this);
                            cb?.Invoke(this);
                        });
                    }
                }

                break;

            case LinkContext.LinkClose:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is not null && plaintext.AsSpan().SequenceEqual(LinkId))
                    {
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

    private void ReceiveProof(ReceivedPacket received, List<Action> after)
    {
        var packet = received.Packet;
        if (packet.Context == LinkContext.None)
        {
            var data = packet.Payload.Span;
            if (data.Length != 32 + 64)
            {
                return;
            }

            for (var i = 0; i < _receipts.Count; i++)
            {
                var receipt = _receipts[i];
                if (receipt.Status == PacketReceiptStatus.Sent && data[..32].SequenceEqual(receipt.Hash) && ValidatePeer(data[32..], receipt.Hash))
                {
                    receipt.Status = PacketReceiptStatus.Delivered;
                    receipt.Rtt = Now - receipt.SentAt;
                    LastProof = Now;
                    _receipts.RemoveAt(i);
                    var cb = receipt.Delivered;
                    after.Add(() =>
                    {
                        receipt.Complete(true);
                        cb?.Invoke(receipt);
                    });
                    break;
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
                try
                {
                    prove = callback(packet);
                }
                catch (Exception ex)
                {
                    _mgr.Log?.Invoke("Proof request callback failed: " + ex.Message);
                }
            }
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

    private void CloseLocked(LinkTeardownReason reason, List<Action> after)
    {
        if (Status == LinkStatus.Closed)
        {
            return;
        }

        Status = LinkStatus.Closed;
        TeardownReason = reason;
        CancelResources(after);
        foreach (var r in _receipts.ToArray())
        {
            r.Status = PacketReceiptStatus.Failed;
            after.Add(() => r.Complete(false));
        }

        _receipts.Clear();
        if (_channel is { } closingChannel)
        {
            after.Add(closingChannel.Shutdown);
        }
        foreach (var req in _pendingRequests.ToArray())
        {
            after.Add(() => req.Fail());
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
        lock (_gate)
        {
            switch (Status)
            {
                case LinkStatus.Pending:
                case LinkStatus.Handshake:
                    if (now >= RequestTime + EstablishmentTimeout)
                    {
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
                    if (now >= receipt.SentAt + receipt.Timeout)
                    {
                        receipt.Status = PacketReceiptStatus.Failed;
                        _receipts.RemoveAt(i);
                        var cb = receipt.TimedOut;
                        after.Add(() =>
                        {
                            receipt.Complete(false);
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
                        after.Add(req.Fail);
                    }
                }

                TickResources(now, after);
            }
        }

        foreach (var a in after)
        {
            _mgr.Dispatch(a);
        }
    }


}


