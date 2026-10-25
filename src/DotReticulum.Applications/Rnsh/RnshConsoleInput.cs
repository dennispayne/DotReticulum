using System.Text;

namespace DotReticulum.Applications.Rnsh;

internal sealed class RnshConsoleInput : Stream
{
    private readonly Queue<byte> _pending = new();
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        while (_pending.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable) { await Task.Delay(10, cancellationToken).ConfigureAwait(false); continue; }
            var key = Console.ReadKey(true);
            var text = key.Key switch
            {
                ConsoleKey.UpArrow => "\x1b[A", ConsoleKey.DownArrow => "\x1b[B", ConsoleKey.RightArrow => "\x1b[C", ConsoleKey.LeftArrow => "\x1b[D",
                ConsoleKey.Home => "\x1b[H", ConsoleKey.End => "\x1b[F", ConsoleKey.Insert => "\x1b[2~", ConsoleKey.Delete => "\x1b[3~",
                ConsoleKey.PageUp => "\x1b[5~", ConsoleKey.PageDown => "\x1b[6~",
                _ => key.KeyChar == '\0' ? "" : key.KeyChar.ToString(),
            };
            foreach (var b in Encoding.UTF8.GetBytes(text)) _pending.Enqueue(b);
        }
        var count = Math.Min(buffer.Length, _pending.Count);
        for (var i = 0; i < count; i++) buffer.Span[i] = _pending.Dequeue();
        return count;
    }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
