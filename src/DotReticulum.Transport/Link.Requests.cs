using DotReticulum.Core;

namespace DotReticulum.Transport;

public enum RequestStatus : byte
{
    Failed = 0x00,
    Sent = 0x01,
    Delivered = 0x02,
    Receiving = 0x03,
    Ready = 0x04,
}

/// <summary>The requester's handle for an outstanding request (upstream <c>RequestReceipt</c>).</summary>
public sealed class RequestReceipt
{
    private readonly TaskCompletionSource<object?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _settlementGate = new();
    private readonly Link? _link;
    private CancellationTokenRegistration _cancellation;
    private long? _maxResponseSize;

    internal RequestReceipt(byte[] requestId, double sentAt, double timeout, Link? link = null)
    {
        RequestId = requestId;
        SentAt = sentAt;
        Timeout = timeout;
        _link = link;
    }

    public byte[] RequestId { get; internal set; }

    private RequestStatus _status = RequestStatus.Sent;

    public RequestStatus Status
    {
        get { lock (_settlementGate) return _status; }
        internal set
        {
            lock (_settlementGate)
            {
                if (_status is not (RequestStatus.Ready or RequestStatus.Failed)) _status = value;
            }
        }
    }

    public double SentAt { get; }

    public double Timeout { get; }

    /// <summary>Time after which a still-pending request fails; defaults to <c>SentAt + Timeout</c>.</summary>
    internal double Deadline { get => _deadline ?? SentAt + Timeout; set => _deadline = value; }

    private double? _deadline;

    /// <summary>Largest acceptable response resource, or null for unlimited.</summary>
    public long? MaxResponseSize
    {
        get { lock (_settlementGate) return _maxResponseSize; }
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_settlementGate) _maxResponseSize = value;
        }
    }

    public PacketReceipt? PacketReceipt { get; internal set; }
    public long RequestSize { get; internal set; }
    public long? ResponseSize { get; internal set; }
    public long? ResponseTransferSize { get; internal set; }
    public bool UseApplicationValues { get; internal set; }

    /// <summary>Metadata sent along with a <see cref="FileResponse"/>.</summary>
    public object? Metadata { get; internal set; }

    public object? Response { get; private set; }

    /// <summary>The original MessagePack [request ID, response] envelope, preserving binary map key types; null for a metadata/file response.</summary>
    public byte[]? RawResponse { get; private set; }

    /// <summary>Response resource progress 0..1 while <see cref="Status"/> is Receiving.</summary>
    public double Progress { get; internal set; }

    public Action<RequestReceipt>? ResponseReceived { get; set; }

    public Action<RequestReceipt>? Failed { get; set; }

    public Action<RequestReceipt>? ProgressChanged { get; set; }

    /// <summary>Completes with the response, or faults with <see cref="TimeoutException"/> when the request fails.</summary>
    public Task<object?> ResponseTask => _done.Task;

    internal void Complete(object? response, byte[]? rawResponse = null)
    {
        lock (_settlementGate)
        {
            if (Status is RequestStatus.Ready or RequestStatus.Failed)
            {
                return;
            }

            Response = response;
            RawResponse = rawResponse;
            Status = RequestStatus.Ready;
            Progress = 1;
        }
        _cancellation.Unregister();
        var delivered = _link?.MarkRequestDelivered(PacketReceipt);
        _done.TrySetResult(response);
        Dispatch(() =>
        {
            Invoke(delivered);
            Invoke(ProgressChanged);
            Invoke(ResponseReceived);
        });
    }

    internal void Fail(bool notify = true)
    {
        lock (_settlementGate)
        {
            if (Status is RequestStatus.Ready or RequestStatus.Failed)
            {
                return;
            }

            Status = RequestStatus.Failed;
            _done.TrySetException(new TimeoutException("The request failed or timed out."));
        }
        _cancellation.Unregister();
        if (notify) Notify(Failed);
    }

    /// <summary>Cancels this request and its active or queued resources without closing the link.</summary>
    public void Cancel() => _link?.CancelRequest(this);

    internal void CancelCompletion(CancellationToken token)
    {
        lock (_settlementGate)
        {
            if (_status is RequestStatus.Ready or RequestStatus.Failed) return;
            _status = RequestStatus.Failed;
            _done.TrySetCanceled(token);
        }
        _cancellation.Unregister();
        Notify(Failed);
    }

    internal void BindCancellation(CancellationToken token)
    {
        if (!token.CanBeCanceled) return;
        _cancellation = token.UnsafeRegister(_ => _link?.CancelRequest(this, token), null);
        if (Status is RequestStatus.Ready or RequestStatus.Failed) _cancellation.Unregister();
    }

    internal void NotifyProgress(Action? delivered = null) => Dispatch(() =>
    {
        Invoke(delivered);
        Invoke(ProgressChanged);
    });

    private void Notify(Action<RequestReceipt>? callback)
    {
        if (callback is null) return;
        Dispatch(() => Invoke(callback));
    }

    private void Dispatch(Action callback)
    {
        if (_link is { } link) link.Post(callback);
        else callback();
    }

    private void Invoke(Action<RequestReceipt>? callback) => Invoke(callback is null ? null : () => callback(this));

    private void Invoke(Action? callback)
    {
        try { callback?.Invoke(); }
        catch (Exception ex)
        {
            if (_link is { } link) link.Log("Request callback failed: " + ex.Message);
            else System.Diagnostics.Trace.TraceError("Request callback failed: {0}", ex);
        }
    }
}

