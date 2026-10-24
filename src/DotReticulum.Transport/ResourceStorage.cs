namespace DotReticulum.Transport;

/// <summary>Local hardening limits, not additional fields in the Reticulum wire protocol.</summary>
public sealed record ResourcePolicy
{
    public long MaxDataSize { get; init; } = 1024L * 1024 * 1024;
    public long MaxSegmentSize { get; init; } = Resource.MaxEfficientSize;
    public int MaxParts { get; init; } = 8192;
    public int MaxSegments { get; init; } = 1025;
    public int MaxMetadataSize { get; init; } = Resource.MaxEfficientSize - 4;
    public int MaxIncomingResources { get; init; } = 4;
    public int MaxQueuedResources { get; init; } = 64;
    public int MaxCompletedFileResources { get; init; } = 64;
    public long MemoryThreshold { get; init; } = 64L * 1024 * 1024;
    public string StorageDirectory { get; init; } = Path.Combine(Environment.CurrentDirectory, ".reticulum", "resources");
    public double MultipartTimeout { get; init; } = 300;

    internal void Validate()
    {
        if (MaxDataSize < 0 || MaxSegmentSize < 1 || MaxSegmentSize > Resource.AutoCompressMaxSize ||
            MaxParts < 1 || MaxSegments < 1 || MaxMetadataSize < 0 || MaxMetadataSize > Resource.MetadataMaxSize ||
            MaxIncomingResources < 1 || MaxQueuedResources < 0 || MaxCompletedFileResources < 1 ||
            MemoryThreshold < 0 || MemoryThreshold > int.MaxValue ||
            !double.IsFinite(MultipartTimeout) || MultipartTimeout <= 0 || string.IsNullOrWhiteSpace(StorageDirectory))
        {
            throw new ArgumentOutOfRangeException(nameof(ResourcePolicy), "Invalid resource policy limits.");
        }
    }
}

/// <summary>Owned receive storage; small resources preserve the byte-array API.</summary>
internal sealed class ResourceStorage(ResourcePolicy policy) : IDisposable
{
    private Stream _stream = new MemoryStream();
    private string? _path;
    private bool _disposed;

    public long Length => _stream.Length;
    public bool IsFileBacked => _path is not null;

    internal static FileStream CreateOwnedFile(string path, FileOptions options,
        FileShare share = FileShare.Read, FileAccess access = FileAccess.ReadWrite)
    {
        var configuration = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = access,
            Share = share,
            BufferSize = 64 * 1024,
            Options = options,
        };
        if (!OperatingSystem.IsWindows())
            configuration.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, configuration);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data.Length > policy.MaxDataSize - _stream.Length)
        {
            throw new InvalidDataException("Resource data exceeds the configured resource limit.");
        }

        if (_path is null && _stream.Length + data.Length > policy.MemoryThreshold)
        {
            ResourceStorageScavenger.Initialize(policy.StorageDirectory);
            var path = Path.Combine(policy.StorageDirectory, $"resource-{Guid.NewGuid():N}.bin");
            var file = CreateOwnedFile(path, FileOptions.SequentialScan);
            try
            {
                _stream.Position = 0;
                _stream.CopyTo(file);
            }
            catch
            {
                file.Dispose();
                File.Delete(path);
                throw;
            }

            _stream.Dispose();
            _stream = file;
            _path = path;
        }

        _stream.Write(data);
    }

    public byte[]? ToMemory() => _stream is MemoryStream memory ? memory.ToArray() : null;

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Flush();
        return _path is { } path
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : new MemoryStream(((MemoryStream)_stream).ToArray(), writable: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var stream = _stream;
        _stream = Stream.Null;
        try { stream.Dispose(); }
        finally { if (_path is { } path) File.Delete(path); }
    }
}

internal sealed class ResourceInput(Stream stream, bool leaveOpen) : IDisposable
{
    public long Length { get; } = stream.Length - stream.Position;
    private readonly long _offset = stream.Position;
    private bool _disposed;
    private CancellationTokenRegistration _cancellation;

    public void RegisterCancellation(CancellationToken cancellationToken, Resource resource)
    {
        _cancellation.Unregister();
        _cancellation = cancellationToken.UnsafeRegister(static state => ((Resource)state!).Cancel(), resource);
        if (_disposed) _cancellation.Unregister();
    }

    public byte[] Read(long offset, int length, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = checked(_offset + offset);
        var data = new byte[length];
        var read = 0;
        while (read < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(data, read, length - read);
            if (count == 0) throw new EndOfStreamException("Resource input ended before its declared length.");
            read += count;
        }

        return data;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Unregister();
        var source = stream;
        stream = Stream.Null;
        if (!leaveOpen) source.Dispose();
    }
}
