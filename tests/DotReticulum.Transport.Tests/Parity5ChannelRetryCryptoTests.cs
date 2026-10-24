using System.Buffers.Binary;
using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Crypto;
using P4 = DotReticulum.Transport.Tests.Parity4LinkMtuTests;

namespace DotReticulum.Transport.Tests;

// GenerateParity5ChannelRetryCryptoVectors.py executes unmodified upstream methods
// through the real Transport.outbound and Token crypto at a29bfb51c468945c96a1ee22cae0114e993e2391.
// Only interface I/O is captured; random IVs are not replaced.
public sealed class Parity5ChannelRetryCryptoTests
{
    private static JsonElement Vectors { get; } = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "parity5-channel-retry-crypto-vectors.json"))).RootElement;

    [Fact]
    public void ActualReferencePacketResendChangesCiphertextAndHashButPreservesEnvelopeAndSequence()
    {
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", Vectors.GetProperty("revision").GetString());
        using var token = new Token(Hex(Vectors, "syntheticSessionKey"));
        var attempts = Vectors.GetProperty("attempts").EnumerateArray().ToArray();
        var envelope = Hex(attempts[0], "envelope");
        foreach (var attempt in attempts)
        {
            var packet = Packet.Parse(Hex(attempt, "raw"));
            Assert.Equal(PacketType.Data, packet.PacketType);
            Assert.Equal(DestinationType.Link, packet.DestinationType);
            Assert.Equal(LinkContext.Channel, packet.Context);
            Assert.Equal(Hex(attempt, "ciphertext"), packet.Payload.ToArray());
            Assert.Equal(Hex(attempt, "iv"), packet.Payload.Span[..16].ToArray());
            Assert.Equal(Hex(attempt, "packetHash"), packet.CalculateHash());
            Assert.Equal(Hex(attempt, "receiptHash"), packet.CalculateHash());
            Assert.Equal(Hex(attempt, "outletPacketId"), packet.CalculateHash());
            Assert.Equal(envelope, token.Decrypt(packet.Payload.Span));
            Assert.Equal(envelope, Hex(attempt, "envelope"));
            Assert.Equal(65535, BinaryPrimitives.ReadUInt16BigEndian(envelope.AsSpan(2)));
            Assert.Equal(65535, attempt.GetProperty("sequence").GetInt32());
            Assert.True(attempt.GetProperty("samePacketObjectAsInitial").GetBoolean());
            Assert.True(attempt.GetProperty("receiptIsNew").GetBoolean());
        }
        foreach (var field in new[] { "iv", "ciphertext", "packetHash", "receiptHash", "outletPacketId" })
            Assert.Equal(attempts.Length, attempts.Select(attempt => attempt.GetProperty(field).GetString()).Distinct().Count());
        foreach (var method in new[] { "Packet.resend", "Packet.pack", "LinkChannelOutlet.resend", "Link.encrypt",
                     "Token.encrypt", "Transport.outbound", "Transport._outbound", "PacketReceipt.__init__" })
            Assert.True(Vectors.GetProperty("methodProvenance").GetProperty(method).GetProperty("calls").GetInt32() > 0);
        var proof = Hex(Vectors, "finalProof");
        Assert.Equal(Hex(attempts[^1], "packetHash"), proof[..32]);
        Assert.True(Ed25519.Verify(Hex(Vectors, "syntheticPeerSigningPublicKey"), proof.AsSpan(0, 32), proof.AsSpan(32)));
        Assert.True(Vectors.GetProperty("finalProofAccepted").GetBoolean());
        Assert.Equal(0, Vectors.GetProperty("nextSequenceAfterDelivery").GetInt32());
    }

    [Fact]
    public async Task LinkRetryReencryptsSameEnvelopeAndOnlyCurrentHashProofSettlesCurrentReceipt()
    {
        await using var node = await P4.Harness.Create();
        var link = P4.FixedInitiator(node);
        var channel = link.GetChannel();
        var envelope = channel.Send(new Parity5ChannelTests.BytesMessage { Data = "real channel retry"u8.ToArray() });
        var originalReceipt = envelope.Receipt!;
        var originalPacket = originalReceipt.SentPacket!;
        var originalHash = originalReceipt.Hash.ToArray();
        var originalEnvelope = envelope.Raw!.ToArray();
        var timeout = originalReceipt.TimedOut!;
        Assert.True(originalReceipt.Conclude(PacketReceiptStatus.Failed, node.Router.Links.UnixNow));
        timeout(originalReceipt);

        var replacement = envelope.Receipt!;
        var replacementPacket = replacement.SentPacket!;
        Assert.NotSame(originalReceipt, replacement);
        Assert.Equal(2, envelope.Tries);
        Assert.Equal(0, envelope.Sequence);
        Assert.Equal(originalEnvelope, envelope.Raw);
        Assert.NotEqual(Convert.ToHexString(originalHash), Convert.ToHexString(replacement.Hash));
        Assert.NotEqual(Convert.ToHexString(originalPacket.Payload.Span), Convert.ToHexString(replacementPacket.Payload.Span));
        Assert.Equal(replacementPacket.CalculateHash(), replacement.Hash);
        Assert.True(replacement.CoreCallbacks);
        using var token = new Token(link.DerivedKeyForTests);
        Assert.Equal(originalEnvelope, token.Decrypt(originalPacket.Payload.Span));
        Assert.Equal(originalEnvelope, token.Decrypt(replacementPacket.Payload.Span));

        using var identity = Identity.FromPrivateKey(P4.Hex(P4.Vectors, "destinationPrivateKey"));
        byte[] staleProofData = [.. originalHash, .. identity.Sign(originalHash)];
        var staleProof = Packet.Create(PacketType.Proof, DestinationType.Link, link.LinkId,
            staleProofData);
        link.Receive(P4.Received(staleProof, node.Interface));
        Assert.Equal(MessageState.Sent, envelope.State);
        Assert.Same(replacement, envelope.Receipt);

        var currentHash = replacement.Hash.ToArray();
        byte[] currentProofData = [.. currentHash, .. identity.Sign(currentHash)];
        var currentProof = Packet.Create(PacketType.Proof, DestinationType.Link, link.LinkId,
            currentProofData);
        link.Receive(P4.Received(currentProof, node.Interface));
        Assert.Equal(MessageState.Delivered, envelope.State);
        timeout(originalReceipt);
        Assert.Equal(2, envelope.Tries);
        Assert.Equal(1, channel.Send(new Parity5ChannelTests.BytesMessage()).Sequence);
        link.Teardown();
    }

    private static byte[] Hex(JsonElement element, string property) =>
        Convert.FromHexString(element.GetProperty(property).GetString()!);
}
