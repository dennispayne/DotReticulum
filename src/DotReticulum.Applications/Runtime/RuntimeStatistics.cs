using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Runtime;

public static class RuntimeStatistics
{
    private sealed record Sample(long ReceivedBytes, long SentBytes, long SentPackets, long AcceptedPackets,
        long ReceivedAnnounceBytes, long SentAnnounceBytes, long ReceivedPathRequestBytes, long SentPathRequestBytes);

    private sealed record Rates(double? ReceiveBitsPerSecond, double? SendBitsPerSecond,
        double? ReceivePacketsPerSecond, double? SendPacketsPerSecond,
        double? AnnounceReceiveBitsPerSecond, double? AnnounceSendBitsPerSecond,
        double? PathReceiveBitsPerSecond, double? PathSendBitsPerSecond);

    private sealed class TrafficSampler
    {
        private readonly object _gate = new();
        private Dictionary<IPacketInterface, Sample> _previous = [];
        private long? _timestamp;

        public Dictionary<IPacketInterface, Rates> SampleRates(IReadOnlyList<PacketInterfaceStatistics> counters,
            IReadOnlyDictionary<IPacketInterface, InterfaceTrafficControlInfo> traffic, TimeProvider time)
        {
            lock (_gate)
            {
                var timestamp = time.GetTimestamp();
                var elapsed = _timestamp is { } previousTimestamp
                    ? time.GetElapsedTime(previousTimestamp, timestamp).TotalSeconds : 0;
                var measured = new Dictionary<IPacketInterface, Rates>();
                var next = new Dictionary<IPacketInterface, Sample>();
                foreach (var counter in counters)
                {
                    var control = traffic.GetValueOrDefault(counter.Interface);
                    var sample = new Sample(counter.ReceivedBytes, counter.SentBytes, counter.SentPackets,
                        counter.AcceptedPackets, control?.ReceivedAnnounceBytes ?? counter.ReceivedAnnounceBytes,
                        control?.SentAnnounceBytes ?? counter.SentAnnounceBytes, control?.ReceivedPathRequestBytes ?? 0,
                        control?.SentPathRequestBytes ?? 0);
                    next[counter.Interface] = sample;
                    if (elapsed <= 0 || !_previous.TryGetValue(counter.Interface, out var old)) continue;
                    static double Rate(long current, long previous, double seconds) =>
                        Math.Max(0, current - previous) * 8d / seconds;
                    measured[counter.Interface] = new Rates(
                        Rate(sample.ReceivedBytes, old.ReceivedBytes, elapsed),
                        Rate(sample.SentBytes, old.SentBytes, elapsed),
                        Math.Max(0, sample.AcceptedPackets - old.AcceptedPackets) / elapsed,
                        Math.Max(0, sample.SentPackets - old.SentPackets) / elapsed,
                        Rate(sample.ReceivedAnnounceBytes, old.ReceivedAnnounceBytes, elapsed),
                        Rate(sample.SentAnnounceBytes, old.SentAnnounceBytes, elapsed),
                        Rate(sample.ReceivedPathRequestBytes, old.ReceivedPathRequestBytes, elapsed),
                        Rate(sample.SentPathRequestBytes, old.SentPathRequestBytes, elapsed));
                }
                _previous = next;
                _timestamp = timestamp;
                return measured;
            }
        }
    }

    private static readonly ConditionalWeakTable<PacketInterfaceManager, TrafficSampler> TrafficSamplers = new();

    public static byte[] InterfaceHash(IPacketInterface iface, string name) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{iface.GetType().Name}[{name}]"));

