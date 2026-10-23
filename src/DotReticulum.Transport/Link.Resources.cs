using DotReticulum.Core;
using DotReticulum.Crypto;

namespace DotReticulum.Transport;

public sealed partial class Link
{
    private readonly List<Resource> _outgoingResources = [];
    private readonly List<Resource> _incomingResources = [];
    private readonly Queue<Resource> _queuedResources = new();
    private readonly Dictionary<string, Resource.SegmentAccumulator> _segments = [];

    /// <summary>Upstream <c>set_resource_strategy</c>: whether advertised resources are accepted (None, App via <see cref="ResourceAdvertised"/>, or All).</summary>
    public ResourceStrategy ResourceStrategy { get; set; } = ResourceStrategy.AcceptNone;

    /// <summary>Consulted for <see cref="ResourceStrategy.AcceptApp"/>: return true to accept the advertised resource.</summary>
    public Func<ResourceAdvertisement, bool>? ResourceAdvertised { get; set; }

    /// <summary>Called when an accepted incoming resource starts transferring.</summary>
    public Action<Resource>? ResourceStarted { get; set; }

    /// <summary>Called when an accepted incoming resource concludes (completed, failed or corrupt).</summary>
    public Action<Resource>? ResourceReceived { get; set; }

    /// <summary>Upstream <c>expected_rate</c> in bits per second, once a transfer provided a measurement.</summary>
    public double? ExpectedRate { get; internal set; }

    internal int? LastResourceWindow { get; set; }
    internal double? LastResourceEifr { get; set; }

    /// <summary>Bytes of link-establishment traffic seen by this link (request + proof), the seed of the first rate estimate.</summary>
    internal double EstablishmentCost { get; set; }

    internal object Gate => _gate;

    internal bool HasOutgoingResource => _outgoingResources.Count > 0;

    internal void Log(string message) => _mgr.Log?.Invoke(message);

    internal void Post(Action action) => _mgr.Dispatch(action);

    internal void SendPrebuilt(Packet packet) => SendProofPacket(packet);

    internal void SendResourceProof(Packet packet)
    {
        _mgr.Router.CacheResourceProof(packet);
        SendProofPacket(packet);
    }

    internal void RequestResourceProof(byte[] resourceHash, byte[] expectedProof)
    {
        var expected = Packet.Create(PacketType.Proof, DestinationType.Link, LinkId,
            (byte[])[.. resourceHash, .. expectedProof], LinkContext.ResourcePrf);
        var hash = expected.CalculateHash();
        if (_mgr.Router.GetResourceProof(hash, LinkId) is { } cached)
        {
            ReceiveExtendedProof(new ReceivedPacket(cached.ToArray(), cached, AttachedInterface!), []);
        }
        else
        {
            SendWithContext(hash, LinkContext.CacheRequest, createReceipt: false, encrypt: false);
        }
    }

    /// <summary>Wire size of a link-encrypted control packet carrying <paramref name="plaintextLength"/> bytes (used for rate estimates).</summary>
    internal static int OverheadOfEncrypted(int plaintextLength) => Packet.Header1Size + Token.Overhead + (((plaintextLength / 16) + 1) * 16);

    internal void QueueResource(Resource resource) => _queuedResources.Enqueue(resource);

    internal void RegisterOutgoing(Resource resource) => _outgoingResources.Add(resource);

    internal void RegisterIncoming(Resource resource) => _incomingResources.Add(resource);

    internal bool HasIncomingResource(byte[] hash) => _incomingResources.Any(r => r.Hash.AsSpan().SequenceEqual(hash));

    internal void CancelOutgoing(Resource resource) => _outgoingResources.Remove(resource);

    internal void CancelIncoming(Resource resource) => _incomingResources.Remove(resource);

    internal Resource.SegmentAccumulator SegmentAccumulatorFor(byte[] originalHash)
    {
        var key = Convert.ToHexStringLower(originalHash);
        if (!_segments.TryGetValue(key, out var acc))
        {
            _segments[key] = acc = new Resource.SegmentAccumulator();
        }

        return acc;
    }

    internal void ForgetSegments(byte[] originalHash)
    {
        if (_segments.Remove(Convert.ToHexStringLower(originalHash), out var accumulator)) accumulator.Data.Dispose();
    }

    internal void NotifyResourceStarted(Resource resource)
    {
        var cb = ResourceStarted;
        if (cb is not null)
        {
            Post(() => cb(resource));
        }
    }

    /// <summary>Upstream <c>resource_concluded</c>: bookkeeping when a resource finishes; also releases the next queued outgoing resource.</summary>
    internal void ResourceConcluded(Resource resource)
    {
        var concludedAt = Now;
        if (_incomingResources.Remove(resource))
        {
            LastResourceWindow = resource.WindowSize;
            LastResourceEifr = resource.Eifr;
            ExpectedRate = resource.Size * 8 / Math.Max(concludedAt - resource.StartedTransferring, 0.0001);
        }

        if (_outgoingResources.Remove(resource))
        {
            ExpectedRate = resource.Size * 8 / Math.Max(concludedAt - resource.StartedTransferring, 0.0001);
            if (_outgoingResources.Count == 0 && _queuedResources.TryDequeue(out var next) && Status == LinkStatus.Active)
            {
                next.StartQueued();
            }
        }
    }

