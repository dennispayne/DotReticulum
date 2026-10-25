using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DotReticulum.Core;
using DotReticulum.Crypto;

namespace DotReticulum.Applications.Runtime;

public sealed record DiscoveryOptions
{
    public bool Enabled { get; init; }
    public int RequiredStampValue { get; init; } = 16;
    public IReadOnlyList<byte[]> AllowedSources { get; init; } = [];
    public int MaxAutoConnected { get; init; }
    public bool AllowUnverifiedImplementations { get; init; }
    public int Capacity { get; init; } = 2048;
    public string? InterfaceMode { get; init; }
    public int InterfaceGravity { get; init; }
    public bool? AnnouncesToInternal { get; init; }
}

/// <summary>Integer keys and ordering are the pinned RNS Discovery.py advertisement contract.</summary>
public sealed record DiscoveryAdvertisement
{
    public string InterfaceType { get; init; } = "TCPServerInterface";
    public bool Transport { get; init; }
    public required byte[] TransportIdentity { get; init; }
    public string Implementation { get; init; } = "DotReticulum";
    public string Version { get; init; } = "0.1.0";
    public required string? Name { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? Height { get; init; }
    public required string ReachableOn { get; init; }
    public int Port { get; init; }
    public int? Frequency { get; init; }
    public int? Bandwidth { get; init; }
    public int? SpreadingFactor { get; init; }
    public int? CodingRate { get; init; }
    public int? Channel { get; init; }
    public int? Modulation { get; init; }
    public byte[]? OperatorAddress { get; init; }
    public string? NetworkName { get; init; }
    public string? Passphrase { get; init; }
    public bool PublishIfac { get; init; }
    public bool Encrypt { get; init; }
    public int StampValue { get; init; } = 16;
}

public sealed record DiscoveryRecord(
    string InterfaceType, bool Transport, string TransportId, string NetworkId,
    string? Implementation, string? Version, string Name, string? ReachableOn, int? Port,
    double? Latitude, double? Longitude, double? Height, string? NetworkName, string? Passphrase,
    byte[]? OperatorAddress, byte[] Stamp, int StampValue, int Hops, double Received,
    string DiscoveryHash, byte[] Packed)
{
    public double Discovered { get; init; } = Received;
    public double LastHeard { get; init; } = Received;
    public long HeardCount { get; init; }
    public string Status { get; init; } = "available";
    public int StatusCode => Status == "available" ? 1000 : Status == "unknown" ? 100 : 0;
    public string EndpointHash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        (ReachableOn ?? "") + (Port.HasValue ? ":" + Port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""))));
}

public static partial class DiscoveryCodec
{
    public const string Aspect = "rnstransport.discovery.interface";
    public const int ExpandRounds = 20;
    public const int StampSize = 32;
    // The announce engine enforces the actual MTU. This bound also covers direct callers.
    public const int MaximumPayload = 4096;
    public static IReadOnlyList<string> DiscoverableTypes { get; } = Array.AsReadOnly(
        new[] { "BackboneInterface", "TCPServerInterface", "I2PInterface", "RNodeInterface", "WeaveInterface", "KISSInterface" });

    public static byte[] PackAdvertisement(DiscoveryAdvertisement value)
    {
        if (!DiscoverableTypes.Contains(value.InterfaceType))
            throw new NotSupportedException($"Discovery advertisement for {value.InterfaceType} is unsupported; no bearer implementation is implied.");
        if (value.TransportIdentity.Length != 16)
            throw new ArgumentException("Invalid discovery identity.");
        var tcp = value.InterfaceType is "TCPServerInterface" or "BackboneInterface";
        if (tcp && (!IsHost(value.ReachableOn) || value.Port is < 1 or > 65535))
            throw new ArgumentException("Invalid discovery TCP endpoint.");
        if (value.InterfaceType == "I2PInterface" && !IsHost(value.ReachableOn))
            throw new ArgumentException("Invalid discovery I2P endpoint.");
        if (value.InterfaceType == "KISSInterface" && value.Modulation is not null)
            throw new NotSupportedException("Pinned upstream discovery cannot sanitize a numeric KISS discovery_modulation; omit this setting.");
        var map = new Dictionary<object, object?>
        {
            [0L] = value.InterfaceType, [1L] = value.Transport, [254L] = value.TransportIdentity,
            [253L] = value.Implementation, [252L] = value.Version, [255L] = value.Name is null ? null : SanitizePublished(value.Name),
            [3L] = value.Latitude, [4L] = value.Longitude, [5L] = value.Height,
        };
        if (value.OperatorAddress is not null)
        {
            if (value.OperatorAddress.Length != 16) throw new ArgumentException("Operator address must be 16 bytes.");
            map[240L] = value.OperatorAddress;
        }
        if (tcp || value.InterfaceType == "I2PInterface") map[2L] = SanitizePublished(value.ReachableOn);
        if (tcp) map[6L] = value.Port;
        if (value.InterfaceType is "RNodeInterface" or "WeaveInterface" or "KISSInterface")
        {
            map[9L] = value.Frequency;
            map[10L] = value.Bandwidth;
        }
        if (value.InterfaceType == "RNodeInterface")
        {
            map[11L] = value.SpreadingFactor;
            map[12L] = value.CodingRate;
        }
        if (value.InterfaceType == "WeaveInterface")
        {
            map[14L] = value.Channel;
            map[13L] = value.Modulation;
        }
        if (value.InterfaceType == "KISSInterface") map[13L] = null;
        if (value.PublishIfac)
        {
            if (!string.IsNullOrEmpty(value.NetworkName)) map[7L] = SanitizePublished(value.NetworkName);
            if (!string.IsNullOrEmpty(value.Passphrase)) map[8L] = SanitizePublished(value.Passphrase);
        }
        return MsgPack.Pack(map);
    }

