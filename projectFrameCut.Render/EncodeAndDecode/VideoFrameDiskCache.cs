using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Sources;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.SymbolStore;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static projectFrameCut.Render.EncodeAndDecode.FFmpegHelper;

namespace projectFrameCut.Render.EncodeAndDecode
{
    public sealed class VideoFrameDiskCache : IDisposable
    {
        public static string? CacheBaseDir = null;
        /// <summary>
        /// When true (default), cached frames are compressed with GZip.
        /// Set to false to skip compression for faster I/O at the cost of more disk space.
        /// </summary>
        public static bool EnableCompression { get; set; } = true;
        public static long MaximumCacheSizeBytes { get; set; } = 0L * 1024 * 1024 * 1024; // 0 GB (unlimited)
        public static CompressionLevel DefaultCompressionLevel { get; set; } = CompressionLevel.SmallestSize;
        private string _cacheDir;
        private readonly string _sourceVideoPath;
        private bool _disposed;
        private readonly ConcurrentDictionary<uint, bool> _pendingWrites = new();

        private const int HeaderSize = 18; // Magic(4) + Type(1) + Width(4) + Height(4) + HasAlpha(1) + MaxBrightness(4)

        public VideoFrameDiskCache(string videoPath)
        {
            _sourceVideoPath = videoPath;
            string hash = VideoFrameDiskCacheManager.TouchCache(videoPath).MD5;
            _cacheDir = Path.Combine(CacheBaseDir ?? Path.Combine(Path.GetTempPath(), "projectFrameCutVideoCache"), hash);
            try { Directory.CreateDirectory(_cacheDir); } catch { }
        }

        private static string ComputeShortHash(string path)
        {
            try
            {
                byte[] hashBytes = MD5.HashData(File.ReadAllBytes(path));
                return Convert.ToHexString(hashBytes, 0, 16);
            }
            catch
            {
                var invalid = Path.GetInvalidFileNameChars();
                var sb = new StringBuilder();
                foreach (char c in path)
                    sb.Append(invalid.Contains(c) ? '_' : c);
                return sb.ToString();
            }
        }

        private string GetPath(uint frameNumber) =>
            Path.Combine(_cacheDir, $"{frameNumber}.vfc");

        public bool IsWritePending(uint frameNumber) => _pendingWrites.ContainsKey(frameNumber);

        // ============ Write methods (async, fire-and-forget) ============

        public void Save8bppFrameAsync(uint frameNumber, IPicture<byte> picture)
        {
            if (_disposed) return;
            if (!_pendingWrites.TryAdd(frameNumber, true)) return;

            Task.Run(() =>
            {
                try
                {
                    Save8bppSync(frameNumber, picture);
                }
                catch (Exception ex)
                {
                    Log(ex, $"DiskCache: failed to write 8bpp frame {frameNumber}", this);
                    //CleanupFile(frameNumber);
                }
                finally
                {
                    _pendingWrites.TryRemove(frameNumber, out _);
                }
            });
        }

        public void Save16bppFrameAsync(uint frameNumber, IPicture<ushort> picture)
        {
            if (_disposed) return;
            if (!_pendingWrites.TryAdd(frameNumber, true)) return;

            Task.Run(() =>
            {
                try
                {
                    Save16bppSync(frameNumber, picture);
                }
                catch (Exception ex)
                {
                    Log(ex, $"DiskCache: failed to write 16bpp frame {frameNumber}", this);
                    //CleanupFile(frameNumber);
                }
                finally
                {
                    _pendingWrites.TryRemove(frameNumber, out _);
                }
            });
        }

        public void SaveHDRFrameAsync(uint frameNumber, HDRPicture16bpp picture)
        {
            if (_disposed) return;
            if (!_pendingWrites.TryAdd(frameNumber, true)) return;

            Task.Run(() =>
            {
                try
                {
                    SaveHDRSync(frameNumber, picture);
                }
                catch (Exception ex)
                {
                    Log(ex, $"DiskCache: failed to write HDR frame {frameNumber}", this);
                    //CleanupFile(frameNumber);
                }
                finally
                {
                    _pendingWrites.TryRemove(frameNumber, out _);
                }
            });
        }

        // ============ Read methods (synchronous, called under decoder lock) ============

