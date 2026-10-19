using DotReticulum.Core;

namespace DotReticulum.Transport;

public enum LinkStatus : byte
{
    Pending = 0x00,
    Handshake = 0x01,
    Active = 0x02,
    Stale = 0x03,
    Closed = 0x04,
}

public enum LinkTeardownReason : byte
{
    None = 0x00,
    Timeout = 0x01,
    InitiatorClosed = 0x02,
    DestinationClosed = 0x03,
}

public enum ResourceStrategy : byte
{
    AcceptNone = 0x00,
    AcceptApp = 0x01,
    AcceptAll = 0x02,
}

/// <summary>Upstream <c>Destination.PROVE_*</c>.</summary>
public enum ProofStrategy : byte
{
    None = 0x21,
    App = 0x22,
    All = 0x23,
}

/// <summary>Upstream <c>Destination.ALLOW_*</c>.</summary>
public enum RequestPolicy : byte
{
    None = 0x00,
    All = 0x01,
    List = 0x02,
}

/// <summary>Arguments of a request handler (upstream <c>response_generator(path, data, request_id, link_id, remote_identity, requested_at)</c>).</summary>
public sealed record RequestContext(string Path, object? Data, byte[] RequestId, byte[] LinkId, Identity? RemoteIdentity, double RequestedAt);

public sealed record RequestHandler(string Path, Func<RequestContext, object?> Generator, RequestPolicy Allow, IReadOnlyCollection<byte[]>? AllowedList, bool AutoCompress);

/// <summary>Link context bytes (upstream <c>Packet.*</c> context constants).</summary>
public static class LinkContext
{
    public const byte None = 0x00;
    public const byte Resource = 0x01;
    public const byte ResourceAdv = 0x02;
    public const byte ResourceReq = 0x03;
    public const byte ResourceHmu = 0x04;
    public const byte ResourcePrf = 0x05;
    public const byte ResourceIcl = 0x06;
    public const byte ResourceRcl = 0x07;
    public const byte CacheRequest = 0x08;
    public const byte Request = 0x09;
    public const byte Response = 0x0A;
    public const byte PathResponse = 0x0B;
    public const byte Command = 0x0C;
    public const byte CommandStatus = 0x0D;
    public const byte Channel = 0x0E;
    public const byte Keepalive = 0xFA;
    public const byte LinkIdentify = 0xFB;
    public const byte LinkClose = 0xFC;
    public const byte LinkProof = 0xFD;
    public const byte LrRtt = 0xFE;
    public const byte LrProof = 0xFF;
}
