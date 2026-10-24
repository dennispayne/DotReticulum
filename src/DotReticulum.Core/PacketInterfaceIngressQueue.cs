namespace DotReticulum.Core;

/// <summary>A live view of one manager's bounded ingress queue; its identity scopes shared bearer feedback.</summary>
public sealed class PacketInterfaceIngressQueue
{
    private sealed record Source(Func<int> Depth, int Capacity);
    private Source _source;

    internal PacketInterfaceIngressQueue(Func<int> depth, int capacity)
    {
        _source = new Source(depth, capacity);
    }

    public int Depth => Volatile.Read(ref _source).Depth();
    public int Capacity => Volatile.Read(ref _source).Capacity;
    public event Action<int, int>? HighWatermarkReached;

    internal void SetSource(Func<int> depth, int capacity)
    {
        ArgumentNullException.ThrowIfNull(depth);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Volatile.Write(ref _source, new Source(depth, capacity));
    }

    internal void NotifyPressure()
    {
        var source = Volatile.Read(ref _source);
        var depth = source.Depth();
        var highWatermark = Math.Max(128, Math.Max(4, (int)(0.9 * source.Capacity)));
        if (depth < highWatermark || HighWatermarkReached is not { } observers) return;
        foreach (Action<int, int> observer in observers.GetInvocationList())
        {
            try { observer(depth, source.Capacity); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Ingress feedback observer failed: {0}", error); }
        }
    }
}

/// <summary>Connects a bearer's ingress control to its owning manager before the bearer starts.</summary>
public interface IPacketInterfaceIngressFeedback
{
    void SetIngressQueue(PacketInterfaceIngressQueue queue);
}