        public bool TryLoad8bpp(uint frameNumber, [MaybeNullWhen(false)] out Picture8bpp picture)
        {
            picture = null;
            if (_disposed) return false;
            if (_pendingWrites.ContainsKey(frameNumber)) return false;

            string path = GetPath(frameNumber);
            if (!File.Exists(path)) return false;

            try
            {
                byte[] rawData = ReadAndDecompress(path, out int width, out int height, out int frameType, out float _, out bool hasAlpha);
                if (frameType != 0 || rawData == null) return false;

                int pixelCount = width * height;
                int expectedSize = pixelCount * 3 + (hasAlpha ? pixelCount * 4 : 0);
                if (rawData.Length < expectedSize) return false;

                var pic = new Picture8bpp(width, height);
                Buffer.BlockCopy(rawData, 0, pic.r, 0, pixelCount);
                Buffer.BlockCopy(rawData, pixelCount, pic.g, 0, pixelCount);
                Buffer.BlockCopy(rawData, pixelCount * 2, pic.b, 0, pixelCount);
                if (hasAlpha)
                {
                    pic.a = new float[pixelCount];
                    Buffer.BlockCopy(rawData, pixelCount * 3, pic.a, 0, pixelCount * 4);
                    pic.hasAlphaChannel = true;
                }
                pic.ProcessStack = [new PictureProcessStack
                  {
                      OperationDisplayName = $"Loaded from disk cache, frame #{frameNumber}",
                      Operator = typeof(VideoFrameDiskCache),
                      ProcessingFuncStackTrace = new StackTrace(true),
                  }];
                picture = pic;
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        public bool TryLoad16bpp(uint frameNumber, [MaybeNullWhen(false)] out Picture16bpp picture)
        {
            picture = null;
            if (_disposed) return false;
            if (_pendingWrites.ContainsKey(frameNumber)) return false;

            string path = GetPath(frameNumber);
            if (!File.Exists(path)) return false;

            try
            {
                byte[] rawData = ReadAndDecompress(path, out int width, out int height, out int frameType, out float _, out bool hasAlpha);
                if (frameType != 1 || rawData == null) return false;

                int pixelCount = width * height;
                int ushortBytes = pixelCount * 2;
                int expectedSize = ushortBytes * 3 + (hasAlpha ? pixelCount * 4 : 0);
                if (rawData.Length < expectedSize) return false;

                var pic = new Picture16bpp(width, height);
                Buffer.BlockCopy(rawData, 0, pic.r, 0, ushortBytes);
                Buffer.BlockCopy(rawData, ushortBytes, pic.g, 0, ushortBytes);
                Buffer.BlockCopy(rawData, ushortBytes * 2, pic.b, 0, ushortBytes);
                if (hasAlpha)
                {
                    pic.a = new float[pixelCount];
                    Buffer.BlockCopy(rawData, ushortBytes * 3, pic.a, 0, pixelCount * 4);
                    pic.hasAlphaChannel = true;
                }
                pic.ProcessStack = [new PictureProcessStack
                  {
                      OperationDisplayName = $"Loaded from disk cache, frame #{frameNumber}",
                      Operator = typeof(VideoFrameDiskCache),
                      ProcessingFuncStackTrace = new StackTrace(true),
                  }];
                picture = pic;
                return true;
            }
            catch (Exception ex)
            {
                Log(ex, $"DiskCache: failed to read 16bpp frame {frameNumber}", this);
                //CleanupFile(frameNumber);
                return false;
            }
        }

        public bool TryLoadHDR(uint frameNumber, [MaybeNullWhen(false)] out HDRPicture16bpp picture)
        {
            picture = null;
            if (_disposed) return false;
            if (_pendingWrites.ContainsKey(frameNumber)) return false;

            string path = GetPath(frameNumber);
            if (!File.Exists(path)) return false;

            try
            {
                byte[] rawData = ReadAndDecompress(path, out int width, out int height, out int frameType, out float maxBrightness, out bool hasAlpha);
                if (frameType != 2 || rawData == null) return false;

                int pixelCount = width * height;
                int ushortBytes = pixelCount * 2;
                int expectedSize = ushortBytes * 3 + pixelCount * 4 + (hasAlpha ? pixelCount * 4 : 0);
                if (rawData.Length < expectedSize) return false;

                var pic = new HDRPicture16bpp(width, height)
                {
                    MaximumBrightness = maxBrightness > 0f && float.IsFinite(maxBrightness) ? maxBrightness : 1000f,
                };
                Buffer.BlockCopy(rawData, 0, pic.r, 0, ushortBytes);
                Buffer.BlockCopy(rawData, ushortBytes, pic.g, 0, ushortBytes);
                Buffer.BlockCopy(rawData, ushortBytes * 2, pic.b, 0, ushortBytes);
                Buffer.BlockCopy(rawData, ushortBytes * 3, pic.Brightness, 0, pixelCount * 4);

                int alphaOffset = ushortBytes * 3 + pixelCount * 4;
                if (hasAlpha)
                {
                    pic.a = new float[pixelCount];
                    Buffer.BlockCopy(rawData, alphaOffset, pic.a, 0, pixelCount * 4);
                    pic.hasAlphaChannel = true;
                }
                pic.ProcessStack = [new PictureProcessStack
                  {
                      OperationDisplayName = $"Loaded from HDR disk cache, frame #{frameNumber}",
                      Operator = typeof(VideoFrameDiskCache),
                      ProcessingFuncStackTrace = new StackTrace(true),
                  }];
                picture = pic;
                return true;
            }
            catch (Exception ex)
            {
                Log(ex, $"DiskCache: failed to read HDR frame {frameNumber}", this);
                //CleanupFile(frameNumber);
                return false;
            }
        }

        // ============ Internal serialization ============

        private static void WriteHeader(Span<byte> header, int frameType, int width, int height, bool hasAlpha, float maxBrightness)
        {
            header[0] = (byte)'V'; header[1] = (byte)'F';
            header[2] = (byte)'C'; header[3] = (byte)'D';
            header[4] = (byte)frameType;
            Unsafe.WriteUnaligned(ref header[5], width);
            Unsafe.WriteUnaligned(ref header[9], height);
            // Flags: bit0=HasAlpha, bit1=!Compressed (0=compressed for backward compat)
            byte flags = hasAlpha ? (byte)1 : (byte)0;
            if (!EnableCompression) flags |= 2;
            header[13] = flags;
            Unsafe.WriteUnaligned(ref header[14], maxBrightness);
        }

        private unsafe void Save8bppSync(uint frameNumber, IPicture<byte> picture)
        {
            string path = GetPath(frameNumber);
            if (File.Exists(path)) return;

            int pixelCount = picture.Width * picture.Height;
            bool hasAlpha = picture.hasAlphaChannel && picture.a != null;
            int dataSize = pixelCount * 3 + (hasAlpha ? pixelCount * 4 : 0);

            byte[] rawData = GC.AllocateUninitializedArray<byte>(dataSize, pinned: false);
            Buffer.BlockCopy(picture.r, 0, rawData, 0, pixelCount);
            Buffer.BlockCopy(picture.g, 0, rawData, pixelCount, pixelCount);
            Buffer.BlockCopy(picture.b, 0, rawData, pixelCount * 2, pixelCount);
            if (hasAlpha)
                Buffer.BlockCopy(picture.a!, 0, rawData, pixelCount * 3, pixelCount * 4);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[HeaderSize];
            WriteHeader(header, 0, picture.Width, picture.Height, hasAlpha, 0f);
            fs.Write(header);

            if (EnableCompression)
            {
                using var gz = new GZipStream(fs, DefaultCompressionLevel, leaveOpen: true);
                gz.Write(rawData, 0, rawData.Length);
            }
            else
            {
                fs.Write(rawData, 0, rawData.Length);
            }

            EnforceCacheSizeLimit();
        }

        private unsafe void Save16bppSync(uint frameNumber, IPicture<ushort> picture)
        {
            string path = GetPath(frameNumber);
            if (File.Exists(path)) return;

            int pixelCount = picture.Width * picture.Height;
            bool hasAlpha = picture.hasAlphaChannel && picture.a != null;
            int ushortBytes = pixelCount * 2;
            int dataSize = ushortBytes * 3 + (hasAlpha ? pixelCount * 4 : 0);

            byte[] rawData = GC.AllocateUninitializedArray<byte>(dataSize, pinned: false);
            Buffer.BlockCopy(picture.r, 0, rawData, 0, ushortBytes);
            Buffer.BlockCopy(picture.g, 0, rawData, ushortBytes, ushortBytes);
            Buffer.BlockCopy(picture.b, 0, rawData, ushortBytes * 2, ushortBytes);
            if (hasAlpha)
                Buffer.BlockCopy(picture.a!, 0, rawData, ushortBytes * 3, pixelCount * 4);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[HeaderSize];
            WriteHeader(header, 1, picture.Width, picture.Height, hasAlpha, 0f);
            fs.Write(header);

            if (EnableCompression)
            {
                using var gz = new GZipStream(fs, DefaultCompressionLevel, leaveOpen: true);
                gz.Write(rawData, 0, rawData.Length);
            }
            else
            {
                fs.Write(rawData, 0, rawData.Length);
            }

            EnforceCacheSizeLimit();
        }

        private unsafe void SaveHDRSync(uint frameNumber, HDRPicture16bpp picture)
        {
            string path = GetPath(frameNumber);
            if (File.Exists(path)) return;

            int pixelCount = picture.Width * picture.Height;
            bool hasAlpha = picture.hasAlphaChannel && picture.a != null;
            int ushortBytes = pixelCount * 2;
            int dataSize = ushortBytes * 3 + pixelCount * 4 + (hasAlpha ? pixelCount * 4 : 0);

            byte[] rawData = GC.AllocateUninitializedArray<byte>(dataSize, pinned: false);
            Buffer.BlockCopy(picture.r, 0, rawData, 0, ushortBytes);
            Buffer.BlockCopy(picture.g, 0, rawData, ushortBytes, ushortBytes);
            Buffer.BlockCopy(picture.b, 0, rawData, ushortBytes * 2, ushortBytes);
            Buffer.BlockCopy(picture.Brightness, 0, rawData, ushortBytes * 3, pixelCount * 4);
            if (hasAlpha)
                Buffer.BlockCopy(picture.a!, 0, rawData, ushortBytes * 3 + pixelCount * 4, pixelCount * 4);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[HeaderSize];
            WriteHeader(header, 2, picture.Width, picture.Height, hasAlpha, picture.MaximumBrightness);
            fs.Write(header);

            if (EnableCompression)
            {
                using var gz = new GZipStream(fs, DefaultCompressionLevel, leaveOpen: true);
                gz.Write(rawData, 0, rawData.Length);
            }
            else
            {
                fs.Write(rawData, 0, rawData.Length);
            }

            EnforceCacheSizeLimit();
        }

        private static byte[]? ReadAndDecompress(string path, out int width, out int height, out int frameType, out float maxBrightness, out bool hasAlpha)
        {
            width = 0; height = 0; frameType = -1; maxBrightness = 0f; hasAlpha = false;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);

            Span<byte> header = stackalloc byte[HeaderSize];
            if (fs.Read(header) < HeaderSize)
                return null;

            if (header[0] != 'V' || header[1] != 'F' || header[2] != 'C' || header[3] != 'D')
                return null;

            frameType = header[4];
            if (frameType < 0 || frameType > 2)
                return null;

            width = Unsafe.ReadUnaligned<int>(ref header[5]);
            height = Unsafe.ReadUnaligned<int>(ref header[9]);
            hasAlpha = (header[13] & 1) != 0;
            // Bit1=0 means compressed (backward compatible), Bit1=1 means uncompressed
            bool compressed = (header[13] & 2) == 0;
            maxBrightness = Unsafe.ReadUnaligned<float>(ref header[14]);

            if (width <= 0 || height <= 0)
                return null;

            using var ms = new MemoryStream(Math.Max(width * height * 6, 4096));
            if (compressed)
            {
                using var gz = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true);
                gz.CopyTo(ms);
            }
            else
            {
                fs.CopyTo(ms);
            }
            return ms.ToArray();
        }

