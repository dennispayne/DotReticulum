using System.Security.Cryptography;
using DotReticulum.Core;
using DotReticulum.Transport;
using static DotReticulum.Applications.Runtime.PersistenceFormats;

namespace DotReticulum.Applications.Runtime;

/// <summary>Atomic, bounded runtime persistence. Missing files mean an empty initial state; corrupt files
/// throw contextual InvalidDataException and are never silently replaced during load.
/// Files use same-directory staging and atomic replacement. Only the owning host should save shared state.</summary>
public sealed class RuntimeStateStore
{
    public const int MaximumStateBytes = 64 * 1024 * 1024;
    public const int MaximumRatchetBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly TimeProvider _time;
    public string StorageDirectory { get; }
    public string StatePath => Path.Combine(StorageDirectory, "runtime-state-v1.msgpack");

    public RuntimeStateStore(string storageDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        StorageDirectory = Path.GetFullPath(storageDirectory);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Stable upstream raw-64 private identity format. Creation races publish one complete winner;
    /// all losers dispose their generated identity and load that winner. Existing corruption is an error.
    /// Unix creation is owner-only; Windows inherits directory ACLs, which the caller must protect.</summary>
    public static Identity LoadOrCreateIdentity(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            try { return LoadIdentity(path); }
            catch (FileNotFoundException) { }
            using var candidate = Identity.Generate();
            var staging = path + ".out." + Guid.NewGuid().ToString("N");
            var key = candidate.ExportPrivateKey();
            try
            {
                using (var file = CreateFile(staging))
                {
                    file.Write(key);
                    file.Flush(flushToDisk: true);
                }
                AtomicFilePublication.TryPublishNew(staging, path);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                if (File.Exists(staging)) File.Delete(staging);
            }
            return LoadIdentity(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
        {
            throw new InvalidDataException($"Could not load or create runtime identity '{path}': {exception.Message}", exception);
        }
    }

    private static Identity LoadIdentity(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length != Identity.KeySize) throw new InvalidDataException($"Runtime identity '{path}' must contain exactly 64 private-key bytes.");
        var key = new byte[Identity.KeySize];
        try
        {
            file.ReadExactly(key);
            return Identity.FromPrivateKey(key);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task SaveAsync(RuntimeStateSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] packed;
        try { packed = EncodeState(snapshot); }
        catch (Exception exception) when (IsFormatFailure(exception))
        {
            throw new InvalidDataException($"Invalid runtime snapshot for '{StatePath}': {exception.Message}", exception);
        }
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await AtomicWriteAsync(StatePath, packed, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new IOException($"Could not persist runtime state '{StatePath}'.", exception); }
        finally { _io.Release(); }
    }

    public async Task<RuntimeStateSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await ReadBoundedAsync(StatePath, MaximumStateBytes, cancellationToken).ConfigureAwait(false);
            if (data is null) return RuntimeStateSnapshot.Empty;
            try { return DecodeState(data); }
            catch (Exception exception) when (IsFormatFailure(exception))
            {
                throw new InvalidDataException($"Corrupt runtime state '{StatePath}': {exception.Message}", exception);
            }
        }
        finally { _io.Release(); }
    }

    public Task SaveAsync(IRuntimeStateProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return SaveAsync(provider.CaptureState(), cancellationToken);
    }

    public async Task RestoreAsync(IRuntimeStateProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var snapshot = await LoadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        provider.RestoreState(snapshot);
    }

