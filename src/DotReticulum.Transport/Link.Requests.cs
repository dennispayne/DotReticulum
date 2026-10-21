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

    internal RequestReceipt(byte[] requestId, double sentAt, double timeout)
    {
        RequestId = requestId;
        SentAt = sentAt;
        Timeout = timeout;
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
    public long? MaxResponseSize { get; set; }

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
            _done.TrySetResult(response);
        }
        ResponseReceived?.Invoke(this);
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
        if (notify) Failed?.Invoke(this);
    }
}

public sealed partial class Link
{
    /// <summary>Upstream <c>Link.request</c>: sends a request for <paramref name="path"/> (a REQUEST packet, or a Resource when it exceeds the MDU).</summary>
    public RequestReceipt? Request(string path, object? data = null, Action<RequestReceipt>? responseCallback = null,
        Action<RequestReceipt>? failedCallback = null, Action<RequestReceipt>? progressCallback = null, double? timeout = null)
    {
        var packed = MsgPack.Pack(new object?[] { _mgr.UnixNow, LocalDestination.PathHash(path), data });
        lock (_gate)
        {
            if (Status != LinkStatus.Active)
            {
                return null;
            }

            var effectiveTimeout = timeout ?? (((Rtt ?? 0) * TrafficTimeoutFactor) + (ResponseMaxGraceTime * 1.125));
            if (packed.Length <= Mdu)
            {
                var receipt = SendWithContext(packed, LinkContext.Request, createReceipt: true, encrypt: true)!;
                var request = new RequestReceipt(receipt.Hash[..Packet.HashLength], Now, effectiveTimeout)
                {
                    ResponseReceived = responseCallback,
                    Failed = failedCallback,
                    ProgressChanged = progressCallback,
                };
                _pendingRequests.Add(request);
                return request;
            }

            return StartRequestResource(packed, responseCallback, failedCallback, progressCallback, effectiveTimeout);
        }
    }

    internal void HandleRequest(byte[] requestId, byte[] packedRequest)
    {
        try
        {
            if (Status != LinkStatus.Active || _owner is null || !MsgPack.TryUnpack(packedRequest, out var unpacked)
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

            var response = handler.Generator(new RequestContext(handler.Path, request[2], requestId, LinkId, RemoteIdentity, MsgPack.ToDouble(request[0])));
            if (response is null)
            {
                return;
            }

            Respond(requestId, response, handler.AutoCompress);
        }
        catch (Exception ex)
        {
            _mgr.Log?.Invoke("Error while handling a request: " + ex.Message);
        }
    }

    /// <summary>Sends a response prepared by an application's owned asynchronous request worker.</summary>
    public bool Respond(ReadOnlySpan<byte> requestId, object response, bool autoCompress = true)
    {
        if (requestId.Length != Packet.HashLength) throw new ArgumentException("Invalid request ID.", nameof(requestId));
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate)
        {
            if (Status != LinkStatus.Active) return false;
            if (response is FileResponse file)
            {
                StartResponseResource(file.Data, requestId.ToArray(), autoCompress, file.Metadata ?? Array.Empty<object?>());
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

    internal void HandleResponse(byte[] requestId, object? response, object? metadata = null, byte[]? rawResponse = null)
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
            match.Metadata = metadata;
            try { match.Complete(response, rawResponse); }
            catch (Exception ex) { _mgr.Log?.Invoke("Request response callback failed: " + ex.Message); }
        }
    }
}
