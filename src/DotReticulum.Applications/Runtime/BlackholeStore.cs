using System.Text;
using DotReticulum.Core;

namespace DotReticulum.Applications.Runtime;

/// <summary>
/// Single-owner, thread-safe blackhole directory. Mutations are persisted before becoming visible.
/// Live remote updates are additive, including conflicts/empty lists, as in the pinned updater.
/// Restart trusts configured file provenance, prefers local entries and filters expired entries.
/// Corruption fails explicitly without replacing the current in-memory policy.
/// </summary>
public sealed class BlackholeStore
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly byte[] _local;
    private readonly HashSet<string> _sources;
    private readonly TimeProvider _time;
    private Dictionary<string, BlackholeRecord> _records = new(StringComparer.Ordinal);

    public BlackholeStore(string storageDirectory, byte[] localIdentityHash, IEnumerable<byte[]>? authorizedSources = null, TimeProvider? timeProvider = null)
    {
        ValidateHash(localIdentityHash);
        _directory = Path.GetFullPath(storageDirectory);
        _local = localIdentityHash.ToArray();
        _sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hash in authorizedSources ?? [])
        {
            ValidateHash(hash);
            _sources.Add(Convert.ToHexStringLower(hash));
        }

        if (_sources.Count > 256) throw new ArgumentException("At most 256 blackhole sources may be configured.", nameof(authorizedSources));
        _time = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(_directory);
        Reload();
    }

    public Action<string, Exception>? Diagnostic { get; set; }

    /// <summary>Invoked outside the store lock after a policy change. Coordinator may remove newly blocked paths.</summary>
    public event Action? Changed;

    public byte[] LocalIdentityHash => _local.ToArray();
    public IReadOnlyList<byte[]> AuthorizedSources => _sources.Select(Convert.FromHexString).ToArray();
    public bool IsSourceAuthorized(byte[] identityHash) => identityHash.Length == Identity.HashSize && _sources.Contains(Convert.ToHexStringLower(identityHash));
    public bool IsBlackholed(byte[] identityHash) => IsBlackholed((ReadOnlyMemory<byte>)identityHash);

    /// <summary>Matches upstream membership checks; expiry is applied by RemoveExpired on the maintenance tick.</summary>
    public bool IsBlackholed(ReadOnlyMemory<byte> identityHash)
    {
        if (identityHash.Length != Identity.HashSize) throw new ArgumentException("Identity hash must be 16 bytes.", nameof(identityHash));
        lock (_gate) return _records.ContainsKey(Convert.ToHexStringLower(identityHash.Span));
    }

    public IReadOnlyList<BlackholeEntry> Snapshot()
    {
        lock (_gate) return _records.Values.Select(r => r.Snapshot()).ToArray();
    }

    public byte[] PackPublication()
    {
        lock (_gate) return BlackholeWire.Pack(_records.Values);
    }

    /// <summary>Detached MessagePack value retaining binary map keys, for RPC and link responses.</summary>
    public IReadOnlyList<KeyValuePair<object, object?>> PublicationValue()
    {
        lock (_gate) return BlackholeWire.Value(_records.Values);
    }

    /// <returns>True when added, null when already present, false for an invalid hash (upstream public API).</returns>
    public bool? BlackholeIdentity(byte[] identityHash, double? until = null, string? reason = null)
    {
        if (identityHash.Length != Identity.HashSize) return false;
        if (until is { } expiry && !double.IsFinite(expiry)) throw new ArgumentOutOfRangeException(nameof(until));
        if (reason is not null && Encoding.UTF8.GetByteCount(reason) > BlackholeWire.MaximumReasonBytes) throw new ArgumentOutOfRangeException(nameof(reason));
        lock (_gate)
        {
            var key = Convert.ToHexStringLower(identityHash);
            if (_records.ContainsKey(key)) return null;
            if (_records.Count >= BlackholeWire.MaximumEntries) throw new InvalidOperationException("Blackhole identity limit reached.");
            var next = new Dictionary<string, BlackholeRecord>(_records, StringComparer.Ordinal)
            {
                [key] = new(identityHash.ToArray(), _local.ToArray(), until, reason, null),
            };
            EnsureCapacity(next);
            PersistLocal(next);
            _records = next;
        }

        NotifyChanged();
        return true;
    }

    public bool? UnblackholeIdentity(byte[] identityHash)
    {
        if (identityHash.Length != Identity.HashSize) return false;
        lock (_gate)
        {
            var key = Convert.ToHexStringLower(identityHash);
            if (!_records.ContainsKey(key)) return null;
            var next = new Dictionary<string, BlackholeRecord>(_records, StringComparer.Ordinal);
            next.Remove(key);
            PersistLocal(next);
            _records = next;
        }

        // Like upstream, removal does not rewrite a remote source file; reload/update may restore it.
        NotifyChanged();
        return true;
    }

    /// <summary>Rejects unconfigured suppliers, malformed lists and over-capacity updates atomically. Empty/conflicting lists never erase entries.</summary>
    public int ApplySourceUpdate(byte[] sourceIdentityHash, ReadOnlySpan<byte> packed)
    {
        if (!IsSourceAuthorized(sourceIdentityHash)) throw new UnauthorizedAccessException("Unconfigured blackhole source.");
        var received = BlackholeWire.Read(packed);
        var added = 0;
        lock (_gate)
        {
            var next = new Dictionary<string, BlackholeRecord>(_records, StringComparer.Ordinal);
            foreach (var record in received)
            {
                if (next.TryAdd(Convert.ToHexStringLower(record.Hash), record with { ObtainedFrom = sourceIdentityHash.ToArray() })) added++;
            }

            if (next.Count > BlackholeWire.MaximumEntries) throw new InvalidOperationException("Blackhole identity limit reached.");
            if (added == 0) return 0;
            EnsureCapacity(next);
            WriteAtomic(Convert.ToHexStringLower(sourceIdentityHash), packed);
            _records = next;
        }

        NotifyChanged();
        return added;
    }

    public int RemoveExpired()
    {
        var removed = 0;
        lock (_gate)
        {
            var now = UnixNow;
            var stale = _records.Where(p => p.Value.Until is { } until && MsgPack.ToDouble(until) != 0 && now > MsgPack.ToDouble(until))
                .Select(p => p.Key).ToArray();
            foreach (var key in stale) { _records.Remove(key); removed++; }
        }

        if (removed > 0) NotifyChanged();
        return removed;
    }

    public void Reload()
    {
        lock (_gate)
        {
            var next = new Dictionary<string, BlackholeRecord>(StringComparer.Ordinal);
            // Upstream protects local entries regardless of directory enumeration order; remote ties
            // are unspecified there. Sorting gives this implementation deterministic remote ties.
            foreach (var name in new[] { "local" }.Concat(_sources.Order(StringComparer.Ordinal)))
            {
                var path = Path.Combine(_directory, name);
                if (!File.Exists(path)) continue;
                if (new FileInfo(path).Length > BlackholeWire.MaximumBytes) throw new InvalidDataException($"Blackhole source file '{name}' exceeds the byte limit.");
                var supplier = name == "local" ? _local : Convert.FromHexString(name);
                List<BlackholeRecord> records;
                try { records = BlackholeWire.Read(File.ReadAllBytes(path), supplier); }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    throw new InvalidDataException($"Invalid blackhole source file '{name}'.", ex);
                }

                foreach (var record in records)
                {
                    var key = Convert.ToHexStringLower(record.Hash);
                    if (next.TryGetValue(key, out var existing) && existing.ObtainedFrom is null) continue;
                    if (record.Until is not null && UnixNow >= MsgPack.ToDouble(record.Until)) continue;
                    next[key] = record with { Source = supplier.ToArray(), ObtainedFrom = name == "local" ? null : supplier.ToArray() };
                }

                if (next.Count > BlackholeWire.MaximumEntries) throw new InvalidDataException("Persisted blackhole identity limit reached.");
            }

            EnsureCapacity(next);
            _records = next;
        }

        NotifyChanged();
    }

    private double UnixNow => (_time.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds;
    private void PersistLocal(Dictionary<string, BlackholeRecord> records) =>
        WriteAtomic("local", BlackholeWire.Pack(records.Values.Where(r => r.ObtainedFrom is null)));

    private void WriteAtomic(string name, ReadOnlySpan<byte> packed)
    {
        if (packed.Length > BlackholeWire.MaximumBytes) throw new InvalidDataException("Blackhole list exceeds the byte limit.");
        var path = Path.Combine(_directory, name);
        var staging = path + ".tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(packed);
                stream.Flush(flushToDisk: true);
            }

            // Windows indexers/antivirus can briefly retain a newly closed file without
            // delete sharing. Keep the old policy intact while retrying only those errors.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(staging, path, overwrite: true); break; }
                catch (Exception ex) when (attempt < 5 && OperatingSystem.IsWindows()
                    && ex is IOException or UnauthorizedAccessException
                    && (ex.HResult & 0xffff) is 5 or 32 or 33
                    && !Directory.Exists(path) && !Directory.Exists(staging))
                {
                    Thread.Sleep(10 << attempt);
                }
            }
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private static void EnsureCapacity(Dictionary<string, BlackholeRecord> records)
    {
        if (BlackholeWire.Pack(records.Values).Length > BlackholeWire.MaximumBytes)
            throw new InvalidDataException("Combined blackhole list exceeds the byte limit.");
    }

    private static void ValidateHash(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Length != Identity.HashSize) throw new ArgumentException("Identity hash must be 16 bytes.", nameof(hash));
    }

    private void NotifyChanged()
    {
        if (Changed is not { } observers) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                Report("Blackhole policy change observer failed.", ex);
            }
        }
    }

    private void Report(string message, Exception error)
    {
        if (Diagnostic is { } diagnostic)
        {
            try { diagnostic(message, error); return; }
            catch (Exception observerError) when (observerError is not OutOfMemoryException and not StackOverflowException)
            {
                System.Diagnostics.Trace.TraceError("Blackhole diagnostic observer failed: {0}", observerError);
            }
        }

        System.Diagnostics.Trace.TraceError("{0} {1}", message, error);
    }
}
