using FFmpeg.AutoGen;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.EncodeAndDecode;

internal sealed unsafe class FFmpegStreamIOContext : IDisposable
{
    private const int BufferSize = 32 * 1024;
    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private readonly Lock _lock = new();
    private readonly avio_alloc_context_read_packet _readPacket;
    private readonly avio_alloc_context_seek _seek;
    private GCHandle _handle;
    private AVIOContext* _context;
    private Exception? _error;
    private bool _disposed;

    public long Length { get; }
    public AVIOContext* Context => _context;

    public FFmpegStreamIOContext(Stream source, long length, bool leaveOpen)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _leaveOpen = leaveOpen;
        _readPacket = ReadPacket;
        _seek = Seek;

        try
        {
            if (!source.CanRead) throw new ArgumentException("The video stream must be readable.", nameof(source));
            if (!source.CanSeek) throw new ArgumentException("The video stream must be seekable.", nameof(source));
            if (source.Position != 0) throw new ArgumentException("The video stream position must be zero.", nameof(source));
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length), "The video stream length must be positive.");
            if (source.Length != length) throw new ArgumentException("The supplied length must equal the complete stream length.", nameof(length));

            Length = length;
            byte* buffer = (byte*)ffmpeg.av_malloc(BufferSize);
            if (buffer == null) throw new OutOfMemoryException("Failed to allocate the FFmpeg stream buffer.");

            _handle = GCHandle.Alloc(this);
            _context = ffmpeg.avio_alloc_context(
                buffer,
                BufferSize,
                0,
                (void*)GCHandle.ToIntPtr(_handle),
                _readPacket,
                default,
                _seek);
            if (_context == null)
            {
                ffmpeg.av_free(buffer);
                throw new OutOfMemoryException("Failed to allocate the FFmpeg IO context.");
            }
        }
        catch
        {
            if (_handle.IsAllocated) _handle.Free();
            if (!leaveOpen) source.Dispose();
            throw;
        }
    }

    public void Attach(AVFormatContext* formatContext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (formatContext == null) throw new ArgumentNullException(nameof(formatContext));
        formatContext->pb = _context;
        formatContext->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
    }

    public int Open(AVFormatContext** formatContext)
    {
        Attach(*formatContext);
        int result = ffmpeg.avformat_open_input(formatContext, null, null, null);
        ThrowIfFaulted();
        return result;
    }

    public int Check(int result)
    {
        ThrowIfFaulted();
        return result;
    }

    public void ThrowIfFaulted()
    {
        if (_error is Exception ex) throw new IOException("The video stream failed while FFmpeg was reading it.", ex);
    }

    private static FFmpegStreamIOContext Get(void* opaque) =>
        (FFmpegStreamIOContext)(GCHandle.FromIntPtr((nint)opaque).Target ??
            throw new ObjectDisposedException(nameof(FFmpegStreamIOContext)));

    private static int ReadPacket(void* opaque, byte* buffer, int bufferSize)
    {
        FFmpegStreamIOContext io;
        try { io = Get(opaque); }
        catch { return -5; }
        return io.Read(buffer, bufferSize);
    }

    private int Read(byte* buffer, int bufferSize)
    {
        try
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                long remaining = Length - _source.Position;
                if (remaining <= 0) return ffmpeg.AVERROR_EOF;
                int read = _source.Read(new Span<byte>(buffer, (int)Math.Min(bufferSize, remaining)));
                if (read == 0) throw new EndOfStreamException($"The video stream ended before its declared length of {Length} bytes.");
                return read;
            }
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _error, ex, null);
            return -5;
        }
    }

    private static long Seek(void* opaque, long offset, int whence)
    {
        FFmpegStreamIOContext io;
        try { io = Get(opaque); }
        catch { return -5; }
        return io.Seek(offset, whence);
    }

    private long Seek(long offset, int whence)
    {
        try
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                whence &= ~ffmpeg.AVSEEK_FORCE;
                if (whence == ffmpeg.AVSEEK_SIZE) return Length;

                long target = whence switch
                {
                    0 => offset,
                    1 => checked(_source.Position + offset),
                    2 => checked(Length + offset),
                    _ => throw new ArgumentOutOfRangeException(nameof(whence))
                };
                if (target < 0 || target > Length) throw new IOException($"FFmpeg tried to seek outside the video stream: {target}/{Length}.");
                if (_source.Seek(target, SeekOrigin.Begin) != target) throw new IOException($"The video stream failed to seek to {target}.");
                return target;
            }
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _error, ex, null);
            return -5;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_context != null)
        {
            if (_context->buffer != null)
            {
                ffmpeg.av_free(_context->buffer);
                _context->buffer = null;
            }
            AVIOContext* context = _context;
            _context = null;
            ffmpeg.avio_context_free(&context);
        }
        if (_handle.IsAllocated) _handle.Free();
        if (!_leaveOpen) _source.Dispose();
    }
}
