using DotReticulum.Core;
using DotReticulum.Interfaces;

namespace DotReticulum.Applications.Rnsd;

public sealed partial class RnsdHost
{
    private readonly Dictionary<IPacketInterface, Action> _interfaceReconfigure = new();
    private readonly HashSet<IPacketInterface> _pendingTunnelSynthesis = new();
    private readonly System.Threading.Channels.Channel<bool> _tunnelSignals =
        System.Threading.Channels.Channel.CreateBounded<bool>(new System.Threading.Channels.BoundedChannelOptions(1)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    private void TransportInterfaceConnected(IPacketInterface iface)
    {
        lock (_gate)
        {
            if (_interfaceReconfigure.TryGetValue(iface, out var configure)) configure();
            if (iface is not TcpClientPacketInterface) return;
            if (_pendingTunnelSynthesis.Count >= 1024 && !_pendingTunnelSynthesis.Contains(iface))
            {
                _log(2, "Tunnel synthesis queue capacity reached; connection was not queued.");
                return;
            }
            _pendingTunnelSynthesis.Add(iface);
        }
        _tunnelSignals.Writer.TryWrite(true);
    }

    private void TransportInterfaceDisconnected(IPacketInterface iface) => Router.DetachInterface(iface);

    private void TransportInterfaceRemoved(IPacketInterface iface)
    {
        Router.DetachInterface(iface);
        lock (_gate)
        {
            _interfaceReconfigure.Remove(iface);
            _pendingTunnelSynthesis.Remove(iface);
        }
    }

    private async Task ProcessTunnelSynthesisAsync(CancellationToken token)
    {
        await foreach (var _ in _tunnelSignals.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            IPacketInterface[] pending;
            lock (_gate)
            {
                pending = _pendingTunnelSynthesis.ToArray();
                _pendingTunnelSynthesis.Clear();
            }
            foreach (var iface in pending)
            {
                await _mutation.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!_manager.Contains(iface) || iface is not IReconnectingPacketInterface { IsConnected: true }) continue;
                    try { await Router.SynthesizeTunnelAsync(iface, InterfaceHash(iface), token).ConfigureAwait(false); }
                    catch (IOException error) { _log(2, $"Tunnel synthesis interrupted by interface loss: {error.Message}"); }
                    catch (ObjectDisposedException) when (!_manager.Contains(iface)) { }
                }
                finally { _mutation.Release(); }
            }
        }
    }
}
