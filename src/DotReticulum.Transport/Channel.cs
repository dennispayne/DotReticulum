using System.Buffers.Binary;

namespace DotReticulum.Transport;

public enum ChannelExceptionType
{
    NoMessageType = 0,
    InvalidMessageType = 1,
    NotRegistered = 2,
    LinkNotReady = 3,
    AlreadySent = 4,
    TooBig = 5,
}

public sealed class ChannelException(ChannelExceptionType type, string message) : Exception(message)
{
    public ChannelExceptionType Type { get; } = type;
}

public enum MessageState
{
    New = 0,
    Sent = 1,
    Delivered = 2,
    Failed = 3,
}

/// <summary>Base of every message sent over a <see cref="Channel"/>. <see cref="MsgType"/> must be unique per channel; values from 0xF000 are reserved.</summary>
public abstract class MessageBase
{
    public abstract ushort MsgType { get; }

    public abstract byte[] Pack();

    public abstract void Unpack(ReadOnlySpan<byte> raw);
}

/// <summary>A message in flight on a channel (upstream <c>Envelope</c>).</summary>
public sealed class Envelope
{
    internal Envelope(MessageBase? message, byte[]? raw, int sequence)
    {
        Message = message;
        Raw = raw;
        Sequence = sequence;
    }

    public MessageBase? Message { get; internal set; }

    public int Sequence { get; internal set; }

    public int Tries { get; internal set; }

    internal byte[]? Raw { get; set; }

    internal PacketReceipt? Receipt { get; set; }

    internal bool Unpacked { get; set; }

    public MessageState State => Receipt?.Status switch
    {
        PacketReceiptStatus.Delivered => MessageState.Delivered,
        PacketReceiptStatus.Sent => MessageState.Sent,
        PacketReceiptStatus.Failed or PacketReceiptStatus.Culled => MessageState.Failed,
        _ => MessageState.New,
    };
}

/// <summary>
/// Reliable, ordered, windowed message delivery over a link (upstream <c>RNS.Channel.Channel</c>). Messages are packed as
/// <c>msgtype(2) | sequence(2) | length(2) | data</c> into a single link packet (context 0x0E) and retried until proven.
/// </summary>
public sealed class Channel
{
    public const int Window = 2;
    public const int WindowMin = 2;
    public const int WindowMinLimitSlow = 2;
    public const int WindowMinLimitMedium = 5;
    public const int WindowMinLimitFast = 16;
    public const int WindowMaxSlow = 5;
    public const int WindowMaxMedium = 12;
    public const int WindowMaxFast = 48;
    public const int WindowMax = WindowMaxFast;
    public const int FastRateThreshold = 10;
    public const double RttFast = 0.18;
    public const double RttMedium = 0.75;
    public const double RttSlow = 1.45;
    public const int WindowFlexibility = 4;
    public const int SeqMax = 0xFFFF;
    public const int SeqModulus = SeqMax + 1;
    public const int MaximumPendingMessages = 256;
    public const int MaximumMessageHandlers = 64;
    private const int MaxTries = 5;
    private const int EnvelopeOverhead = 6;

    private readonly IChannelOutlet _outlet;
    private readonly object _lock = new();
    private readonly object _sendLock = new();
    private readonly object _receiveLock = new();
    private readonly List<Envelope> _txRing = [];
    private readonly Dictionary<int, Envelope> _rxRing = [];
    private readonly List<Func<MessageBase, bool>> _handlers = [];
    private readonly List<Func<MessageBase, bool>> _systemHandlers = [];
    private readonly Queue<MessageBase> _pendingMessages = [];
    private Task _handlerTask = Task.CompletedTask;
    private bool _handlerRunning;
    private readonly Dictionary<ushort, Func<MessageBase>> _factories = [];
    private int _nextSequence;
    private int _nextRxSequence;
    private int _fastRateRounds;
    private int _mediumRateRounds;
    private bool _shutdown;
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _sendReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the owning link closes and pending channel work has been released.</summary>
    public Task Completion => _completion.Task;
    internal CancellationToken Closed => _closed.Token;

