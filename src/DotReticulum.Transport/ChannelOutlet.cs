namespace DotReticulum.Transport;

/// <summary>The packet boundary used by the channel state machine.</summary>
internal interface IChannelOutlet
{
    int Mdu { get; }
    double Rtt { get; }
    bool IsUsable { get; }
    Link? Link { get; }
    PacketReceipt? Send(byte[] raw);
    PacketReceipt? Resend(PacketReceipt receipt, byte[] raw);
    void Teardown();
    void Log(string message);
}

internal sealed class LinkChannelOutlet(Link link) : IChannelOutlet
{
    public int Mdu => link.Mdu;
    public double Rtt => link.Rtt ?? 0;
    public bool IsUsable => link.Status is LinkStatus.Active or LinkStatus.Stale;
    public Link Link => link;
    public PacketReceipt? Send(byte[] raw)
    {
        var receipt = link.SendWithContext(raw, LinkContext.Channel, createReceipt: true, encrypt: true);
        if (receipt is not null) receipt.CoreCallbacks = true;
        return receipt;
    }
    public PacketReceipt? Resend(PacketReceipt receipt, byte[] raw) => link.ResendChannelPacket(receipt);
    public void Teardown() => link.Teardown();
    public void Log(string message) => link.Log($"Channel {link.LinkIdHex}: {message}");
}
