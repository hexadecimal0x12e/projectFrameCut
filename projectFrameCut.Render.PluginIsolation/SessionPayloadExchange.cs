using projectFrameCut.Render.Contracts;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;

namespace projectFrameCut.Render.PluginIsolation;

public sealed class SessionPayloadExchange : IIsolationPayloadExchange
{
    private readonly string _root;
    private readonly int _maximumInlineBytes;
    private readonly long _maximumPayloadBytes;
    private int _disposed;

    public SessionPayloadExchange(string sessionRoot, int maximumInlineBytes = 64 * 1024, long maximumPayloadBytes = PluginIsolationProtocol.DefaultMaximumPayloadBytes)
    {
        _root = Path.GetFullPath(sessionRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _maximumInlineBytes = maximumInlineBytes;
        _maximumPayloadBytes = maximumPayloadBytes;
        Directory.CreateDirectory(Path.Combine(_root, "payloads"));
    }

    public async ValueTask<IsolationPayloadLease> PublishAsync(ReadOnlyMemory<byte> data, IsolationPayloadKind preferredKind, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (data.Length > _maximumPayloadBytes) throw new InvalidDataException("Isolation payload is too large.");
        if (preferredKind == IsolationPayloadKind.Inline && data.Length <= _maximumInlineBytes)
        {
            var inline = data.ToArray();
            return new(new IsolationPayloadReference
            {
                Kind = IsolationPayloadKind.Inline,
                Access = IsolationPayloadAccess.ReadOnly,
                Length = inline.Length,
                InlineData = inline,
                ContentHash = Convert.ToHexString(SHA256.HashData(inline)),
            });
        }

        var kind = preferredKind is IsolationPayloadKind.LocalFile ? IsolationPayloadKind.LocalFile : IsolationPayloadKind.SharedMemory;
        var locator = $"payloads/{Guid.NewGuid():N}.bin";
        var path = ResolvePath(locator);
        await File.WriteAllBytesAsync(path, data.ToArray(), cancellationToken).ConfigureAwait(false);
        var reference = new IsolationPayloadReference
        {
            Kind = kind,
            Locator = locator,
            Access = IsolationPayloadAccess.ReadOnly,
            Length = data.Length,
            ContentHash = Convert.ToHexString(SHA256.HashData(data.Span)),
        };
        return new(reference, ReleaseAsync);
    }

    public async ValueTask<IsolationPayloadLease> AllocateAsync(long length, IsolationPayloadKind preferredKind, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ValidateLength(length);
        if (preferredKind == IsolationPayloadKind.Inline && length <= _maximumInlineBytes)
        {
            var data = new byte[checked((int)length)];
            return new(new IsolationPayloadReference
            {
                Kind = IsolationPayloadKind.Inline,
                Access = IsolationPayloadAccess.ReadWrite,
                Length = length,
                InlineData = data,
            });
        }
        var locator = $"payloads/{Guid.NewGuid():N}.bin";
        var path = ResolvePath(locator);
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.Asynchronous | FileOptions.RandomAccess))
            stream.SetLength(length);
        return new(new IsolationPayloadReference
        {
            Kind = preferredKind is IsolationPayloadKind.LocalFile ? IsolationPayloadKind.LocalFile : IsolationPayloadKind.SharedMemory,
            Locator = locator,
            Access = IsolationPayloadAccess.ReadWrite,
            Length = length,
        }, ReleaseAsync);
    }

    public ValueTask<Stream> OpenReadAsync(IsolationPayloadReference reference, CancellationToken cancellationToken = default)
    {
        ValidateReference(reference, forWrite: false);
        if (reference.Kind == IsolationPayloadKind.Inline)
            return ValueTask.FromResult<Stream>(new MemoryStream(reference.InlineData, checked((int)reference.Offset), checked((int)(reference.Length - reference.Offset)), writable: false));
        var path = ResolvePath(reference.Locator);
        VerifyFile(path, reference);
        if (reference.Kind == IsolationPayloadKind.SharedMemory)
        {
            var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, reference.Length, MemoryMappedFileAccess.Read);
            return ValueTask.FromResult<Stream>(new OwnedMappedStream(map, map.CreateViewStream(reference.Offset, reference.Length - reference.Offset, MemoryMappedFileAccess.Read)));
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
        stream.Position = reference.Offset;
        return ValueTask.FromResult<Stream>(stream);
    }

    public ValueTask<Stream> OpenWriteAsync(IsolationPayloadReference reference, CancellationToken cancellationToken = default)
    {
        ValidateReference(reference, forWrite: true);
        if (reference.Kind == IsolationPayloadKind.Inline)
            return ValueTask.FromResult<Stream>(new MemoryStream(reference.InlineData, checked((int)reference.Offset), checked((int)(reference.Length - reference.Offset)), writable: true));
        var path = ResolvePath(reference.Locator);
        if (reference.Kind == IsolationPayloadKind.SharedMemory)
        {
            var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, reference.Length, MemoryMappedFileAccess.ReadWrite);
            return ValueTask.FromResult<Stream>(new OwnedMappedStream(map, map.CreateViewStream(reference.Offset, reference.Length - reference.Offset, MemoryMappedFileAccess.Write)));
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
        stream.Position = reference.Offset;
        return ValueTask.FromResult<Stream>(stream);
    }

    public ValueTask ReleaseAsync(IsolationPayloadReference reference)
    {
        if (reference.Kind != IsolationPayloadKind.Inline)
        {
            try { File.Delete(ResolvePath(reference.Locator)); } catch { }
        }
        return ValueTask.CompletedTask;
    }

    private void ValidateReference(IsolationPayloadReference reference, bool forWrite)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateLength(reference.Length);
        if (reference.Offset < 0 || reference.Offset > reference.Length) throw new InvalidDataException("Isolation payload offset is invalid.");
        if (forWrite && reference.Access == IsolationPayloadAccess.ReadOnly) throw new UnauthorizedAccessException("Isolation payload is read-only.");
        if (!forWrite && reference.Access == IsolationPayloadAccess.WriteOnly) throw new UnauthorizedAccessException("Isolation payload is write-only.");
        if (reference.Kind == IsolationPayloadKind.Inline && reference.InlineData.LongLength != reference.Length)
            throw new InvalidDataException("Inline payload length does not match its descriptor.");
        if (reference.Kind == IsolationPayloadKind.Inline && !string.IsNullOrEmpty(reference.ContentHash)
            && !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(reference.ContentHash), SHA256.HashData(reference.InlineData)))
            throw new InvalidDataException("Inline payload hash validation failed.");
    }

    private void ValidateLength(long length)
    {
        if (length < 0 || length > _maximumPayloadBytes) throw new InvalidDataException("Isolation payload length is invalid.");
    }

    private string ResolvePath(string locator)
    {
        if (string.IsNullOrWhiteSpace(locator) || Path.IsPathRooted(locator)) throw new InvalidDataException("Isolation payload locator must be relative.");
        var path = Path.GetFullPath(Path.Combine(_root, locator.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(_root, comparison)) throw new UnauthorizedAccessException("Isolation payload locator escapes the session root.");
        for (var current = path; !string.Equals(current, _root.TrimEnd(Path.DirectorySeparatorChar), comparison); current = Path.GetDirectoryName(current)!)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Isolation payload locator crosses a reparse point.");
            }
            if (string.IsNullOrEmpty(Path.GetDirectoryName(current))) break;
        }
        return path;
    }

    private static void VerifyFile(string path, IsolationPayloadReference reference)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != reference.Length) throw new InvalidDataException("Isolation payload file length does not match its descriptor.");
        if (!string.IsNullOrEmpty(reference.ContentHash))
        {
            using var stream = info.OpenRead();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(reference.ContentHash), SHA256.HashData(stream)))
                throw new InvalidDataException("Isolation payload hash validation failed.");
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private sealed class OwnedMappedStream(MemoryMappedFile map, MemoryMappedViewStream view) : Stream
    {
        public override bool CanRead => view.CanRead;
        public override bool CanSeek => view.CanSeek;
        public override bool CanWrite => view.CanWrite;
        public override long Length => view.Length;
        public override long Position { get => view.Position; set => view.Position = value; }
        public override void Flush() => view.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => view.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => view.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => view.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => view.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => view.Seek(offset, origin);
        public override void SetLength(long value) => view.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => view.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => view.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => view.WriteAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { view.Dispose(); map.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await view.DisposeAsync().ConfigureAwait(false);
            map.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

public sealed class SessionResourceBroker : IIsolationResourceBroker
{
    private readonly string _root;
    private readonly long _maximumBytes;

    public SessionResourceBroker(string sessionRoot, long maximumBytes = PluginIsolationProtocol.DefaultMaximumPayloadBytes)
    {
        _root = Path.GetFullPath(sessionRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _maximumBytes = maximumBytes;
        Directory.CreateDirectory(Path.Combine(_root, "resources"));
    }

    public async ValueTask<IsolationPayloadLease> BrokerReadOnlyFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await BrokerReadOnlyStreamAsync(source, source.Length, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IsolationPayloadLease> BrokerReadOnlyStreamAsync(Stream source, long length, CancellationToken cancellationToken = default)
    {
        if (!source.CanRead || length < 0 || length > _maximumBytes) throw new InvalidDataException("Brokered resource length is invalid.");
        var locator = $"resources/{Guid.NewGuid():N}.bin";
        var path = Resolve(locator);
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            if (output.Length != length) throw new InvalidDataException("Brokered stream length changed while copying.");
        }
        using var verify = File.OpenRead(path);
        return new(new IsolationPayloadReference
        {
            Kind = IsolationPayloadKind.TransferredResource,
            Locator = locator,
            Access = IsolationPayloadAccess.ReadOnly,
            Length = length,
            ContentHash = Convert.ToHexString(SHA256.HashData(verify)),
        }, r => { try { File.Delete(Resolve(r.Locator)); } catch { } return ValueTask.CompletedTask; });
    }

    public string ResolveReadOnlyFile(IsolationPayloadReference reference)
    {
        if (reference.Kind is not (IsolationPayloadKind.TransferredResource or IsolationPayloadKind.LocalFile))
            throw new InvalidDataException("The resource reference is not file-backed.");
        var path = Resolve(reference.Locator);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != reference.Length) throw new InvalidDataException("Brokered resource length validation failed.");
        using var stream = info.OpenRead();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(reference.ContentHash), SHA256.HashData(stream)))
            throw new InvalidDataException("Brokered resource hash validation failed.");
        return path;
    }

    private string Resolve(string locator)
    {
        if (string.IsNullOrWhiteSpace(locator) || Path.IsPathRooted(locator)) throw new InvalidDataException("Resource locator must be relative.");
        var path = Path.GetFullPath(Path.Combine(_root, locator.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(_root, comparison)) throw new UnauthorizedAccessException("Resource locator escapes the session root.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Brokered resources cannot be reparse points.");
        return path;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
