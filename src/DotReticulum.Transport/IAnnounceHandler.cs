using DotReticulum.Core;

namespace DotReticulum.Transport;

/// <summary>An announce that passed validation and was accepted into the announce state, as delivered to handlers.</summary>
/// <param name="DestinationHash">The announced destination hash.</param>
/// <param name="Identity">The announced identity (public keys only); dispose when done.</param>
/// <param name="AppData">The announced app_data (see <see cref="AnnounceData.AppData"/>), or null.</param>
/// <param name="PacketHash">The full packet hash of the announce.</param>
/// <param name="IsPathResponse">True when the announce carried the PATH_RESPONSE context.</param>
/// <param name="Hops">Hop count including this hop.</param>
/// <param name="Ratchet">The announced ratchet public key, or null.</param>
public sealed record AnnounceReceived(
    byte[] DestinationHash,
    Identity Identity,
    byte[]? AppData,
    byte[] PacketHash,
    bool IsPathResponse,
    int Hops,
    byte[]? Ratchet);

/// <summary>
/// Upstream announce handler contract (<c>Transport.register_announce_handler</c>). Called synchronously from the engine after the
/// announce has been accepted, outside the engine lock; handlers must not block. Exceptions thrown by a handler are swallowed.
/// </summary>
public interface IAnnounceHandler
{
    /// <summary>
    /// Full destination name such as <c>lxmf.delivery</c> (without identity). Only announces whose destination hash equals
    /// <c>hash(aspect_filter, announced identity)</c> are delivered. Null delivers every announce.
    /// </summary>
    string? AspectFilter { get; }

    /// <summary>When true, path responses are delivered in addition to live announces.</summary>
    bool ReceivePathResponses => false;

    void ReceivedAnnounce(AnnounceReceived announce);
}

/// <summary>A handler built from a delegate.</summary>
public sealed class CallbackAnnounceHandler(string? aspectFilter, Action<AnnounceReceived> callback, bool receivePathResponses = false) : IAnnounceHandler
{
    public string? AspectFilter { get; } = aspectFilter;
    public bool ReceivePathResponses { get; } = receivePathResponses;
    public void ReceivedAnnounce(AnnounceReceived announce) => callback(announce);
}
