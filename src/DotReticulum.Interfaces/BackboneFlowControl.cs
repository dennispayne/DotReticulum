using System.Net.Sockets;

namespace DotReticulum.Interfaces;

internal sealed class BackboneTransmitBuffer(int maximumBytes, int maximumFrames)
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _frames = [];
    private int _offset;
    private int _buffered;
    private long _sent;

    public int Buffered { get { lock (_gate) return _buffered; } }
    public int Frames { get { lock (_gate) return _frames.Count; } }
    public long Sent { get { lock (_gate) return _sent; } }

    public bool Append(byte[] frame)
    {
        lock (_gate)
        {
            if (frame.Length > maximumBytes - _buffered || _frames.Count >= maximumFrames) return false;
            _frames.Enqueue(frame);
            _buffered += frame.Length;
            return true;
        }
    }

    // Exactly one consumer owns the head offset. Producers only append immutable, complete frames.
    public async ValueTask<int> DrainAsync(
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<int>> send, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> memory;
        lock (_gate)
        {
            if (_frames.Count == 0) return 0;
            memory = _frames.Peek().AsMemory(_offset);
        }
        int written;
        try { written = await send(memory, token).ConfigureAwait(false); }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending or SocketError.Interrupted)
        {
            return 0;
        }
        if (written < 0 || written > memory.Length) throw new IOException("Invalid Backbone socket write count.");
        lock (_gate)
        {
            _offset += written;
            _buffered -= written;
            _sent += written;
            if (_offset == _frames.Peek().Length)
            {
                _frames.Dequeue();
                _offset = 0;
            }
        }
        return written;
    }
}

internal sealed class BackboneEgressControl(double now)
{
    private long _previousSent;
    private int _zeroTicks;
    private double _lastDrain = now;
    public bool Stalled { get; private set; }

    public bool Evaluate(int buffered, long sent, double now)
    {
        var drained = sent - _previousSent;
        _previousSent = sent;
        if (buffered == 0)
        {
            _zeroTicks = 0;
            _lastDrain = now;
            Stalled = false;
            return false;
        }
        if (now - _lastDrain >= 12) return true;
        if (drained > 0)
        {
            _lastDrain = now;
            _zeroTicks = 0;
            var eta = (double)buffered / drained;
            if (buffered > 128 * 1024 && eta > 10) Stalled = true;
            else if (eta < 5 || buffered <= 128 * 1024) Stalled = false;
        }
        else if (buffered > 128 * 1024)
        {
            if (++_zeroTicks >= 3) Stalled = true;
        }
        else
        {
            _zeroTicks = 0;
            Stalled = false;
        }
        return false;
    }
}

internal sealed class BackboneFlapTable(BackbonePacketInterfaceOptions options)
{
    private readonly Dictionary<string, (double Last, int Count)> _entries = [];

    public int BlockedCount(double now, BackbonePacketInterfaceOptions? policy = null)
    {
        var active = policy ?? options;
        if (!active.BlockFastFlapping) return 0;
        Expire(now, active);
        return _entries.Values.Count(entry => entry.Count > active.FastFlapGrace);
    }

    public bool IsBlocked(string address, double now, BackbonePacketInterfaceOptions? policy = null)
    {
        var active = policy ?? options;
        if (!active.BlockFastFlapping) return false;
        Expire(now, active);
        return (_entries.TryGetValue(address, out var entry) && entry.Count > active.FastFlapGrace) ||
               (!_entries.ContainsKey(address) && _entries.Count >= active.MaximumFlapEntries);
    }

    public void Disconnected(string address, double connectedSeconds, double now, BackbonePacketInterfaceOptions? policy = null)
    {
        var active = policy ?? options;
        if (!active.BlockFastFlapping || connectedSeconds >= active.FastFlapThreshold.TotalSeconds) return;
        Expire(now, active);
        if (_entries.TryGetValue(address, out var entry))
            _entries[address] = (now, entry.Count == int.MaxValue ? int.MaxValue : entry.Count + 1);
        else if (_entries.Count < active.MaximumFlapEntries) _entries[address] = (now, 1);
    }

    private void Expire(double now, BackbonePacketInterfaceOptions active)
    {
        foreach (var address in _entries.Where(entry => now - entry.Value.Last > active.FastFlapBlockTime.TotalSeconds)
                     .Select(entry => entry.Key).ToArray())
            _entries.Remove(address);
    }
}
