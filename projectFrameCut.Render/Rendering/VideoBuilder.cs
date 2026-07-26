using FFmpeg.AutoGen;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using projectFrameCut.Drawing.Base.ReadWriteConvert;

namespace projectFrameCut.Render.Rendering
{
    public class VideoBuilder : IDisposable
    {
        string outputPath;
        IVideoWriter writer;
        uint index;
        bool running = true, stopped = false, buildStarted = false;
        ConcurrentDictionary<uint, IPicture> Cache = new();
        VfdPictureEncoder _cacheEncoder = new();

        // ============ Disk cache routing fields ============
        private bool _enableDiskCacheRouting;
        private double _diskCacheThreshold = 0.7;
        private ConcurrentDictionary<uint, string> _diskCache = new(); // frame index -> temp file path
        private ConcurrentDictionary<uint, byte> _diskCacheBpp = new(); // frame index -> bit depth indicator (8, 16, or 32 for HDR)
        private string? _diskCacheDir;
        private int _framesOnDisk;
        private static long _diskCacheRoutedCount = 0, _diskCacheRestoredCount = 0;

        private int _totalFramesCount = 0;
        private int _writtenFramesCount = 0;

        public int TotalFramesCount => _totalFramesCount;
        public int WrittenFramesCount => _writtenFramesCount;

        /// <summary>
        /// Number of frames that have been appended (via <see cref="Append"/> or <see cref="TryPreAppend"/>)
        /// but not yet written to the output file by the <see cref="Build"/> thread.
        /// </summary>
        public int PendingWriteCount => Cache.Count;

        /// <summary>
        /// When it's true, adding a frame with an existing index will throw an exception, 
        /// or when <see cref="BlockWrite"/> is enabled, writing a frame with an existing index will throw an exception.
        /// </summary>
        public bool StrictMode { get; set; } = true;

        /// <summary>
        /// Ignore all frame range check and allow writing frames with duplicated indexes. 
        /// </summary>
        /// <remarks>
        /// Affected when <see cref="StrictMode"/> is false.
        /// </remarks>
        public bool AllowDuplicatedFrameWrite { get; set; } = false;
        /// <summary>
        /// Call GC to collect unreferenced objects after each frame is written.
        /// </summary>
        public bool DoGCAfterEachWrite { get; set; } = false;
        /// <summary>
        /// Dispose the source <see cref="IPicture"/> when it's written to video.
        /// </summary>
        public bool DisposeFrameAfterEachWrite { get; set; } = true;

        /// <summary>
        /// Don't write frames to cache, write directly to file when appended.
        /// </summary>
        public bool BlockWrite { get; set; } = false;
        /// <summary>
        /// When enabled, frames are routed to a temporary disk cache when the in-memory buffer
        /// usage reaches <see cref="DiskCacheThreshold"/> (as a fraction of <see cref="Duration"/>).
        /// The writer thread loads them back from disk in order and writes them out.
        /// </summary>
        public bool EnableDiskCacheRouting
        {
            get => _enableDiskCacheRouting;
            set
            {
                _enableDiskCacheRouting = value;
                if (value) EnsureDiskCacheDir();
            }
        }
        /// <summary>
        /// Fraction of <see cref="Duration"/> at which the disk-cache routing kicks in.
        /// E.g. 0.7 means "when 70% of the frames are buffered in memory, spill new frames to disk".
        /// Only used when <see cref="EnableDiskCacheRouting"/> is true.
        /// </summary>
        public double DiskCacheThreshold
        {
            get => _diskCacheThreshold;
            set => _diskCacheThreshold = Math.Clamp(value, 0.1, 0.95);
        }
        /// <summary>
        /// Number of frames currently stored in the disk cache (spillover from memory).
        /// </summary>
        public int FramesOnDisk => _framesOnDisk;
        /// <summary>
        /// Maximum number of frames allowed in the disk cache (0 = unlimited).
        /// When exceeded, <see cref="DiskCacheFull"/> returns true and the writer backpressure
        /// mechanism (via <see cref="PendingWriteCount"/>) naturally throttles the Renderer.
        /// Each frame file is roughly fixed-size, so this acts as a disk space cap.
        /// </summary>
        public int DiskCacheMaxFrameCount { get; set; }
        /// <summary>
        /// Returns <c>true</c> when <see cref="DiskCacheMaxFrameCount"/> is set (&gt; 0)
        /// and the number of frames on disk has reached the limit.
        /// </summary>
        public bool DiskCacheFull => DiskCacheMaxFrameCount > 0 && Volatile.Read(ref _framesOnDisk) >= DiskCacheMaxFrameCount;
        /// <summary>
        /// Maximum number of frames expected to be pending in the write buffer at once.
        /// When set (&gt; 0), <see cref="DiskCacheThreshold"/> is interpreted as a fraction
        /// of this value. E.g. with MaxPendingFrames=120 and Threshold=0.7, disk routing
        /// triggers when ~84 frames are buffered.
        /// When 0 (default), falls back to <see cref="Duration"/> as the comparison base.
        /// Typically set to the same value as the Renderer's <c>MaxPendingWriteFrames</c>.
        /// </summary>
        public int DiskCacheMaxPendingFrames { get; set; }
        /// <summary>
        /// Root directory for the disk cache. If not set, defaults to
        /// <c>%TEMP%\projectFrameCutVBCache\{Guid}</c>.
        /// Must be set before <see cref="EnableDiskCacheRouting"/> is enabled (or set the property first),
        /// otherwise the default path is used. Safe to set at any time — the new path takes effect
        /// for the next batch of frames spilled to disk.
        /// </summary>
        /// <remarks>
        /// The builder creates a subdirectory with a unique name inside this root.
        /// That subdirectory is automatically cleaned up on <see cref="Dispose"/>.
        /// </remarks>
        public string? DiskCacheDirectory { get; set; }
        /// <summary>
        /// When true, the builder will prefer to write frames to disk cache, even if the memory buffer is not full.
        /// </summary>
        public bool ForceUseDiskCache { get; set; }
        /// <summary>
        /// Generate preview to the specified path when enabled.
        /// </summary>
        public bool EnablePreview { get; set; } = false;
        /// <summary>
        /// Path of the preview image.
        /// </summary>
        public string? PreviewPath { get; set; } = null;