    public static byte[] Create(DiscoveryAdvertisement value, Identity? networkIdentity = null, CancellationToken cancellationToken = default)
    {
        if (value.Encrypt && networkIdentity is null) throw new InvalidOperationException("Encrypted discovery requires a network identity.");
        if (value.StampValue is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(value));
        var packed = PackAdvertisement(value);
        var stamp = GenerateStamp(SHA256.HashData(packed), value.StampValue, cancellationToken);
        var body = packed.Concat(stamp).ToArray();
        try
        {
            return new[] { value.Encrypt ? (byte)2 : (byte)0 }
                .Concat(value.Encrypt ? networkIdentity!.Encrypt(body) : body).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    public static byte[] Workblock(ReadOnlySpan<byte> material)
    {
        var block = new byte[ExpandRounds * 256];
        for (var n = 0; n < ExpandRounds; n++)
        {
            var salt = SHA256.HashData(material.ToArray().Concat(MsgPack.Pack(n)).ToArray());
            KeyDerivation.HkdfSha256(material, 256, salt).CopyTo(block, n * 256);
        }
        return block;
    }

    public static int StampValue(ReadOnlySpan<byte> block, ReadOnlySpan<byte> stamp)
    {
        if (stamp.Length != StampSize) throw new ArgumentException("Stamp must be 32 bytes.");
        var hash = SHA256.HashData(block.ToArray().Concat(stamp.ToArray()).ToArray());
        var bits = 0;
        foreach (var b in hash)
        {
            if (b == 0) { bits += 8; continue; }
            return bits + BitOperations.LeadingZeroCount((uint)b) - 24;
        }
        return bits;
    }

    public static byte[] GenerateStamp(byte[] material, int cost, CancellationToken cancellationToken = default)
    {
        if (cost is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(cost));
        var block = Workblock(material);
        var input = new byte[block.Length + StampSize];
        block.CopyTo(input, 0);
        Span<byte> hash = stackalloc byte[32];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RandomNumberGenerator.Fill(input.AsSpan(block.Length));
            SHA256.HashData(input, hash);
            var bits = 0;
            foreach (var b in hash)
            {
                if (b == 0) { bits += 8; continue; }
                bits += BitOperations.LeadingZeroCount((uint)b) - 24;
                break;
            }
            if (bits >= cost) return input.AsSpan(block.Length).ToArray();
        }
    }

    public static DiscoveryRecord Decode(ReadOnlySpan<byte> appData, Identity announcedIdentity, int hops,
        int requiredValue = 16, Identity? networkIdentity = null, double received = 0)
    {
        if (appData.Length is <= StampSize + 1 or > MaximumPayload) throw new FormatException("Invalid discovery payload size.");
        byte[]? clear = null;
        try
        {
            // Signed/unknown flags are reserved and ignored by the reference.
            var body = appData[1..];
            if ((appData[0] & 2) != 0)
            {
                if (networkIdentity?.HasPrivateKey != true) throw new FormatException("Encrypted discovery without a network identity.");
                clear = networkIdentity.Decrypt(body) ?? throw new FormatException("Discovery decryption failed.");
                body = clear;
            }
            if (body.Length <= StampSize) throw new FormatException("Truncated discovery data.");
            var packed = body[..^StampSize].ToArray();
            var stamp = body[^StampSize..].ToArray();
            var value = StampValue(Workblock(SHA256.HashData(packed)), stamp);
            if (value < requiredValue) throw new FormatException($"Insufficient discovery stamp value {value}.");
            if (MsgPack.Unpack(packed) is not Dictionary<object, object?> map) throw new FormatException("Discovery is not a map.");
            object? Field(long key) => map.TryGetValue(key, out var field) ? field : throw new FormatException($"Missing discovery field {key}.");
            string? Text(long key) => map.TryGetValue(key, out var field) && field is string text ? text : null;
            double? Coordinate(long key) => Field(key) switch { null => null, double d when double.IsFinite(d) => d, _ => throw new FormatException("Invalid coordinate.") };
            var type = Field(0) as string ?? throw new FormatException("Invalid interface type.");
            if (!DiscoverableTypes.Contains(type) && type != "TCPClientInterface") throw new FormatException("Unknown interface type.");
            var transport = Field(1) is bool b ? b : throw new FormatException("Invalid transport flag.");
            var transportId = Field(254) is byte[] { Length: 16 } id ? Convert.ToHexStringLower(id) : throw new FormatException("Invalid transport identity.");
            if (Field(255) is not null and not string) throw new FormatException("Invalid discovery name.");
            var name = SanitizeName(Text(255)) ?? $"Discovered {type}";
            var host = Text(2);
            if (map.ContainsKey(2L) && (host is null || !IsHost(host))) throw new FormatException("Invalid discovery host.");
            int? port = null;
            if (type is "BackboneInterface" or "TCPServerInterface")
            {
                if (host is null || Field(6) is not long p || p is < 1 or > 65535) throw new FormatException("Invalid TCP endpoint.");
                port = (int)p;
            }
            if (type == "I2PInterface" && host is null) throw new FormatException("Missing I2P reachable address.");
            int[] requiredFields = type switch
            {
                "RNodeInterface" => [9, 10, 11, 12],
                "WeaveInterface" => [9, 10, 14, 13],
                "KISSInterface" => [9, 10, 13],
                _ => [],
            };
            foreach (var field in requiredFields) _ = Field(field);
            byte[]? op = null;
            if (map.TryGetValue(240L, out var operatorAddress))
            {
                if (operatorAddress is not null and not byte[]) throw new FormatException("Invalid operator address.");
                if (operatorAddress is byte[] { Length: 16 } address) op = address;
            }
            return new(type, transport, transportId, announcedIdentity.HexHash, Text(253), Text(252), name, host, port,
                Coordinate(3), Coordinate(4), Coordinate(5), NormalizeIfac(Text(7)), NormalizeIfac(Text(8)),
                op, stamp, value, hops, received, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(transportId + name))), packed);
        }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }

    public static bool IsHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && host == ip.ToString()) return true;
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && host.Contains(':')
                && !host.Contains('[') && !host.Contains(']'))
            {
                var address = host.Split('%')[0];
                if (!address.Contains('.')) return true;
                var tail = address[(address.LastIndexOf(':') + 1)..];
                if (IPAddress.TryParse(tail, out var mapped) && mapped.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && tail == mapped.ToString()) return true;
            }
        }
        if (host.EndsWith('.')) host = host[..^1];
        if (host.Length is 0 or > 253) return false;
        var parts = host.Split('.');
        return !parts[^1].All(char.IsAsciiDigit) && parts.All(p => HostLabel().IsMatch(p));
    }

    public static bool CanAutoConnect(DiscoveryRecord record, bool allowUnverified)
    {
        if (record.InterfaceType != "BackboneInterface" || !record.Transport || record.Port is null || !IsHost(record.ReachableOn)) return false;
        var host = record.ReachableOn!;
        if (host.EndsWith(".onion", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "0.0.0.0") return false;
        if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            && (ip.GetAddressBytes()[0] & 0xfe) == 2) return false;
        if (allowUnverified) return true;
        if (record.Implementation != "RNS" || record.Version is null) return false;
        var components = new List<int>();
        foreach (var part in record.Version.Trim().Split('.'))
        {
            var digits = new string(part.TakeWhile(char.IsAsciiDigit).ToArray());
            if (digits.Length == 0) break;
            if (!int.TryParse(digits, out var n)) return false;
            components.Add(n);
        }
        int[] minimum = [1, 5, 2];
        for (var i = 0; i < Math.Min(components.Count, minimum.Length); i++)
        {
            if (components[i] != minimum[i]) return components[i] > minimum[i];
        }
        return components.Count >= minimum.Length;
    }

    public static string? SanitizeName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        name = new string(name.Where(c => c < 128).ToArray()).Trim();
        foreach (var n in new[] { 5, 3, 2 }) name = name.Replace(new string(' ', n), " ", StringComparison.Ordinal);
        while (name.Length > 0 && !char.IsAsciiLetterOrDigit(name[0])) name = name[1..];
        while (name.Length > 0 && !char.IsAsciiLetterOrDigit(name[^1]) && name[^1] != ')') name = name[..^1];
        return name.Length == 0 ? null : name;
    }

    private static string? NormalizeIfac(string? value) => string.IsNullOrEmpty(value) || value == "None" ? null : value;
    private static string SanitizePublished(string value) => value.Replace("\n", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Trim();
    [GeneratedRegex(@"^(?!-)[a-zA-Z0-9-]{1,63}(?<!-)$", RegexOptions.CultureInvariant)]
    private static partial Regex HostLabel();
}
