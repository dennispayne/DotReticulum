using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Transport;
using Channel = System.Threading.Channels.Channel;

namespace DotReticulum.Applications.Tests;

// Actual RNS 1.5.7 a29bfb51c468945c96a1ee22cae0114e993e2391 observations:
// GenerateProofProbePolicyVectors.py; all three canonical source digests are in the fixture.
[Trait("Category", "Integration")]
public sealed class ProofProbePolicyTests
{
    private static JsonDocument ReadVectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ProofProbePolicy-vectors.json")));

    [Fact]
    public async Task ActualStartupAndDefaultProofPoliciesMatchAllReferenceObservations()
    {
        using var vectors = ReadVectors();
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", vectors.RootElement.GetProperty("revision").GetString());
        foreach (var row in vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            var settings = $"enable_transport={row.GetProperty("transport").GetBoolean()}\n";
            foreach (var (name, field) in new[] { ("respond_to_probes", "probes"), ("use_implicit_proof", "implicit") })
                if (row.GetProperty(field).GetString() is { } value && value != "default")
                    settings += $"{name}={value}\n";
            var capture = new Capture();
            var root = NewRoot();
            try
            {
                var config = Configuration(settings, capture, out var registry);
                Assert.Equal(row.GetProperty("probePolicy").GetBoolean(), config.RespondToProbes);
                Assert.Equal(row.GetProperty("implicitPolicy").GetBoolean(), config.UseImplicitProof);
                await using var host = RnsdHost.Create(config, Path.Combine(root, "transport_identity"), (_, _) => { }, registry);
                await host.StartAsync(CancellationToken.None);
                var destination = Destination.Create(DestinationType.Single, "policy", ["proof"], host.Router.TransportIdentity);
                var local = new LocalDestination(host.Router.TransportIdentity, destination) { ProofStrategy = ProofStrategy.All };
                host.Router.RegisterLocalDestination(local);
                var packet = Packet.Create(PacketType.Data, DestinationType.Single, destination.Hash, local.Encrypt("policy"u8));
                await host.Router.HandleAsync(Received(packet, capture));
                var proof = await capture.NextAsync();
                Assert.Equal(row.GetProperty("proofLength").GetInt32(), proof.Payload.Length);
                Assert.Equal(row.GetProperty("proofSignatureValid").GetBoolean(),
                    host.Router.TransportIdentity.Verify(proof.Payload.Span[^64..], packet.CalculateHash()));
                Assert.Equal(row.GetProperty("proofHashPrefixMatches").GetBoolean(),
                    proof.Payload.Span[..^64].SequenceEqual(packet.CalculateHash()));

                var expectedProbe = row.GetProperty("hasProbe").GetBoolean();
                var stats = host.GetStatistics();
                var hash = stats.GetValueOrDefault("probe_responder") as byte[];
                Assert.Equal(expectedProbe, hash is not null);
                if (!expectedProbe) continue;
                var aspect = row.GetProperty("probeAspect").GetString();
                Assert.Equal("rnstransport.probe", aspect);
                var probe = Destination.Create(DestinationType.Single, "rnstransport", ["probe"], host.Router.TransportIdentity);
                Assert.Equal(probe.Hash, hash);
                Assert.False(row.GetProperty("probeAcceptsLinks").GetBoolean());
                Assert.Equal((byte)ProofStrategy.All, row.GetProperty("probeProofStrategy").GetByte());
                var received = Packet.Create(PacketType.Data, DestinationType.Single, probe.Hash, local.Encrypt("probe"u8));
                await host.Router.HandleAsync(Received(received, capture));
                var probeProof = await capture.NextAsync();
                Assert.Equal(PacketType.Proof, probeProof.PacketType);
                Assert.Equal(received.CalculateTruncatedHash(), probeProof.DestinationHash.ToArray());
                Assert.Equal(row.GetProperty("proofLength").GetInt32(), probeProof.Payload.Length);
                Assert.True(host.Router.TransportIdentity.Verify(probeProof.Payload.Span[^64..], received.CalculateHash()));
                hash![0] ^= 0xff;
                Assert.Equal(probe.Hash, Assert.IsType<byte[]>(host.GetStatistics()["probe_responder"]));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplicationAndExplicitCallerProofsUseTheRightOwnerPolicyWithoutSubscribers(bool implicitProof)
    {
        var capture = new Capture();
        var root = NewRoot();
        try
        {
            var config = Configuration($"use_implicit_proof={implicitProof}\n", capture, out var registry);
            await using var host = RnsdHost.Create(config, Path.Combine(root, "transport_identity"), (_, _) => { }, registry);
            await host.StartAsync(CancellationToken.None);
            var identity = host.Router.TransportIdentity;
            var destination = Destination.Create(DestinationType.Single, "policy", ["app"], identity);
            var local = new LocalDestination(identity, destination) { ProofStrategy = ProofStrategy.None };
            host.Router.RegisterLocalDestination(local);
            Packet Make(byte value) => Packet.Create(PacketType.Data, DestinationType.Single, destination.Hash, local.Encrypt([value]));
            await host.Router.HandleAsync(Received(Make(1), capture));
            Assert.Empty(capture.Sent);
            local.ProofStrategy = ProofStrategy.App;
            local.ProofRequested = _ => true;
            var appPacket = Make(2);
            await host.Router.HandleAsync(Received(appPacket, capture));
            AssertProof(await capture.NextAsync(), appPacket, identity, !implicitProof);
            foreach (var explicitProof in new[] { false, true })
            {
                var packet = Make(explicitProof ? (byte)3 : (byte)4);
                await host.Router.ProveAsync(local, Received(packet, capture), explicitProof);
                AssertProof(await capture.NextAsync(), packet, identity, explicitProof);
            }
            var defaultPacket = Make(5);
            await host.Router.ProveAsync(local, Received(defaultPacket, capture));
            AssertProof(await capture.NextAsync(), defaultPacket, identity, !implicitProof);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ProbeAnnouncementsHonorStrictReferenceTimingCancellationFailureAndRecovery()
    {
        using var vectors = ReadVectors();
        var row = vectors.RootElement.GetProperty("cases")[0];
        var delay = TimeSpan.FromSeconds(row.GetProperty("managementDelay").GetDouble());
        var interval = TimeSpan.FromSeconds(row.GetProperty("managementInterval").GetDouble());
        var capture = new Capture();
        var clock = new Clock();
        var root = NewRoot();
        var logs = new List<string>();
        try
        {
            var config = Configuration("enable_transport=yes\nrespond_to_probes=yes\n", capture, out var registry);
            await using var host = RnsdHost.Create(config, Path.Combine(root, "transport_identity"),
                (_, message) => logs.Add(message), registry, timeProvider: clock);
            await host.StartAsync(CancellationToken.None);
            await host.TickProbeAnnouncementsAsync();
            Assert.Empty(capture.Sent);
            clock.Advance(delay);
            await host.TickProbeAnnouncementsAsync();
            Assert.Empty(capture.Sent);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.TickProbeAnnouncementsAsync(cancelled.Token).AsTask());
            }
            Assert.Empty(capture.Sent);
            await host.TickProbeAnnouncementsAsync();
            var first = await capture.NextAsync();
            Assert.Equal(PacketType.Announce, first.PacketType);
            Assert.Equal(Assert.IsType<byte[]>(host.GetStatistics()["probe_responder"]), first.DestinationHash.ToArray());
            clock.Advance(interval);
            await host.TickProbeAnnouncementsAsync();
            Assert.Single(capture.Sent);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            capture.FailNextSend = true;
            await host.TickProbeAnnouncementsAsync();
            Assert.Contains(logs, message => message == "Could not transmit announce on Capture: injected send failure");
            Assert.Single(capture.Sent);
            await host.TickProbeAnnouncementsAsync();
            Assert.Single(capture.Sent);
            clock.Advance(interval + TimeSpan.FromMilliseconds(1));
            await host.TickProbeAnnouncementsAsync();
            Assert.Equal(PacketType.Announce, (await capture.NextAsync()).PacketType);
            Assert.Equal(2, capture.Sent.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("respond_to_probes")]
    [InlineData("use_implicit_proof")]
    public void InvalidBooleanPoliciesAreRejectedBeforeRuntimeMutation(string setting) =>
        Assert.Throws<FormatException>(() => RnsdConfigParser.Parse($"[reticulum]\n{setting}=perhaps\n[interfaces]\n"));

    private static void AssertProof(Packet proof, Packet packet, Identity identity, bool explicitProof)
    {
        Assert.Equal(PacketType.Proof, proof.PacketType);
        Assert.Equal(explicitProof ? 96 : 64, proof.Payload.Length);
        Assert.True(identity.Verify(proof.Payload.Span[^64..], packet.CalculateHash()));
        Assert.Equal(explicitProof, proof.Payload.Span[..^64].SequenceEqual(packet.CalculateHash()));
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "rns-proof-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ReceivedPacket Received(Packet packet, Capture capture) => new(packet.ToArray(), packet, capture);

    private static RnsdConfig Configuration(string settings, Capture capture, out InterfaceFactoryRegistry registry)
    {
        registry = new InterfaceFactoryRegistry();
        registry.Register("ProofCapture", [], _ => capture);
        return RnsdConfigParser.Parse($"[reticulum]\nshare_instance=no\n{settings}[interfaces]\n[[Capture]]\ntype=ProofCapture\nenabled=yes\n", registry);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class Capture : IPacketInterface
    {
        private readonly Channel<ReadOnlyMemory<byte>> _inbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(8);
        private readonly Channel<Packet> _sent = Channel.CreateBounded<Packet>(16);
        public ConcurrentQueue<Packet> Sent { get; } = new();
        public bool FailNextSend { get; set; }
        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var packet in _inbound.Reader.ReadAllAsync(cancellationToken)) yield return packet;
        }
        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextSend)
            {
                FailNextSend = false;
                throw new IOException("injected send failure");
            }
            var parsed = Packet.Parse(packet);
            Sent.Enqueue(parsed);
            if (!_sent.Writer.TryWrite(parsed)) throw new InvalidOperationException("Test capture capacity exceeded.");
            return ValueTask.CompletedTask;
        }
        public async Task<Packet> NextAsync() => await _sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public ValueTask DisposeAsync()
        {
            _inbound.Writer.TryComplete();
            _sent.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
