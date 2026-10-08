using System.Buffers.Binary;
using System.Security.Cryptography;
using DotReticulum.Core;

namespace DotReticulum.Transport;

public sealed class AnnounceInfo
{
    internal AnnounceInfo(byte[] destinationHash, byte[] publicKey, byte[] nameHash,
        byte[] randomHash, byte[] appData)
    {
        DestinationHash = destinationHash;
        PublicKey = publicKey;
        NameHash = nameHash;
        RandomHash = randomHash;
        AppData = appData;
    }

    public ReadOnlyMemory<byte> DestinationHash { get; }
    public ReadOnlyMemory<byte> PublicKey { get; }
    public ReadOnlyMemory<byte> NameHash { get; }
    public ReadOnlyMemory<byte> RandomHash { get; }
    public ReadOnlyMemory<byte> AppData { get; }
}

/// <summary>Creates and validates signed, non-ratcheted Reticulum announce packets.</summary>
public static class Announce
{
    public const int NameHashLength = 10;
    public const int RandomHashLength = 10;
    public const int SignatureLength = 64;
    public const int FixedPayloadLength = Identity.KeySize + NameHashLength + RandomHashLength + SignatureLength;
    public const int MaximumAppDataLength = Packet.Mtu - Packet.Header1Length - FixedPayloadLength;

    public static Packet Create(Identity identity, string destinationName, ReadOnlySpan<byte> appData = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(destinationName);
        if (!identity.HasPrivateKey)
            throw new InvalidOperationException("Announces require an identity with a private key.");
        if (appData.Length > MaximumAppDataLength)
            throw new ArgumentOutOfRangeException(nameof(appData), "Announce app data exceeds the packet MTU.");

        var publicKey = identity.ExportPublicKey();
        var nameHash = Destination.CalculateNameHash(destinationName);
        var destinationHash = CalculateDestinationHash(nameHash, identity.Hash);
        var randomHash = new byte[RandomHashLength];
        RandomNumberGenerator.Fill(randomHash.AsSpan(0, 5));
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (seconds is < 0 or > 0xffffffffff)
            throw new InvalidOperationException("Current time cannot be represented in an announce.");
        for (var i = 4; i >= 0; i--)
        {
            randomHash[i + 5] = (byte)seconds;
            seconds >>= 8;
        }

        var signedData = new byte[Identity.HashSize + Identity.KeySize + NameHashLength + RandomHashLength + appData.Length];
        var offset = 0;
        destinationHash.CopyTo(signedData, offset);
        offset += Identity.HashSize;
        publicKey.CopyTo(signedData, offset);
        offset += Identity.KeySize;
        nameHash.CopyTo(signedData, offset);
        offset += NameHashLength;
        randomHash.CopyTo(signedData, offset);
        offset += RandomHashLength;
        appData.CopyTo(signedData.AsSpan(offset));

        var signature = identity.Sign(signedData);
        var payload = new byte[FixedPayloadLength + appData.Length];
        offset = 0;
        publicKey.CopyTo(payload, offset);
        offset += publicKey.Length;
        nameHash.CopyTo(payload, offset);
        offset += nameHash.Length;
        randomHash.CopyTo(payload, offset);
        offset += randomHash.Length;
        signature.CopyTo(payload, offset);
        offset += signature.Length;
        appData.CopyTo(payload.AsSpan(offset));

        return Packet.Create(PacketType.Announce, DestinationType.Single, destinationHash, payload);
    }

    public static bool TryValidate(Packet packet, out AnnounceInfo? announce)
    {
        ArgumentNullException.ThrowIfNull(packet);
        announce = null;
        if (packet.Type != PacketType.Announce ||
            packet.DestinationType != DestinationType.Single ||
            packet.ContextFlag ||
            packet.Payload.Length < FixedPayloadLength)
            return false;

        var payload = packet.Payload.Span;
        var publicKey = payload[..Identity.KeySize];
        var nameHash = payload.Slice(Identity.KeySize, NameHashLength);
        var randomHash = payload.Slice(Identity.KeySize + NameHashLength, RandomHashLength);
        var signature = payload.Slice(Identity.KeySize + NameHashLength + RandomHashLength, SignatureLength);
        var appData = payload[FixedPayloadLength..];

        try
        {
            using var identity = Identity.FromPublicKey(publicKey);
            var destinationHash = packet.DestinationHash.Span;
            var expectedDestinationHash = CalculateDestinationHash(nameHash, identity.Hash);
            if (!CryptographicOperations.FixedTimeEquals(destinationHash, expectedDestinationHash))
                return false;

            var signedData = new byte[Identity.HashSize + Identity.KeySize + NameHashLength + RandomHashLength + appData.Length];
            var offset = 0;
            destinationHash.CopyTo(signedData);
            offset += Identity.HashSize;
            publicKey.CopyTo(signedData.AsSpan(offset));
            offset += Identity.KeySize;
            nameHash.CopyTo(signedData.AsSpan(offset));
            offset += NameHashLength;
            randomHash.CopyTo(signedData.AsSpan(offset));
            offset += RandomHashLength;
            appData.CopyTo(signedData.AsSpan(offset));
            if (!identity.Verify(signedData, signature))
                return false;

            announce = new AnnounceInfo(packet.DestinationHash.ToArray(), publicKey.ToArray(),
                nameHash.ToArray(), randomHash.ToArray(), appData.ToArray());
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static byte[] CalculateDestinationHash(ReadOnlySpan<byte> nameHash, ReadOnlySpan<byte> identityHash)
    {
        Span<byte> material = stackalloc byte[NameHashLength + Identity.HashSize];
        nameHash.CopyTo(material);
        identityHash.CopyTo(material[NameHashLength..]);
        return SHA256.HashData(material)[..Identity.HashSize];
    }
}

/// <summary>Limits accepted announces per destination with a bounded in-memory cache.</summary>
public sealed class AnnounceRateLimiter
{
    private readonly TimeSpan _minimumInterval;
    private readonly int _capacity;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recency = [];
    private readonly object _sync = new();

    public AnnounceRateLimiter(TimeSpan minimumInterval, int capacity = 4096, TimeProvider? timeProvider = null)
    {
        if (minimumInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _minimumInterval = minimumInterval;
        _capacity = capacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryAccept(ReadOnlySpan<byte> destinationHash)
    {
        if (destinationHash.Length != Identity.HashSize)
            throw new ArgumentException("Destination hash must be 16 bytes.", nameof(destinationHash));

        var key = Convert.ToHexString(destinationHash);
        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (now - existing.Value.LastAccepted < _minimumInterval)
                    return false;

                existing.Value = new Entry(key, now);
                _recency.Remove(existing);
                _recency.AddLast(existing);
                return true;
            }

            if (_entries.Count == _capacity)
            {
                var oldest = _recency.First!;
                _recency.RemoveFirst();
                _entries.Remove(oldest.Value.DestinationKey);
            }

            var node = _recency.AddLast(new Entry(key, now));
            _entries.Add(key, node);
            return true;
        }
    }

    private sealed record Entry(string DestinationKey, DateTimeOffset LastAccepted);
}
