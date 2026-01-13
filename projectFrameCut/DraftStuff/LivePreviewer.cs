using Microsoft.Maui.Controls.PlatformConfiguration;
using projectFrameCut.Asset;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using SixLabors.ImageSharp.Formats.Png;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using IPicture = projectFrameCut.Shared.IPicture;

namespace projectFrameCut.LivePreview
{
    public class LivePreviewer
    {
        public IClip[]? Clips;
        public uint TotalDuration;
        public string TempPath;
        public string? ProxyRoot;
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
            LogDiagnostic($"[LiveRender] RenderOne request: frame #{frameIndex}");
            var frameHash = Timeline.GetFrameHash(Clips, frameIndex);
            var destPath = Path.Combine(TempPath, $"projectFrameCut_Render_{frameHash}.png");
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
            var layers = Timeline.GetFramesInOneFrame(Clips, frameIndex, targetWidth, targetHeight, true);
            var pic = Timeline.MixtureLayers(layers, frameIndex, targetWidth, targetHeight);
            pic.SaveAsPng8bpp(destPath, encoder);
            return destPath;
        }

        public IPicture GetFrame(uint frameIndex, int targetWidth, int targetHeight)
        {
            var layers = Timeline.GetFramesInOneFrame(Clips, frameIndex, targetWidth, targetHeight, true);
            var pic = Timeline.MixtureLayers(layers, frameIndex, targetWidth, targetHeight);
            return pic;
        }

        public void UpdateDraft(DraftStructureJSON json)
        {
            var elements = (JsonSerializer.SerializeToElement(json).Deserialize<DraftStructureJSON>()?.Clips) ?? throw new NullReferenceException("Failed to cast ClipDraftDTOs to IClips."); //I don't want to write a lot of code to clone attributes from dto to IClip, it's too hard and may cause a lot of mystery bugs.

            var clipsList = new List<IClip>();

            foreach (var clip in elements.Cast<JsonElement>())
            {
                var clipInstance = PluginManager.CreateClip(clip);
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
                clipInstance.ReInit();
                clipsList.Add(clipInstance);

            }

            Clips = clipsList.ToArray();
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

        public async Task<string> RenderSomeFrames(int startIndex, int length, int targetWidth, int targetFramerate, int targetHeight, CancellationToken token)
        {

            var encodeWidth = (targetWidth % 2 == 0) ? targetWidth : targetWidth - 1;
            var encodeHeight = (targetHeight % 2 == 0) ? targetHeight : targetHeight - 1;

            var destPath = Path.Combine(TempPath, $"projectFrameCut_Render_{Guid.NewGuid()}.mp4");
            LogDiagnostic($"[LiveRender] RenderSomeFrames request: frame #{startIndex}, length {length}, adjusted output size {targetWidth}x{targetHeight}");
            using var builder = new VideoBuilder(destPath, encodeWidth, encodeHeight, targetFramerate, "libx264", "AV_PIX_FMT_YUV420P")
            {
                Duration = uint.MaxValue,
                BlockWrite = true //builder doesn't write from non-0 start index when blockwrite is not true
            };
            Renderer renderer = new Renderer
            {
                StartFrame = (uint)startIndex,
                Duration = (uint)(startIndex + length),
                builder = builder,
                Clips = Clips,
                Use16Bit = false,
                MaxThreads = 1,

            };
            renderer.PrepareRender(token);
            renderer.OnProgressChanged += OnProgressChanged;
            await renderer.GoRender(token);
            renderer.OnProgressChanged -= OnProgressChanged;

            builder.Writer.Finish(); //Finish doesn't support non-0 start frame, just end the writer
            builder.Dispose();
            LogDiagnostic($"[LiveRender] RenderSomeFrames finished: {destPath}");
            return destPath;
        }

        private static PngEncoder encoder = new()
        {
            BitDepth = PngBitDepth.Bit8,
        };
    }
}