    /// <summary>Current bounded handler work. Shutdown discards queued messages; an executing user handler must return itself.</summary>
    public Task HandlerCompletion
    {
        get
        {
            lock (_lock) return _handlerTask;
        }
    }

    internal Channel(Link link) : this(new LinkChannelOutlet(link))
    {
    }

    internal Channel(IChannelOutlet outlet)
    {
        _outlet = outlet;
        if (outlet.Rtt > RttSlow)
        {
            WindowSize = 1;
            WindowMaxSize = 1;
            WindowMinSize = 1;
            _windowFlexibility = 1;
        }
        else
        {
            WindowSize = Window;
            WindowMaxSize = WindowMaxSlow;
            WindowMinSize = WindowMin;
            _windowFlexibility = WindowFlexibility;
        }
    }

    private readonly int _windowFlexibility;

    public int WindowSize { get; private set; }

    public int WindowMaxSize { get; private set; }

    public int WindowMinSize { get; private set; }

    /// <summary>Bytes available to a single message (link MDU minus the 6 byte envelope header).</summary>
    public int Mdu => Math.Min(_outlet.Mdu - EnvelopeOverhead, 0xFFFF);

    public Link Link => _outlet.Link ?? throw new InvalidOperationException("This channel has no link outlet.");

    public void RegisterMessageType<T>() where T : MessageBase, new() => RegisterMessageType(() => new T(), false);

    internal void RegisterSystemMessageType<T>() where T : MessageBase, new() => RegisterMessageType(() => new T(), true);

    private void RegisterMessageType(Func<MessageBase> factory, bool isSystemType)
    {
        MessageBase sample;
        try
        {
            sample = factory();
        }
        catch (Exception ex)
        {
            throw new ChannelException(ChannelExceptionType.InvalidMessageType, $"Message type raised an exception when constructed with no arguments: {ex.Message}");
        }

        if (sample.MsgType >= 0xF000 && !isSystemType)
        {
            throw new ChannelException(ChannelExceptionType.InvalidMessageType, "The message type is system-reserved.");
        }

        lock (_lock)
        {
            _factories[sample.MsgType] = factory;
        }
    }

