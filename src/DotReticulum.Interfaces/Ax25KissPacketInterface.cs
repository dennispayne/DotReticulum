namespace DotReticulum.Interfaces;

/// <summary>KISS with the pinned fixed APZRNS destination and source AX.25 UI header.</summary>
public sealed class Ax25KissPacketInterface : KissPacketInterface
{
    public Ax25KissPacketInterface(Stream duplexStream, Ax25KissOptions options) : base(duplexStream, Required(options)) { }
    public Ax25KissPacketInterface(Func<CancellationToken, ValueTask<Stream>> openStream, Ax25KissOptions options) : base(openStream, Required(options)) { }
    public Ax25KissPacketInterface(string portName, Ax25KissOptions options) : base(portName, Required(options)) { }

    private static Ax25KissOptions Required(Ax25KissOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options;
    }
}
