using System.Runtime.CompilerServices;
using DotReticulum.Core;

namespace DotReticulum.Interfaces;

internal interface IBackboneIngressPeer
{
    bool IngressGated { get; }
    double IngressHoldUntil { get; }
    (long Bytes, long Frames) PeekIngressSample();
    (long Bytes, long Frames) TakeIngressSample();
    void SetIngressGate(bool gated, double holdUntil = 0);
}

internal sealed class BackboneSharedIngressState
{
    private static readonly ConditionalWeakTable<PacketInterfaceIngressQueue, BackboneSharedIngressState> States = new();
    private readonly object _gate = new();
    private readonly List<IBackboneIngressPeer> _peers = [];
    private readonly BackboneFlapTable _flaps;
    private readonly PacketInterfaceIngressQueue? _queue;
    private CancellationTokenSource? _lifetime;
    private Task _worker = Task.CompletedTask;
    private int _owners;
    private double _snapshot;
    private long _immediateEvaluations;

    internal BackboneSharedIngressState(BackbonePacketInterfaceOptions options, double now,
        PacketInterfaceIngressQueue? queue = null)
    {
        _queue = queue;
        _flaps = new(options);
        _snapshot = now;
    }

    public PacketInterfaceIngressQueue? Queue => _queue;
    public int PeerCount { get { lock (_gate) return _peers.Count; } }
    internal long ImmediateEvaluations => Interlocked.Read(ref _immediateEvaluations);

    public static BackboneSharedIngressState Acquire(PacketInterfaceIngressQueue? queue, BackbonePacketInterfaceOptions options)
    {
        var state = queue is null ? new BackboneSharedIngressState(options, BackbonePeerEndpoint.Now) :
            States.GetValue(queue, key => new(options, BackbonePeerEndpoint.Now, key));
        lock (state._gate)
        {
            if (state._owners++ == 0 && queue is not null)
            {
                state._snapshot = BackbonePeerEndpoint.Now;
                queue.HighWatermarkReached += state.HighWatermarkReached;
                var lifetime = new CancellationTokenSource();
                state._lifetime = lifetime;
                var previous = state._worker;
                state._worker = Task.Run(async () =>
                {
                    await previous.ConfigureAwait(false);
                    await state.PollAsync(lifetime.Token).ConfigureAwait(false);
                });
            }
        }
        return state;
    }

    public void Register(IBackboneIngressPeer peer)
    {
        lock (_gate) if (!_peers.Contains(peer)) _peers.Add(peer);
    }

    public void Unregister(IBackboneIngressPeer peer)
    {
        lock (_gate) _peers.Remove(peer);
    }

    public bool IsBlocked(string address, double now, BackbonePacketInterfaceOptions options)
    {
        lock (_gate) return _flaps.IsBlocked(address, now, options);
    }

    public int BlockedCount(double now, BackbonePacketInterfaceOptions options)
    {
        lock (_gate) return _flaps.BlockedCount(now, options);
    }

    public void Disconnected(string address, double connectedSeconds, double now, BackbonePacketInterfaceOptions options)
    {
        lock (_gate) _flaps.Disconnected(address, connectedSeconds, now, options);
    }

    public void Evaluate(int depth, int capacity, double now, bool immediate = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        lock (_gate)
        {
            var samples = _peers.Select(peer => (Peer: peer, Sample: peer.PeekIngressSample())).ToArray();
            var span = Math.Max(now - _snapshot, 0.001);
            var mid = Math.Max(2, (int)(0.68 * capacity));
            var low = (int)(0.10 * capacity);
            if (immediate || depth > mid)
            {
                var selected = samples.OrderByDescending(sample => sample.Sample.Frames).FirstOrDefault();
                if (selected.Peer is not null && selected.Sample.Frames > 0 && !selected.Peer.IngressGated)
                {
                    var available = samples.Sum(sample => sample.Sample.Bytes) / span;
                    var allocation = 1.0 / (Math.Max(samples.Length, 32) * 1.5);
                    var hold = immediate ? span / allocation : available / Math.Max(available * allocation, 1) * span;
                    selected.Peer.SetIngressGate(true, now + hold);
                }
            }
            else if (depth < low)
            {
                var release = samples.FirstOrDefault(sample => sample.Peer.IngressGated && sample.Peer.IngressHoldUntil <= now);
                release.Peer?.SetIngressGate(false);
            }
            if (!immediate)
            {
                _snapshot = now;
                foreach (var sample in samples) sample.Peer.TakeIngressSample();
            }
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (token.IsCancellationRequested) break;
                    Evaluate(_queue!.Depth, _queue.Capacity, BackbonePeerEndpoint.Now);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Backbone shared ingress control failed: {0}", error);
            throw;
        }
    }

    private void HighWatermarkReached(int depth, int capacity)
    {
        lock (_gate)
        {
            if (_owners == 0) return;
            var high = Math.Max(128, Math.Max(4, (int)(0.90 * capacity)));
            if (depth < high) return;
            Evaluate(depth, capacity, BackbonePeerEndpoint.Now, immediate: true);
            Interlocked.Increment(ref _immediateEvaluations);
        }
    }

    public async ValueTask ReleaseAsync()
    {
        Task worker;
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            if (_owners <= 0) throw new InvalidOperationException("The Backbone ingress state has no owner to release.");
            if (--_owners != 0) return;
            if (_queue is not null) _queue.HighWatermarkReached -= HighWatermarkReached;
            lifetime = _lifetime;
            lifetime?.Cancel();
            worker = _worker;
        }
        try { await worker.ConfigureAwait(false); }
        finally { lifetime?.Dispose(); }
    }
}
