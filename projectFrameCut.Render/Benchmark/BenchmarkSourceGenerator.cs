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
    /// 覆盖多种 Clip 类型、效果链、多层合成、混合模式、矢量内容、位置动画等渲染路径。
    /// </summary>
    public static class BenchmarkSourceGenerator
    {
        private const string PluginId = "projectFrameCut.Render.Plugins.InternalPluginBase";

        /// <summary>
        /// 生成一个复杂的测试草稿结构。
        /// </summary>
        /// <param name="totalFrames">草稿总帧数（默认 300 = 10s @30fps）</param>
        /// <param name="frameTime">帧时长（默认 1/30）</param>
        /// <param name="width">渲染宽度（默认 1920）</param>
        /// <param name="height">渲染高度（默认 1080）</param>
        public static IClip[] GetDraftStructure(
            uint totalFrames = 300,
            float frameTime = 1f / 30f,
            int width = 1920,
            int height = 1080)
        {
            var clips = new List<IClip>(capacity: 64)
            {
                // ═══════════════════════════════════════════════════════════
                //  1. 背景层 (SubTrack, LayerIndex = 10000)
                //     ExtendToWholeDraft = true → 自动扩展到整个草稿
                // ═══════════════════════════════════════════════════════════
                Background(totalFrames, frameTime),

                // ═══════════════════════════════════════════════════════════
                //  2. 主内容层 (Layer 0) — 多段纯色切换 + 效果组合
                //     段之间有重叠以测试合成器在过渡帧上的行为
                // ═══════════════════════════════════════════════════════════
                RedSegment(width, height, frameTime),
                GreenSegment(width, height, frameTime),
                BlueSegment(width, height, frameTime),
                OrangeSegment(width, height, frameTime),
                PurpleSegment(width, height, frameTime),

                // ═══════════════════════════════════════════════════════════
                //  3. PiP (画中画) 层 (Layer 0, SubLayer 1) — 小尺寸叠加
                //     用于测试不同尺寸 clip 的合成路径
                // ═══════════════════════════════════════════════════════════
                PipOverlay(width, height, frameTime),

                // ═══════════════════════════════════════════════════════════
                //  4. 效果演示层 (Layer 1) — 不同效果的 SolidColorClip
                //     每种效果单独一段，便于 benchmark 分析各效果开销
                // ═══════════════════════════════════════════════════════════
                BlurDemoClip(width, height, frameTime),
                RotationDemoClip(width, height, frameTime),
                SharpenDemoClip(width, height, frameTime),
                VignetteDemoClip(width, height, frameTime),

                // ═══════════════════════════════════════════════════════════
                //  5. 效果链层 (Layer 2) — 多效果叠加在同一 clip 上
                //     测试效果管线的链式处理性能
                // ═══════════════════════════════════════════════════════════
                EffectChainClip1(width, height, frameTime),
                EffectChainClip2(width, height, frameTime),
                EffectChainClip3(width, height, frameTime),

                // ═══════════════════════════════════════════════════════════
                //  6. Jitter 位置动画层 (Layer 3) — 测试 IClipPositionProvider
                // ═══════════════════════════════════════════════════════════
                JitterClipA(width, height, frameTime),
                JitterClipB(width, height, frameTime),

                // ═══════════════════════════════════════════════════════════
                //  7. 文字层 (Layer 4) — 多段文字叠加，部分带效果
                // ═══════════════════════════════════════════════════════════
                TitleOverlay(width, height, frameTime),
                Scene2Overlay(width, height, frameTime),
                Scene3Overlay(width, height, frameTime),
                TextFadeInOverlay(width, height, frameTime),
                MultiColumnText(width, height, frameTime),

                // ═══════════════════════════════════════════════════════════
                //  8. HUD / 装饰层 (Layer 5) — 持续显示的元素
                // ═══════════════════════════════════════════════════════════
                HudInfo(width, height, totalFrames, frameTime),
                TopDivider(width, height, totalFrames, frameTime),
                BottomDivider(width, height, totalFrames, frameTime),
                FrameCounter(width, height, totalFrames, frameTime),
            };

            return clips.ToArray();
        }

        // ────────────────────────────────────────────────────────────────
        //  1. 背景
        // ────────────────────────────────────────────────────────────────

        private static SolidColorClip Background(uint totalFrames, float frameTime) => new()
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
        };

        // ────────────────────────────────────────────────────────────────
        //  2. 主内容层 — 纯色段
        // ────────────────────────────────────────────────────────────────

        private static SolidColorClip RedSegment(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Red Segment",
            LayerIndex = 0,
            StartFrame = 0,
            Duration = 60,
            TargetWidth = width,
            TargetHeight = height,
            TargetX = 0,
            TargetY = 0,
            R = (ushort)(0xE7 * 257),
            G = (ushort)(0x4C * 257),
            B = (ushort)(0x3C * 257), // #E74C3C
            FrameTime = frameTime,
        };

        private static SolidColorClip GreenSegment(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Green Segment",
            LayerIndex = 0,
            StartFrame = 50,  // 10 帧重叠 → 测试叠化/合成
            Duration = 60,
            TargetWidth = width,
            TargetHeight = height,
            TargetX = 0,
            TargetY = 0,
            R = (ushort)(0x2E * 257),
            G = (ushort)(0xCC * 257),
            B = (ushort)(0x71 * 257), // #2ECC71
            FrameTime = frameTime,
        };

        private static SolidColorClip BlueSegment(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Blue Segment",
            LayerIndex = 0,
            StartFrame = 100, // 10 帧重叠
            Duration = 60,
            TargetWidth = width,
            TargetHeight = height,
            TargetX = 0,
            TargetY = 0,
            R = (ushort)(0x34 * 257),
            G = (ushort)(0x98 * 257),
            B = (ushort)(0xDB * 257), // #3498DB
            FrameTime = frameTime,
        };

        private static SolidColorClip OrangeSegment(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Orange Segment",
            LayerIndex = 0,
            StartFrame = 150,
            Duration = 60,
            TargetWidth = width,
            TargetHeight = height,
            TargetX = 0,
            TargetY = 0,
            R = (ushort)(0xE6 * 257),
            G = (ushort)(0x7E * 257),
            B = (ushort)(0x22 * 257), // #E67E22
            FrameTime = frameTime,
        };

        private static SolidColorClip PurpleSegment(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Purple Segment",
            LayerIndex = 0,
            StartFrame = 200,
            Duration = 100,
            TargetWidth = width,
            TargetHeight = height,
            TargetX = 0,
            TargetY = 0,
            R = (ushort)(0x9B * 257),
            G = (ushort)(0x59 * 257),
            B = (ushort)(0xB6 * 257), // #9B59B6
            FrameTime = frameTime,
        };

        // ────────────────────────────────────────────────────────────────
        //  3. PiP 画中画 — 小尺寸叠加（测试非全屏 clip 合成）
        // ────────────────────────────────────────────────────────────────

        private static SolidColorClip PipOverlay(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "PiP Overlay",
            LayerIndex = 0,
            SubLayerIndex = 1,
            StartFrame = 30,
            Duration = 240,
            TargetWidth = width / 3,
            TargetHeight = height / 3,
            TargetX = width - width / 3 - 20,
            TargetY = height - height / 3 - 60,
            R = (ushort)(0x1A * 257),
            G = (ushort)(0x1A * 257),
            B = (ushort)(0x2E * 257), // #1A1A2E
            A = 0.85f,
            FrameTime = frameTime,
        };

        // ────────────────────────────────────────────────────────────────
        //  4. 效果演示层 (Layer 1) — 每种效果单独一段
        // ────────────────────────────────────────────────────────────────

        private static SolidColorClip BlurDemoClip(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Blur Effect Demo",
            LayerIndex = 1,
            StartFrame = 0,
            Duration = 60,
            TargetWidth = width / 2 - 10,
            TargetHeight = height / 2 - 10,
            TargetX = 10,
            TargetY = 10,
            R = (ushort)(0xE7 * 257),
            G = (ushort)(0x4C * 257),
            B = (ushort)(0x3C * 257),
            FrameTime = frameTime,
            Effects =
            [
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "Blur",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 1,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Sigma", 8.0f },
                    },
                },
            ],
        };

        private static SolidColorClip RotationDemoClip(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Rotation Effect Demo",
            LayerIndex = 1,
            StartFrame = 0,
            Duration = 60,
            TargetWidth = width / 2 - 10,
            TargetHeight = height / 2 - 10,
            TargetX = width / 2 + 5,
            TargetY = 10,
            R = (ushort)(0x2E * 257),
            G = (ushort)(0xCC * 257),
            B = (ushort)(0x71 * 257),
            FrameTime = frameTime,
            Effects =
            [
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "Rotation",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 1,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Angle", 15.0f },
                        { "ExpandCanvas", false },
                    },
                },
            ],
        };

        private static SolidColorClip SharpenDemoClip(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Sharpen Effect Demo",
            LayerIndex = 1,
            StartFrame = 70,
            Duration = 60,
            TargetWidth = width / 2 - 10,
            TargetHeight = height / 2 - 10,
            TargetX = 10,
            TargetY = height / 2 + 10,
            R = (ushort)(0x34 * 257),
            G = (ushort)(0x98 * 257),
            B = (ushort)(0xDB * 257),
            FrameTime = frameTime,
            Effects =
            [
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "Sharpen",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 1,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Amount", 2.5f },
                    },
                },
            ],
        };

        private static SolidColorClip VignetteDemoClip(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Vignette Effect Demo",
            LayerIndex = 1,
            StartFrame = 70,
            Duration = 60,
            TargetWidth = width / 2 - 10,
            TargetHeight = height / 2 - 10,
            TargetX = width / 2 + 5,
            TargetY = height / 2 + 10,
            R = (ushort)(0xE6 * 257),
            G = (ushort)(0x7E * 257),
            B = (ushort)(0x22 * 257),
            FrameTime = frameTime,
            Effects =
            [
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "Vignette",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 1,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Strength", 0.7f },
                        { "Radius", 0.5f },
                    },
                },
            ],
        };

        // ────────────────────────────────────────────────────────────────
        //  5. 效果链层 (Layer 2) — 多效果叠加在同一 clip 上
        // ────────────────────────────────────────────────────────────────

        private static SolidColorClip EffectChainClip1(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Effect Chain: FadeOpacity + ColorAdjustment",
            LayerIndex = 2,
            StartFrame = 10,
            Duration = 80,
            TargetWidth = width,
            TargetHeight = height,
            TargetX = 0,
            TargetY = 0,
            R = (ushort)(0xE7 * 257),
            G = (ushort)(0x4C * 257),
            B = (ushort)(0x3C * 257),
            FrameTime = frameTime,
            Effects =
            [
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "FadeOpacity",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 1,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Opacity", 0.85f },
                    },
                },
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "ColorAdjustment",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 2,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Brightness", 1.1f },
                        { "Contrast", 1.15f },
                        { "Saturation", 1.2f },
                        { "Hue", 0f },
                        { "Gamma", 1.0f },
                        { "Vibrance", 0.1f },
                        { "Temperature", 0f },
                        { "Invert", false },
                        { "Grayscale", 0f },
                        { "Opacity", 1.0f },
                    },
                },
            ],
        };

        private static SolidColorClip EffectChainClip2(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Effect Chain: Blur + Sharpen + FadeOpacity",
            LayerIndex = 2,
            StartFrame = 110,
            Duration = 80,
            TargetWidth = width,
            TargetHeight = height,
            TargetX = 0,
            TargetY = 0,
            R = (ushort)(0x2E * 257),
            G = (ushort)(0xCC * 257),
            B = (ushort)(0x71 * 257),
            FrameTime = frameTime,
            Effects =
            [
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "Blur",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 1,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Sigma", 3.0f },
                    },
                },
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "Sharpen",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 2,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Amount", 1.5f },
                    },
                },
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "FadeOpacity",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 3,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Opacity", 0.8f },
                    },
                },
            ],
        };

        private static SolidColorClip EffectChainClip3(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Effect Chain: Flip + Rotation + Vignette",
            LayerIndex = 2,
            StartFrame = 210,
            Duration = 90,
            TargetWidth = width,
            TargetHeight = height,
            TargetX = 0,
            TargetY = 0,
            R = (ushort)(0x9B * 257),
            G = (ushort)(0x59 * 257),
            B = (ushort)(0xB6 * 257),
            FrameTime = frameTime,
            Effects =
            [
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "Flip",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 1,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Horizontal", true },
                        { "Vertical", false },
                    },
                },
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "Rotation",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 2,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Angle", 8.0f },
                        { "ExpandCanvas", false },
                    },
                },
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "Vignette",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 3,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Strength", 0.4f },
                        { "Radius", 0.7f },
                    },
                },
            ],
        };

        // ────────────────────────────────────────────────────────────────
        //  6. Jitter 位置动画层 (Layer 3) — IClipPositionProvider
        // ────────────────────────────────────────────────────────────────

        private static SolidColorClip JitterClipA(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Jitter Both Axes",
            LayerIndex = 3,
            StartFrame = 20,
            Duration = 130,
            TargetWidth = 200,
            TargetHeight = 120,
            TargetX = width / 2 - 100,
            TargetY = height / 2 - 60,
            R = (ushort)(0xF1 * 257),
            G = (ushort)(0xC4 * 257),
            B = (ushort)(0x0F * 257), // #F1C40F
            A = 0.9f,
            FrameTime = frameTime,
            Effects =
            [
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "Jitter",
                    FromPlugin = PluginId,
                    IsContinuousEffect = true,
                    Enabled = true,
                    Index = 1,
                    Parameters = new Dictionary<string, object>
                    {
                        { "MaxOffsetX", 15 },
                        { "MaxOffsetY", 10 },
                        { "Seed", 42 },
                        { "Direction", "Both" },
                    },
                },
            ],
        };

        private static SolidColorClip JitterClipB(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Jitter X Only",
            LayerIndex = 3,
            StartFrame = 170,
            Duration = 130,
            TargetWidth = 200,
            TargetHeight = 120,
            TargetX = width / 2 - 100,
            TargetY = height / 2 - 60,
            R = (ushort)(0x1A * 257),
            G = (ushort)(0xBC * 257),
            B = (ushort)(0x9C * 257), // #1ABC9C
            A = 0.9f,
            FrameTime = frameTime,
            Effects =
            [
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "Jitter",
                    FromPlugin = PluginId,
                    IsContinuousEffect = true,
                    Enabled = true,
                    Index = 1,
                    Parameters = new Dictionary<string, object>
                    {
                        { "MaxOffsetX", 25 },
                        { "MaxOffsetY", 0 },
                        { "Seed", 99 },
                        { "Direction", "XOnly" },
                    },
                },
            ],
        };

        // ────────────────────────────────────────────────────────────────
        //  7. 文字层 (Layer 4) — 多段文字叠加，部分带效果
        // ────────────────────────────────────────────────────────────────

        private static TextClip TitleOverlay(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Title Overlay",
            LayerIndex = 4,
            StartFrame = 0,
            Duration = 50,
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
                    FontSize = 90,
                    X = width / 2f,
                    Y = height / 2f - 60,
                    Alignment = TextAlignment.Center,
                    FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535),
                    FillB = (ushort)(1f * 65535), FillA = 1f,
                },
                new TextEntry
                {
                    Text = "Render Benchmark",
                    FontName = string.Empty,
                    FontSize = 40,
                    X = width / 2f,
                    Y = height / 2f + 20,
                    Alignment = TextAlignment.Center,
                    FillR = (ushort)(0.85f * 65535), FillG = (ushort)(0.85f * 65535),
                    FillB = (ushort)(0.85f * 65535), FillA = 0.85f,
                },
                new TextEntry
                {
                    Text = "v5.0 — Performance Test Suite",
                    FontName = string.Empty,
                    FontSize = 22,
                    X = width / 2f,
                    Y = height / 2f + 75,
                    Alignment = TextAlignment.Center,
                    FillR = (ushort)(0.6f * 65535), FillG = (ushort)(0.6f * 65535),
                    FillB = (ushort)(0.6f * 65535), FillA = 0.5f,
                },
            ],
            Effects =
            [
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "FadeOpacity",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 1,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Opacity", 0.95f },
                    },
                },
            ],
        };

        private static TextClip Scene2Overlay(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Scene 2 Overlay",
            LayerIndex = 4,
            StartFrame = 60,
            Duration = 80,
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
                    FontSize = 72,
                    X = width / 2f,
                    Y = height / 2f - 40,
                    Alignment = TextAlignment.Center,
                    FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535),
                    FillB = (ushort)(1f * 65535), FillA = 1f,
                },
                new TextEntry
                {
                    Text = "Measuring render throughput...",
                    FontName = string.Empty,
                    FontSize = 30,
                    X = width / 2f,
                    Y = height / 2f + 30,
                    Alignment = TextAlignment.Center,
                    FillR = (ushort)(0.7f * 65535), FillG = (ushort)(0.7f * 65535),
                    FillB = (ushort)(0.7f * 65535), FillA = 0.6f,
                },
                new TextEntry
                {
                    Text = "1920×1080  |  30 fps  |  Multi-Layer",
                    FontName = string.Empty,
                    FontSize = 20,
                    X = width / 2f,
                    Y = height / 2f + 80,
                    Alignment = TextAlignment.Center,
                    FillR = (ushort)(0.5f * 65535), FillG = (ushort)(0.5f * 65535),
                    FillB = (ushort)(0.5f * 65535), FillA = 0.4f,
                },
            ],
            Effects =
            [
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "FadeOpacity",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 1,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Opacity", 0.8f },
                    },
                },
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "ColorAdjustment",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 2,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Brightness", 0.9f },
                        { "Contrast", 1.2f },
                        { "Saturation", 1.1f },
                        { "Hue", 0f },
                        { "Gamma", 0.95f },
                        { "Vibrance", 0f },
                        { "Temperature", 0f },
                        { "Invert", false },
                        { "Grayscale", 0f },
                        { "Opacity", 1.0f },
                    },
                },
            ],
        };

        private static TextClip Scene3Overlay(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Scene 3 Overlay",
            LayerIndex = 4,
            StartFrame = 150,
            Duration = 70,
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
                    FontSize = 80,
                    X = width / 2f,
                    Y = height / 2f - 60,
                    Alignment = TextAlignment.Center,
                    FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535),
                    FillB = (ushort)(1f * 65535), FillA = 1f,
                },
                new TextEntry
                {
                    Text = "Benchmark Complete",
                    FontName = string.Empty,
                    FontSize = 36,
                    X = width / 2f,
                    Y = height / 2f + 20,
                    Alignment = TextAlignment.Center,
                    FillR = (ushort)(0.8f * 65535), FillG = (ushort)(0.8f * 65535),
                    FillB = (ushort)(0.8f * 65535), FillA = 0.7f,
                },
            ],
            Effects =
            [
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "ZoomIn",
                    FromPlugin = PluginId,
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
        };

        private static TextClip TextFadeInOverlay(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "TextFadeIn Demo",
            LayerIndex = 4,
            StartFrame = 230,
            Duration = 70,
            TargetWidth = width,
            TargetHeight = height,
            TargetX = 0,
            TargetY = 0,
            FrameTime = frameTime,
            TextEntries =
            [
                new TextEntry
                {
                    Text = "Text Effects",
                    FontName = string.Empty,
                    FontSize = 68,
                    X = width / 2f,
                    Y = height / 2f - 50,
                    Alignment = TextAlignment.Center,
                    FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535),
                    FillB = (ushort)(1f * 65535), FillA = 1f,
                },
                new TextEntry
                {
                    Text = "Fade In + Color Adjustment",
                    FontName = string.Empty,
                    FontSize = 28,
                    X = width / 2f,
                    Y = height / 2f + 30,
                    Alignment = TextAlignment.Center,
                    FillR = (ushort)(0.85f * 65535), FillG = (ushort)(0.85f * 65535),
                    FillB = (ushort)(0.65f * 65535), FillA = 0.8f,
                },
            ],
            Effects =
            [
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "TextFadeIn",
                    FromPlugin = PluginId,
                    IsContinuousEffect = true,
                    Enabled = true,
                    Index = 1,
                    Parameters = new Dictionary<string, object>(),
                },
                new EffectAndMixtureJSONStructure
                {
                    TypeName = "FadeOpacity",
                    FromPlugin = PluginId,
                    Enabled = true,
                    Index = 2,
                    Parameters = new Dictionary<string, object>
                    {
                        { "Opacity", 0.9f },
                    },
                },
            ],
        };

        private static TextClip MultiColumnText(int width, int height, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Multi-Column Text",
            LayerIndex = 4,
            StartFrame = 145,
            Duration = 80,
            TargetWidth = width,
            TargetHeight = height,
            TargetX = 0,
            TargetY = 0,
            FrameTime = frameTime,
            TextEntries =
            [
                new TextEntry
                {
                    Text = "Layer 0: Color Backgrounds",
                    FontName = string.Empty,
                    FontSize = 24,
                    X = 80,
                    Y = 180,
                    Alignment = TextAlignment.Left,
                    FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535),
                    FillB = (ushort)(1f * 65535), FillA = 0.7f,
                },
                new TextEntry
                {
                    Text = "Layer 1: Single Effects",
                    FontName = string.Empty,
                    FontSize = 24,
                    X = 80,
                    Y = 260,
                    Alignment = TextAlignment.Left,
                    FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535),
                    FillB = (ushort)(1f * 65535), FillA = 0.7f,
                },
                new TextEntry
                {
                    Text = "Layer 2: Effect Chains",
                    FontName = string.Empty,
                    FontSize = 24,
                    X = 80,
                    Y = 340,
                    Alignment = TextAlignment.Left,
                    FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535),
                    FillB = (ushort)(1f * 65535), FillA = 0.7f,
                },
                new TextEntry
                {
                    Text = "Layer 3: Position Animation",
                    FontName = string.Empty,
                    FontSize = 24,
                    X = 80,
                    Y = 420,
                    Alignment = TextAlignment.Left,
                    FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535),
                    FillB = (ushort)(1f * 65535), FillA = 0.7f,
                },
                new TextEntry
                {
                    Text = "Layer 4: Text + Typography",
                    FontName = string.Empty,
                    FontSize = 24,
                    X = 80,
                    Y = 500,
                    Alignment = TextAlignment.Left,
                    FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535),
                    FillB = (ushort)(1f * 65535), FillA = 0.7f,
                },
                new TextEntry
                {
                    Text = "Layer 5: HUD / Overlay",
                    FontName = string.Empty,
                    FontSize = 24,
                    X = 80,
                    Y = 580,
                    Alignment = TextAlignment.Left,
                    FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535),
                    FillB = (ushort)(1f * 65535), FillA = 0.7f,
                },
            ],
        };

        // ────────────────────────────────────────────────────────────────
        //  8. HUD / 装饰层 (Layer 5) — 持续显示的元素
        // ────────────────────────────────────────────────────────────────

        private static TextClip HudInfo(int width, int height, uint totalFrames, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "HUD Info",
            LayerIndex = 5,
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
                    Text = "FrameCut Benchmark v5.0  |  1920×1080  |  30fps  |  6 Layers",
                    FontName = string.Empty,
                    FontSize = 16,
                    X = 20,
                    Y = height - 30,
                    Alignment = TextAlignment.Left,
                    FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535),
                    FillB = (ushort)(1f * 65535), FillA = 0.4f,
                },
            ],
        };

        private static SolidColorClip TopDivider(int width, int height, uint totalFrames, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Top Divider",
            LayerIndex = 5,
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
        };

        private static SolidColorClip BottomDivider(int width, int height, uint totalFrames, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Bottom Divider",
            LayerIndex = 5,
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
        };

        private static TextClip FrameCounter(int width, int height, uint totalFrames, float frameTime) => new()
        {
            Id = Guid.NewGuid(),
            Name = "Frame Counter",
            LayerIndex = 5,
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
                    FillR = (ushort)(1f * 65535), FillG = (ushort)(1f * 65535),
                    FillB = (ushort)(1f * 65535), FillA = 0.35f,
                },
            ],
        };
    }
}
