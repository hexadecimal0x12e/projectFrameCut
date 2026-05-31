using FFmpeg.AutoGen;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.Rendering
{
    public class VideoBuilder : IDisposable
    {
        string outputPath;
        IVideoWriter writer;
        uint index;
        bool running = true, stopped = false, buildStarted = false;
        ConcurrentDictionary<uint, IPicture> Cache = new();

        private int _totalFramesCount = 0;
        private int _writtenFramesCount = 0;

        public int TotalFramesCount => _totalFramesCount;
        public int WrittenFramesCount => _writtenFramesCount;

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
        private uint countSinceLastPreview = 0;

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

            if (!IPicture.AllowPixelModeDowngrade && writer.TargetPPB is IPicture.PicturePixelMode m)
            {
                if (frame.BitPerPixel < m) throw new InvalidOperationException($"Frame #{index}'s PicturePixelMode {(int)frame.BitPerPixel} is smaller than target's PicturePixelMode {(int)m}, and IPicture.AllowPixelModeDowngrade is false.")
                {
                    Data = { { "PictureObject", frame }, { "ProcessStack", PictureProcessStack.FormatProcessStackForLog(frame.ProcessStack) } }
                };
            }
        write:
            if (!BlockWrite)
            {
                Cache.AddOrUpdate(index, frame,
                    (_, _) => throw new InvalidOperationException($"Frame #{index} has already been added.")
                    {
                        Data = { { "PictureObject", frame }, { "ProcessStack", PictureProcessStack.FormatProcessStackForLog(frame.ProcessStack) } }
                    }
                    );
            }
            else
            {
                writer.Append(frame);
                if (LogStat) Log($"[VideoBuilder] Frame #{index} added.");
            }

            if (EnablePreview && ++countSinceLastPreview >= minFrameCountToGeneratePreview)
            {
                OnPreviewGenerated?.Invoke(this, frame.DeepCopy());
                countSinceLastPreview = 0;
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
            }
            catch { }
            writer.Dispose();
            GC.SuppressFinalize(this);
        }

        public Thread Build()
        {
            if (BlockWrite)
            {
                Log($"[VideoWriter] Working in sync-writing mode.");
                return new(() => { }    );
            }
            return new Thread(() =>
            {
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

        public void Finish(Func<uint, IPicture> regenerator, uint totalFrames = 0)
        {
            running = false;
            WaitForBuildThreadToStop();

            var missingFrames = new List<uint>();
            uint currentIndex = index;

            while (Cache.Count > 0 || missingFrames.Count > 0)
            {
                if (Cache.ContainsKey(currentIndex))
                {
                    writer.Append(Cache.TryRemove(currentIndex, out var f) ? f : throw new KeyNotFoundException());
                    Log($"[VideoBuilder] Frame #{currentIndex} added.");
                    currentIndex++;
                    continue;
                }

                if (missingFrames.Count == 0 && !Cache.ContainsKey(currentIndex))
                {
                    uint maxCheck = currentIndex + 100;
                    for (uint i = currentIndex; i < maxCheck; i++)
                    {
                        if (!Cache.ContainsKey(i) && (Cache.Count == 0 || i <= Cache.Keys.Max()) && i <= totalFrames)
                        {
                            missingFrames.Add(i);
                        }
                        else if (Cache.ContainsKey(i))
                        {
                            break;
                        }
                    }

                    if (missingFrames.Count > 0)
                    {
                        Log($"[VideoBuilder] WARN: Frames #{missingFrames[0]}-#{missingFrames[missingFrames.Count - 1]} not found, rebuilding {missingFrames.Count} frames...");
                        foreach (var frameIdx in missingFrames)
                        {
                            writer.Append(regenerator(frameIdx));
                        }
                        missingFrames.Clear();
                    }
                    else if (Cache.Count == 0)
                    {
                        break;
                    }
                }
                else if (missingFrames.Count > 0)
                {
                    uint frameToProcess = missingFrames[0];
                    missingFrames.RemoveAt(0);

                    if (Cache.ContainsKey(frameToProcess))
                    {
                        writer.Append(Cache.TryRemove(frameToProcess, out var f) ? f : throw new KeyNotFoundException());
                        Log($"[VideoBuilder] Rebuilt frame #{frameToProcess} added.");

                        if (frameToProcess == currentIndex)
                            currentIndex++;
                    }
                }
                else
                {
                    currentIndex++;
                }


            }

            Dispose();
        }

        public void Interrupt()
        {
            Log("[VideoBuilder] Interrupt signal received. Stopping the video writer...");
            running = false;

            while (Cache.TryRemove(index, out var frame))
            {
                WriteFrame(index, frame);
                Log($"[VideoBuilder] Frame #{index} wrote during interrupt drain.");
            }

            var remainingFrameIndexes = Cache.Keys.OrderBy(frameIndex => frameIndex).ToArray();
            if (remainingFrameIndexes.Length > 0)
            {
                Log($"[VideoBuilder] WARN: Non-contiguous frames remain in cache during interrupt: {FormatFrameRanges(remainingFrameIndexes)}. Writing them in ascending order before closing.", "warn");

                foreach (var frameIndex in remainingFrameIndexes)
                {
                    if (Cache.TryRemove(frameIndex, out var remainingFrame))
                    {
                        WriteFrame(frameIndex, remainingFrame);
                        Log($"[VideoBuilder] Non-contiguous frame #{frameIndex} wrote during interrupt drain.");

                    }
                }
            }

            Dispose();
        }

        private void WaitForBuildThreadToStop()
        {
            if (BlockWrite || !Volatile.Read(ref buildStarted))
                return;

            while (!Volatile.Read(ref stopped))
                Thread.Sleep(50);
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

    }



}