        /// <summary>
        /// Control whether output which frame has written.
        /// </summary>
        public bool LogStat { get; set; }

        public event EventHandler<IPicture>? OnPreviewGenerated;
        /// <summary>
        /// the minimum number of frames between generating preview images.
        /// </summary>
        public int minFrameCountToGeneratePreview { get; set; } = 10;
        private uint countSinceLastPreview = 0, lastPreviewFrame = 0;

        /// <summary>
        /// The duration (in frames) of the video to be built.
        /// </summary>
        public uint Duration { get; set; }
        public int Width => writer.Width;
        public int Height => writer.Height;
        public IVideoWriter Writer => writer;

        /// <summary>
        /// Indicates whether the frame has been pended to write.
        /// </summary>
        /// <remarks>
        /// For each Key-Value pair, the key is the frame index.
        /// When value is True means the frame has been written to the video file, 
        /// and when value is False means the frame is still in the cache waiting to be written.
        /// If the key is not present, it means the frame has not been added yet.
        /// </remarks>
        public ConcurrentDictionary<uint, bool> FramePendedToWrite { get; private set; } = new();

        public VideoBuilder(IVideoWriter writer)
        {
            this.writer = writer;
            writer.Initialize();
            _cacheEncoder = new VfdPictureEncoder(false);
        }

        public VideoBuilder(string path, int width, int height, int framerate, string encoder, string fmt, string? writerType = null)
        {
            outputPath = path;
            index = 0;
            writer = string.IsNullOrWhiteSpace(writerType)
                ? PluginManager.CreateVideoWriter(encoder)
                : PluginManager.CreateVideoWriter(writerType);
            writer.Width = width;
            writer.Height = height;
            writer.FramePerSecond = framerate;
            writer.PixelFormat = fmt;
            writer.OutputPath = outputPath;

            if (!string.IsNullOrWhiteSpace(writerType))
            {
                writer.CodecName = encoder;
            }
            else if (string.IsNullOrWhiteSpace(writer.CodecName))
            {
                writer.CodecName = encoder;
            }
            writer.Initialize();


        }

        public bool Disposed { get; private set; }

