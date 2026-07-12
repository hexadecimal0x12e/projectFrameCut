using projectFrameCut.Drawing.Text;
using projectFrameCut.Drawing.Text.Entry;
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
    /// <summary>
    /// 生成丰富的测试项目结构，用于渲染管线性能基准测试。
    /// 包含多种 Clip 类型、多层合成、效果处理等场景。
    /// </summary>
    public static class BenchmarkSourceGenerator
    {
        /// <summary>
        /// 生成一个复杂的测试草稿结构，覆盖以下渲染路径：
        ///   - SolidColorClip（纯色填充）
        ///   - TextClip（矢量文字渲染）
        ///   - 多层叠加合成（Layer 0 / 1 / 2 / SubTrack）
        ///   - 连续效果（ZoomIn）
        ///   - 普通效果（FadeOpacity）
        ///   - Clip 重叠过渡区域
        /// </summary>
        public static IClip[] GetDraftStructure()
        {
            const int width = 1920;
            const int height = 1080;
            const float frameTime = 1f / 30f; // 30 fps
            const uint totalFrames = 300;      // 10 秒

            const string pluginId = "projectFrameCut.Render.Plugins.InternalPluginBase";

            var clips = new List<IClip>(capacity: 16)
            {
                // ────────────────────────────────────────────────────────
                //  1. 背景层 (SubTrack, LayerIndex >= 10000)
                //     ExtendToWholeDraft = true → 自动扩展到整个草稿
                // ────────────────────────────────────────────────────────
                new SolidColorClip
                {
                    Id = Guid.NewGuid(),
                    Name = "Background",
                    LayerIndex = 10000,
                    Duration = totalFrames,
                    R = (ushort)(0x2C * 257),
                    G = (ushort)(0x3E * 257),
                    B = (ushort)(0x50 * 257), // #2C3E50
                    ExtendToWholeDraft = true,
                    FrameTime = frameTime,
                },

                // ────────────────────────────────────────────────────────
                //  2. 主内容层 (Layer 0) — 多段纯色切换，模拟幻灯片
                //     段之间有少量重叠以测试合成器在过渡帧上的行为
                // ────────────────────────────────────────────────────────
                new SolidColorClip
                {
                    Id = Guid.NewGuid(),
                    Name = "Red Segment",
                    LayerIndex = 0,
                    StartFrame = 0,
                    Duration = 100,
                    TargetWidth = width,
                    TargetHeight = height,
                    TargetX = 0,
                    TargetY = 0,
                    R = (ushort)(0xE7 * 257),
                    G = (ushort)(0x4C * 257),
                    B = (ushort)(0x3C * 257), // #E74C3C
                    FrameTime = frameTime,
                },
                new SolidColorClip
                {
                    Id = Guid.NewGuid(),
                    Name = "Green Segment",
                    LayerIndex = 0,
                    StartFrame = 90,  // 与前一段 10 帧重叠 → 测试叠化/合成
                    Duration = 120,
                    TargetWidth = width,
                    TargetHeight = height,
                    TargetX = 0,
                    TargetY = 0,
                    R = (ushort)(0x2E * 257),
                    G = (ushort)(0xCC * 257),
                    B = (ushort)(0x71 * 257), // #2ECC71
                    FrameTime = frameTime,
                },
                new SolidColorClip
                {
                    Id = Guid.NewGuid(),
                    Name = "Blue Segment",
                    LayerIndex = 0,
                    StartFrame = 195, // 与前一段 15 帧重叠
                    Duration = 110,
                    TargetWidth = width,
                    TargetHeight = height,
                    TargetX = 0,
                    TargetY = 0,
                    R = (ushort)(0x34 * 257),
                    G = (ushort)(0x98 * 257),
                    B = (ushort)(0xDB * 257), // #3498DB
                    FrameTime = frameTime,
                },

                // ────────────────────────────────────────────────────────
                //  3. 文字层 (Layer 1) — 多段文字叠加，其中部分带效果
                // ────────────────────────────────────────────────────────

                // 片头标题 — 无效果
                new TextClip
                {
                    Id = Guid.NewGuid(),
                    Name = "Title Overlay",
                    LayerIndex = 1,
                    StartFrame = 5,
                    Duration = 85,
                    TargetWidth = width,
                    TargetHeight = height,
                    TargetX = 0,
                    TargetY = 0,
                    FrameTime = frameTime,
                    TextEntries =
                [
                    new TextEntry
                    {
                        Text = "FrameCut",
                        FontName = string.Empty,
                        FontSize = 72,
                        X = width / 2f,
                        Y = height / 2f - 60,
                        Alignment = TextAlignment.Center,
                        FillR = (ushort)1f, FillG = (ushort)1f, FillB = (ushort)1f, FillA = 1f,
                    },
                    new TextEntry
                    {
                        Text = "Render Benchmark",
                        FontName = string.Empty,
                        FontSize = 36,
                        X = width / 2f,
                        Y = height / 2f + 20,
                        Alignment = TextAlignment.Center,
                        FillR = (ushort)(0.8f * 65535), FillG = (ushort)(0.8f * 65535), FillB = (ushort)(0.8f * 65535), FillA = 0.8f,
                    },
                ],
                },

                // 场景二 — 带 FadeOpacity 效果
                new TextClip
                {
                    Id = Guid.NewGuid(),
                    Name = "Scene 2 Overlay",
                    LayerIndex = 1,
                    StartFrame = 100,
                    Duration = 90,
                    TargetWidth = width,
                    TargetHeight = height,
                    TargetX = 0,
                    TargetY = 0,
                    FrameTime = frameTime,
                    TextEntries =
                [
                    new TextEntry
                    {
                        Text = "Performance Test",
                        FontName = string.Empty,
                        FontSize = 64,
                        X = width / 2f,
                        Y = height / 2f - 30,
                        Alignment = TextAlignment.Center,
                        FillR = (ushort)1f, FillG = (ushort)1f, FillB = (ushort)1f, FillA = 1f,
                    },
                    new TextEntry
                    {
                        Text = "Measuring render throughput...",
                        FontName = string.Empty,
                        FontSize = 28,
                        X = width / 2f,
                        Y = height / 2f + 40,
                        Alignment = TextAlignment.Center,
                        FillR = (ushort)(0.7f * 65535), FillG = (ushort)(0.7f * 65535), FillB = (ushort)(0.7f * 65535), FillA = 0.6f,
                    },
                ],
                    Effects =
                [
                    new EffectAndMixtureJSONStructure
                    {
                        TypeName = "FadeOpacity",
                        FromPlugin = pluginId,
                        Enabled = true,
                        Index = 1,
                        Parameters = new Dictionary<string, object>
                        {
                            { "Opacity", 0.7f },
                        },
                    },
                ],
                },

                // 场景三 — 带 ZoomIn 连续效果
                new TextClip
                {
                    Id = Guid.NewGuid(),
                    Name = "Scene 3 Overlay",
                    LayerIndex = 1,
                    StartFrame = 200,
                    Duration = 100,
                    TargetWidth = width,
                    TargetHeight = height,
                    TargetX = 0,
                    TargetY = 0,
                    FrameTime = frameTime,
                    TextEntries =
                [
                    new TextEntry
                    {
                        Text = "Results",
                        FontName = string.Empty,
                        FontSize = 72,
                        X = width / 2f,
                        Y = height / 2f - 50,
                        Alignment = TextAlignment.Center,
                        FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535), FillB = (ushort)(1f * 65535), FillA = 1f ,
                    },
                    new TextEntry
                    {
                        Text = "Benchmark Complete",
                        FontName = string.Empty,
                        FontSize = 32,
                        X = width / 2f,
                        Y = height / 2f + 30,
                        Alignment = TextAlignment.Center,
                        FillR = (ushort)(0.8f * 65535), FillG = (ushort)(0.8f * 65535), FillB = (ushort)(0.8f * 65535), FillA = 0.7f,
                    },
                ],
                    Effects =
                [
                    new EffectAndMixtureJSONStructure
                    {
                        TypeName = "ZoomIn",
                        FromPlugin = pluginId,
                        IsContinuousEffect = true,
                        Enabled = true,
                        Index = 1,
                        Parameters = new Dictionary<string, object>
                        {
                            { "TargetX", width },
                            { "TargetY", height },
                        },
                    },
                ],
                },

                // ────────────────────────────────────────────────────────
                //  4. HUD / 装饰层 (Layer 2) — 持续显示的元素
                // ────────────────────────────────────────────────────────

                // 底部信息条
                new TextClip
                {
                    Id = Guid.NewGuid(),
                    Name = "HUD Info",
                    LayerIndex = 2,
                    StartFrame = 0,
                    Duration = totalFrames,
                    TargetWidth = width,
                    TargetHeight = height,
                    TargetX = 0,
                    TargetY = 0,
                    FrameTime = frameTime,
                    TextEntries =
                [
                    new TextEntry
                    {
                        Text = "FrameCut Benchmark v3.0  |  1920×1080  |  30fps",
                        FontName = string.Empty,
                        FontSize = 16,
                        X = 20,
                        Y = height - 30,
                        Alignment = TextAlignment.Left,
                        FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535), FillB = (ushort)(1f * 65535), FillA = 0.4f,
                    },
                ],
                },

                // 顶部装饰线
                new SolidColorClip
                {
                    Id = Guid.NewGuid(),
                    Name = "Top Divider",
                    LayerIndex = 2,
                    StartFrame = 0,
                    Duration = totalFrames,
                    TargetWidth = width,
                    TargetHeight = 3,
                    TargetX = 0,
                    TargetY = 0,
                    R = (ushort)(0xE7 * 257),
                    G = (ushort)(0x4C * 257),
                    B = (ushort)(0x3C * 257),
                    A = 0.6f,
                    FrameTime = frameTime,
                },

                // 底部装饰线
                new SolidColorClip
                {
                    Id = Guid.NewGuid(),
                    Name = "Bottom Divider",
                    LayerIndex = 2,
                    StartFrame = 0,
                    Duration = totalFrames,
                    TargetWidth = width,
                    TargetHeight = 3,
                    TargetX = 0,
                    TargetY = height - 3,
                    R = (ushort)(0x34 * 257),
                    G = (ushort)(0x98 * 257),
                    B = (ushort)(0xDB * 257),
                    A = 0.6f,
                    FrameTime = frameTime,
                },

                // 右上角帧计数器装饰
                new TextClip
                {
                    Id = Guid.NewGuid(),
                    Name = "Frame Counter",
                    LayerIndex = 2,
                    StartFrame = 0,
                    Duration = totalFrames,
                    TargetWidth = width,
                    TargetHeight = height,
                    TargetX = 0,
                    TargetY = 0,
                    FrameTime = frameTime,
                    TextEntries =
                [
                    new TextEntry
                    {
                        Text = "Frame",
                        FontName = string.Empty,
                        FontSize = 14,
                        X = width - 80,
                        Y = 12,
                        Alignment = TextAlignment.Right,
                        FillR =(ushort)(1f * 65535), FillG =(ushort)(1f * 65535), FillB =(ushort)(1f * 65535), FillA = 0.35f,
                    },
                ],
                }
            };

            return clips.ToArray();
        }
    }
}