    /// <summary>Persists a signed, destination-bound secret snapshot. All metadata is covered by the
    /// identity signature, unlike upstream's key-only envelope. Capturing happens before the first await.</summary>
    public async Task SaveRatchetsAsync(byte[] destinationHash, Identity identity, DestinationRatchets ratchets,
        CancellationToken cancellationToken = default)
    {
        var path = RatchetPath(destinationHash);
        using var snapshot = ratchets.ExportSnapshot();
        cancellationToken.ThrowIfCancellationRequested();
        var encoded = EncodeRatchets(path, destinationHash, identity, snapshot);
        try
        {
            await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await AtomicWriteAsync(path, encoded, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new IOException($"Could not persist destination ratchets '{path}'.", exception); }
            finally { _io.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    /// <summary>Explicit opt-in equivalent of upstream Destination.enable_ratchets(path). Loads or creates
    /// a signed ring and binds mandatory save-before-announce persistence to automatic rotations.
    /// The ring owns an independent signing identity, never the caller's identity. Dispose the returned
    /// ring to zero its keys and signing material. The token applies only to opening/initial persistence;
    /// subsequent synchronous rotations and signed reload-on-decryption-failure run independently of that token's
    /// later cancellation. Reload replaces keys only, preserving the live ring's enforcement/rotation policy.</summary>
    public async Task<DestinationRatchets> OpenPersistentRatchetsAsync(byte[] destinationHash, Identity identity,
        CancellationToken cancellationToken = default)
    {
        var path = RatchetPath(destinationHash);
        var hash = destinationHash.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var privateKey = identity.ExportPrivateKey();
        Identity signer;
        try { signer = Identity.FromPrivateKey(privateKey); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
        DestinationRatchets? ring = null;
        try
        {
            ring = await LoadRatchetsAsync(hash, signer, cancellationToken).ConfigureAwait(false) ?? new DestinationRatchets();
            var openingToken = cancellationToken;
            ring.EnablePersistence(snapshot => SaveRatchetSnapshot(path, hash, signer, snapshot, openingToken), signer);
            openingToken = default;
            ring.EnableReload(() =>
            {
                using var reloaded = LoadRatchetsAsync(hash, signer).GetAwaiter().GetResult()
                    ?? throw new IOException($"Destination ratchet file '{path}' disappeared before reload.");
                return reloaded.ExportPrivateKeys();
            });
            return ring;
        }
        catch
        {
            ring?.Dispose();
            signer.Dispose();
            throw;
        }
    }

    private void SaveRatchetSnapshot(string path, byte[] destinationHash, Identity identity,
        DestinationRatchetSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encoded = EncodeRatchets(path, destinationHash, identity, snapshot);
        try
        {
            _io.Wait(cancellationToken);
            try { AtomicWriteAsync(path, encoded, cancellationToken).GetAwaiter().GetResult(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new IOException($"Could not persist destination ratchets '{path}'.", exception); }
            finally { _io.Release(); }
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    private static byte[] EncodeRatchets(string path, byte[] destinationHash, Identity identity, DestinationRatchetSnapshot snapshot)
    {
        try
        {
            if (snapshot.Retained is < 1 or > MaximumEntries || snapshot.PrivateKeys.Count > snapshot.Retained ||
                snapshot.PrivateKeys.Any(key => key.Length != Identity.RatchetSize) || snapshot.LatestRotationUnix < 0 || snapshot.Interval < TimeSpan.Zero)
                throw new InvalidDataException("Invalid destination ratchet keys or rotation policy.");
            var payload = PersistenceSecretMessagePack.Pack(new Dictionary<object, object?>
            {
                ["version"] = 1L, ["destination"] = destinationHash.ToArray(), ["identity"] = identity.Hash.ToArray(),
                ["keys"] = snapshot.PrivateKeys.Cast<object?>().ToArray(), ["rotation"] = snapshot.LatestRotationUnix,
                ["retained"] = snapshot.Retained, ["interval_ticks"] = snapshot.Interval.Ticks, ["enforce"] = snapshot.Enforce,
            });
            try
            {
                return PersistenceSecretMessagePack.Pack(new Dictionary<object, object?> { ["payload"] = payload, ["signature"] = identity.Sign(payload) });
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        catch (Exception exception) when (IsFormatFailure(exception))
        { throw new InvalidDataException($"Invalid destination ratchet snapshot '{path}': {exception.Message}", exception); }
    }

    /// <summary>Loads a local secret ring without expiring retained keys (upstream local rings trim by
    /// retained count, not wall time). Missing files return null; invalid signatures never create fresh keys.</summary>
    public async Task<DestinationRatchets?> LoadRatchetsAsync(byte[] destinationHash, Identity identity,
        CancellationToken cancellationToken = default)
    {
        var path = RatchetPath(destinationHash);
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? encoded = null;
        byte[]? payload = null;
        object? decodedPayload = null;
        try
        {
            encoded = await ReadBoundedAsync(path, MaximumRatchetBytes, cancellationToken).ConfigureAwait(false);
            if (encoded is null) return null;
            var envelope = Map(MsgPack.Unpack(encoded), "destination ratchet envelope");
            payload = NullableBytes(Required(envelope, "payload"), "ratchet payload") ?? throw new InvalidDataException("Null ratchet payload.");
            var signature = Bytes(Required(envelope, "signature"), 64, "ratchet signature");
            if (!identity.Verify(signature, payload)) throw new InvalidDataException("Destination ratchet signature is invalid for the supplied identity.");
            decodedPayload = MsgPack.Unpack(payload);
            var fields = Map(decodedPayload, "destination ratchet payload");
            if (Integer(Required(fields, "version"), "ratchet version") != 1) throw new InvalidDataException("Unsupported ratchet format version.");
            if (!Bytes(Required(fields, "destination"), 16, "ratchet destination").AsSpan().SequenceEqual(destinationHash) ||
                !Bytes(Required(fields, "identity"), 16, "ratchet identity").AsSpan().SequenceEqual(identity.Hash))
                throw new InvalidDataException("Destination ratchet owner does not match.");
            var rotation = Integer(Required(fields, "rotation"), "ratchet rotation");
            var retained = Integer(Required(fields, "retained"), "ratchet retained count");
            var interval = Integer(Required(fields, "interval_ticks"), "ratchet rotation interval");
            var enforce = Required(fields, "enforce") is bool value ? value : throw new InvalidDataException("Ratchet enforcement must be a boolean.");
            var keys = Array(Required(fields, "keys"), "ratchet keys");
            if (rotation < 0 || retained is < 1 or > MaximumEntries || interval < 0 || keys.Length > retained)
                throw new InvalidDataException("Invalid destination ratchet rotation policy or retained count.");
            var privateKeys = keys.Select(k => Bytes(k, Identity.RatchetSize, "ratchet private key")).ToArray();
            var ring = DestinationRatchets.FromPrivateKeys(privateKeys, rotation, checked((int)retained));
            ring.Retained = checked((int)retained);
            ring.Interval = TimeSpan.FromTicks(interval);
            ring.Enforce = enforce;
            return ring;
        }
        catch (Exception exception) when (IsFormatFailure(exception))
        {
            throw new InvalidDataException($"Corrupt destination ratchets '{path}': {exception.Message}", exception);
        }
        finally
        {
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            ZeroBuffers(decodedPayload);
            _io.Release();
        }
    }

    private string RatchetPath(byte[] destinationHash)
    {
        ArgumentNullException.ThrowIfNull(destinationHash);
        if (destinationHash.Length != 16) throw new ArgumentException("Destination hash must be 16 bytes.", nameof(destinationHash));
        return Path.Combine(StorageDirectory, "destination-ratchets", Convert.ToHexStringLower(destinationHash) + ".v1.msgpack");
    }

    private byte[] EncodeState(RuntimeStateSnapshot snapshot)
    {
        var known = EncodeKnownDestinations(snapshot.KnownDestinations);
        var paths = EncodePaths(snapshot.Paths);
        var tunnels = EncodeTunnels(snapshot.Tunnels);
        DecodeKnownDestinations(known);
        DecodePaths(paths);
        DecodeTunnels(tunnels);
        var body = MsgPack.Pack(new Dictionary<object, object?>
        {
            ["known_destinations"] = known, ["paths"] = paths, ["tunnels"] = tunnels,
            ["known_ratchets"] = snapshot.KnownDestinations.Where(e => e.Entry.Ratchet is not null).Select(e =>
                (object?)new object?[] { e.DestinationHash, e.Entry.Ratchet, e.Entry.RatchetReceivedTimestamp }).ToArray(),
            ["packet_hashes"] = snapshot.PacketHashes.Cast<object?>().ToArray(),
            ["cached_packets"] = snapshot.CachedPackets.Select(p => (object?)(p.CachedAt is { } cachedAt
                ? new object?[] { p.PacketHash, p.Raw, p.Announce, cachedAt }
                : new object?[] { p.PacketHash, p.Raw, p.Announce })).ToArray(),
        });
        if (body.Length > MaximumStateBytes - 128) throw new InvalidDataException("Runtime snapshot exceeds the byte limit.");
        var packed = MsgPack.Pack(new Dictionary<object, object?>
        {
            ["version"] = 1L, ["payload"] = body, ["sha256"] = SHA256.HashData(body),
        });
        DecodeState(packed);
        return packed;
    }

    private RuntimeStateSnapshot DecodeState(byte[] packed)
    {
        if (packed.Length > MaximumStateBytes) throw new InvalidDataException("Runtime state exceeds the byte limit.");
        var envelope = Map(MsgPack.Unpack(packed), "runtime envelope");
        if (Integer(Required(envelope, "version"), "runtime version") != 1) throw new InvalidDataException("Unsupported runtime state version.");
        var body = NullableBytes(Required(envelope, "payload"), "runtime payload") ?? throw new InvalidDataException("Null runtime payload.");
        var hash = Bytes(Required(envelope, "sha256"), 32, "runtime payload checksum");
        if (!CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(body))) throw new InvalidDataException("Runtime payload checksum mismatch.");
        var state = Map(MsgPack.Unpack(body), "runtime payload");
        byte[] FieldBytes(string name) => NullableBytes(Required(state, name), name) ?? throw new InvalidDataException($"Null runtime {name}.");
        var known = DecodeKnownDestinations(FieldBytes("known_destinations")).ToDictionary(e => Convert.ToHexString(e.DestinationHash));
        var publicRatchets = Array(Required(state, "known_ratchets"), "public ratchets");
        Bound(publicRatchets.Length, "public ratchets");
        var ratchetDestinations = new HashSet<string>(StringComparer.Ordinal);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        foreach (var value in publicRatchets)
        {
            var entry = Array(value, "public ratchet");
            if (entry.Length != 3) throw new InvalidDataException("Public ratchet must have three fields.");
            var destination = Bytes(entry[0], 16, "public ratchet destination");
            var key = Convert.ToHexString(destination);
            if (!ratchetDestinations.Add(key)) throw new InvalidDataException("Duplicate public ratchet destination.");
            if (!known.TryGetValue(key, out var remembered)) throw new InvalidDataException("Public ratchet belongs to an unknown destination.");
            var ratchet = Bytes(entry[1], 32, "public ratchet key");
            var received = Timestamp(entry[2], "public ratchet received");
            if (received > long.MaxValue) throw new InvalidDataException("Public ratchet timestamp exceeds supported Unix seconds.");
            if (now < received + KnownDestinations.RatchetExpiry.TotalSeconds)
                known[key] = remembered with { Entry = remembered.Entry with { Ratchet = ratchet, RatchetReceivedUnix = (long)received, RatchetReceivedTimestamp = received } };
        }
        var paths = DecodePaths(FieldBytes("paths"));
        var tunnels = DecodeTunnels(FieldBytes("tunnels"));
        var hashes = Array(Required(state, "packet_hashes"), "packet hashes");
        Bound(hashes.Length, "packet hashes");
        var packetHashes = hashes.Select(h => Bytes(h, 32, "packet hash")).ToArray();
        if (packetHashes.Select(Convert.ToHexString).Distinct(StringComparer.Ordinal).Count() != packetHashes.Length)
            throw new InvalidDataException("Duplicate persisted packet hash.");
        var packets = Array(Required(state, "cached_packets"), "cached packets");
        Bound(packets.Length, "cached packets");
        var cached = new List<PersistedPacket>(packets.Length);
        var packetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in packets)
        {
            var fields = Array(value, "cached packet");
            if (fields.Length is not (3 or 4)) throw new InvalidDataException("Cached packet must have three fields and an optional cache timestamp.");
            var packetHash = Bytes(fields[0], 32, "cached packet hash");
            var raw = NullableBytes(fields[1], "cached packet wire bytes") ?? throw new InvalidDataException("Null cached packet.");
            var announce = fields[2] is bool kind ? kind : throw new InvalidDataException("Invalid cached packet kind.");
            double? cachedAt = fields.Length == 4 && fields[3] is not null ? Timestamp(fields[3], "packet cache timestamp") : null;
            var packet = Packet.Parse(raw);
            if (!packet.CalculateHash().AsSpan().SequenceEqual(packetHash)) throw new InvalidDataException("Cached packet hash does not match the wire packet.");
            if (announce && (packet.PacketType != PacketType.Announce || !Announce.Validate(packet).IsValid))
                throw new InvalidDataException("Cached announce is not a valid signed announce.");
            if (!packetIds.Add((announce ? "a" : "p") + Convert.ToHexString(packetHash))) throw new InvalidDataException("Duplicate cached packet.");
            cached.Add(new(packetHash, raw, announce, cachedAt));
        }
        return new(known.Values.ToArray(), paths.Where(p => p.Expires > now).ToArray(),
            tunnels.Where(t => t.Expires > now).Select(t => t with { Paths = t.Paths.Where(p => p.Expires > now).ToArray() }).ToArray(),
            packetHashes, cached);
    }

    private static FileStream CreateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static async Task AtomicWriteAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + ".out." + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = CreateFile(staging))
            {
                await file.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await FileSystemSharingRetry.ExecuteAsync(() => File.Move(staging, path, overwrite: true),
                cancellationToken).ConfigureAwait(false);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static async Task<byte[]?> ReadBoundedAsync(string path, int maximum, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (file.Length > maximum) throw new InvalidDataException($"Persistence file '{path}' exceeds {maximum} bytes.");
            var data = new byte[checked((int)file.Length)];
            try
            {
                await file.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
                return data;
            }
            catch { CryptographicOperations.ZeroMemory(data); throw; }
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception exception) when (attempt < 8 && FileSystemSharingRetry.IsTransient(exception))
        { await Task.Delay(Math.Min(200, 10 << attempt), cancellationToken).ConfigureAwait(false); }
        catch (EndOfStreamException exception)
        { throw new InvalidDataException($"Persistence file '{path}' was truncated while reading.", exception); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new IOException($"Could not read persistence file '{path}': {exception.Message}", exception); }
    }

    private static bool IsFormatFailure(Exception exception) => exception is FormatException or InvalidDataException or
        ArgumentException or OverflowException or CryptographicException or InvalidOperationException;

    private static void ZeroBuffers(object? value)
    {
        switch (value)
        {
            case byte[] bytes: CryptographicOperations.ZeroMemory(bytes); break;
            case object?[] array: foreach (var item in array) ZeroBuffers(item); break;
            case Dictionary<object, object?> map: foreach (var (key, item) in map) { ZeroBuffers(key); ZeroBuffers(item); } break;
        }
    }
}