        public void Append(uint index, IPicture frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (frame.Width != Width || frame.Height != Height)
                throw new ArgumentException($"The result ({frame.Tag ?? "untagged"})'s size {frame.Width}*{frame.Height} is different from original size ({Width}*{Height}). Please check the source.")
                {
                    Data = { { "PictureObject", frame }, { "ProcessStack", PictureProcessStack.FormatProcessStackForLog(frame.ProcessStack) } }
                };
            if (AllowDuplicatedFrameWrite) goto write;
            if (index > Duration)
            {
                Log($"[VideoBuilder] WARN: Frame #{index} is out of duration {Duration}, ignored.", "warn");
                if (DisposeFrameAfterEachWrite) frame.Dispose();
                return;
            }
            if (!FramePendedToWrite.TryAdd(index, false))
            {
                if (StrictMode)
                {
                    throw new InvalidOperationException($"Frame #{index} has already been added.")
                    {
                        Data = { { "PictureObject", frame }, { "ProcessStack", PictureProcessStack.FormatProcessStackForLog(frame.ProcessStack) } }
                    };
                }
                else
                {
                    Log($"[VideoBuilder] WARN: Frame #{index} has already been added, ignored.", "warn");
                    if (DisposeFrameAfterEachWrite) frame.Dispose();
                    return;
                }
            }

            Interlocked.Increment(ref _totalFramesCount);
            frame.Tag = string.IsNullOrWhiteSpace(frame.Tag) ? $"frame #{index}" : $"{frame.Tag} | frame #{index}";
        write:
            if (!IPicture.AllowPixelModeDowngrade && writer.TargetPPB is IPicture.PicturePixelMode m)
            {
                if (frame.BitPerPixel < m) throw new InvalidOperationException($"Frame #{index}'s PicturePixelMode {(int)frame.BitPerPixel} is smaller than target's PicturePixelMode {(int)m}, and IPicture.AllowPixelModeDowngrade is false.")
                {
                    Data = { { "PictureObject", frame }, { "ProcessStack", PictureProcessStack.FormatProcessStackForLog(frame.ProcessStack) } }
                };
            }
            if (!BlockWrite)
            {
                if (ForceUseDiskCache || ShouldRouteToDisk())
                {
                    SaveFrameToDisk(index, frame);
                }
                else
                {
                    Cache.AddOrUpdate(index, frame,
                        (_, _) => throw new InvalidOperationException($"Frame #{index} has already been added.")
                        {
                            Data = { { "PictureObject", frame }, { "ProcessStack", PictureProcessStack.FormatProcessStackForLog(frame.ProcessStack) } }
                        }
                        );
                }
            }
            else
            {
                writer.Append(frame);
                if (LogStat) Log($"[VideoBuilder] Frame #{index} added.");
            }

            if (EnablePreview && ++countSinceLastPreview >= minFrameCountToGeneratePreview && lastPreviewFrame < index)
            {
                OnPreviewGenerated?.Invoke(this, frame.Clone());
                countSinceLastPreview = 0;
                lastPreviewFrame = index;
            }

        }

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            try
            {
                running = false;
                foreach (var kv in Cache)
                {
                    try { kv.Value?.Dispose(); } catch { }
                }
                Cache.Clear();
                FramePendedToWrite.Clear();
                CleanupDiskCache();
            }
            catch { }
            writer.Dispose();
            GC.SuppressFinalize(this);
        }

        public Thread Build(int[]? threadAffinityMask = null)
        {
            if (BlockWrite)
            {
                Log($"[VideoWriter] Working in sync-writing mode.");
                return new(() => { });
            }
            return new Thread(() =>
            {
                if (threadAffinityMask.ArrayAny())
                {
                    ThreadAffinityHelper.SetCurrentThreadAffinity(threadAffinityMask);
                }
                Volatile.Write(ref buildStarted, true);
                Log($"[VideoBuilder] Successfully started writer for {outputPath}");

                try
                {
                    while (running)
                    {
                        if (Cache.TryRemove(index, out var frame))
                        {
                            WriteFrame(index, frame, LogStat ? $"[VideoBuilder] Frame #{index} wrote." : null);
                        }
                        else if (_enableDiskCacheRouting && TryLoadDiskFrame(index, out var diskFrame))
                        {
                            WriteFrame(index, diskFrame, LogStat ? $"[VideoBuilder] Frame #{index} wrote (from disk cache)." : null);
                        }
                        else
                        {
                            Thread.Sleep(1);
                        }
                    }
                }
                finally
                {
                    Thread.Sleep(50);
                    stopped = true;
                }
            })
            {
                Name = $"VideoWriter for {outputPath}",
                Priority = ThreadPriority.AboveNormal
            };


        }

