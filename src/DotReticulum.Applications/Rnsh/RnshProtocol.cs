using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnsh;

// Pinned RNS Utilities/rnsh/protocol.py:46-148, protocol version 1.
public abstract class RnshMessage : MessageBase
{
    protected static object?[] Array(ReadOnlySpan<byte> raw, int length) =>
        MsgPack.Unpack(raw) is object?[] values && values.Length == length
            ? values : throw new FormatException("Invalid rnsh message shape.");
    protected static int Number(object? value) => checked((int)MsgPack.ToInt64(value));
    public static void Register(DotReticulum.Transport.Channel channel)
    {
        channel.RegisterMessageType<RnshNoop>();
        channel.RegisterMessageType<RnshWindow>();
        channel.RegisterMessageType<RnshExecute>();
        channel.RegisterMessageType<RnshStream>();
        channel.RegisterMessageType<RnshVersion>();
        channel.RegisterMessageType<RnshError>();
        channel.RegisterMessageType<RnshExited>();
    }
}

public sealed class RnshNoop : RnshMessage
{
    public override ushort MsgType => 0xac00;
    public override byte[] Pack() => [];
    public override void Unpack(ReadOnlySpan<byte> raw) { }
}

public sealed class RnshWindow : RnshMessage
{
    public override ushort MsgType => 0xac02;
    public int? Rows { get; set; }
    public int? Columns { get; set; }
    public int? HorizontalPixels { get; set; }
    public int? VerticalPixels { get; set; }
    public override byte[] Pack() => MsgPack.Pack(new object?[] { Rows, Columns, HorizontalPixels, VerticalPixels });
    public override void Unpack(ReadOnlySpan<byte> raw)
    {
        var a = Array(raw, 4);
        Rows = a[0] is null ? null : Number(a[0]);
        Columns = a[1] is null ? null : Number(a[1]);
        HorizontalPixels = a[2] is null ? null : Number(a[2]);
        VerticalPixels = a[3] is null ? null : Number(a[3]);
    }
}

public sealed class RnshExecute : RnshMessage
{
    public override ushort MsgType => 0xac03;
    public string[] Command { get; set; } = [];
    public bool PipeStdin { get; set; }
    public bool PipeStdout { get; set; }
    public bool PipeStderr { get; set; }
    public object? TerminalFlags { get; set; }
    public string? Terminal { get; set; }
    public RnshWindow Window { get; set; } = new();
    public override byte[] Pack() => MsgPack.Pack(new object?[] { Command, PipeStdin, PipeStdout, PipeStderr,
        TerminalFlags, Terminal, Window.Rows, Window.Columns, Window.HorizontalPixels, Window.VerticalPixels });
    public override void Unpack(ReadOnlySpan<byte> raw)
    {
        var a = Array(raw, 10);
        Command = a[0] is null ? [] : a[0] is object?[] argv && argv.All(v => v is string)
            ? argv.Cast<string>().ToArray() : throw new FormatException("Invalid rnsh argv.");
        PipeStdin = a[1] is bool input ? input : throw new FormatException("Invalid stdin mode.");
        PipeStdout = a[2] is bool output ? output : throw new FormatException("Invalid stdout mode.");
        PipeStderr = a[3] is bool error ? error : throw new FormatException("Invalid stderr mode.");
        TerminalFlags = a[4];
        Terminal = a[5] is null or string ? (string?)a[5] : throw new FormatException("Invalid TERM.");
        Window.Unpack(MsgPack.Pack(a[6..]));
    }
}

public sealed class RnshStream : RnshMessage
{
    private readonly StreamDataMessage _inner = new();
    public override ushort MsgType => 0xac04;
    public int StreamId { get; set; }
    public byte[] Data { get; set; } = [];
    public bool Eof { get; set; }
    public bool Compressed { get; set; }
    public override byte[] Pack() => new StreamDataMessage(StreamId, Data, Eof, Compressed).Pack();
    public override void Unpack(ReadOnlySpan<byte> raw)
    {
        _inner.Unpack(raw);
        StreamId = _inner.StreamId;
        Data = _inner.Data;
        Eof = _inner.Eof;
        // Data is already decompressed, so re-emission must not retain this bit.
        Compressed = false;
    }
}

public sealed class RnshVersion : RnshMessage
{
    public override ushort MsgType => 0xac05;
    public string Software { get; set; } = "DotReticulum";
    public int Protocol { get; set; } = 1;
    public override byte[] Pack() => MsgPack.Pack(new object?[] { Software, Protocol });
    public override void Unpack(ReadOnlySpan<byte> raw)
    {
        var a = Array(raw, 2);
        Software = a[0] as string ?? throw new FormatException("Invalid software version.");
        Protocol = Number(a[1]);
    }
}

public sealed class RnshError : RnshMessage
{
    public override ushort MsgType => 0xac06;
    public string? Error { get; set; }
    public bool Fatal { get; set; }
    public object? Data { get; set; }
    public override byte[] Pack() => MsgPack.Pack(new object?[] { Error, Fatal, Data });
    public override void Unpack(ReadOnlySpan<byte> raw)
    {
        var a = Array(raw, 3);
        Error = a[0] is null or string ? (string?)a[0] : throw new FormatException("Invalid error text.");
        Fatal = a[1] is bool fatal ? fatal : throw new FormatException("Invalid fatal flag.");
        Data = a[2];
    }
}

public sealed class RnshExited : RnshMessage
{
    public override ushort MsgType => 0xac07;
    public int ReturnCode { get; set; }
    public override byte[] Pack() => MsgPack.Pack(ReturnCode);
    public override void Unpack(ReadOnlySpan<byte> raw) => ReturnCode = Number(MsgPack.Unpack(raw));
}
