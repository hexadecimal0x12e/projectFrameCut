using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.Benchmark
{
    public static class BenchmarkSourceGenerator
    {
        public static IClip[] GetDraftStructure()
        {
            const int relativeWidth = 1920;
            const int relativeHeight = 1080;
            const float frameTime = 1f / 30f;

            static object J(object value) => JsonSerializer.SerializeToElement(value, value.GetType());

            static Dictionary<string, object> Params(params (string Key, object Value)[] items)
            {
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var (key, value) in items)
                {
                    dict[key] = J(value);
                }
                return dict;
            }

            static EffectAndMixtureJSONStructure Effect(
                string typeName,
                string name,
                int index,
                Dictionary<string, object>? parameters,
                int relW,
                int relH)
                => new()
                {
                    FromPlugin = projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID,
                    TypeName = typeName,
                    Name = name,
                    Index = index,
                    Enabled = true,
                    RelativeWidth = relW,
                    RelativeHeight = relH,
                    Parameters = parameters,
                };

            List<IClip> clips =
            [
                // 背景：纯色 + 裁剪/缩放 + 连续缩放(ZoomIn)
                new SolidColorClip
                {
                    Id = "bench-solid-bg-001",
                    Name = "Benchmark Solid BG",
                    LayerIndex = 0,
                    StartFrame = 0,
                    RelativeStartFrame = 0,
                    Duration = 900,
                    FrameTime = frameTime,
                    SecondPerFrameRatio = 1f,
                    MixtureMode = MixtureMode.Overlay,
                    R = 5000,
                    G = 9000,
                    B = 14000,
                    A = 1.0f,
                    targetWidth = relativeWidth,
                    targetHeight = relativeHeight,
                    Effects =
                    [
                        Effect("Crop", "Crop center-ish", 1, Params(("StartX", 120), ("StartY", 60), ("Width", 1680), ("Height", 960)), relativeWidth, relativeHeight),
                        Effect("Resize", "Resize back", 2, Params(("Width", relativeWidth), ("Height", relativeHeight), ("PreserveAspectRatio", true)), relativeWidth, relativeHeight),
                        Effect("ZoomIn", "ZoomIn slight", 3, Params(("TargetX", 1200), ("TargetY", 675)), relativeWidth, relativeHeight),
                    ]
                },

                // 叠加文字：多段文字 + 抖动(Jitter) + 位置(Place) + 缩放(Resize)
                new TextClip
                {
                    Id = "bench-text-001",
                    Name = "Benchmark Text Overlay",
                    LayerIndex = 1,
                    StartFrame = 0,
                    RelativeStartFrame = 0,
                    Duration = 900,
                    FrameTime = frameTime,
                    SecondPerFrameRatio = 1f,
                    MixtureMode = MixtureMode.Overlay,
                    TextEntries =
                    [
                        new TextClip.TextClipEntry("projectFrameCut Benchmark", 80, 80, "Arial", 72, 65535, 65535, 65535, 1.0f),
                        new TextClip.TextClipEntry("SolidColorClip + Effects", 90, 180, "Arial", 44, 65535, 50000, 20000, 1.0f),
                    ],
                    Effects =
                    [
                        Effect("Resize", "Downscale text", 1, Params(("Width", 1600), ("Height", 900), ("PreserveAspectRatio", true)), relativeWidth, relativeHeight),
                        Effect("Place", "Place to corner", 2, Params(("StartX", 40), ("StartY", 30)), relativeWidth, relativeHeight),
                        Effect("Jitter", "Jitter mild", 3, Params(("MaxOffsetX", 6), ("MaxOffsetY", 4), ("Seed", 42)), relativeWidth, relativeHeight),
                    ]
                },

                // 色键块：纯绿 -> RemoveColor(抠绿) -> Place（可用于测试透明叠加链路）
                new SolidColorClip
                {
                    Id = "bench-solid-key-001",
                    Name = "Benchmark Keyed Solid",
                    LayerIndex = 2,
                    StartFrame = 120,
                    RelativeStartFrame = 0,
                    Duration = 420,
                    FrameTime = frameTime,
                    SecondPerFrameRatio = 1f,
                    MixtureMode = MixtureMode.Overlay,
                    R = 0,
                    G = 65535,
                    B = 0,
                    A = 1.0f,
                    targetWidth = 900,
                    targetHeight = 500,
                    Effects =
                    [
                        Effect("RemoveColor", "Key out green", 1, Params(("R", (ushort)0), ("G", (ushort)65535), ("B", (ushort)0), ("A", (ushort)65535), ("Tolerance", (ushort)1500)), relativeWidth, relativeHeight),
                        Effect("Place", "Place mid", 2, Params(("StartX", 520), ("StartY", 340)), relativeWidth, relativeHeight),
                        Effect("Jitter", "Jitter strong", 3, Params(("MaxOffsetX", 20), ("MaxOffsetY", 12), ("Seed", 7)), relativeWidth, relativeHeight),
                    ]
                },

                // 第二段文字：更大的字 + ZoomIn + Crop（让效果栈更丰富）
                new TextClip
                {
                    Id = "bench-text-002",
                    Name = "Benchmark Text Center",
                    LayerIndex = 1,
                    StartFrame = 360,
                    RelativeStartFrame = 0,
                    Duration = 420,
                    FrameTime = frameTime,
                    SecondPerFrameRatio = 1f,
                    MixtureMode = MixtureMode.Overlay,
                    TextEntries =
                    [
                        new TextClip.TextClipEntry("基准测试", 520, 420, "HarmonyOS_Sans_SC_Regular", 120, 65535, 40000, 8000, 1.0f),
                        new TextClip.TextClipEntry("ZoomIn / Crop / Resize", 500, 560, "HarmonyOS_Sans_SC_Regular", 52, 50000, 65535, 50000, 1.0f),
                    ],
                    Effects =
                    [
                        Effect("Crop", "Crop band", 1, Params(("StartX", 200), ("StartY", 200), ("Width", 1520), ("Height", 680)), relativeWidth, relativeHeight),
                        Effect("ZoomIn", "ZoomIn medium", 2, Params(("TargetX", 900), ("TargetY", 506)), relativeWidth, relativeHeight),
                        Effect("Resize", "Resize to full", 3, Params(("Width", relativeWidth), ("Height", relativeHeight), ("PreserveAspectRatio", true)), relativeWidth, relativeHeight),
                    ]
                },
            ];

            return clips.ToArray();
        }
    }
}
