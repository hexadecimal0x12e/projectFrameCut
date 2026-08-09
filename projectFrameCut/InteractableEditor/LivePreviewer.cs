using Microsoft.Maui.Controls.PlatformConfiguration;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut.LivePreview
{
    public class LivePreviewer
    {
        private const string StaticFrameCacheVersion = "v2-target-layout";
        public IClip[]? Clips;
        public ISoundTrack[]? SoundTracks;
        public int targetFrameRate = 60;
        public uint TotalDuration;
        public string TempPath = string.Empty;
        public string? ProxyRoot;
        public int ProjectRelativeWidth { get; set; }
        public int ProjectRelativeHeight { get; set; }
        public event Action<double, TimeSpan>? OnProgressChanged;

        public bool IsFrameRendered(uint frameIndex)
        {
            if (Clips == null) return false;
            var frameHash = Timeline.GetFrameHash(Clips, frameIndex);
            var destPath = Path.Combine(TempPath, $"projectFrameCut_Render_{frameHash}.png");
            return Path.Exists(destPath);
        }

        public string RenderFrame(uint frameIndex, int targetWidth, int targetHeight)
        {
            ArgumentNullException.ThrowIfNull(Clips, "Clips not set yet.");
            (targetWidth, targetHeight) = NormalizeTargetSize(targetWidth, targetHeight, requireEven: false);
            LogDiagnostic($"[LiveRender] RenderOne request: frame #{frameIndex}");
            var frameHash = Timeline.GetFrameHash(Clips, frameIndex);
            var cacheKey = BuildFrameCacheKey(frameHash, targetWidth, targetHeight);
            var destPath = Path.Combine(TempPath, $"projectFrameCut_Render_{cacheKey}.png");
            LogDiagnostic($"[LiveRender] FrameHash:{frameHash}");
            if (Path.Exists(destPath))
            {
                LogDiagnostic($"[LiveRender] Frame already exist; skip");
                return destPath;
            }
            else
            {
                LogDiagnostic($"[LiveRender] Generating frame #{frameIndex} ({frameHash})...");
            }
            foreach (var item in Clips)
            {
                try
                {
                    item.ReInit(8);
                    if (!ClipInitializationFailure.HasDeferredFailures(item.ExtraData))
                        ClipInitializationFailure.Clear(item);
                }
                catch (Exception ex)
                {
                    ClipInitializationFailure.Mark(item, "Source or ResolveEffect", ex);
                    Log(ex, $"Initialize live-render clip {item.Name} ({item.Id}); using checkerboard fallback", this);
                }
            }
            var layers = Timeline.GetFramesInOneFrame(
                Clips,
                frameIndex,
                targetWidth,
                targetHeight,
                forceResize: true,
                projectRelativeWidth: ProjectRelativeWidth,
                projectRelativeHeight: ProjectRelativeHeight);
            var pic = Timeline.MixtureLayers(
                layers,
                frameIndex,
                targetWidth,
                targetHeight,
                autoCenterImplicitClip: true,
                projectRelativeWidth: ProjectRelativeWidth,
                projectRelativeHeight: ProjectRelativeHeight);
            pic.ToBitPerPixel(8).SaveToPng(destPath);
            return destPath;
        }

        public IPicture GetFrame(uint frameIndex, int targetWidth, int targetHeight)
        {
            ArgumentNullException.ThrowIfNull(Clips, "Clips");
            (targetWidth, targetHeight) = NormalizeTargetSize(targetWidth, targetHeight, requireEven: false);
            var layers = Timeline.GetFramesInOneFrame(
                Clips,
                frameIndex,
                targetWidth,
                targetHeight,
                forceResize: true,
                projectRelativeWidth: ProjectRelativeWidth,
                projectRelativeHeight: ProjectRelativeHeight);
            var pic = Timeline.MixtureLayers(
                layers,
                frameIndex,
                targetWidth,
                targetHeight,
                autoCenterImplicitClip: true,
                projectRelativeWidth: ProjectRelativeWidth,
                projectRelativeHeight: ProjectRelativeHeight);
            return pic;
        }

        public async Task UpdateDraft(DraftStructureJSON json)
        {
            var clips = json.Clips;
            if (clips is null || clips.Length == 0) return;

            var clipsList = new List<IClip>();
            var reinitTasks = new List<Task>();

            foreach (var clip in clips)
            {
                if (clip.ClipType == ClipMode.MarkingClip)
                {
                    continue;
                }

                DraftImportAndExportHelper.RestoreFailedInitializationData(clip);
                var clipJson = JsonSerializer.SerializeToElement(clip);
                var clipInstance = PluginManager.CreateClip(clipJson);
                if (clipInstance.FilePath is not null)
                {
                    if (clipInstance.FilePath.StartsWith('$'))
                    {
                        var asset = AssetDatabase.Assets[clipInstance.FilePath.Substring(1)];
                        clipInstance.FilePath = asset.Path;
                        var proxyPath = Path.Combine(MauiProgram.DataPath, "My Assets", ".proxy", $"{asset.AssetId}.mp4");
                        if (Path.Exists(proxyPath))
                        {
                            clipInstance.FilePath = proxyPath;
                            Log($"The proxy for {clipInstance.Name} is used.");
                        }
                        else
                        {
                            Log($"The proxy for {clipInstance.Name} does not exist.");
                        }
                    }
                    else if (ProxyRoot is not null && clipInstance.FilePath is not null)
                    {
                        var proxiedPath = Path.Combine(ProxyRoot, $"{Path.GetFileNameWithoutExtension(clipInstance.FilePath)}.proxy.mp4");

                        if (Path.Exists(proxiedPath))
                        {
                            clipInstance.FilePath = proxiedPath;
                            Log($"The proxy for {clipInstance.Name} is used.");
                        }
                        else
                        {
                            Log($"The proxy for {clipInstance.Name} does not exist.");
                        }
                    }
                }
                clipsList.Add(clipInstance);
                reinitTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        clipInstance.ReInit(8);
                        if (!ClipInitializationFailure.HasDeferredFailures(clipInstance.ExtraData))
                            ClipInitializationFailure.Clear(clipInstance);
                    }
                    catch (Exception ex)
                    {
                        ClipInitializationFailure.Mark(clipInstance, "Source or ResolveEffect", ex);
                        Log(ex, $"Initialize live-preview clip {clipInstance.Name} ({clipInstance.Id}); using checkerboard fallback", this);
                    }
                }));
            }

            await Task.WhenAll(reinitTasks);

            Clips = clipsList.ToArray();
            SoundTracks = DraftImportAndExportHelper.JSONToISoundTracks(json).ToArray();
            long max = 0;
            foreach (var clip in Clips)
            {
                max = Math.Max(clip.StartFrame + clip.Duration, max);

            }

            if (max > uint.MaxValue)
            {
                throw new OverflowException($"Project duration overflow, total frames exceed {uint.MaxValue}.");
            }

            TotalDuration = (uint)max;

            Log($"[LiveRender] Updated clips, total {Clips.Length} clips.");
        }

        public bool HasAudioSources()
        {
            var hasAudioClip = Clips?.Any(c => c.ClipType == ClipMode.AudioClip || c.ClipType == ClipMode.VideoClip) ?? false;
            var hasSoundTrack = SoundTracks?.Any() ?? false;
            return hasAudioClip || hasSoundTrack;
        }

        public async Task ResetAudioPlaybackSources(int ppb = 8)
        {
            if (Clips is not null)
            {
                foreach (var clip in Clips.Where(c => c.ClipType == ClipMode.AudioClip || c.ClipType == ClipMode.VideoClip))
                {
                    await Task.Run(() => clip.ReInit(ppb));
                }
            }

            if (SoundTracks is not null)
            {
                foreach (var track in SoundTracks)
                {
                    await Task.Run(track.ReInit);
                }
            }
        }

        public async Task<string?> RenderSomeAudio(int startIndex, int length, int targetFramerate, CancellationToken token, int sampleRate = 96000, int channels = 2)
        {
            if (!HasAudioSources())
            {
                return null;
            }

            var id = Guid.NewGuid();
            var audDestPath = Path.Combine(TempPath, $"projectFrameCut_Render_{id}.wav");

            using var writer = new AudioWriter(audDestPath, sampleRate, channels, "pcm_s16le");
            var composer = new AudioComposer<float>
            {
                Clips = Clips ?? Array.Empty<IClip>(),
                SoundTracks = SoundTracks,
                Writer = writer,
                StartFrame = (uint)startIndex,
                Duration = (uint)length,
            };

            await Task.Run(() => composer.Compose(targetFramerate, sampleRate, channels, 40960, token), token);
            writer.Finish();
            return audDestPath;
        }

        public async Task<string> RenderSomeFrames(int startIndex, int length, int targetWidth, int targetFramerate, int targetHeight, CancellationToken token, bool includeAudio = true)
        {

            (targetWidth, targetHeight) = NormalizeTargetSize(targetWidth, targetHeight, requireEven: false);

            var (encodeWidth, encodeHeight) = NormalizeTargetSize(targetWidth, targetHeight, requireEven: true);

            var id = Guid.NewGuid();
            var resultPath = Path.Combine(TempPath, $"projectFrameCut_Render_{id}_result.mp4");
            var destPath = Path.Combine(TempPath, $"projectFrameCut_Render_{id}.mp4");
            var audDestPath = Path.Combine(TempPath, $"projectFrameCut_Render_{id}.wav");
            LogDiagnostic($"[LiveRender] RenderSomeFrames request: frame #{startIndex}, length {length}, output {targetWidth}x{targetHeight}, encode {encodeWidth}x{encodeHeight}");
            using var builder = new VideoBuilder(destPath, encodeWidth, encodeHeight, targetFramerate, "libx264", "AV_PIX_FMT_YUV420P")
            {
                Duration = uint.MaxValue,
                BlockWrite = true //builder doesn't write from non-0 start index when blockwrite is not true
            };
            Renderer renderer = new Renderer
            {
                StartFrame = (uint)startIndex,
                Duration = (uint)length,
                builder = builder,
                Clips = Clips,
                Use16Bit = false,
                AutoCenterImplicitClip = true,
                MaxThreads = 1,
                ProjectRelativeWidth = ProjectRelativeWidth > 0 ? ProjectRelativeWidth : targetWidth,
                ProjectRelativeHeight = ProjectRelativeHeight > 0 ? ProjectRelativeHeight : targetHeight,

            };
            renderer.PrepareRender(token);
            renderer.OnProgressChanged += OnProgressChanged;
            await renderer.GoRender(token);
            renderer.OnProgressChanged -= OnProgressChanged;

            if (includeAudio)
            {
                audDestPath = await RenderSomeAudio(startIndex, length, targetFramerate, token) ?? string.Empty;
            }
            builder.Writer.Finish(); //Finish doesn't support non-0 start frame, just end the writer
            builder.Dispose();

            if (includeAudio && !string.IsNullOrWhiteSpace(audDestPath) && File.Exists(audDestPath))
            {
                await Task.Run(() => VideoAudioMuxer.MuxFromFiles(destPath, audDestPath, resultPath, true), token);
                File.Delete(audDestPath);
            }
            else
            {
                File.Copy(destPath, resultPath, true);
            }

            File.Delete(destPath);
            LogDiagnostic($"[LiveRender] RenderSomeFrames finished: {resultPath}");
            return resultPath;
        }

        private static (int width, int height) NormalizeTargetSize(int width, int height, bool requireEven)
        {
            var normalizedWidth = Math.Max(1, width);
            var normalizedHeight = Math.Max(1, height);

            if (!requireEven)
            {
                return (normalizedWidth, normalizedHeight);
            }

            if ((normalizedWidth & 1) == 1)
            {
                normalizedWidth++;
            }

            if ((normalizedHeight & 1) == 1)
            {
                normalizedHeight++;
            }

            normalizedWidth = Math.Max(2, normalizedWidth);
            normalizedHeight = Math.Max(2, normalizedHeight);
            return (normalizedWidth, normalizedHeight);
        }

        private static string BuildFrameCacheKey(string frameHash, int width, int height)
            => $"{StaticFrameCacheVersion}_{frameHash}_{width}x{height}";
    }
}