public sealed partial class Link
{
    public const int MaximumPendingRequests = 256;
    /// <summary>Upstream <c>Link.request</c>: sends a request for <paramref name="path"/> (a REQUEST packet, or a Resource when it exceeds the MDU).</summary>
    public RequestReceipt? Request(string path, object? data = null, Action<RequestReceipt>? responseCallback = null,
        Action<RequestReceipt>? failedCallback = null, Action<RequestReceipt>? progressCallback = null, double? timeout = null,
        long? maxResponseSize = null, CancellationToken cancellationToken = default, bool useApplicationValues = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maxResponseSize < 0) throw new ArgumentOutOfRangeException(nameof(maxResponseSize));
        if (timeout is { } seconds && (!double.IsFinite(seconds) || seconds <= 0)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var packed = MsgPack.Pack(new object?[] { _mgr.UnixNow, LocalDestination.PathHash(path), data });
        lock (_gate)
        {
            if (Status != LinkStatus.Active)
            {
                return null;
            }
            if (_pendingRequests.Count >= MaximumPendingRequests)
            {
                _mgr.Log?.Invoke($"Pending request limit reached on {LinkIdHex}");
                throw new InvalidOperationException("The link's pending request limit has been reached.");
            }

            var effectiveTimeout = timeout ?? (((Rtt ?? 0) * TrafficTimeoutFactor) + (ResponseMaxGraceTime * 1.125));
            if (packed.Length <= Mdu)
            {
                RequestReceipt? request = null;
                SendWithContext(packed, LinkContext.Request, createReceipt: true, encrypt: true, beforeSend: packetReceipt =>
                {
                    packetReceipt!.Timeout = effectiveTimeout;
                    request = new RequestReceipt(packetReceipt.TruncatedHash, Now, effectiveTimeout, this)
                    {
                        ResponseReceived = responseCallback,
                        Failed = failedCallback,
                        ProgressChanged = progressCallback,
                        MaxResponseSize = maxResponseSize,
                        UseApplicationValues = useApplicationValues,
                        RequestSize = packed.LongLength,
                        PacketReceipt = packetReceipt,
                    };
                    packetReceipt.TimedOut = _ => FailRequest(request);
                    _pendingRequests.Add(request);
                });
                request?.BindCancellation(cancellationToken);
                return request;
            }

            var resourceRequest = StartRequestResource(packed, responseCallback, failedCallback, progressCallback, effectiveTimeout, maxResponseSize);
            if (resourceRequest is not null) resourceRequest.UseApplicationValues = useApplicationValues;
            resourceRequest?.BindCancellation(cancellationToken);
            return resourceRequest;
        }
    }

    internal void CancelRequest(RequestReceipt request, CancellationToken token = default)
    {
        lock (_gate)
        {
            if (!_pendingRequests.Remove(request)) return;
            if (request.PacketReceipt is { } packet)
            {
                _receipts.Remove(packet);
                packet.Conclude(PacketReceiptStatus.Failed, _mgr.UnixNow);
            }
            request.CancelCompletion(token);
            CancelRequestResources(request.RequestId);
        }
    }

    private void FailRequest(RequestReceipt request)
    {
        lock (_gate)
        {
            _pendingRequests.Remove(request);
            if (request.PacketReceipt is { Status: PacketReceiptStatus.Sent } packet)
            {
                _receipts.Remove(packet);
                packet.Conclude(PacketReceiptStatus.Failed, _mgr.UnixNow);
            }
        }
        request.Fail();
    }

    internal Action? MarkRequestDelivered(PacketReceipt? receipt)
    {
        if (receipt is null) return null;
        lock (_gate)
        {
            _receipts.Remove(receipt);
            if (receipt.Conclude(PacketReceiptStatus.Delivered, _mgr.UnixNow) && receipt.Delivered is { } callback)
                return () => callback(receipt);
        }
        return null;
    }