        public void Finish(Func<uint, IPicture> regenerator, uint totalFrames = 0, Action<uint, float>? onWritingProgressUpdate = null)
        {
            Log($"[VideoBuilder] Finishing writing job, {Cache.Count} frames are still in cache{(_enableDiskCacheRouting && _framesOnDisk > 0 ? $", {_framesOnDisk} on disk." : ".")}");
            running = false;
            WaitForBuildThreadToStop();

            for (uint idx = index; idx < totalFrames; idx++)
            {
                if (Cache.TryRemove(idx, out var f))
                {
                    writer.Append(f);
                    if (LogStat) Log($"[VideoBuilder] Frame #{idx} added.");
                }
                else if (_enableDiskCacheRouting && TryLoadDiskFrame(idx, out var fDisk))
                {
                    writer.Append(fDisk);
                    FramePendedToWrite[idx] = true;
                    Interlocked.Increment(ref _writtenFramesCount);
                    if (DisposeFrameAfterEachWrite) fDisk.Dispose();
                    if (LogStat) Log($"[VideoBuilder] Frame #{idx} added (from disk cache).");
                }
                else
                {
                    writer.Append(regenerator(idx));
                    Log($"[VideoBuilder] Frame #{idx} regenerated because of missing frame.");
                }

                if (onWritingProgressUpdate is not null) onWritingProgressUpdate(idx, (float)idx / totalFrames);
            }

            Dispose();
        }

        public void Interrupt()
        {
            Log("[VideoBuilder] Interrupt signal received. Stopping the video writer...");
            running = false;

            try
            {
                // Write frames in sequential order, checking both memory and disk
                while (true)
                {
                    if (Cache.TryRemove(index, out var frame))
                    {
                        try
                        {
                            WriteFrame(index, frame);
                            Log($"[VideoBuilder] Frame #{index} wrote during interrupt drain.");
                        }
                        catch { Log($"A error occurred while writing frame #{index} during interrupt drain. Skipping..."); }
                    }
                    else if (_enableDiskCacheRouting && TryLoadDiskFrame(index, out var diskFrame))
                    {
                        try
                        {
                            WriteFrame(index, diskFrame);
                            Log($"[VideoBuilder] Frame #{index} wrote from disk cache during interrupt drain.");
                        }
                        catch { Log($"A error occurred while writing frame #{index} during interrupt drain. Skipping..."); }
                    }
                    else
                    {
                        break;
                    }
                }

                var remainingFrameIndexes = Cache.Keys.OrderBy(frameIndex => frameIndex).ToArray();
                if (remainingFrameIndexes.Length > 0)
                {
                    Log($"[VideoBuilder] WARN: Non-contiguous frames remain in cache during interrupt: {FormatFrameRanges(remainingFrameIndexes)}. Writing them in ascending order before closing.", "warn");

                    foreach (var frameIndex in remainingFrameIndexes)
                    {
                        if (Cache.TryRemove(frameIndex, out var remainingFrame))
                        {
                            try
                            {
                                WriteFrame(frameIndex, remainingFrame);
                                Log($"[VideoBuilder] Non-contiguous frame #{frameIndex} wrote during interrupt drain.");
                            }
                            catch { Log($"A error occurred while writing frame #{index} during interrupt drain. Skipping..."); }

                        }
                    }
                }

                // Drain any remaining disk-cached frames that weren't covered by the sequential loop
                if (_enableDiskCacheRouting && !_diskCache.IsEmpty)
                {
                    Log($"[VideoBuilder] WARN: {_framesOnDisk} frames remain in disk cache during interrupt. Writing them before closing.", "warn");
                    DrainDiskCache();
                }
            }
            catch { }

            Dispose();
        }

        private void WaitForBuildThreadToStop()
        {
            if (BlockWrite || !Volatile.Read(ref buildStarted))
                return;

            while (!Volatile.Read(ref stopped))
                Thread.Sleep(50);
        }

        private void EnsureDiskCacheDir()
        {
            if (_diskCacheDir is not null) return;
            var baseDir = DiskCacheDirectory ?? Path.Combine(Path.GetTempPath(), "pjfc_VideoFrameCache");
            _diskCacheDir = Path.Combine(baseDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_diskCacheDir);
            Log($"[VideoBuilder] Disk cache directory created: {_diskCacheDir}");
        }

        private string GetDiskCachePath(uint frameIndex) =>
            Path.Combine(_diskCacheDir!, $"{frameIndex}.vfc");

