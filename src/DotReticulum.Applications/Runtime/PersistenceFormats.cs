using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Applications.Runtime;

/// <summary>Reflection-free upstream MessagePack persistence contracts.
/// Source: RNS 1.5.7 a29bfb51c468945c96a1ee22cae0114e993e2391, Identity.py
/// 101-335/410-513 and Transport.py 3745-3950. See docs/runtime-state.md for format differences.</summary>
public static class PersistenceFormats
{
    public const int MaximumEntries = 65536;

    public static byte[] EncodeKnownDestinations(IEnumerable<KnownDestinationSnapshot> entries) => MsgPack.Pack(KnownMap(entries));

    public static IReadOnlyList<KnownDestinationSnapshot> DecodeKnownDestinations(ReadOnlySpan<byte> packed)
    {
        var map = PersistenceMessagePack.BinaryMap(packed, 16);
        Bound(map.Count, "known destinations");
        var result = new List<KnownDestinationSnapshot>(map.Count);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            var hash = Bytes(key, 16, "destination hash");
            if (!hashes.Add(Convert.ToHexString(hash))) throw new InvalidDataException("Duplicate destination hash.");
            var entry = Array(value, "known destination");
            if (entry.Length is not (4 or 5)) throw new InvalidDataException("Known destination must have four legacy or five current fields.");
            var timestamp = Timestamp(entry[0], "last announce");
            if (timestamp > long.MaxValue) throw new InvalidDataException("Last announce exceeds supported Unix seconds.");
            var usage = entry.Length == 4 ? 0 : Number(entry[4], "last use");
            if (usage < 0 && usage != -1) throw new InvalidDataException("Last use must be nonnegative or the retained sentinel -1.");
            var publicKey = Bytes(entry[2], Identity.KeySize, "destination public key");
            using var identity = Identity.FromPublicKey(publicKey);
            result.Add(new(hash, new KnownDestination((long)timestamp, Bytes(entry[1], 32, "announce hash"),
                publicKey, NullableBytes(entry[3], "application data"), null, 0, usage) { RememberedTimestamp = timestamp }));
        }
        return result;
    }

    public static byte[] EncodePaths(IEnumerable<PersistedPath> paths) => MsgPack.Pack(paths.Select(PathValue).ToArray());
    public static IReadOnlyList<PersistedPath> DecodePaths(ReadOnlySpan<byte> packed) => Paths(MsgPack.Unpack(packed));
    public static byte[] EncodeTunnels(IEnumerable<PersistedTunnel> tunnels) => MsgPack.Pack(tunnels.Select(TunnelValue).ToArray());
    public static IReadOnlyList<PersistedTunnel> DecodeTunnels(ReadOnlySpan<byte> packed) => Tunnels(MsgPack.Unpack(packed));

    /// <summary>Upstream Destination._persist_ratchets envelope; signature covers the MessagePack key array.
    /// The returned buffer contains private keys and must be zeroed by the caller.</summary>
    public static byte[] EncodeUpstreamRatchets(DestinationRatchetSnapshot snapshot, Identity identity)
    {
        using var owned = snapshot.Clone();
        var keys = PersistenceSecretMessagePack.Pack(owned.PrivateKeys.Cast<object?>().ToArray());
        try { return PersistenceSecretMessagePack.Pack(new Dictionary<object, object?> { ["signature"] = identity.Sign(keys), ["ratchets"] = keys }); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(keys); }
    }

    internal static Dictionary<object, object?> KnownMap(IEnumerable<KnownDestinationSnapshot> entries)
    {
        var result = new Dictionary<object, object?>();
        foreach (var item in entries)
        {
            var entry = item.Entry;
            result.Add(item.DestinationHash, new object?[]
            {
                entry.RememberedTimestamp, entry.PacketHash, entry.PublicKey, entry.AppData,
                entry.LastUsedUnix == -1 ? -1L : entry.LastUsedUnix == 0 ? 0L : (object)entry.LastUsedUnix,
            });
            Bound(result.Count, "known destinations");
        }
        return result;
    }

    internal static object?[] PathValue(PersistedPath path) =>
        [path.DestinationHash, path.Timestamp, path.ReceivedFrom, path.Hops, path.Expires,
            path.RandomBlobs.Cast<object?>().ToArray(), path.InterfaceHash, path.PacketHash];

    internal static object?[] TunnelValue(PersistedTunnel tunnel) =>
        [tunnel.TunnelId, tunnel.InterfaceHash, tunnel.Paths.Select(PathValue).ToArray(), tunnel.Expires];

    internal static IReadOnlyList<PersistedPath> Paths(object? value)
    {
        var paths = Array(value, "paths");
        Bound(paths.Length, "paths");
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        return paths.Select(value =>
        {
            var path = Array(value, "path");
            if (path.Length != 8) throw new InvalidDataException("Persisted path must have eight fields.");
            var hash = Bytes(path[0], 16, "path destination");
            if (!hashes.Add(Convert.ToHexString(hash))) throw new InvalidDataException("Duplicate path destination.");
            var hops = Integer(path[3], "path hops");
            if (hops is < 0 or > 128) throw new InvalidDataException("Path hops must be between 0 and 128.");
            var blobs = Array(path[5], "path random blobs");
            if (blobs.Length > AnnounceEngine.MaxRandomBlobs) throw new InvalidDataException("Too many persisted path random blobs.");
            return new PersistedPath(hash, Timestamp(path[1], "path timestamp"), Bytes(path[2], 16, "path next hop"),
                checked((int)hops), Timestamp(path[4], "path expiry"), blobs.Select(b => Bytes(b, 10, "path random blob")).ToArray(),
                path[6] is null ? null : Bytes(path[6], 32, "path interface hash"), Bytes(path[7], 32, "path packet hash"));
        }).ToArray();
    }

    internal static IReadOnlyList<PersistedTunnel> Tunnels(object? value)
    {
        var tunnels = Array(value, "tunnels");
        Bound(tunnels.Length, "tunnels");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return tunnels.Select(value =>
        {
            var tunnel = Array(value, "tunnel");
            if (tunnel.Length != 4) throw new InvalidDataException("Persisted tunnel must have four fields.");
            var id = Bytes(tunnel[0], 32, "tunnel id");
            if (!ids.Add(Convert.ToHexString(id))) throw new InvalidDataException("Duplicate tunnel id.");
            return new PersistedTunnel(id, tunnel[1] is null ? null : Bytes(tunnel[1], 32, "tunnel interface hash"),
                Paths(tunnel[2]), Timestamp(tunnel[3], "tunnel expiry"));
        }).ToArray();
    }

    internal static Dictionary<object, object?> Map(object? value, string context) =>
        value as Dictionary<object, object?> ?? throw new InvalidDataException($"{context} must be a MessagePack map.");
    internal static object?[] Array(object? value, string context) =>
        value as object?[] ?? throw new InvalidDataException($"{context} must be a MessagePack array.");
    internal static byte[] Bytes(object? value, int length, string context) =>
        value is byte[] bytes && bytes.Length == length ? bytes : throw new InvalidDataException($"{context} must be exactly {length} bytes.");
    internal static byte[]? NullableBytes(object? value, string context) =>
        value is null ? null : value as byte[] ?? throw new InvalidDataException($"{context} must be bytes or null.");
    internal static double Number(object? value, string context)
    {
        var number = MsgPack.ToDouble(value);
        if (!double.IsFinite(number)) throw new InvalidDataException($"{context} must be finite.");
        return number;
    }
    internal static double Timestamp(object? value, string context)
    {
        var timestamp = Number(value, context);
        if (timestamp < 0) throw new InvalidDataException($"{context} must be a nonnegative Unix timestamp.");
        return timestamp;
    }
    internal static long Integer(object? value, string context) =>
        value is long number ? number : throw new InvalidDataException($"{context} must be an integer.");
    internal static object? Required(Dictionary<object, object?> map, string field) =>
        map.TryGetValue(field, out var value) ? value : throw new InvalidDataException($"Missing persistence field '{field}'.");
    internal static void Bound(int count, string context)
    {
        if (count > MaximumEntries) throw new InvalidDataException($"{context} exceeds {MaximumEntries} entries.");
    }
}
