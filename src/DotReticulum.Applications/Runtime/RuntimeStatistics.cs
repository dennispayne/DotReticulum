using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Runtime;

public static class RuntimeStatistics
{
    public static byte[] InterfaceHash(IPacketInterface iface, string name) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{iface.GetType().Name}[{name}]"));

    public static Dictionary<object, object?> Collect(PacketInterfaceManager manager,
        IReadOnlyList<(string Name, IPacketInterface Interface)> names, byte[]? transportIdentity = null, bool profiling = false)
    {
        var counters = manager.GetStatistics();
        var configuredNames = names.ToDictionary(i => i.Interface, i => i.Name);
        var displayNames = new Dictionary<IPacketInterface, string>();
        foreach (var counter in counters)
        {
            var name = configuredNames.GetValueOrDefault(counter.Interface);
            if (name is null)
            {
                var device = (counter.Interface as IPacketInterfaceDiagnostics)?.GetDiagnostics();
                name = $"{counter.Interface.GetType().Name}[{device?.GetValueOrDefault("peer") ?? "local"}]";
            }
            displayNames[counter.Interface] = name;
        }
        long rxb = 0, txb = 0, arxb = 0, atxb = 0, rxPackets = 0, txPackets = 0, malformed = 0;
        long? bearerRx = 0, bearerTx = 0;
        var entries = new List<object?>();
        foreach (var counter in counters)
        {
            var name = displayNames[counter.Interface];
            var device = (counter.Interface as IPacketInterfaceDiagnostics)?.GetDiagnostics();
            var entry = new Dictionary<object, object?>
            {
                ["name"] = name,
                ["short_name"] = name,
                ["hash"] = InterfaceHash(counter.Interface, name),
                ["type"] = counter.Interface.GetType().Name,
                ["rxb"] = device?.GetValueOrDefault("rxb"),
                ["txb"] = device?.GetValueOrDefault("txb"),
                ["arxb"] = counter.ReceivedAnnounceBytes,
                ["atxb"] = counter.SentAnnounceBytes,
                ["arxc"] = counter.ReceivedAnnounces,
                ["atxc"] = counter.SentAnnounces,
                ["clients"] = device?.GetValueOrDefault("clients"),
                ["status"] = device?.GetValueOrDefault("status"),
                ["protocol_violations"] = counter.MalformedPackets,
                ["ifac_violations"] = null,
                ["dotreticulum_decoded_rxb"] = counter.ReceivedBytes,
                ["dotreticulum_decoded_txb"] = counter.SentBytes,
                ["dotreticulum_received_packets"] = counter.ReceivedPackets,
                ["dotreticulum_sent_packets"] = counter.SentPackets,
                ["dotreticulum_failed_sends"] = counter.FailedSends,
                ["dotreticulum_failed_send_bytes"] = counter.FailedSendBytes,
                ["dotreticulum_backpressure_waits"] = counter.BackpressureWaits,
                ["dotreticulum_pending_sends"] = counter.PendingSends,
            };
            if (counter.Parent is { } parent && displayNames.TryGetValue(parent, out var parentName))
            {
                entry["parent_interface_name"] = parentName;
                entry["parent_interface_hash"] = InterfaceHash(parent, parentName);
            }
            if (device is not null)
                foreach (var (key, value) in device) entry.TryAdd(key, value);
            entries.Add(entry);
            if (counter.Parent is not null) continue;
            bearerRx = bearerRx is { } received && device?.GetValueOrDefault("rxb") is { } actualRx ? received + MsgPack.ToInt64(actualRx) : null;
            bearerTx = bearerTx is { } sent && device?.GetValueOrDefault("txb") is { } actualTx ? sent + MsgPack.ToInt64(actualTx) : null;
            rxb += counter.ReceivedBytes;
            txb += counter.SentBytes;
            arxb += counter.ReceivedAnnounceBytes;
            atxb += counter.SentAnnounceBytes;
            rxPackets += counter.ReceivedPackets;
            txPackets += counter.SentPackets;
            malformed += counter.MalformedPackets;
        }
        var stats = new Dictionary<object, object?>
        {
            ["interfaces"] = entries.ToArray(),
            ["rxb"] = bearerRx,
            ["txb"] = bearerTx,
            ["dotreticulum_decoded_rxb"] = rxb,
            ["dotreticulum_decoded_txb"] = txb,
            ["arxb"] = arxb,
            ["atxb"] = atxb,
            ["rx_packets"] = rxPackets,
            ["tx_packets"] = txPackets,
            ["malformed_packets"] = malformed,
            ["rxqt"] = manager.QueuedPackets,
            ["rxqtd"] = null,
            ["tqpressure"] = manager.QueuePressure,
            ["dotreticulum_queue_capacity"] = manager.ChannelCapacity,
            ["dotreticulum_queue_policy"] = "wait",
            ["txq"] = null,
            ["transport_id"] = transportIdentity?.ToArray(),
            ["rss"] = null,
        };
        if (profiling)
        {
            using var process = Process.GetCurrentProcess();
            stats["rss"] = process.WorkingSet64;
            stats["dotreticulum_profile"] = new Dictionary<object, object?>
            {
                ["cpu_seconds"] = process.TotalProcessorTime.TotalSeconds,
                ["gc_heap_bytes"] = GC.GetTotalMemory(false),
                ["allocated_bytes"] = GC.GetTotalAllocatedBytes(),
                ["threads"] = process.Threads.Count,
            };
        }
        return stats;
    }
}
