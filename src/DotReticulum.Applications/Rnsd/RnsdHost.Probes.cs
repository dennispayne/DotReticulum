using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Rnsd;

public sealed partial class RnsdHost
{
    private LocalDestination? _probeDestination;
    private DateTimeOffset _lastProbeAnnounce;
    private static readonly TimeSpan ProbeAnnounceInterval = TimeSpan.FromHours(2);

    private void InitializeProbeResponder()
    {
        if (!_configuration.RespondToProbes || !Router.TransportEnabled) return;
        var identity = Router.TransportIdentity;
        var destination = Destination.Create(DestinationType.Single, "rnstransport", ["probe"], identity);
        _probeDestination = new LocalDestination(identity, destination)
        {
            AcceptLinks = false,
            ProofStrategy = ProofStrategy.All,
        };
        Router.RegisterLocalDestination(_probeDestination);
        _lastProbeAnnounce = _timeProvider.GetUtcNow() - ProbeAnnounceInterval + TimeSpan.FromSeconds(15);
        _log(3, $"Transport probe responder enabled at {Convert.ToHexStringLower(destination.Hash)}.");
    }

    internal async ValueTask TickProbeAnnouncementsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_probeDestination is null) return;
        var now = _timeProvider.GetUtcNow();
        if (now - _lastProbeAnnounce <= ProbeAnnounceInterval) return;
        _lastProbeAnnounce = now;
        await Router.AnnounceAsync(_probeDestination, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
