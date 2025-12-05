using projectFrameCut.Render;
using projectFrameCut.Shared;
using SixLabors.ImageSharp.Formats.Png;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.LivePreview
{
    public class LivePreviewer
    {
        public IClip[]? Clips;
        public string TempPath;

        public string RenderFrame(uint frameIndex, int targetWidth, int targetHeight)
        {
            Log($"[LiveRender] RenderOne request: frame #{frameIndex}");
            var frameHash = Timeline.GetFrameHash(Clips ?? throw new NullReferenceException("Clips not set yet."), frameIndex);
            var destPath = Path.Combine(TempPath, $"projectFrameCut_Render_{frameHash}.png");
            Log($"[LiveRender] FrameHash:{frameHash}");
            if (Path.Exists(destPath))
            {
                LogDiagnostic($"[LiveRender] Frame already exist; skip");
                return destPath;
            }
            else
            {
                Log($"[LiveRender] Generating frame #{frameIndex} ({frameHash})...");
            }
            var layers = Timeline.GetFramesInOneFrame(Clips, frameIndex, targetWidth, targetHeight, true);
            var pic = Timeline.MixtureLayers(layers, frameIndex, targetWidth, targetHeight);
            pic.SaveAsPng8bpp(destPath, encoder);
            return destPath;
        }

        public void UpdateDraft(DraftStructureJSON json)
        {
            List<JsonElement> clipsJson = json.Clips.Select(c => (JsonElement)c).ToList();

            var clipsList = new List<IClip>();

            foreach (var clip in clipsJson)
            {
                clipsList.Add(IClip.FromJSON(clip));
            }

            Clips = clipsList.ToArray();

            foreach (var clip in Clips)
            {
                clip.ReInit();
            }

            Log($"[LiveRender] Updated clips, total {Clips.Length} clips.");
        }

        private static PngEncoder encoder = new()
        {
            BitDepth = PngBitDepth.Bit8,
        };
    }
}
