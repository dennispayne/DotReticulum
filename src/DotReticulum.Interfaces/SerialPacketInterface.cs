using System.IO.Ports;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

/// <summary>Exchanges HDLC-framed Reticulum packets over a serial port or duplex stream.</summary>
public sealed class SerialPacketInterface : IPacketInterface
{
    private readonly SerialPort? _serialPort;
    private readonly Stream? _providedStream;
    private readonly bool _ownsStream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Stream? _stream;
    private int _state;
    private int _receiving;

    public SerialPacketInterface(string portName, int baudRate = 115200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        _serialPort = new SerialPort(portName, baudRate);
    }

    public SerialPacketInterface(Stream stream, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("The serial stream must support reading and writing.", nameof(stream));

        _providedStream = stream;
        _ownsStream = ownsStream;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) == 2, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("The serial interface has already been started.");

        try
        {
            if (_serialPort is not null)
            {
                _serialPort.Open();
                _stream = _serialPort.BaseStream;
            }
            else
            {
                _stream = _providedStream;
            }
        }
        catch
        {
            Interlocked.Exchange(ref _state, 0);
            throw;
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stream = GetStartedStream();
        if (Interlocked.Exchange(ref _receiving, 1) != 0)
            throw new InvalidOperationException("Only one receive operation is allowed.");

        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var receiveToken = linkedCancellation.Token;
        var decoder = new HdlcPacketFraming.Decoder();
        var readBuffer = new byte[4096];
        var decodedPackets = new Queue<byte[]>();
        try
        {
            while (true)
            {
                int count;
                try
                {
                    count = await stream.ReadAsync(readBuffer, receiveToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (receiveToken.IsCancellationRequested)
                {
                    yield break;
                }
                catch (IOException) when (Volatile.Read(ref _state) == 2)
                {
                    yield break;
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _state) == 2)
                {
                    yield break;
                }

                if (count == 0)
                    yield break;

                decoder.Push(readBuffer.AsSpan(0, count), packet => decodedPackets.Enqueue(packet));
                while (decodedPackets.TryDequeue(out var packet))
                    yield return packet;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _receiving, 0);
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        var stream = GetStartedStream();
        var frame = HdlcPacketFraming.Encode(packet.Span);
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var sendToken = linkedCancellation.Token;
        await _writeLock.WaitAsync(sendToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, sendToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _state, 2) == 2)
            return ValueTask.CompletedTask;

        _lifetime.Cancel();
        if (_serialPort is not null)
            _serialPort.Dispose();
        else if (_ownsStream)
            _providedStream?.Dispose();
        _stream = null;
        return ValueTask.CompletedTask;
    }

    private Stream GetStartedStream()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) == 2, this);
        return _state == 1
            ? _stream!
            : throw new InvalidOperationException("The serial interface has not been started.");
    }
}