    /// <summary>
    /// Sends <paramref name="data"/> as a Resource. Large payloads are split into segments (the callback fires once, after the last segment).
    /// Only one outgoing resource is active per link; further ones queue.
    /// </summary>
    public Resource SendResource(byte[] data, object? metadata = null, bool autoCompress = true, Action<Resource>? concluded = null,
        Action<Resource>? progress = null, double? timeout = null)
    {
        lock (_gate)
        {
            if (Status != LinkStatus.Active)
            {
                throw new InvalidOperationException("The link is not active.");
            }

            var resource = Resource.CreateOutgoing(this, data, metadata, autoCompress, concluded, progress, timeout);
            resource.Advertise();
            return resource;
        }
    }

    private RequestReceipt? StartRequestResource(byte[] packed, Action<RequestReceipt>? responseCallback, Action<RequestReceipt>? failedCallback,
        Action<RequestReceipt>? progressCallback, double timeout)
    {
        var requestId = Identity.TruncatedHash(packed);
        var receipt = new RequestReceipt(requestId, Now, timeout)
        {
            ResponseReceived = responseCallback,
            Failed = failedCallback,
            ProgressChanged = progressCallback,
            Deadline = double.MaxValue,
        };
        _pendingRequests.Add(receipt);
        var resource = Resource.CreateOutgoing(this, packed, null, true, r =>
        {
            lock (_gate)
            {
                if (r.Status == ResourceStatus.Complete && receipt.Status is not (RequestStatus.Ready or RequestStatus.Failed))
                {
                    receipt.Status = RequestStatus.Delivered;
                    receipt.Deadline = Now + timeout;
                }
                else if (_pendingRequests.Remove(receipt))
                {
                    receipt.Fail();
                }
            }
        }, null, null, requestId: requestId, isResponse: false);
        resource.Advertise();
        return receipt;
    }

    private void StartResponseResource(byte[] packedResponse, byte[] requestId, bool autoCompress, object? metadata = null)
    {
        var resource = Resource.CreateOutgoing(this, packedResponse, metadata, autoCompress, null, null, null, requestId: requestId, isResponse: true);
        resource.Advertise();
    }

    private void CancelResources(List<Action> after)
    {
        foreach (var r in _incomingResources.ToArray())
        {
            r.CancelLocked();
        }

        foreach (var r in _outgoingResources.ToArray())
        {
            r.CancelLocked();
        }

        while (_queuedResources.TryDequeue(out var queued)) queued.CancelLocked();
        foreach (var accumulator in _segments.Values) accumulator.Data.Dispose();
        _segments.Clear();
    }

    private void TickResources(double now, List<Action> after)
    {
        foreach (var r in _incomingResources.ToArray())
        {
            r.Tick(now);
        }

        foreach (var r in _outgoingResources.ToArray())
        {
            r.Tick(now);
        }
    }

    private void ReceiveExtended(ReceivedPacket received, List<Action> after)
    {
        var packet = received.Packet;
        switch (packet.Context)
        {
            case LinkContext.ResourceAdv:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is null)
                    {
                        return;
                    }

                    try
                    {
                        HandleAdvertisement(ResourceAdvertisement.Unpack(plaintext));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or InvalidCastException or OverflowException)
                    {
                        _mgr.Log?.Invoke($"Invalid resource advertisement on {LinkIdHex}: {ex.Message}");
                        after.Add(Teardown);
                    }

                    break;
                }