    internal async ValueTask HandleRequestAsync(byte[] requestId, byte[] packedRequest, CancellationToken cancellationToken)
    {
        try
        {
            if (Status != LinkStatus.Active || _owner is null || !MsgPack.TryUnpackApplicationValue(packedRequest, out var unpacked)
                || unpacked is not object?[] { Length: >= 3 } request || request[1] is not byte[] pathHash)
            {
                return;
            }

            var handler = _owner.FindRequestHandler(pathHash);
            if (handler is null)
            {
                return;
            }

            var allowed = handler.Allow switch
            {
                RequestPolicy.All => true,
                RequestPolicy.List => RemoteIdentity is { } id && handler.AllowedList is { } list && list.Any(h => h.AsSpan().SequenceEqual(id.Hash)),
                _ => false,
            };
            if (!allowed)
            {
                _mgr.Log?.Invoke($"Request for {handler.Path} from an unauthorised peer was ignored");
                return;
            }
            if (!handler.UseApplicationValues)
            {
                if (!MsgPack.TryUnpack(packedRequest, out var legacy) || legacy is not object?[] legacyRequest)
                {
                    Log($"Request for {handler.Path} cannot be represented by the legacy application-value view");
                    return;
                }
                request = legacyRequest;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closedCancellation.Token);
            var response = await handler.Generator(new RequestContext(handler.Path, request[2], requestId, LinkId, RemoteIdentity, MsgPack.ToDouble(request[0])),
                owned.Token).ConfigureAwait(false);
            if (owned.IsCancellationRequested && response is StreamFileResponse { LeaveOpen: false } discarded)
                await discarded.Data.DisposeAsync().ConfigureAwait(false);
            owned.Token.ThrowIfCancellationRequested();
            if (response is null)
            {
                return;
            }

            Respond(requestId, response, handler.AutoCompress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _closedCancellation.IsCancellationRequested)
        {
            _mgr.Log?.Invoke($"Request work cancelled on {LinkIdHex}");
        }
        catch (Exception ex)
        {
            _mgr.Log?.Invoke("Error while handling a request: " + ex.Message);
        }
    }

    private void HandlePackedResponse(byte[] packed, bool smallPacket)
    {
        if (!MsgPack.TryUnpackApplicationValue(packed, out var unpacked) ||
            unpacked is not object?[] { Length: >= 2 } envelope || envelope[0] is not byte[] requestId)
        {
            Log($"Rejected malformed response envelope on {LinkIdHex}");
            return;
        }
        RequestReceipt? pending;
        lock (_gate) pending = _pendingRequests.Find(request => request.RequestId.AsSpan().SequenceEqual(requestId));
        if (pending is null) return;
        var size = smallPacket ? MsgPack.Pack(envelope[1]).LongLength - 2 : (long?)null;
        var response = envelope[1];
        if (!pending.UseApplicationValues)
        {
            if (!MsgPack.TryUnpack(packed, out var legacy) || legacy is not object?[] legacyEnvelope)
            {
                Log($"Response cannot be represented by the legacy application-value view on {LinkIdHex}");
                FailRequest(pending);
                return;
            }
            response = legacyEnvelope[1];
        }
        HandleResponse(requestId, response, rawResponse: packed, responseSize: size, transferSize: size, checkSize: smallPacket);
    }

    /// <summary>Sends a response prepared by an application's owned asynchronous request worker.</summary>
    public bool Respond(ReadOnlySpan<byte> requestId, object response, bool autoCompress = true)
    {
        if (requestId.Length != Packet.HashLength) throw new ArgumentException("Invalid request ID.", nameof(requestId));
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate)
        {
            if (Status != LinkStatus.Active)
            {
                if (response is StreamFileResponse { LeaveOpen: false } discarded) discarded.Data.Dispose();
                return false;
            }
            if (response is FileResponse file)
            {
                StartResponseResource(file.Data, requestId.ToArray(), autoCompress, file.Metadata ?? Array.Empty<object?>());
            }
            else if (response is StreamFileResponse streamFile)
            {
                var preparing = StartResponseResourceStream(streamFile.Data, requestId.ToArray(), autoCompress,
                    streamFile.Metadata, streamFile.LeaveOpen, _closedCancellation.Token);
                _mgr.ObserveRequiredCleanup(preparing, "stream response preparation");
            }
            else
            {
                var packedResponse = MsgPack.Pack(new object?[] { requestId.ToArray(), response });
                if (packedResponse.Length <= Mdu)
                    SendWithContext(packedResponse, LinkContext.Response, createReceipt: false, encrypt: true);
                else
                    StartResponseResource(packedResponse, requestId.ToArray(), autoCompress);
            }
            return true;
        }
    }

    internal void HandleResponse(byte[] requestId, object? response, object? metadata = null, byte[]? rawResponse = null,
        long? responseSize = null, long? transferSize = null, bool checkSize = false)
    {
        RequestReceipt? match = null;
        lock (_gate)
        {
            if (Status != LinkStatus.Active)
            {
                return;
            }

            match = _pendingRequests.Find(r => r.RequestId.AsSpan().SequenceEqual(requestId));
            if (match is not null)
            {
                _pendingRequests.Remove(match);
            }
        }

        if (match is not null)
        {
            match.ResponseSize = responseSize ?? match.ResponseSize;
            match.ResponseTransferSize = transferSize ?? match.ResponseTransferSize;
            if (checkSize && match.MaxResponseSize is { } max && responseSize > max)
            {
                _mgr.Log?.Invoke($"Rejected response with excessive size {responseSize} on {LinkIdHex}");
                match.Fail();
                return;
            }
            match.Metadata = metadata;
            try { match.Complete(response, rawResponse); }
            catch (Exception ex) { _mgr.Log?.Invoke("Request response callback failed: " + ex.Message); }
        }
    }
}