    public static Dictionary<object, object?> Collect(PacketInterfaceManager manager,
        IReadOnlyList<(string Name, IPacketInterface Interface)> names, byte[]? transportIdentity = null, bool profiling = false,
        TransportRouter? router = null, TimeProvider? timeProvider = null)
    {
        var counters = manager.GetStatistics();
        var traffic = router is null ? new Dictionary<IPacketInterface, InterfaceTrafficControlInfo>()
            : counters.ToDictionary(counter => counter.Interface, counter => router.GetInterfaceTrafficControl(counter.Interface));
        var rates = TrafficSamplers.GetValue(manager, _ => new TrafficSampler())
            .SampleRates(counters, traffic, timeProvider ?? TimeProvider.System);
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
        long rxb = 0, txb = 0, arxb = 0, atxb = 0, prxb = 0, ptxb = 0, prxc = 0, ptxc = 0;
        long rxPackets = 0, txPackets = 0, malformed = 0;
        double? rxSpeed = 0, txSpeed = 0;
        double arxSpeed = 0, atxSpeed = 0, prxSpeed = 0, ptxSpeed = 0;
        double arxFrequency = 0, atxFrequency = 0, prxFrequency = 0, ptxFrequency = 0;
        var roots = counters.Where(counter => counter.Parent is null).ToArray();
        var ratesAvailable = roots.Length > 0 && roots.All(counter => rates.ContainsKey(counter.Interface));
        var rootRates = roots.Select(counter => rates.GetValueOrDefault(counter.Interface)).ToArray();
        var entries = new List<object?>();
        foreach (var counter in counters)
        {
            var name = displayNames[counter.Interface];
            var device = (counter.Interface as IPacketInterfaceDiagnostics)?.GetDiagnostics();
            var entry = new Dictionary<object, object?>
            {
                ["name"] = name,
                ["short_name"] = name,
                ["outgoing"] = counter.Outgoing,
                ["hash"] = InterfaceHash(counter.Interface, name),
                ["type"] = counter.Interface.GetType().Name,
                ["rxb"] = counter.ReceivedBytes,
                ["txb"] = counter.SentBytes,
                ["arxb"] = counter.ReceivedAnnounceBytes,
                ["atxb"] = counter.SentAnnounceBytes,
                ["arxc"] = counter.ReceivedAnnounces,
                ["atxc"] = counter.SentAnnounces,
                ["clients"] = device?.GetValueOrDefault("clients"),
                ["status"] = device?.GetValueOrDefault("status"),
                ["protocol_violations"] = counter.ProtocolViolations,
                ["packet_filter_hits"] = counter.FilteredPackets,
                ["ifac_violations"] = counter.IfacViolations,
                ["ifac_size"] = counter.IfacSize,
                ["ifac_netname"] = counter.IfacNetworkName,
                ["ifac_signature"] = counter.IfacSignature,
                ["bitrate"] = counter.Interface.Bitrate > 0 ? counter.Interface.Bitrate : (long?)null,
                ["dotreticulum_hardware_mtu"] = counter.Interface.HardwareMtu,
                ["dotreticulum_mtu_discovery"] = counter.Interface.SupportsMtuDiscovery,
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
            if (router is not null)
            {
                var control = traffic[counter.Interface];
                var children = counters.Where(child => ReferenceEquals(child.Parent, counter.Interface))
                    .Select(child => traffic.GetValueOrDefault(child.Interface)).Where(child => child is not null).ToArray();
                entry["arxb"] = control.ReceivedAnnounceBytes;
                entry["atxb"] = control.SentAnnounceBytes;
                entry["arxc"] = control.ReceivedAnnounces;
                entry["atxc"] = control.SentAnnounces;
                entry["prxb"] = control.ReceivedPathRequestBytes;
                entry["ptxb"] = control.SentPathRequestBytes;
                entry["prxc"] = control.ReceivedPathRequests;
                entry["ptxc"] = control.SentPathRequests;
                entry["incoming_announce_frequency"] = control.IncomingAnnounceFrequency;
                entry["outgoing_announce_frequency"] = control.OutgoingAnnounceFrequency;
                entry["incoming_pr_frequency"] = control.IncomingPathRequestFrequency;
                entry["outgoing_pr_frequency"] = control.OutgoingPathRequestFrequency;
                entry["burst_active"] = control.AnnounceBurstActive;
                entry["pr_burst_active"] = control.PathRequestBurstActive;
                entry["burst_activated"] = control.AnnounceBurstActivated;
                entry["pr_burst_activated"] = control.PathRequestBurstActivated;
                if (children.Length > 0)
                {
                    var activeAnnounceChildren = children.Where(child => child!.AnnounceBurstActive).ToArray();
                    var activePathChildren = children.Where(child => child!.PathRequestBurstActive).ToArray();
                    entry["burst_active"] = activeAnnounceChildren.Length > 0;
                    entry["pr_burst_active"] = activePathChildren.Length > 0;
                    entry["burst_count"] = activeAnnounceChildren.Length;
                    entry["pr_burst_count"] = activePathChildren.Length;
                    var announceActivation = (activeAnnounceChildren.Length > 0 ? activeAnnounceChildren : children)
                        .Where(child => child!.AnnounceBurstActivated.HasValue)
                        .Select(child => child!.AnnounceBurstActivated!.Value).DefaultIfEmpty().Min();
                    var pathActivation = (activePathChildren.Length > 0 ? activePathChildren : children)
                        .Where(child => child!.PathRequestBurstActivated.HasValue)
                        .Select(child => child!.PathRequestBurstActivated!.Value).DefaultIfEmpty().Min();
                    entry["burst_activated"] = announceActivation > 0 ? announceActivation : null;
                    entry["pr_burst_activated"] = pathActivation > 0 ? pathActivation : null;
                }
                entry["held_announces"] = router.HeldIngressAnnounces(counter.Interface);
                entry["announce_queue"] = router.QueuedAnnounces(counter.Interface);
                entry["gravity"] = router.GetInterfaceAnnounceOptions(counter.Interface).Gravity;
                var rateLimit = router.GetInterfaceAnnounceOptions(counter.Interface).RateLimit;
                entry["announce_rate_target"] = rateLimit?.Target.TotalSeconds;
                entry["announce_rate_grace"] = rateLimit?.Grace;
                entry["announce_rate_penalty"] = rateLimit?.Penalty.TotalSeconds;
                entry["mode"] = (long)router.GetInterfaceMode(counter.Interface);
                entry["announces_to_internal"] = router.GetInterfaceAnnounceOptions(counter.Interface).AnnouncesToInternal;
                prxb += control.ReceivedPathRequestBytes;
                ptxb += control.SentPathRequestBytes;
                prxc += control.ReceivedPathRequests;
                ptxc += control.SentPathRequests;
                arxFrequency += control.IncomingAnnounceFrequency;
                atxFrequency += control.OutgoingAnnounceFrequency;
                prxFrequency += control.IncomingPathRequestFrequency;
                ptxFrequency += control.OutgoingPathRequestFrequency;
                if (device?.GetValueOrDefault("blocked_ip_count") is { } blockedCount)
                    entry["blocked_ips"] = blockedCount;
                if (device?.GetValueOrDefault("tx_buffered_bytes") is { } buffered)
                    entry["txbuffered"] = buffered;
                else if (device?.GetValueOrDefault("txbuffered") is { } upstreamBuffered)
                    entry["txbuffered"] = upstreamBuffered;
                if (device?.GetValueOrDefault("tx_drops") is { } drops)
                    entry["txdrp"] = drops;
                else if (device?.GetValueOrDefault("txdrp") is { } upstreamDrops)
                    entry["txdrp"] = upstreamDrops;
                if (device?.GetValueOrDefault("tx_dropped_bytes") is { } droppedBytes)
                    entry["txdrb"] = droppedBytes;
                else if (device?.GetValueOrDefault("txdrb") is { } upstreamDroppedBytes)
                    entry["txdrb"] = upstreamDroppedBytes;
                if (device?.GetValueOrDefault("tx_stalled") is { } stalled)
                    entry["txstalled"] = stalled;
                else if (device?.GetValueOrDefault("txstalled") is { } upstreamStalled)
                    entry["txstalled"] = upstreamStalled;
                if (device?.GetValueOrDefault("tx_write_pending_bytes") is { } pendingWriteBytes)
                    entry["tx_write_pending_bytes"] = pendingWriteBytes;
            }
            if (rates.TryGetValue(counter.Interface, out var interfaceRates))
            {
                entry["rxs"] = interfaceRates.ReceiveBitsPerSecond;
                entry["txs"] = interfaceRates.SendBitsPerSecond;
                entry["arxs"] = interfaceRates.AnnounceReceiveBitsPerSecond;
                entry["atxs"] = interfaceRates.AnnounceSendBitsPerSecond;
                if (router is not null)
                {
                    entry["prxs"] = interfaceRates.PathReceiveBitsPerSecond;
                    entry["ptxs"] = interfaceRates.PathSendBitsPerSecond;
                }
                arxSpeed += interfaceRates.AnnounceReceiveBitsPerSecond ?? 0;
                atxSpeed += interfaceRates.AnnounceSendBitsPerSecond ?? 0;
                prxSpeed += interfaceRates.PathReceiveBitsPerSecond ?? 0;
                ptxSpeed += interfaceRates.PathSendBitsPerSecond ?? 0;
                if (counter.Parent is null)
                {
                    rxSpeed += interfaceRates.ReceiveBitsPerSecond;
                    txSpeed += interfaceRates.SendBitsPerSecond;
                }
            }
            entries.Add(entry);
            if (counter.Parent is not null) continue;
            rxPackets += counter.AcceptedPackets;
            txPackets += counter.SentPackets;
            rxb += counter.ReceivedBytes;
            txb += counter.SentBytes;
            arxb += counter.ReceivedAnnounceBytes;
            atxb += counter.SentAnnounceBytes;
            malformed += counter.MalformedPackets;
        }
        var inbound = router?.InboundQueues;
        var queueCapacities = router?.InboundQueueCapacities;
        var queueTotalCapacity = queueCapacities?.Sum() ?? 0;
        var queueHeights = inbound?.Heights ?? [];
        var queueDrops = inbound?.Dropped ?? [];
        double Pressure(int index) => queueCapacities is not null && index < queueCapacities.Count
            && queueCapacities[index] > 0 ? (double)queueHeights[index] / queueCapacities[index] : 0;
        using var process = Process.GetCurrentProcess();
        var stats = new Dictionary<object, object?>
        {
            ["interfaces"] = entries.ToArray(),
            ["rxb"] = rxb,
            ["txb"] = txb,
            ["dotreticulum_decoded_rxb"] = rxb,
            ["dotreticulum_decoded_txb"] = txb,
            ["arxb"] = arxb,
            ["atxb"] = atxb,
            ["prxb"] = router is null ? null : prxb,
            ["ptxb"] = router is null ? null : ptxb,
            ["prxc"] = router is null ? null : prxc,
            ["ptxc"] = router is null ? null : ptxc,
            ["rx_packets"] = rxPackets,
            ["tx_packets"] = txPackets,
            ["malformed_packets"] = malformed,
            ["rxqt"] = inbound?.Total ?? manager.QueuedPackets,
            ["rxqd"] = queueHeights.Count > 0 ? queueHeights[0] : null,
            ["rxqa"] = queueHeights.Count > 1 ? queueHeights[1] : null,
            ["rxqp"] = queueHeights.Count > 2 ? queueHeights[2] : null,
            ["rxqil"] = queueHeights.Count > 3 ? queueHeights[3] : null,
            ["rxqtd"] = queueDrops.Count > 0 ? queueDrops.Sum() : null,
            ["rxqdd"] = queueDrops.Count > 0 ? queueDrops[0] : null,
            ["rxqad"] = queueDrops.Count > 1 ? queueDrops[1] : null,
            ["rxqpd"] = queueDrops.Count > 2 ? queueDrops[2] : null,
            ["rxqild"] = queueDrops.Count > 3 ? queueDrops[3] : null,
            ["tqpressure"] = inbound is not null && queueTotalCapacity > 0 ? (double)inbound.Total / queueTotalCapacity : manager.QueuePressure,
            ["dqpressure"] = Pressure(0),
            ["aqpressure"] = Pressure(1),
            ["pqpressure"] = Pressure(2),
            ["ilqpressure"] = Pressure(3),
            ["dotreticulum_queue_capacity"] = manager.ChannelCapacity,
            ["dotreticulum_queue_policy"] = "wait",
            ["dotreticulum_manager_queue_depth"] = manager.QueuedPackets,
            ["rxs"] = ratesAvailable ? rxSpeed : null,
            ["txs"] = ratesAvailable ? txSpeed : null,
            ["arxs"] = ratesAvailable ? arxSpeed : null,
            ["atxs"] = ratesAvailable ? atxSpeed : null,
            ["prxs"] = ratesAvailable ? prxSpeed : null,
            ["ptxs"] = ratesAvailable ? ptxSpeed : null,
            ["arxf"] = router is null ? null : arxFrequency,
            ["atxf"] = router is null ? null : atxFrequency,
            ["prxf"] = router is null ? null : prxFrequency,
            ["ptxf"] = router is null ? null : ptxFrequency,
            ["rxpps"] = ratesAvailable && router is not null ? Math.Round(rootRates.Sum(rate => rate!.ReceivePacketsPerSecond ?? 0)) : null,
            ["txpps"] = ratesAvailable && router is not null ? Math.Round(rootRates.Sum(rate => rate!.SendPacketsPerSecond ?? 0)) : null,
            ["txq"] = null,
            ["transport_id"] = transportIdentity?.ToArray(),
            ["rss"] = process.WorkingSet64,
        };
        if (profiling)
        {
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
