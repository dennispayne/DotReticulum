using DotReticulum.Core;

namespace DotReticulum.Transport;

public enum PathResponsiveness : byte
{
    Unknown,
    Unresponsive,
    Responsive,
}

public sealed partial class AnnounceEngine
{
    public PathResponsiveness GetPathResponsiveness(ReadOnlySpan<byte> destinationHash)
    {
        lock (_gate) return _paths.TryGetValue(Key(destinationHash), out var path) ? path.Responsiveness : PathResponsiveness.Unknown;
    }

    public bool SetPathResponsiveness(ReadOnlySpan<byte> destinationHash, PathResponsiveness state)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        lock (_gate)
        {
            if (!_paths.TryGetValue(Key(destinationHash), out var path)) return false;
            path.Responsiveness = state;
            return true;
        }
    }

    /// <summary>Only call after authenticating an LRPROOF against the announced destination identity.</summary>
    internal void RebalancePath(ReadOnlySpan<byte> destinationHash, int hops)
    {
        lock (_gate)
        {
            if (_paths.TryGetValue(Key(destinationHash), out var path)) path.Hops = hops;
        }
    }

    public int PendingPathResponses
    {
        get { lock (_gate) return _responses.Values.Sum(entries => entries.Count); }
    }

    internal void DeferRebroadcast(AnnounceRebroadcast due)
    {
        if (due.Packet.Context == Announce.PathResponseContext)
        {
            QueuePathResponse(due.Packet.DestinationHash.Span, due.TargetInterface, TimeSpan.Zero);
            return;
        }
        lock (_gate)
        {
            if (_table.TryGetValue(Key(due.Packet.DestinationHash.Span), out var entry)
                && entry.Packet.CalculateHash().AsSpan().SequenceEqual(due.Packet.CalculateHash()))
            {
                entry.Retries = Math.Max(0, entry.Retries - 1);
                entry.RetransmitAt = Now;
            }
        }
    }
}
