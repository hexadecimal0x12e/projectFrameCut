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

        private ulong requireCount = 0, cacheHitCount = 0;

        public VideoFrameDiskCache(string videoPath)
        {
            _sourceVideoPath = videoPath;
            VideoFrameDiskCacheManager.CacheMetadata info = null!;
            try
            {
                info = VideoFrameDiskCacheManager.TouchCache(videoPath);
                _cacheDir = Path.Combine(CacheBaseDir ?? Path.Combine(Path.GetTempPath(), "projectFrameCutVideoCache"), info.MD5);
            }
            catch (Exception ex)
            {
                Log(ex, $"Create VideoFrameDiskCache for {videoPath}", this);
                _disposed = true;
                return;
            }

            long length = -1;
            try
            {
                Directory.CreateDirectory(_cacheDir);
                length = Directory.GetFiles(_cacheDir).Length;
            }
            catch { }
            LogDiagnostic($"VideoFrameDiskCache initialized for '{videoPath}'({info?.MD5 ?? "?"}), already {length} entries files");

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
            requireCount++;
            string path = GetPath(frameNumber);
            if (!File.Exists(path)) return false;

            try
            {
                cacheHitCount++;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                if (!fs.TryLoadVfd(out picture))
                {
                    picture = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log(ex, $"DiskCache: failed to read 8bpp frame {frameNumber}", this);
                //CleanupFile(frameNumber);
                return false;
            }
        }

        public bool TryLoad16bpp(uint frameNumber, [MaybeNullWhen(false)] out Picture16bpp picture)
        {
            picture = null;
            if (_disposed) return false;
            if (_pendingWrites.ContainsKey(frameNumber)) return false;
            requireCount++;
            string path = GetPath(frameNumber);
            if (!File.Exists(path)) return false;

            try
            {
                cacheHitCount++;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                if (!fs.TryLoadVfd(out picture))
                {
                    picture = null;
                    return false;
                }
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
            requireCount++;
            string path = GetPath(frameNumber);
            if (!File.Exists(path)) return false;

            try
            {
                cacheHitCount++;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                if (!fs.TryLoadVfd(out picture))
                {
                    picture = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log(ex, $"DiskCache: failed to read HDR frame {frameNumber}", this);
                //CleanupFile(frameNumber);
                return false;
            }
        }

        private unsafe void Save8bppSync(uint frameNumber, IPicture<byte> picture)
        {
            if (_disposed) return;
            string path = GetPath(frameNumber);
            if (File.Exists(path)) return;

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
            picture.SaveAsVfd(fs, EnableCompression);

            EnforceCacheSizeLimit();
        }

        private unsafe void Save16bppSync(uint frameNumber, IPicture<ushort> picture)
        {
            if (_disposed) return;
            string path = GetPath(frameNumber);
            if (File.Exists(path)) return;

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
            picture.SaveAsVfd(fs, EnableCompression);

            EnforceCacheSizeLimit();
        }

        private unsafe void SaveHDRSync(uint frameNumber, HDRPicture16bpp picture)
        {
            if (_disposed) return;
            string path = GetPath(frameNumber);
            if (File.Exists(path)) return;

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
            picture.SaveAsVfd(fs, EnableCompression);

            EnforceCacheSizeLimit();
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
            Log($"VideoFrameDiskCache for '{_sourceVideoPath}' disposed. Cache hit rate: {(requireCount > 0 ? ((double)cacheHitCount / requireCount * 100).ToString("F2") : "N/A")}% ({cacheHitCount}/{requireCount})");
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