        private bool ShouldRouteToDisk()
        {
            if (!_enableDiskCacheRouting) return false;
            if (DiskCacheFull) return false; // disk limit reached -> fall back to memory (fills -> Renderer pauses via MaxPendingWriteFrames)
            if (DiskCacheMaxPendingFrames > 0)
                return Cache.Count >= (int)(DiskCacheMaxPendingFrames * _diskCacheThreshold);
            if (Duration > 0)
                return (double)Cache.Count / Duration >= _diskCacheThreshold;
            return Cache.Count >= 60;
        }

        /// <summary>
        /// Save a frame to the disk cache. The frame is consumed (disposed if <see cref="DisposeFrameAfterEachWrite"/> is set).
        /// </summary>
        private void SaveFrameToDisk(uint index, IPicture frame)
        {
            EnsureDiskCacheDir();
            var path = GetDiskCachePath(index);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);

            if (frame is HDRPicture16bpp hdr)
            {
                hdr.Save(fs, _cacheEncoder);
                _diskCacheBpp[index] = 32;
            }
            else if (frame.BitPerPixel == 16)
            {
                ((IPicture<ushort>)frame).Save(fs, _cacheEncoder);
                _diskCacheBpp[index] = 16;
            }
            else
            {
                ((IPicture<byte>)frame).Save(fs, _cacheEncoder);
                _diskCacheBpp[index] = 8;
            }

            _diskCache[index] = path;
            Interlocked.Increment(ref _framesOnDisk);
            Interlocked.Increment(ref _diskCacheRoutedCount);
            LogDiagnostic($"[VideoBuilder] Frame #{index} routed to disk cache ({_framesOnDisk} on disk).");