            case LinkContext.ResourceReq:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is not { Length: > 33 })
                    {
                        return;
                    }

                    var hashStart = plaintext[0] == 0xFF ? 1 + Resource.MapHashLength : 1;
                    if (plaintext.Length < hashStart + 32)
                    {
                        return;
                    }

                    var hash = plaintext.AsSpan(hashStart, 32);
                    foreach (var r in _outgoingResources.ToArray())
                    {
                        if (r.Hash.AsSpan().SequenceEqual(hash) && r.HandleRequestPacketOnce(Convert.ToHexStringLower(packet.CalculateHash())))
                        {
                            r.HandleRequest(plaintext);
                        }
                    }

                    break;
                }

            case LinkContext.ResourceHmu:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is not { Length: > 32 })
                    {
                        return;
                    }

                    foreach (var r in _incomingResources.ToArray())
                    {
                        if (r.Hash.AsSpan().SequenceEqual(plaintext.AsSpan(0, 32)))
                        {
                            try
                            {
                                r.HashmapUpdatePacket(plaintext);
                            }
                            catch (Exception ex) when (ex is InvalidDataException or InvalidCastException)
                            {
                                r.CancelLocked();
                            }
                        }
                    }

                    break;
                }

            case LinkContext.ResourceIcl:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is { Length: >= 32 })
                    {
                        foreach (var r in _incomingResources.ToArray())
                        {
                            if (r.Hash.AsSpan().SequenceEqual(plaintext.AsSpan(0, 32)))
                            {
                                r.CancelLocked();
                            }
                        }
                    }

                    break;
                }

            case LinkContext.ResourceRcl:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is { Length: >= 32 })
                    {
                        foreach (var r in _outgoingResources.ToArray())
                        {
                            if (r.Hash.AsSpan().SequenceEqual(plaintext.AsSpan(0, 32)))
                            {
                                r.Rejected();
                            }
                        }
                    }

                    break;
                }

            case LinkContext.Resource:
                foreach (var r in _incomingResources.ToArray())
                {
                    r.ReceivePart(packet, received.Raw.Length);
                }

                break;
        }
    }

    private void ReceiveExtendedProof(ReceivedPacket received, List<Action> after)
    {
        var data = received.Packet.Payload.Span;
        if (received.Packet.Context != LinkContext.ResourcePrf || data.Length < 32)
        {
            return;
        }

        foreach (var r in _outgoingResources.ToArray())
        {
            if (r.Hash.AsSpan().SequenceEqual(data[..32]))
            {
                r.ValidateProof(data);
            }
        }
    }

    private void HandleAdvertisement(ResourceAdvertisement adv)
    {
        adv.Link = this;
        if (adv.IsRequest)
        {
            if (_owner is null || !_owner.HasRequestHandlers)
            {
                return;
            }

            if (_owner.MaxRequestSize is { } max && adv.DataSize > max)
            {
                Resource.Reject(this, adv);
                _mgr.Log?.Invoke($"Rejected request with excessive size {adv.DataSize} on {LinkIdHex}");
                return;
            }

            Resource.Accept(this, adv, RequestResourceConcluded, null, null);
        }
        else if (adv.IsResponse)
        {
            var pending = _pendingRequests.Find(p => adv.RequestId is not null && p.RequestId.AsSpan().SequenceEqual(adv.RequestId));
            if (pending is null)
            {
                return;
            }

            if (pending.MaxResponseSize is { } max && adv.DataSize > max)
            {
                Resource.Reject(this, adv);
                _pendingRequests.Remove(pending);
                pending.Fail();
                return;
            }

            var resource = Resource.Accept(this, adv, ResponseResourceConcluded, r => ResponseResourceProgress(pending, r), adv.RequestId);
            if (resource is not null)
            {
                pending.Status = RequestStatus.Receiving;
            }
        }
        else if (ResourceStrategy == ResourceStrategy.AcceptAll)
        {
            Resource.Accept(this, adv, ResourceReceived, null, null);
        }
        else if (ResourceStrategy == ResourceStrategy.AcceptApp && ResourceAdvertised is { } callback)
        {
            bool accept;
            try
            {
                accept = callback(adv);
            }
            catch (Exception ex)
            {
                _mgr.Log?.Invoke("Resource accept callback failed: " + ex.Message);
                return;
            }

            if (accept)
            {
                Resource.Accept(this, adv, ResourceReceived, null, null);
            }
            else
            {
                Resource.Reject(this, adv);
            }
        }
    }

    private void ResponseResourceProgress(RequestReceipt pending, Resource resource)
    {
        if (pending.Status == RequestStatus.Failed)
        {
            resource.Cancel();
            return;
        }

        pending.Status = RequestStatus.Receiving;
        pending.Progress = resource.Progress;
        pending.ProgressChanged?.Invoke(pending);
    }

    private void RequestResourceConcluded(Resource resource)
    {
        if (resource.Status != ResourceStatus.Complete || resource.Data is null)
        {
            _mgr.Log?.Invoke($"Incoming request resource failed with status {resource.Status}");
            return;
        }

        var packed = resource.Data;
        var requestId = Identity.TruncatedHash(packed);
        _ = Task.Run(() => HandleRequest(requestId, packed));
    }

    private void ResponseResourceConcluded(Resource resource)
    {
        if (resource.Status == ResourceStatus.Complete && resource.Data is not null)
        {
            if (resource.HasMetadata)
            {
                if (resource.RequestId is { } id)
                {
                    HandleResponse(id, resource.Data, resource.Metadata);
                }
            }
            else if (MsgPack.TryUnpack(resource.Data, out var unpacked) && unpacked is object?[] { Length: >= 2 } arr && arr[0] is byte[] requestId)
            {
                HandleResponse(requestId, arr[1], rawResponse: resource.Data);
            }

            return;
        }

        RequestReceipt? pending;
        lock (_gate)
        {
            pending = _pendingRequests.Find(p => resource.RequestId is not null && p.RequestId.AsSpan().SequenceEqual(resource.RequestId));
            if (pending is not null)
            {
                _pendingRequests.Remove(pending);
            }
        }

        pending?.Fail();
    }
}
