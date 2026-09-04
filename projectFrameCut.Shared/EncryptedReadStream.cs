using System.Buffers.Binary;
using System.Security.Cryptography;

namespace projectFrameCut.Shared;

public sealed class EncryptedReadStream : Stream
{
    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private readonly Lock _lock = new();
    private readonly AesGcm _aes;
    private readonly byte[] _key;
    private readonly byte[] _headerData;
    private readonly byte[] _nonce = new byte[EncryptedStreamCrypto.NonceSize];
    private readonly byte[] _aad = new byte[EncryptedStreamCrypto.HeaderDataSize + sizeof(uint)];
    private readonly byte[] _plainBlock;
    private readonly byte[] _cipherBlock;
    private readonly byte[] _tag = new byte[EncryptedStreamCrypto.TagSize];
    private readonly int _blockSize;
    private readonly uint _nonceTail;
    private long _position;
    private long _cachedBlock = -1;
    private int _cachedBlockLength;
    private bool _disposed;

    public EncryptedReadStream(Stream source, byte[] key, bool leaveOpen = false)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _leaveOpen = leaveOpen;
        EncryptedStreamCrypto.ValidateKey(key);
        _key = key.ToArray();
        _aes = new AesGcm(_key, EncryptedStreamCrypto.TagSize);

        try
        {
            if (!source.CanRead) throw new ArgumentException("The encrypted stream must be readable.", nameof(source));
            if (!source.CanSeek) throw new ArgumentException("The encrypted stream must be seekable.", nameof(source));
            if (source.Position != 0) throw new ArgumentException("The encrypted stream position must be zero.", nameof(source));
            if (source.Length < EncryptedStreamCrypto.HeaderSize)
                throw new InvalidDataException("The encrypted stream is shorter than its header.");

            byte[] header = new byte[EncryptedStreamCrypto.HeaderSize];
            source.ReadExactly(header);
            if (!header.AsSpan(0, 8).SequenceEqual(EncryptedStreamCrypto.Magic))
                throw new InvalidDataException("The stream is not a supported encrypted projectFrameCut asset.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)) != EncryptedStreamCrypto.Version)
                throw new NotSupportedException("The encrypted stream version is not supported.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10)) != EncryptedStreamCrypto.HeaderSize)
                throw new InvalidDataException("The encrypted stream header size is invalid.");

            _blockSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
            EncryptedStreamCrypto.ValidateBlockSize(_blockSize);
            Length = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
            long blockCount = EncryptedStreamCrypto.GetBlockCount(Length, _blockSize);
            if (blockCount > uint.MaxValue)
                throw new InvalidDataException("The encrypted stream contains too many blocks.");
            if (source.Length != EncryptedStreamCrypto.GetEncryptedLength(Length, _blockSize))
                throw new InvalidDataException("The encrypted stream length does not match its header.");

            _headerData = header.AsSpan(0, EncryptedStreamCrypto.HeaderDataSize).ToArray();
            _headerData.CopyTo(_aad, 0);
            _headerData.AsSpan(24, EncryptedStreamCrypto.NonceSize).CopyTo(_nonce);
            _nonceTail = BinaryPrimitives.ReadUInt32LittleEndian(_nonce.AsSpan(8));
            BinaryPrimitives.WriteUInt32LittleEndian(_nonce.AsSpan(8), _nonceTail ^ uint.MaxValue);
            _aes.Decrypt(_nonce, ReadOnlySpan<byte>.Empty,
                header.AsSpan(EncryptedStreamCrypto.HeaderDataSize, EncryptedStreamCrypto.TagSize),
                Span<byte>.Empty, _headerData);

            _plainBlock = new byte[_blockSize];
            _cipherBlock = new byte[_blockSize];
            Logger.LogDiagnostic($"Opened encrypted stream: {Length} plaintext bytes, {_blockSize}-byte blocks.");
        }
        catch
        {
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_key);
            if (!leaveOpen) source.Dispose();
            throw;
        }
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length { get; }
    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_position >= Length || buffer.IsEmpty) return 0;

            int total = 0;
            while (!buffer.IsEmpty && _position < Length)
            {
                long block = _position / _blockSize;
                EnsureBlock(block);
                int offset = (int)(_position % _blockSize);
                int length = Math.Min(buffer.Length, _cachedBlockLength - offset);
                _plainBlock.AsSpan(offset, length).CopyTo(buffer);
                buffer = buffer[length..];
                _position += length;
                total += length;
            }
            return total;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (position < 0 || position > Length)
                throw new IOException($"Cannot seek outside the plaintext stream: {position}/{Length}.");
            _position = position;
            return position;
        }
    }

    private void EnsureBlock(long block)
    {
        if (_cachedBlock == block) return;
        int length = (int)Math.Min(_blockSize, Length - block * _blockSize);
        long encryptedOffset = checked(EncryptedStreamCrypto.HeaderSize +
            block * (_blockSize + EncryptedStreamCrypto.TagSize));
        _source.Position = encryptedOffset;
        _source.ReadExactly(_cipherBlock.AsSpan(0, length));
        _source.ReadExactly(_tag);
        BinaryPrimitives.WriteUInt32LittleEndian(_nonce.AsSpan(8), _nonceTail ^ (uint)block);
        BinaryPrimitives.WriteUInt32LittleEndian(_aad.AsSpan(EncryptedStreamCrypto.HeaderDataSize), (uint)block);
        try
        {
            _aes.Decrypt(_nonce, _cipherBlock.AsSpan(0, length), _tag,
                _plainBlock.AsSpan(0, length), _aad);
            _cachedBlock = block;
            _cachedBlockLength = length;
        }
        catch (CryptographicException ex)
        {
            Logger.Log(ex, $"Encrypted stream block {block} failed authentication.", this);
            throw;
        }
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_key);
            CryptographicOperations.ZeroMemory(_plainBlock);
            CryptographicOperations.ZeroMemory(_cipherBlock);
            CryptographicOperations.ZeroMemory(_tag);
            CryptographicOperations.ZeroMemory(_nonce);
            CryptographicOperations.ZeroMemory(_aad);
            if (!_leaveOpen) _source.Dispose();
            Logger.LogDiagnostic("Closed encrypted stream.");
        }
        base.Dispose(disposing);
    }
}