        private void CleanupFile(uint frameNumber)
        {
            try { File.Delete(GetPath(frameNumber)); } catch { }
        }

        private void EnforceCacheSizeLimit()
        {
            if (MaximumCacheSizeBytes <= 0) return;

            try
            {
                var di = new DirectoryInfo(_cacheDir);
                if (!di.Exists) return;

                var files = di.GetFiles("*.vfc")
                    .OrderBy(f => f.LastAccessTime)
                    .ToList();

                long totalSize = files.Sum(f => f.Length);

                while (totalSize > MaximumCacheSizeBytes && files.Count > 0)
                {
                    var fileToDelete = files[0];
                    files.RemoveAt(0);

                    try
                    {
                        fileToDelete.Delete();
                        totalSize -= fileToDelete.Length;
                    }
                    catch { }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    public static class VideoFrameDiskCacheManager
    {
        private static ConcurrentDictionary<string, CacheMetadata> _caches = new();
        private static Lock _fileLock = new();
        static VideoFrameDiskCacheManager()
        {
            TryInitMetadata();
        }

        public static bool TryInitMetadata()
        {
            using (_fileLock.EnterScope())
            {
                string metadataPath = Path.Combine(VideoFrameDiskCache.CacheBaseDir ?? Path.Combine(Path.GetTempPath(), "projectFrameCutVideoCache"), "metadata.json");
                if (File.Exists(metadataPath))
                {
                    try
                    {
                        var metadataJson = File.ReadAllText(metadataPath);
                        var metadata = JsonSerializer.Deserialize<ConcurrentDictionary<string, CacheMetadata>>(metadataJson);
                        if (metadata != null)
                        {
                            _caches = new ConcurrentDictionary<string, CacheMetadata>(metadata);
                            return true;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            return false;
        }

        public static IReadOnlyCollection<CacheMetadata> Caches => (IReadOnlyCollection<CacheMetadata>)_caches.Values;

        public static string TryGetHash(string path)
        {
            if (_caches.FirstOrDefault(c => c.Value.Path == path && c.Value.Size == new FileInfo(path).Length).Value is CacheMetadata metadata)
                return metadata.MD5;
            return Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path)), 0, 16);
        }

        public static CacheMetadata TouchCache(string videoPath)
        {
            var hash = TryGetHash(videoPath);

            _caches[hash] = _caches.GetValueOrDefault(hash, new CacheMetadata { MD5 = hash, Path = videoPath, Size = new FileInfo(videoPath).Length, LastAccess = DateTime.Now, CreateAt = DateTime.Now }) with { LastAccess = DateTime.Now };

            using (_fileLock.EnterScope())
            {
                File.WriteAllText(Path.Combine(VideoFrameDiskCache.CacheBaseDir ?? Path.Combine(Path.GetTempPath(), "projectFrameCutVideoCache"), "metadata.json"), JsonSerializer.Serialize(_caches));
            }

            return _caches[hash];
        }

        public static void WriteMetadata(string videoPath)
        {
            var hash = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(videoPath)), 0, 16);
            _caches[hash] = new CacheMetadata
            {
                Path = videoPath,
                Size = new FileInfo(videoPath).Length,
                MD5 = hash,
                LastAccess = DateTime.Now,
                CreateAt = DateTime.Now
            };

            using (_fileLock.EnterScope())
            {
                File.WriteAllText(Path.Combine(VideoFrameDiskCache.CacheBaseDir ?? Path.Combine(Path.GetTempPath(), "projectFrameCutVideoCache"), "metadata.json"), JsonSerializer.Serialize(_caches));
            }
        }

        public static void RemoveFromCache(string hash)
        {
            try
            {
                var baseDir = VideoFrameDiskCache.CacheBaseDir ?? Path.Combine(Path.GetTempPath(), "projectFrameCutVideoCache");
                var dir = Path.Combine(baseDir, hash);
                try
                {
                    if (Directory.Exists(dir))
                        Directory.Delete(dir, true);
                }
                catch { }

                _caches.TryRemove(hash, out _);
                try
                {
                    using (_fileLock.EnterScope())
                    {
                        File.WriteAllText(Path.Combine(baseDir, "metadata.json"), JsonSerializer.Serialize(_caches));
                    }
                }
                catch { }
            }
            catch
            {
            }
        }

        public static void ManualBuildCache(string videoPath, IProgress<double> progress, CancellationToken token)
        {
            var cache = new VideoFrameDiskCache(videoPath);
            if (HDRDecoderContext.IsHdrVideo(videoPath))
            {
                using (HDRDecoderContext d = new HDRDecoderContext(videoPath))
                {
                    for (uint i = 0; i < d.TotalFrames; i++)
                    {
                        if (token.IsCancellationRequested) return;
                        d.GetFrame(i, false).Dispose();
                        progress.Report((double)i / d.TotalFrames);
                    }
                }
            }
            else
            {
                if (FFmpegHelper.DetectVideoBitDepth(videoPath) > 8)
                {
                    using (var s = new DecoderContext16Bit(videoPath))
                    {
                        for (uint i = 0; i < s.TotalFrames; i++)
                        {
                            if (token.IsCancellationRequested) return;
                            s.GetFrame(i, false).Dispose();
                            progress.Report((double)i / s.TotalFrames);
                        }
                    }
                }
                else
                {
                    using (var s = new DecoderContextHW(videoPath))
                    {
                        for (uint i = 0; i < s.TotalFrames; i++)
                        {
                            if (token.IsCancellationRequested) return;
                            s.GetFrame(i, false).Dispose();
                            progress.Report((double)i / s.TotalFrames);
                        }
                    }

                }

            }
        }

        public sealed record CacheMetadata
        {
            public string Path { get; set; } = "";
            public long Size { get; set; }
            public string MD5 { get; set; } = "";
            public DateTime LastAccess { get; set; }
            public DateTime CreateAt { get; set; }
        }
    }
}