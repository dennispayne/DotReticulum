using DotReticulum.Core;
using DotReticulum.Crypto;

namespace DotReticulum.Transport;

public sealed partial class Link
{
    private readonly List<Resource> _outgoingResources = [];
    private readonly List<Resource> _incomingResources = [];
    private readonly Queue<Resource> _queuedResources = new();
    private readonly Dictionary<string, Resource.SegmentAccumulator> _segments = [];
    private readonly HashSet<Task> _resourceWork = [];
    private readonly HashSet<Resource> _completedFileResources = [];
    private readonly CancellationTokenSource _resourceCancellation = new();

    /// <summary>Explicit local transfer/storage limits. Set before starting resources.</summary>
    public ResourcePolicy ResourcePolicy { get; set; } = new();

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

    internal void Log(string message)
    {
        try { _mgr.Log?.Invoke(message); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Resource diagnostic observer failed: {0}; original: {1}", ex, message); }
    }

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

    internal void QueueResource(Resource resource)
    {
        if (_queuedResources.Count >= ResourcePolicy.MaxQueuedResources)
            throw new InvalidOperationException("The resource queue is full.");
        _queuedResources.Enqueue(resource);
    }

    internal void RegisterOutgoing(Resource resource) => _outgoingResources.Add(resource);

    internal void RegisterIncoming(Resource resource) => _incomingResources.Add(resource);

    internal bool HasIncomingResource(byte[] hash) => _incomingResources.Any(r => r.Hash.AsSpan().SequenceEqual(hash));

    internal void CancelOutgoing(Resource resource) => _outgoingResources.Remove(resource);

    internal void CancelIncoming(Resource resource) => _incomingResources.Remove(resource);

    internal void CancelRequestResources(byte[] requestId)
    {
        lock (_gate)
        {
            foreach (var resource in _incomingResources.Concat(_outgoingResources).Concat(_queuedResources).ToArray())
            {
                if (resource.RequestId is { } id && id.AsSpan().SequenceEqual(requestId))
                    resource.CancelLocked();
            }
            foreach (var accumulator in _segments.Values.ToArray())
            {
                if (accumulator.RequestId is { } id && id.AsSpan().SequenceEqual(requestId))
                    accumulator.LastResource?.CancelLocked();
            }
        }
    }

    internal bool CanAcceptResource(ResourceAdvertisement advertisement)
    {
        if (_incomingResources.Count >= ResourcePolicy.MaxIncomingResources ||
            _completedFileResources.Count >= ResourcePolicy.MaxCompletedFileResources) return false;
        var key = Convert.ToHexStringLower(advertisement.OriginalHash);
        var logicalTransfers = _segments.Count + _incomingResources.Count(resource => resource.TotalSegments == 1);
        if (advertisement.SegmentIndex == 1)
            return !_segments.ContainsKey(key) &&
                logicalTransfers < ResourcePolicy.MaxIncomingResources &&
                _completedFileResources.Count + logicalTransfers < ResourcePolicy.MaxCompletedFileResources;
        return _segments.TryGetValue(key, out var accumulator) &&
            accumulator.NextSegment == advertisement.SegmentIndex &&
            !_incomingResources.Any(r => r.OriginalHash.AsSpan().SequenceEqual(advertisement.OriginalHash));
    }

    internal Resource.SegmentAccumulator SegmentAccumulatorFor(ResourceAdvertisement advertisement)
    {
        var key = Convert.ToHexStringLower(advertisement.OriginalHash);
        if (!_segments.TryGetValue(key, out var acc))
        {
            _segments[key] = acc = new Resource.SegmentAccumulator(ResourcePolicy)
            {
                TotalSize = advertisement.DataSize,
                TotalSegments = advertisement.TotalSegments,
                Flags = advertisement.Flags & ~0x02,
                RequestId = advertisement.RequestId,
                LastActivity = Now,
            };
        }

        return acc;
    }

    internal void ForgetSegments(byte[] originalHash)
    {
        if (_segments.Remove(Convert.ToHexStringLower(originalHash), out var accumulator)) accumulator.Dispose();
    }

    internal void DetachSegments(byte[] originalHash) => _segments.Remove(Convert.ToHexStringLower(originalHash));
    internal void ForgetCompletedResource(Resource resource) => _completedFileResources.Remove(resource);

    internal void TrackResourceWork(Task task)
    {
        _resourceWork.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (_gate) _resourceWork.Remove(completed);
            if (completed.IsFaulted) Log("Resource work failed: " + completed.Exception!.GetBaseException().Message);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Joins owned assembly and input-proxy work. Call outside the link lock after teardown.</summary>
    public async Task DrainResourceWorkAsync()
    {
        while (true)
        {
            Task[] work;
            lock (_gate)
            {
                _resourceWork.RemoveWhere(task => task.IsCompleted);
                work = _resourceWork.ToArray();
            }
            if (work.Length == 0) return;
            try { await Task.WhenAll(work).ConfigureAwait(false); }
            catch (Exception ex) { Log("Joining resource work failed: " + ex.Message); }
        }
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

        if (resource.IsFileBacked) _completedFileResources.Add(resource);
        var outgoing = _outgoingResources.Remove(resource);
        if (outgoing)
        {
            ExpectedRate = resource.Size * 8 / Math.Max(concludedAt - resource.StartedTransferring, 0.0001);
        }

        var queued = _queuedResources.Count;
        for (var i = 0; i < queued; i++)
        {
            var item = _queuedResources.Dequeue();
            if (!ReferenceEquals(item, resource)) _queuedResources.Enqueue(item);
        }
        if (_outgoingResources.Count == 0 && Status == LinkStatus.Active && _queuedResources.TryDequeue(out var next))
        {
            next.StartQueued();
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

    /// <summary>Sends the remaining bytes of a seekable stream, reading only one segment at a time.</summary>
    public Resource SendResource(Stream source, object? metadata = null, bool autoCompress = true, Action<Resource>? concluded = null,
        Action<Resource>? progress = null, double? timeout = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
        => SendResourceStreamCore(source, metadata, autoCompress, concluded, progress, timeout, leaveOpen, cancellationToken);

    private Resource SendResourceStreamCore(Stream source, object? metadata, bool autoCompress, Action<Resource>? concluded,
        Action<Resource>? progress, double? timeout, bool leaveOpen, CancellationToken cancellationToken,
        byte[]? requestId = null, bool isResponse = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ResourceInput? input = null;
        lock (_gate)
        {
            try
            {
                if (!source.CanRead || !source.CanSeek)
                    throw new ArgumentException("Use SendResourceAsync for a non-seekable readable stream.", nameof(source));
                if (Status != LinkStatus.Active) throw new InvalidOperationException("The link is not active.");
                input = new ResourceInput(source, leaveOpen);
                var resource = Resource.CreateOutgoing(this, input, metadata, autoCompress, concluded, progress, timeout,
                    requestId: requestId, isResponse: isResponse, cancellationToken: cancellationToken);
                resource.InternalCallback = isResponse;
                resource.RegisterCancellation(cancellationToken);
                resource.Advertise();
                return resource;
            }
            catch
            {
                if (input is not null)
                {
                    try { input.Dispose(); }
                    catch (Exception ex) { Log("Disposing failed resource input failed: " + ex.Message); }
                }
                else DisposeUntransferredInput(source, leaveOpen);
                throw;
            }
        }
    }

    /// <summary>Owns the opened file until the logical multipart transfer concludes.</summary>
    public Resource SendResourceFile(string path, object? metadata = null, bool autoCompress = true, Action<Resource>? concluded = null,
        Action<Resource>? progress = null, double? timeout = null, CancellationToken cancellationToken = default)
        => SendResource(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan),
            metadata, autoCompress, concluded, progress, timeout, cancellationToken: cancellationToken);

    /// <summary>Proxies non-seekable input to bounded owned disk storage before advertising.</summary>
    public Task<Resource> SendResourceAsync(Stream source, object? metadata = null, bool autoCompress = true,
        Action<Resource>? concluded = null, Action<Resource>? progress = null, double? timeout = null,
        bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var transferred = false;
        lock (_gate)
        {
            try
            {
                if (Status != LinkStatus.Active) throw new InvalidOperationException("The link is not active.");
                ResourcePolicy.Validate();
                if (_resourceWork.Count >= ResourcePolicy.MaxQueuedResources + ResourcePolicy.MaxIncomingResources)
                    throw new InvalidOperationException("The resource preparation queue is full.");
                transferred = true;
                var task = ProxyResourceAsync(source, metadata, autoCompress, concluded, progress, timeout, leaveOpen, cancellationToken);
                TrackResourceWork(task);
                return task;
            }
            catch
            {
                if (!transferred) DisposeUntransferredInput(source, leaveOpen);
                throw;
            }
        }
    }

    private void DisposeUntransferredInput(Stream source, bool leaveOpen)
    {
        if (leaveOpen) return;
        try { source.Dispose(); }
        catch (Exception ex) { Log("Disposing rejected resource input failed: " + ex.Message); }
    }

    private async Task<Resource> ProxyResourceAsync(Stream source, object? metadata, bool autoCompress, Action<Resource>? concluded,
        Action<Resource>? progress, double? timeout, bool leaveOpen, CancellationToken cancellationToken,
        byte[]? requestId = null, bool isResponse = false)
    {
        ResourceCancellationScope? cancellation = null;
        FileStream? file = null;
        var disposeSource = !leaveOpen;
        try
        {
            cancellation = new ResourceCancellationScope(cancellationToken, _resourceCancellation.Token,
                exception => Log("Resource input cancellation callback failed: " + exception.Message));
            var path = Path.Combine(ResourcePolicy.StorageDirectory, $"input-{Guid.NewGuid():N}.bin");
            ResourceStorageScavenger.Initialize(ResourcePolicy.StorageDirectory);
            file = ResourceStorage.CreateOwnedFile(path, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await source.ReadAsync(buffer, cancellation.Token).ConfigureAwait(false)) != 0)
            {
                if (count > ResourcePolicy.MaxDataSize - file.Length)
                    throw new InvalidDataException("Stream input exceeds the configured resource size limit.");
                await file.WriteAsync(buffer.AsMemory(0, count), cancellation.Token).ConfigureAwait(false);
            }
            if (disposeSource)
            {
                disposeSource = false;
                await source.DisposeAsync().ConfigureAwait(false);
            }
            file.Position = 0;
            var inputFile = file;
            file = null;
            var resource = SendResourceStreamCore(inputFile, metadata, autoCompress, concluded, progress, timeout,
                leaveOpen: false, cancellation.Token, requestId, isResponse);
            resource.RegisterCancellation(cancellationToken);
            return resource;
        }
        finally
        {
            try { if (cancellation is not null) await cancellation.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { file?.Dispose(); }
                finally { if (disposeSource) await source.DisposeAsync().ConfigureAwait(false); }
            }
        }
    }

    private RequestReceipt? StartRequestResource(byte[] packed, Action<RequestReceipt>? responseCallback, Action<RequestReceipt>? failedCallback,
        Action<RequestReceipt>? progressCallback, double timeout, long? maxResponseSize)
    {
        var requestId = Identity.TruncatedHash(packed);
        var receipt = new RequestReceipt(requestId, Now, timeout, this)
        {
            ResponseReceived = responseCallback,
            Failed = failedCallback,
            ProgressChanged = progressCallback,
            Deadline = double.MaxValue,
            MaxResponseSize = maxResponseSize,
            RequestSize = packed.LongLength,
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
        resource.InternalCallback = true;
        resource.Advertise();
        return receipt;
    }

    private void StartResponseResource(byte[] packedResponse, byte[] requestId, bool autoCompress, object? metadata = null)
    {
        var resource = Resource.CreateOutgoing(this, packedResponse, metadata, autoCompress, null, null, null, requestId: requestId, isResponse: true);
        resource.InternalCallback = true;
        resource.Advertise();
    }

    /// <summary>Starts an owned stream/file response; non-seekable preparation is bounded and tracked through teardown.</summary>
    private Task<Resource> StartResponseResourceStream(Stream source, byte[] requestId, bool autoCompress,
        object? metadata = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var transferred = false;
        lock (_gate)
        {
            try
            {
                if (!source.CanRead) throw new ArgumentException("Response stream must be readable.", nameof(source));
                if (Status != LinkStatus.Active) throw new InvalidOperationException("The link is not active.");
                ResourcePolicy.Validate();
                if (source.CanSeek)
                {
                    transferred = true;
                    return Task.FromResult(SendResourceStreamCore(source, metadata ?? Array.Empty<object?>(), autoCompress,
                        null, null, null, leaveOpen, cancellationToken, requestId, isResponse: true));
                }
                if (_resourceWork.Count >= ResourcePolicy.MaxQueuedResources + ResourcePolicy.MaxIncomingResources)
                    throw new InvalidOperationException("The resource preparation queue is full.");
                transferred = true;
                var preparation = ProxyResourceAsync(source, metadata ?? Array.Empty<object?>(), autoCompress,
                    null, null, null, leaveOpen, cancellationToken, requestId, isResponse: true);
                TrackResourceWork(preparation);
                return preparation;
            }
            catch
            {
                if (!transferred) DisposeUntransferredInput(source, leaveOpen);
                throw;
            }
        }
    }

    private void CancelResources(List<Action> after)
    {
        TrackResourceWork(CancelResourceInputCallbacksAsync());
        foreach (var r in _incomingResources.ToArray())
        {
            r.CancelLocked();
        }

        foreach (var r in _outgoingResources.ToArray())
        {
            r.CancelLocked();
        }

        while (_queuedResources.TryDequeue(out var queued)) queued.CancelLocked();
        foreach (var accumulator in _segments.Values.ToArray())
        {
            accumulator.LastResource?.CancelLocked();
            accumulator.Dispose();
        }
        _segments.Clear();
        foreach (var resource in _completedFileResources.ToArray()) resource.Dispose();
        _completedFileResources.Clear();
    }

    private async Task CancelResourceInputCallbacksAsync()
    {
        try { await _resourceCancellation.CancelAsync().ConfigureAwait(false); }
        finally { _resourceCancellation.Dispose(); }
    }

    private void TickResources(double now, List<Action> after)
    {
        foreach (var accumulator in _segments.Values.ToArray())
        {
            if (now - accumulator.LastActivity > ResourcePolicy.MultipartTimeout)
                accumulator.LastResource?.CancelLocked();
        }
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

                    UpdatePhysicalLayer(packet);
                    try
                    {
                        HandleAdvertisement(ResourceAdvertisement.Unpack(plaintext));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or InvalidCastException or OverflowException or FormatException)
                    {
                        Log($"Invalid resource advertisement on {LinkIdHex}: {ex.Message}");
                        Teardown();
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

                    UpdatePhysicalLayer(packet);
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

                    UpdatePhysicalLayer(packet);
                    foreach (var r in _incomingResources.ToArray())
                    {
                        if (r.Hash.AsSpan().SequenceEqual(plaintext.AsSpan(0, 32)))
                        {
                            try
                            {
                                r.HashmapUpdatePacket(plaintext);
                            }
                            catch (Exception ex) when (ex is InvalidDataException or InvalidCastException or OverflowException or ArgumentException or FormatException)
                            {
                                Log("Invalid resource hashmap update: " + ex.Message);
                                r.CancelLocked();
                            }
                        }
                    }

                    break;
                }

            case LinkContext.ResourceIcl:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is { Length: 32 })
                    {
                        UpdatePhysicalLayer(packet);
                        foreach (var r in _incomingResources.ToArray())
                        {
                            if (r.Hash.AsSpan().SequenceEqual(plaintext.AsSpan(0, 32)))
                            {
                                r.CancelLocked();
                            }
                        }
                        foreach (var accumulator in _segments.Values.ToArray())
                        {
                            if (accumulator.LastResource is { } segment && segment.Hash.AsSpan().SequenceEqual(plaintext))
                                segment.CancelLocked();
                        }
                    }

                    break;
                }

            case LinkContext.ResourceRcl:
                {
                    var plaintext = Decrypt(packet.Payload.Span);
                    if (plaintext is { Length: 32 })
                    {
                        UpdatePhysicalLayer(packet);
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
                    UpdatePhysicalLayer(packet);
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
                UpdatePhysicalLayer(received.Packet);
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
                Log($"Rejected request with excessive size {adv.DataSize} on {LinkIdHex}");
                return;
            }

            Resource.Accept(this, adv, RequestResourceConcluded, null, null, internalCallback: true);
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

            var resource = Resource.Accept(this, adv, ResponseResourceConcluded, r => ResponseResourceProgress(pending, r),
                adv.RequestId, internalCallback: true);
            if (resource is not null)
            {
                pending.ResponseSize = adv.DataSize;
                pending.ResponseTransferSize = (pending.ResponseTransferSize ?? 0) + adv.TransferSize;
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
                Log("Resource accept callback failed: " + ex.Message);
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
        var delivered = MarkRequestDelivered(pending.PacketReceipt);
        pending.Progress = resource.Progress;
        pending.NotifyProgress(delivered);
    }

    private void RequestResourceConcluded(Resource resource)
    {
        using var receivedResource = resource;
        if (resource.Status != ResourceStatus.Complete || resource.Data is null)
        {
            Log($"Incoming request resource failed with status {resource.Status}");
            return;
        }

        var packed = resource.Data;
        var requestId = Identity.TruncatedHash(packed);
        _mgr.DispatchRequestAsync(ct => HandleRequestAsync(requestId, packed, ct));
    }

    private void ResponseResourceConcluded(Resource resource)
    {
        using var receivedResource = resource;
        if (resource.Status == ResourceStatus.Complete && resource.Data is not null)
        {
            if (resource.HasMetadata)
            {
                if (resource.RequestId is { } id)
                {
                    HandleResponse(id, resource.Data, resource.Metadata);
                }
            }
            else HandlePackedResponse(resource.Data, smallPacket: false);

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
