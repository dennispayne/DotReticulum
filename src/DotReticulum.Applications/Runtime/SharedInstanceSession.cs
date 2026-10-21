using System.Net;
using System.Net.Sockets;
using DotReticulum.Core;
using DotReticulum.Interfaces;

namespace DotReticulum.Applications.Runtime;

public sealed record SharedInstanceOptions
{
    public bool Enabled { get; init; } = true;
    public bool Required { get; init; }
    public int InstancePort { get; init; } = 37428;
    public int ControlPort { get; init; } = 37429;
    /// <summary>Abstract socket name without the upstream "\0rns/" prefix. Linux only; null selects loopback TCP.</summary>
    public string? SocketPath { get; init; }
    public byte[]? RpcKey { get; init; }
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumClients { get; init; } = 64;
    public EndPoint BearerEndpoint => SocketPath is null ? new IPEndPoint(IPAddress.Loopback, InstancePort) : Unix("");
    public EndPoint RpcEndpoint => SocketPath is null ? new IPEndPoint(IPAddress.Loopback, ControlPort) : Unix("/rpc");
    private EndPoint Unix(string suffix)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Upstream shared Unix sockets require Linux abstract AF_UNIX; select shared_instance_type=tcp on this platform.");
        if (string.IsNullOrWhiteSpace(SocketPath) || SocketPath.Contains('\0')) throw new ArgumentException("Invalid shared socket name.");
        return new UnixDomainSocketEndPoint("\0rns/" + SocketPath + suffix);
    }
}

public enum SharedInstanceRole { Standalone, Host, Client }

/// <summary>Election owns its reservation; the manager starts the resulting bearer once after constructing its router.</summary>
public sealed class SharedInstanceSession : IAsyncDisposable
{
    private SharedInstanceSession(SharedInstanceRole role, IPacketInterface? bearer)
    {
        Role = role;
        Bearer = bearer;
    }
    public SharedInstanceRole Role { get; }
    public IPacketInterface? Bearer { get; }
    public ISpawningPacketInterface? Server => Bearer as ISpawningPacketInterface;

    public static async ValueTask<SharedInstanceSession> OpenAsync(SharedInstanceOptions options,
        CancellationToken token = default, Action<Exception>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        token.ThrowIfCancellationRequested();
        if (!options.Enabled)
        {
            if (options.Required) throw new InvalidOperationException("Shared instance required but sharing is disabled.");
            return new(SharedInstanceRole.Standalone, null);
        }
        if (options.InstancePort is < 1 or > 65535 || options.ControlPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options));
        var endpoint = options.BearerEndpoint;
        if (!options.Required)
        {
            var server = new LocalSharedServerInterface(endpoint, options.MaximumClients, diagnostic);
            try
            {
                server.Reserve();
                return new(SharedInstanceRole.Host, server);
            }
            catch (SocketException ex)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                Report(diagnostic, ex);
            }
        }
        var client = new LocalSharedPacketInterface(endpoint, options.ReconnectDelay, options.OperationTimeout, diagnostic);
        try
        {
            await client.PrepareAsync(token).ConfigureAwait(false);
            return new(SharedInstanceRole.Client, client);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Report(diagnostic, ex);
            if (options.Required) throw new IOException("No reachable shared instance is available, but one was required.", ex);
            return new(SharedInstanceRole.Standalone, null);
        }
    }
    private static void Report(Action<Exception>? diagnostic, Exception error)
    {
        try { diagnostic?.Invoke(error); }
        catch (Exception observer) { System.Diagnostics.Trace.TraceError("Shared election diagnostic failed: {0}; original: {1}", observer, error); }
    }
    public ValueTask DisposeAsync() => Bearer?.DisposeAsync() ?? ValueTask.CompletedTask;
}
