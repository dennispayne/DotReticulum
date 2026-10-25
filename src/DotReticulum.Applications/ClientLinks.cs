using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications;

/// <summary>Outcome of <see cref="ClientLinks.ConnectAsync"/>: a link, or the reason there is none.</summary>
public sealed record LinkResult(Link? Link, string? Error)
{
    public bool Ok => Link is not null;
}

public static class ClientLinks
{
    /// <summary>Resolves the destination's identity (requesting a path when needed) and establishes a link to it.</summary>
    public static async Task<LinkResult> ConnectAsync(ReticulumNode node, byte[] destinationHash, TimeSpan pathTimeout, CancellationToken cancellationToken = default)
    {
        if (!await node.WaitForPathAsync(destinationHash, pathTimeout, cancellationToken).ConfigureAwait(false))
            return new LinkResult(null, "Path not found");
        var identity = await node.ResolveIdentityAsync(destinationHash, pathTimeout, cancellationToken).ConfigureAwait(false);
        if (identity is null) return new LinkResult(null, "Path not found");

        var up = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = node.Router.Links.Connect(identity, destinationHash, _ => up.TrySetResult(true), _ => up.TrySetResult(false));
        using var registration = cancellationToken.Register(() => up.TrySetCanceled(cancellationToken));
        bool established;
        try
        {
            established = await up.Task.WaitAsync(TimeSpan.FromSeconds(link.EstablishmentTimeout + 5), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            link.Teardown();
            throw;
        }
        if (!established || link.Status != LinkStatus.Active)
        {
            link.Teardown();
            return new LinkResult(null, "Could not establish link with " + CliSupport.PrettyHex(destinationHash));
        }

        return new LinkResult(link, null);
    }
}
