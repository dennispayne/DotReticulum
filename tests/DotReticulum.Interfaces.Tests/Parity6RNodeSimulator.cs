using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DotReticulum.Interfaces.Tests;

/// <summary>Deterministic in-memory device. Never opens serial, TCP, Bluetooth or a real radio.</summary>
internal sealed class Parity6RNodeSimulator : IAsyncDisposable
{
    private readonly Stream _device;
    private readonly bool _multi;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly Task _pump;
    private Task? _unplugTask;
    private Task? _disposeTask;
    private int _selected;
    private readonly Channel<(int Port, byte[] Payload)> _transmissions = Channel.CreateUnbounded<(int, byte[])>();
    public Stream Host { get; }
    public ConcurrentQueue<byte[]> HostWrites { get; } = new();
    public ConcurrentQueue<RNodeFrame> ReceivedCommands { get; } = new();
    public byte FirmwareMajor { get; set; } = 1;
    public byte FirmwareMinor { get; set; } = 74;
    public byte Platform { get; set; } = 0x80;
    public bool DetectResponse { get; set; } = true;
    public bool WrongBandwidth { get; set; }
    public bool OmitCodingRate { get; set; }
    public bool ReplyDisplays { get; set; } = true;
    public bool HostDisposed { get; private set; }
    public Exception? PumpFailure { get; private set; }
    public IReadOnlyDictionary<int, byte> Types { get; set; } = new Dictionary<int, byte> { [0] = 0, [2] = 0x10, [10] = 0x21 };

    public Parity6RNodeSimulator(bool multi = false)
    {
        _multi = multi;
        var (host, device) = TestSupport.CreateDuplexPair();
        Host = new RecordingStream(host, HostWrites, () => HostDisposed = true);
        _device = device;
        _pump = PumpAsync();
    }

    public Parity6RNodeSimulator(Stream device, bool multi = false)
    {
        _multi = multi;
        Host = Stream.Null;
        _device = device;
        _pump = PumpAsync();
    }

    public Task<(int Port, byte[] Payload)> NextTransmissionAsync() =>
        _transmissions.Reader.ReadAsync().AsTask().WaitAsync(TestSupport.Patience);

    public Task IncomingAsync(int port, byte[] payload) => SendAsync(_multi
        ? RNodeProtocol.SelectedFrame(port, 0, payload) : RNodeProtocol.Frame(0, payload));

    public Task ReadyAsync() => SendAsync(RNodeProtocol.Frame(0x0f, [1]));
    public Task ControlAsync(byte command, byte[] payload, int port = 0) => SendAsync(_multi
        ? RNodeProtocol.SelectedFrame(port, command, payload) : RNodeProtocol.Frame(command, payload));
    public Task RawAsync(byte[] bytes) => SendAsync(bytes);

    private async Task PumpAsync()
    {
        var decoder = new RNodeFrameDecoder();
        var bytes = new byte[2048];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = await _device.ReadAsync(bytes, _stop.Token);
                if (read == 0) return;
                foreach (var frame in decoder.Feed(bytes.AsSpan(0, read)))
                {
                    ReceivedCommands.Enqueue(frame);
                    switch (frame.Command)
                    {
                        case 0x1f: _selected = frame.Payload[0]; break;
                        case 0x08:
                            if (DetectResponse) await ControlAsync(8, [0x46]);
                            break;
                        case 0x50: await ControlAsync(0x50, [FirmwareMajor, FirmwareMinor]); break;
                        case 0x48: await ControlAsync(0x48, [Platform]); break;
                        case 0x49: await ControlAsync(0x49, [0x42]); break;
                        case 0x71:
                            await ControlAsync(0x71, Types.OrderBy(p => p.Key).SelectMany(p => new[] { (byte)p.Key, p.Value }).ToArray());
                            break;
                        case 0x42:
                            if (ReplyDisplays) await ControlAsync(0x42, Enumerable.Range(0, 512).Select(i => (byte)i).ToArray());
                            break;
                        case 0x66:
                            if (ReplyDisplays) await ControlAsync(0x66, Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray());
                            break;
                        case 0: await _transmissions.Writer.WriteAsync((_selected, frame.Payload), _stop.Token); break;
                        case >= 1 and <= 6:
                            if (frame.Command == 5 && OmitCodingRate) break;
                            await ControlAsync(frame.Command, WrongBandwidth && frame.Command == 2
                                ? [0, 0, 0, 1] : frame.Payload, _selected);
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (IOException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            PumpFailure = exception;
            _transmissions.Writer.TryComplete(exception);
            throw;
        }
        finally { _transmissions.Writer.TryComplete(); }
    }

    private async Task SendAsync(byte[] frame)
    {
        await _write.WaitAsync(_stop.Token);
        try
        {
            await _device.WriteAsync(frame, _stop.Token);
            await _device.FlushAsync(_stop.Token);
        }
        finally { _write.Release(); }
    }

    public ValueTask UnplugAsync()
    {
        lock (_lifecycle) return new(_unplugTask ??= UnplugCoreAsync());
    }

    private async Task UnplugCoreAsync()
    {
        await Task.Yield();
        _stop.Cancel();
        try { await _pump.WaitAsync(TestSupport.Patience); }
        finally
        {
            using var deadline = new CancellationTokenSource(TestSupport.Patience);
            await _write.WaitAsync(deadline.Token);
            try { await _device.DisposeAsync(); }
            finally { _write.Release(); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        try { await UnplugAsync(); }
        finally { await Host.DisposeAsync(); }
    }

    private sealed class RecordingStream(Stream inner, ConcurrentQueue<byte[]> writes, Action closed) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count)
        {
            writes.Enqueue(buffer.AsSpan(offset, count).ToArray());
            inner.Write(buffer, offset, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            writes.Enqueue(buffer.ToArray());
            return inner.WriteAsync(buffer, cancellationToken);
        }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { closed(); inner.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
