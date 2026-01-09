using FFmpeg.AutoGen;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.Rendering
{
    public class VideoBuilder : IDisposable
    {
        string outputPath;
        IVideoWriter builder;
        uint index;
        bool running = true, stopped = false;
        ConcurrentDictionary<uint, IPicture> Cache = new();

        /// <summary>
        /// When it's true, adding a frame with an existing index will throw an exception, 
        /// or when <see cref="BlockWrite"/> is enabled, writing a frame with an existing index will throw an exception.
        /// </summary>
        public bool StrictMode { get; set; } = true;
        /// <summary>
        /// Call GC to collect unreferenced objects after each frame is written.
        /// </summary>
        public bool DoGCAfterEachWrite { get; set; } = true;
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
        public int Width => builder.Width;
        public int Height => builder.Height;
        public IVideoWriter Writer => builder;



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

        public VideoBuilder(string path, int width, int height, int framerate, string encoder, string fmt)
        {
            outputPath = path;
            index = 0;
            builder = PluginManager.CreateVideoWriter(encoder);
            builder.Width = width;
            builder.Height = height;
            builder.FramePerSecond = framerate;
            builder.PixelFormat = fmt;
            builder.OutputPath = outputPath;
            builder.CodecName = encoder;
            builder.Initialize();   
            

        }

        public bool Disposed { get; private set; }

        public void Append(uint index, IPicture frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame.Width != Width || frame.Height != Height)
                throw new ArgumentException($"The result ({frame.filePath})'s size {frame.Width}*{frame.Height} is different from original size ({Width}*{Height}). Please check the source.")
                {
                    Data = { { "PictureObject", frame }, { "ProcessStack", frame.ProcessStack } }
                };

            if (index > Duration)
            {
                Log($"[VideoBuilder] WARN: Frame #{index} is out of duration {Duration}, ignored.", "warn");
                if (DisposeFrameAfterEachWrite && !frame.Flag.HasFlag(IPicture.PictureFlag.NoDisposeAfterWrite)) frame.Dispose();
                return;
            }
            if (!FramePendedToWrite.TryAdd(index, false))
            {
                if (StrictMode)
                {
                    throw new InvalidOperationException($"Frame #{index} has already been added.");
                }
                else
                {
                    Log($"[VideoBuilder] WARN: Frame #{index} has already been added, ignored.", "warn");
                    if (DisposeFrameAfterEachWrite && !frame.Flag.HasFlag(IPicture.PictureFlag.NoDisposeAfterWrite)) frame.Dispose();
                    return;
                }
            }
            if (!BlockWrite)
            {
                Cache.AddOrUpdate(index, frame, (_, _) => throw new InvalidOperationException($"Frame #{index} has already been added."));               
            }
            else
            {
                builder.Append(frame);
                Log($"[VideoBuilder] Frame #{index} added.");
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
            // Best-effort cleanup: if anything remains in cache, dispose it.
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
            builder.Dispose();
        }

        public Thread Build()
        {
            return new Thread(() =>
            {
                Log($"[VideoBuilder] Successfully started writer for {outputPath}");

                do
                {
                    if (Cache.Count == 0)
                    {
                        continue;
                    }

                    if (Cache.ContainsKey(index))
                    {
                        builder.Append(Cache.TryRemove(index, out var f) ? f : throw new KeyNotFoundException());
                        FramePendedToWrite[index] = true;
                        index++;

                        if (DisposeFrameAfterEachWrite && !f.Flag.HasFlag(IPicture.PictureFlag.NoDisposeAfterWrite)) f.Dispose();
                        if (DoGCAfterEachWrite) GC.Collect();
                        Log($"[VideoBuilder] Frame #{index} wrote.");
                    }

                }
                while (running);
                Thread.Sleep(50);
                stopped = true;
            })
            {
                Name = $"VideoWriter for {outputPath}",
                Priority = ThreadPriority.Highest
            };


        }

        public void Finish(Func<uint, IPicture> regenerator, uint totalFrames = 0)
        {
            running = false;
            while (!Volatile.Read(ref stopped))
                Thread.Sleep(500);

            var missingFrames = new List<uint>();
            uint currentIndex = index;

            while (Cache.Count > 0 || missingFrames.Count > 0)
            {
                Console.Error.WriteLine($"@@{currentIndex},{totalFrames}");

                if (Cache.ContainsKey(currentIndex))
                {
                    builder.Append(Cache.TryRemove(currentIndex, out var f) ? f : throw new KeyNotFoundException());
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
                            builder.Append(regenerator(frameIdx));
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
                        builder.Append(Cache.TryRemove(frameToProcess, out var f) ? f : throw new KeyNotFoundException());
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

    }



}