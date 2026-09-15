using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System.IO;
using System.Security.Cryptography;
using static projectFrameCut.Shared.Logger;

namespace projectFrameCut.Render.EncodeAndDecode;

/// <summary>
/// Reads a projectFrameCut encrypted video without creating a decrypted temporary file.
/// </summary>
public sealed class EncryptedVideoDecoderContext : IVideoSource<byte>
{
    private readonly string _sourceName;
    private readonly Func<string, byte[]> _keyProvider;
    private readonly DecoderContext8Bit _decoder;

    public EncryptedVideoDecoderContext(string path, Func<string, byte[]> keyProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(keyProvider);

        _sourceName = Path.GetFullPath(path);
        _keyProvider = keyProvider;
        Log($"[EncryptedVideoDecoder] Opening encrypted video '{_sourceName}'.");
        _decoder = OpenFile(_sourceName, _keyProvider);
        Log($"[EncryptedVideoDecoder] Opened encrypted video '{_sourceName}' as {_decoder.Width}x{_decoder.Height} at {_decoder.Fps:0.###} FPS.");
    }

    public EncryptedVideoDecoderContext(Stream source, long length, Func<string, byte[]> keyProvider,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keyProvider);

        _sourceName = "<encrypted stream>";
        _keyProvider = keyProvider;
        Log($"[EncryptedVideoDecoder] Opening encrypted stream ({length} bytes).");
        _decoder = OpenStream(source, length, _sourceName, _keyProvider, leaveOpen);
        Log($"[EncryptedVideoDecoder] Opened encrypted stream as {_decoder.Width}x{_decoder.Height} at {_decoder.Fps:0.###} FPS.");
    }

    public string TypeName => nameof(EncryptedVideoDecoderContext);
    public string[] PreferredExtension => [];
    public int? ResultBitPerPixel => _decoder.ResultBitPerPixel;
    public uint Index
    {
        get => _decoder.Index;
        set => _decoder.Index = value;
    }
    public long TotalFrames => _decoder.TotalFrames;
    public double Fps => _decoder.Fps;
    public int Width => _decoder.Width;
    public int Height => _decoder.Height;
    public bool Disposed => _decoder.Disposed;
    public bool EnableLock
    {
        get => _decoder.EnableLock;
        set => _decoder.EnableLock = value;
    }
    public bool StrictMode
    {
        get => _decoder.StrictMode;
        set => _decoder.StrictMode = value;
    }

    public void Initialize() => _decoder.Initialize();

    public IVideoSource CreateNew(string newSource) => new EncryptedVideoDecoderContext(newSource, _keyProvider);

    public IVideoSource FromStream(Stream source, long length, bool leaveOpen = false) =>
        new EncryptedVideoDecoderContext(source, length, _keyProvider, leaveOpen);

    public IPicture<byte> GetFrame(uint targetFrame) => _decoder.GetFrame(targetFrame);

    public IPicture<byte> GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth,
        int sourceHeight, int targetWidth, int targetHeight) =>
        _decoder.GetFrame(targetFrame, sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight);

    public IPicture<byte> GetFrame(uint targetFrame, int targetWidth, int targetHeight) =>
        _decoder.GetFrame(targetFrame, targetWidth, targetHeight);

    public void Dispose() => _decoder.Dispose();

    private static DecoderContext8Bit OpenFile(string path, Func<string, byte[]> keyProvider)
    {
        var source = File.OpenRead(path);
        try
        {
            return OpenStream(source, source.Length, path, keyProvider, leaveOpen: false);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static DecoderContext8Bit OpenStream(Stream source, long length, string sourceName,
        Func<string, byte[]> keyProvider, bool leaveOpen)
    {
        EncryptedReadStream? decrypted = null;
        try
        {
            if (length <= 0)
                throw new ArgumentOutOfRangeException(nameof(length), "The encrypted video stream length must be positive.");
            if (!source.CanSeek || source.Length != length)
                throw new ArgumentException("The supplied length must equal the complete encrypted video stream length.", nameof(length));

            byte[] key = keyProvider(sourceName) ??
                throw new CryptographicException("The encrypted video key provider returned null.");
            decrypted = new EncryptedReadStream(source, key, leaveOpen);
            return new DecoderContext8Bit(decrypted, decrypted.Length);
        }
        catch
        {
            decrypted?.Dispose();
            if (decrypted is null && !leaveOpen)
                source.Dispose();
            throw;
        }
    }
}
