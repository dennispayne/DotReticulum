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
        PacketReceiptStatus.Failed => MessageState.Failed,
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
    private const int MaxTries = 5;
    private const int EnvelopeOverhead = 6;

    private readonly Link _link;
    private readonly object _lock = new();
    private readonly object _sendLock = new();
    private readonly List<Envelope> _txRing = [];
    private readonly List<Envelope> _rxRing = [];
    private readonly List<Func<MessageBase, bool>> _handlers = [];
    private readonly Dictionary<ushort, Func<MessageBase>> _factories = [];
    private int _nextSequence;
    private int _nextRxSequence;
    private int _fastRateRounds;
    private int _mediumRateRounds;

    internal Channel(Link link)
    {
        _link = link;
        if ((link.Rtt ?? 0) > RttSlow)
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
    public int Mdu => Math.Min(_link.Mdu - EnvelopeOverhead, 0xFFFF);

    public Link Link => _link;

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
        lock (_lock)
        {
            if (!_handlers.Contains(handler))
            {
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
        var outstanding = 0;
        foreach (var e in _txRing)
        {
            if (e.Receipt is null || e.Receipt.Status != PacketReceiptStatus.Delivered)
            {
                outstanding++;
            }
        }

        return outstanding < WindowSize;
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
                if (envelope.Raw.Length > _link.Mdu)
                {
                    throw new ChannelException(ChannelExceptionType.TooBig, $"Packed message too big for packet: {envelope.Raw.Length} > {_link.Mdu}");
                }

                _nextSequence = (reserved + 1) % SeqModulus;
            }

            var receipt = _link.SendWithContext(envelope.Raw, LinkContext.Channel, createReceipt: true, encrypt: true);
            if (receipt is null)
            {
                lock (_lock)
                {
                    _nextSequence = reserved;
                }

                throw new ChannelException(ChannelExceptionType.LinkNotReady, "Outlet did not transmit packet");
            }

            lock (_lock)
            {
                EmplaceEnvelope(envelope, _txRing);
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

        return envelope;
    }

    private static byte[] PackEnvelope(MessageBase message, int sequence)
    {
        var data = message.Pack();
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
        receipt.Delivered = _ => PacketDelivered(envelope);
        receipt.TimedOut = _ => PacketTimeout(envelope);
        receipt.Timeout = PacketTimeoutTime(envelope.Tries);
    }

    private double PacketTimeoutTime(int tries) =>
        Math.Pow(1.5, tries - 1) * Math.Max((_link.Rtt ?? 0) * 2.5, 0.025) * (_txRing.Count + 1.5);

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

    private void PacketDelivered(Envelope envelope)
    {
        lock (_lock)
        {
            if (!_txRing.Remove(envelope))
            {
                return;
            }

            if (WindowSize < WindowMaxSize)
            {
                WindowSize++;
            }

            var rtt = _link.Rtt ?? 0;
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

    private void PacketTimeout(Envelope envelope)
    {
        if (envelope.State == MessageState.Delivered)
        {
            return;
        }

        var teardown = false;
        lock (_lock)
        {
            if (!_txRing.Contains(envelope))
            {
                return;
            }

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
                    if (WindowMaxSize > WindowMinSize + _windowFlexibility)
                    {
                        WindowMaxSize--;
                    }
                }
            }
        }

        if (teardown)
        {
            _link.Log("Channel retry count exceeded, tearing down link " + _link.LinkIdHex);
            Shutdown();
            _link.Teardown();
            return;
        }

        var receipt = _link.SendWithContext(envelope.Raw!, LinkContext.Channel, createReceipt: true, encrypt: true);
        if (receipt is null)
        {
            return;
        }

        bool delivered;
        lock (_lock)
        {
            Track(envelope, receipt);
            UpdatePacketTimeouts();
            delivered = receipt.Status == PacketReceiptStatus.Delivered;
        }

        if (delivered)
        {
            PacketDelivered(envelope);
        }
    }

    internal void Shutdown()
    {
        lock (_lock)
        {
            _handlers.Clear();
            foreach (var e in _txRing)
            {
                if (e.Receipt is { } r)
                {
                    r.Delivered = null;
                    r.TimedOut = null;
                }
            }

            _txRing.Clear();
            _rxRing.Clear();
        }
    }

    private bool EmplaceEnvelope(Envelope envelope, List<Envelope> ring)
    {
        for (var i = 0; i < ring.Count; i++)
        {
            var existing = ring[i];
            if (envelope.Sequence == existing.Sequence)
            {
                return false;
            }

            if (envelope.Sequence < existing.Sequence && !(_nextRxSequence - envelope.Sequence > SeqMax / 2))
            {
                ring.Insert(i, envelope);
                return true;
            }
        }

        ring.Add(envelope);
        return true;
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
    internal void Receive(byte[] raw)
    {
        try
        {
            var envelope = new Envelope(null, raw, 0);
            List<MessageBase> ready = [];
            lock (_lock)
            {
                UnpackEnvelope(envelope);
                if (envelope.Sequence < _nextRxSequence)
                {
                    var windowOverflow = (_nextRxSequence + WindowMax) % SeqModulus;
                    if (windowOverflow < _nextRxSequence)
                    {
                        if (envelope.Sequence > windowOverflow)
                        {
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }
                else if (envelope.Sequence > _nextRxSequence + WindowMax)
                {
                    return;
                }

                if (!EmplaceEnvelope(envelope, _rxRing))
                {
                    return;
                }

                List<Envelope> contiguous = [];
                foreach (var e in _rxRing)
                {
                    if (e.Sequence == _nextRxSequence)
                    {
                        contiguous.Add(e);
                        _nextRxSequence = (_nextRxSequence + 1) % SeqModulus;
                        if (_nextRxSequence == 0)
                        {
                            foreach (var wrapped in _rxRing)
                            {
                                if (wrapped.Sequence == _nextRxSequence)
                                {
                                    contiguous.Add(wrapped);
                                    _nextRxSequence = (_nextRxSequence + 1) % SeqModulus;
                                }
                            }
                        }
                    }
                }

                foreach (var e in contiguous)
                {
                    _rxRing.Remove(e);
                    ready.Add(e.Unpacked ? e.Message! : UnpackEnvelope(e));
                }
            }

            foreach (var message in ready)
            {
                RunHandlers(message);
            }
        }
        catch (Exception ex)
        {
            _link.Log("Error while receiving channel data: " + ex.Message);
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
            try
            {
                if (handler(message))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _link.Log("Channel message handler failed: " + ex.Message);
            }
        }
    }

}