            if (DisposeFrameAfterEachWrite) frame.Dispose();
        }

        /// <summary>
        /// Load a previously disk-cached frame back into memory, remove it from the disk cache, and write it out.
        /// Returns false if the frame is not on disk.
        /// </summary>
        private bool TryLoadDiskFrame(uint frameIndex, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out IPicture frame)
        {
            frame = null;
            if (!_diskCache.TryRemove(frameIndex, out var path)) return false;

            if (!File.Exists(path))
            {
                _diskCacheBpp.TryRemove(frameIndex, out _);
                Interlocked.Decrement(ref _framesOnDisk);
                return false;
            }

            try
            {
                // Strategy: read all bytes, delete the file, THEN decode from memory.
                // This guarantees no file handle can block deletion.
                var depth = _diskCacheBpp.GetValueOrDefault(frameIndex, (byte)8);
                byte[] rawData = File.ReadAllBytes(path);
                try { File.Delete(path); } catch { }

                using var ms = new MemoryStream(rawData);
                if (depth == 32)
                {
                    if (!Drawing.Base.PictureExtensions.SharedVfdPictureDecoder.TryLoad(ms, out HDRPicture16bpp? hdr)) { goto fail; }
                    frame = hdr;
                }
                else if (depth == 16)
                {
                    if (!Drawing.Base.PictureExtensions.SharedVfdPictureDecoder.TryLoad(ms, out Picture16bpp? p16)) { goto fail; }
                    frame = p16;
                }
                else
                {
                    if (!Drawing.Base.PictureExtensions.SharedVfdPictureDecoder.TryLoad(ms, out Picture8bpp? p8)) { goto fail; }
                    frame = p8;
                }

                // Decode succeeded — clean up tracking
                _diskCacheBpp.TryRemove(frameIndex, out _);
                Interlocked.Decrement(ref _framesOnDisk);
                Interlocked.Increment(ref _diskCacheRestoredCount);
                LogDiagnostic($"[VideoBuilder] Frame #{frameIndex} restored from disk cache.");
                return true;

            fail:
                _diskCacheBpp.TryRemove(frameIndex, out _);
                Interlocked.Decrement(ref _framesOnDisk);
                return false;
            }
            catch
            {
                _diskCacheBpp.TryRemove(frameIndex, out _);
                Interlocked.Decrement(ref _framesOnDisk);
                // File should already be gone from the delete above, but try once more
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Drain all remaining disk-cached frames (in sequential order) during Finish/Interrupt.
        /// </summary>
        private void DrainDiskCache()
        {
            if (!_enableDiskCacheRouting || _diskCache.IsEmpty) return;

            var keys = _diskCache.Keys.OrderBy(k => k).ToArray();
            foreach (var idx in keys)
            {
                if (TryLoadDiskFrame(idx, out var frame))
                {
                    try
                    {
                        writer.Append(frame);
                        FramePendedToWrite[idx] = true;
                        Interlocked.Increment(ref _writtenFramesCount);
                        if (idx >= index) index = idx + 1;
                        if (DisposeFrameAfterEachWrite) frame.Dispose();
                        Log($"[VideoBuilder] Frame #{idx} wrote from disk cache (drain).");
                    }
                    catch
                    {
                        Log($"[VideoBuilder] Error writing frame #{idx} from disk cache during drain. Skipping...");
                        try { frame?.Dispose(); } catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Clean up any remaining disk cache files (e.g. after a failed/interrupted build).
        /// </summary>
        private void CleanupDiskCache()
        {
            if (_diskCacheDir is not null && Directory.Exists(_diskCacheDir))
            {
                try
                {
                    Directory.Delete(_diskCacheDir, true);
                    LogDiagnostic($"[VideoBuilder] Disk cache directory cleaned up: {_diskCacheDir}");
                }
                catch { }
            }
            _diskCache.Clear();
            _diskCacheBpp.Clear();
            _framesOnDisk = 0;
            _diskCache = new();
            _diskCacheBpp = new();
        }

        private void WriteFrame(uint frameIndex, IPicture frame, string? logMessage = null)
        {
            writer.Append(frame);
            FramePendedToWrite[frameIndex] = true;
            Interlocked.Increment(ref _writtenFramesCount);
            if (frameIndex >= index)
                index = frameIndex + 1;

            if (DisposeFrameAfterEachWrite) frame.Dispose();
            if (DoGCAfterEachWrite) GC.Collect();
            if (!string.IsNullOrEmpty(logMessage)) Log(logMessage);
        }

        private static string FormatFrameRanges(uint[] frameIndexes)
        {
            if (frameIndexes.Length == 0)
                return "none";

            var ranges = new List<string>();
            uint rangeStart = frameIndexes[0];
            uint previous = frameIndexes[0];

            for (int i = 1; i < frameIndexes.Length; i++)
            {
                uint current = frameIndexes[i];
                if (current == previous + 1)
                {
                    previous = current;
                    continue;
                }

                ranges.Add(rangeStart == previous ? $"#{rangeStart}" : $"#{rangeStart}-#{previous}");
                rangeStart = current;
                previous = current;
            }

            ranges.Add(rangeStart == previous ? $"#{rangeStart}" : $"#{rangeStart}-#{previous}");
            return string.Join(", ", ranges);
        }

        public bool TryGetCachedFrame(uint index, out IPicture frame)
        {
            return Cache.TryGetValue(index, out frame);
        }

        /// <summary>
        /// 静默添加帧到缓存。如果该帧已存在则跳过，不抛异常。
        /// 用于预缓存场景（如 <see cref="Renderer.RenderSpecificFrame"/> 提前渲染后写入）。
        /// <see cref="StrictMode"/> 和 <see cref="AllowDuplicatedFrameWrite"/> 不影响此方法的行为。
        /// </summary>
        /// <returns>成功添加返回 true，帧已存在返回 false。</returns>
        public bool TryPreAppend(uint index, IPicture frame)
        {
            ArgumentNullException.ThrowIfNull(frame);

            if (FramePendedToWrite.ContainsKey(index))
                return false;

            if (!FramePendedToWrite.TryAdd(index, false))
                return false;

            Interlocked.Increment(ref _totalFramesCount);
            frame.Tag = string.IsNullOrWhiteSpace(frame.Tag) ? $"frame #{index}" : $"{frame.Tag} | frame #{index}";

            if (BlockWrite)
            {
                writer.Append(frame);
                if (LogStat) Log($"[VideoBuilder] Frame #{index} added (pre-cache).");
            }
            else if (ShouldRouteToDisk())
            {
                SaveFrameToDisk(index, frame);
            }
            else
            {
                if (!Cache.TryAdd(index, frame))
                {
                    // Race: someone added between our checks
                    FramePendedToWrite.TryRemove(index, out _);
                    Interlocked.Decrement(ref _totalFramesCount);
                    try { frame.Dispose(); } catch { }
                    return false;
                }
            }

            // Preview handling
            if (EnablePreview && ++countSinceLastPreview >= minFrameCountToGeneratePreview)
            {
                OnPreviewGenerated?.Invoke(this, frame.Clone());
                countSinceLastPreview = 0;
            }

            return true;
        }


    }



}