    /// <summary>Handlers run in registration order; the first returning true stops processing of that message.</summary>
    public void AddMessageHandler(Func<MessageBase, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            if (_shutdown) throw new ObjectDisposedException(nameof(Channel));
            if (!_handlers.Contains(handler))
            {
                if (_handlers.Count == MaximumMessageHandlers)
                    throw new InvalidOperationException("The channel message-handler limit has been reached.");
                _handlers.Add(handler);
            }
        }
    }

    public void RemoveMessageHandler(Func<MessageBase, bool> handler)
    {
        lock (_lock)
        {
            _handlers.Remove(handler);
        }
    }

    internal void AddSystemMessageHandler(Func<MessageBase, bool> handler)
    {
        lock (_lock)
        {
            if (_shutdown) throw new ObjectDisposedException(nameof(Channel));
            if (_systemHandlers.Count == MaximumMessageHandlers)
                throw new InvalidOperationException("The channel stream-reader limit has been reached.");
            _systemHandlers.Add(handler);
        }
    }

    internal void RemoveSystemMessageHandler(Func<MessageBase, bool> handler)
    {
        lock (_lock) _systemHandlers.Remove(handler);
    }

    public bool IsReadyToSend
    {
        get
        {
            lock (_lock)
            {
                return IsReadyLocked();
            }
        }
    }

    private bool IsReadyLocked()
    {
        if (_shutdown || !_outlet.IsUsable) return false;
        // Proof state can settle before the receipt callback is dispatched.
        for (var i = _txRing.Count - 1; i >= 0; i--)
        {
            var envelope = _txRing[i];
            if (envelope.Receipt?.Status == PacketReceiptStatus.Delivered) PacketDelivered(envelope);
        }

        var outstanding = 0;
        foreach (var e in _txRing)
        {
            if (e.Receipt is null || e.Receipt.Status != PacketReceiptStatus.Delivered)
            {
                outstanding++;
            }
        }

        // Packet proofs acknowledge out-of-order frames, not their delivery through the receiver's bounded reorder window.
        var sequenceFits = _txRing.Count == 0
            || (_nextSequence - _txRing[0].Sequence + SeqModulus) % SeqModulus <= WindowMax;
        return outstanding < WindowSize && sequenceFits;
    }

    internal async Task WaitForSendAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_lock)
            {
                if (_shutdown || !_outlet.IsUsable) throw new IOException("The channel is closed.");
                if (IsReadyLocked()) return;
                wait = _sendReady.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void SignalSendReady()
    {
        var signal = _sendReady;
        _sendReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult();
    }

    /// <summary>Sends a message; throws <see cref="ChannelException"/> when the window is full, the message is too big or the link cannot transmit.</summary>
    public Envelope Send(MessageBase message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Envelope envelope;
        bool alreadyDelivered;
        lock (_sendLock)
        {
            int reserved;
            lock (_lock)
            {
                if (!IsReadyLocked())
                {
                    throw new ChannelException(ChannelExceptionType.LinkNotReady, "Link is not ready");
                }

                reserved = _nextSequence;
                envelope = new Envelope(message, null, reserved);
                envelope.Raw = PackEnvelope(message, reserved);
                if (envelope.Raw.Length > _outlet.Mdu)
                {
                    throw new ChannelException(ChannelExceptionType.TooBig, $"Packed message too big for packet: {envelope.Raw.Length} > {_outlet.Mdu}");
                }

                _nextSequence = (reserved + 1) % SeqModulus;
                _txRing.Add(envelope);
            }

            PacketReceipt? receipt;
            try
            {
                receipt = _outlet.Send(envelope.Raw);
                if (receipt is null)
                    throw new ChannelException(ChannelExceptionType.LinkNotReady, "Outlet did not transmit packet");
            }
            catch
            {
                lock (_lock)
                {
                    _txRing.Remove(envelope);
                    _nextSequence = reserved;
                    SignalSendReady();
                }

                throw;
            }

            lock (_lock)
            {
                if (_shutdown)
                    throw new ChannelException(ChannelExceptionType.LinkNotReady, "Channel closed during transmission");
                envelope.Tries++;
                Track(envelope, receipt);
                UpdatePacketTimeouts();
                alreadyDelivered = receipt.Status == PacketReceiptStatus.Delivered;
            }
        }

        if (alreadyDelivered)
        {
            PacketDelivered(envelope);
        }
        else if (envelope.State == MessageState.Failed)
        {
            PacketTimeout(envelope, envelope.Receipt!);
        }

        return envelope;
    }

    private static byte[] PackEnvelope(MessageBase message, int sequence)
    {
        var data = message.Pack();
        if (data.Length > ushort.MaxValue)
            throw new ChannelException(ChannelExceptionType.TooBig, "Channel message length exceeds 65535 bytes.");
        var raw = new byte[EnvelopeOverhead + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(raw, message.MsgType);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(2), (ushort)sequence);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(4), (ushort)data.Length);
        data.CopyTo(raw, EnvelopeOverhead);
        return raw;
    }

    private void Track(Envelope envelope, PacketReceipt receipt)
    {
        envelope.Receipt = receipt;
        receipt.Delivered = r => PacketDelivered(envelope, r);
        receipt.TimedOut = r => PacketTimeout(envelope, r);
        receipt.Timeout = PacketTimeoutTime(envelope.Tries);
    }

    private double PacketTimeoutTime(int tries) =>
        Math.Pow(1.5, tries - 1) * Math.Max(_outlet.Rtt * 2.5, 0.025) * (_txRing.Count + 1.5);

    private void UpdatePacketTimeouts()
    {
        foreach (var e in _txRing)
        {
            var updated = PacketTimeoutTime(e.Tries);
            if (e.Receipt is { } r && r.Status == PacketReceiptStatus.Sent && updated > r.Timeout)
            {
                r.Timeout = updated;
            }
        }
    }

    private void PacketDelivered(Envelope envelope, PacketReceipt? receipt = null)
    {
        lock (_lock)
        {
            if (_shutdown || (receipt is not null && !ReferenceEquals(receipt, envelope.Receipt)) || !_txRing.Remove(envelope))
            {
                return;
            }

            Disconnect(envelope.Receipt);
            SignalSendReady();
            if (WindowSize < WindowMaxSize)
            {
                WindowSize++;
            }

            var rtt = _outlet.Rtt;
            if (rtt == 0)
            {
                return;
            }

            if (rtt > RttFast)
            {
                _fastRateRounds = 0;
                if (rtt > RttMedium)
                {
                    _mediumRateRounds = 0;
                }
                else
                {
                    _mediumRateRounds++;
                    if (WindowMaxSize < WindowMaxMedium && _mediumRateRounds == FastRateThreshold)
                    {
                        WindowMaxSize = WindowMaxMedium;
                        WindowMinSize = WindowMinLimitMedium;
                    }
                }
            }
            else
            {
                _fastRateRounds++;
                if (WindowMaxSize < WindowMaxFast && _fastRateRounds == FastRateThreshold)
                {
                    WindowMaxSize = WindowMaxFast;
                    WindowMinSize = WindowMinLimitFast;
                }
            }
        }
    }

    private void PacketTimeout(Envelope envelope, PacketReceipt expired)
    {
        lock (_sendLock)
        {
            var teardown = false;
            lock (_lock)
            {
                if (_shutdown || !ReferenceEquals(expired, envelope.Receipt)
                    || envelope.State == MessageState.Delivered || !_txRing.Contains(envelope)) return;

                if (envelope.Tries >= MaxTries)
                {
                    teardown = true;
                }
                else
                {
                    envelope.Tries++;
                    if (WindowSize > WindowMinSize)
                    {
                        WindowSize--;
                        if (WindowMaxSize > WindowMinSize + _windowFlexibility) WindowMaxSize--;
                    }
                }
            }

            if (teardown)
            {
                Abort("Channel retry count exceeded");
                return;
            }

            PacketReceipt? receipt;
            try
            {
                receipt = _outlet.Resend(expired, envelope.Raw!);
            }
            catch (Exception ex)
            {
                Abort("Channel retransmission failed: " + ex.Message);
                return;
            }

            if (receipt is null)
            {
                Abort("Channel outlet did not retransmit packet");
                return;
            }

            bool delivered;
            lock (_lock)
            {
                if (_shutdown || !_txRing.Contains(envelope)) return;
                Disconnect(expired);
                Track(envelope, receipt);
                UpdatePacketTimeouts();
                delivered = receipt.Status == PacketReceiptStatus.Delivered;
            }

            if (delivered) PacketDelivered(envelope, receipt);
            else if (receipt.Status is PacketReceiptStatus.Failed or PacketReceiptStatus.Culled)
                PacketTimeout(envelope, receipt);
        }
    }

    internal void Log(string message)
    {
        try
        {
            _outlet.Log(message);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("{0}; channel logging failed: {1}", message, ex);
        }
    }

    internal void Abort(string reason)
    {
        Log(reason);
        Shutdown();
        _outlet.Teardown();
    }

    private static void Disconnect(PacketReceipt? receipt)
    {
        if (receipt is null) return;
        receipt.Delivered = null;
        receipt.TimedOut = null;
    }

    internal void Shutdown()
    {
        lock (_lock)
        {
            if (_shutdown) return;
            _shutdown = true;
            _handlers.Clear();
            _systemHandlers.Clear();
            foreach (var e in _txRing)
            {
                Disconnect(e.Receipt);
            }

            _txRing.Clear();
            _rxRing.Clear();
            _pendingMessages.Clear();
            SignalSendReady();
            _completion.TrySetResult();
        }

        _closed.Cancel();
    }

    private MessageBase UnpackEnvelope(Envelope envelope)
    {
        var raw = envelope.Raw!;
        if (raw.Length < EnvelopeOverhead)
        {
            throw new ChannelException(ChannelExceptionType.NotRegistered, "Channel envelope too short");
        }

        var msgType = BinaryPrimitives.ReadUInt16BigEndian(raw);
        envelope.Sequence = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2));
        if (!_factories.TryGetValue(msgType, out var factory))
        {
            throw new ChannelException(ChannelExceptionType.NotRegistered, $"Unable to find constructor for Channel MSGTYPE 0x{msgType:x}");
        }

        var message = factory();
        message.Unpack(raw.AsSpan(EnvelopeOverhead));
        envelope.Unpacked = true;
        envelope.Message = message;
        return message;
    }

    /// <summary>Handles a decrypted channel packet payload (called by the link).</summary>
    /// <returns>Whether the frame can be proved: retained/delivered data or an already received duplicate.</returns>
    internal bool Receive(byte[] raw)
    {
        lock (_receiveLock) return ReceiveSerialized(raw);
    }

    private bool ReceiveSerialized(byte[] raw)
    {
        try
        {
            var envelope = new Envelope(null, raw, 0);
            List<MessageBase> ready = [];
            lock (_lock)
            {
                if (_shutdown) return false;
                if (raw.Length > _outlet.Mdu) throw new IOException("Channel envelope exceeds the negotiated MDU.");
                UnpackEnvelope(envelope);
                var distance = (envelope.Sequence - _nextRxSequence + SeqModulus) % SeqModulus;
                if (distance > SeqModulus / 2) return true;
                if (distance > WindowMax)
                {
                    Log($"Channel sequence {envelope.Sequence} exceeds the receive window at {_nextRxSequence}.");
                    return false;
                }
                if (!_rxRing.TryAdd(envelope.Sequence, envelope)) return true;

                while (_rxRing.Remove(_nextRxSequence, out var e))
                {
                    _nextRxSequence = (_nextRxSequence + 1) % SeqModulus;
                    ready.Add(e.Message!);
                }
            }

            foreach (var message in ready)
            {
                // Only library-owned stream readers run on this path. Optional user
                // observers run on a separate bounded worker and cannot stall it.
                if (RunSystemHandlers(message)) continue;
                var overflow = false;
                lock (_lock)
                {
                    if (_shutdown) return false;
                    if (_handlers.Count == 0) continue;
                    if (_pendingMessages.Count == MaximumPendingMessages)
                    {
                        overflow = true;
                    }
                    else
                    {
                        _pendingMessages.Enqueue(message);
                        if (!_handlerRunning)
                        {
                            _handlerRunning = true;
                            _handlerTask = Task.Run(DrainHandlers);
                        }
                    }
                }

                if (overflow)
                {
                    Abort("Channel pending-message limit exceeded by a stalled consumer.");
                    return false;
                }
            }
            lock (_lock) return !_shutdown;
        }
        catch (Exception ex)
        {
            Log("Error while receiving channel data: " + ex);
            return false;
        }
    }

    private bool RunSystemHandlers(MessageBase message)
    {
        Func<MessageBase, bool>[] handlers;
        lock (_lock)
        {
            if (_shutdown) return true;
            handlers = [.. _systemHandlers];
        }

        foreach (var handler in handlers)
        {
            if (handler(message)) return true;
        }

        return false;
    }

    private void DrainHandlers()
    {
        while (true)
        {
            MessageBase message;
            lock (_lock)
            {
                if (_shutdown || !_pendingMessages.TryDequeue(out message!))
                {
                    _handlerRunning = false;
                    return;
                }
            }

            RunHandlers(message);
        }
    }

    private void RunHandlers(MessageBase message)
    {
        Func<MessageBase, bool>[] handlers;
        lock (_lock)
        {
            handlers = [.. _handlers];
        }

        foreach (var handler in handlers)
        {
            lock (_lock)
            {
                if (_shutdown) return;
            }

            try
            {
                if (handler(message))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Log("Channel message handler failed: " + ex);
            }
        }
    }

}
