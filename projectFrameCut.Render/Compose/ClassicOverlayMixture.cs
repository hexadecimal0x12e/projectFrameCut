using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.Compose
{
    public class ClassicOverlayMixture : IMixture
    {
        public bool IsDeterministic => true;

        public static bool EnableApproximatePath { get; set; } = true;
        public static bool CaptureStackTraces { get; set; }
        public static IMixture Default => EffectRuntimeDefaults.Mixture;

        private IPicture ResizeTop<TPicture>(TPicture source, int width, int height) where TPicture : IPicture => new EffectPictureResizer(ImplementType).ResizePicture(source, width, height);
        public virtual EffectImplementType ImplementType => EffectImplementType.IPicture;
        public string TypeName => "ClassicOverlayMixture";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool IsReorderable => true;
        public string Name { get; set; }
        public string Id { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public string? BindedEffectProvidingSystemID { get; set; }

        protected bool UseApproximatePath => Parameters?.GetValueOrDefault("AccuracyMode")?.ToString() switch
        {
            "Accurate" => false,
            "Approximate" => true,
            _ => EnableApproximatePath,
        };

        public virtual IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB) => MixInternal(basePicture, topPicture, targetPPB, true, 0, 0, basePicture.Width, basePicture.Height);
        public virtual IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB, int topStartX, int topStartY, int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
            {
                throw new ArgumentException("targetWidth and targetHeight must be positive.");
            }

            return MixInternal(basePicture, topPicture, targetPPB, false, topStartX, topStartY, targetWidth, targetHeight);
        }

        protected IPicture MixInternal<TPicture>(TPicture basePicture, TPicture topPicture, IPicture.PicturePixelMode targetPPB, bool resizeTopWhenDimensionMismatch, int topStartX, int topStartY, int targetWidth, int targetHeight) where TPicture : IPicture
        {
            if (targetWidth <= 0 || targetHeight <= 0)
                throw new ArgumentException("targetWidth and targetHeight must be positive.");

            long started = Stopwatch.GetTimestamp();
            var step = new OverlayedPictureProcessStack
            {
                BaseSteps = basePicture.ProcessStack,
                TopSteps = topPicture.ProcessStack,
                OperationDisplayName = "Overlay effect",
                Operator = GetType(),
                ProcessingFuncStackTrace = CaptureStackTraces ? new(true) : null,
            };
            IPicture? resizedTop = null;
            try
            {
                if (resizeTopWhenDimensionMismatch && (topPicture.Width != targetWidth || topPicture.Height != targetHeight))
                {
                    resizedTop = ResizeTop(topPicture, targetWidth, targetHeight);
                    topPicture = (TPicture)resizedTop;
                    topStartX = topStartY = 0;
                }
                bool custom = GetType() != typeof(ClassicOverlayMixture) && GetType() != typeof(ClassicOverlayMixture<TPicture>);
                PictureCompositor.ChannelBlend? blend = custom ? ((int)targetPPB == 8 ? Blend8 : Blend16) : null;
                var result = PictureCompositor.Mix(basePicture, topPicture, targetPPB, topStartX, topStartY,
                    targetWidth, targetHeight, CompositionMode.Normal, blend, custom ? BlendBrightness : null);
                result.ProcessStack = [step];
                step.Elapsed = Stopwatch.GetElapsedTime(started);
                return result;
            }
            finally
            {
                try
                {
                    resizedTop?.Dispose();
                }
                catch (Exception ex)
                {
                    Log(ex, "Dispose resized composition input", this);
                }
            }
        }

        private (Array Color, Half[] Alpha) Blend8(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int count)
        {
            var result = UseApproximatePath ? ApproximateOverlay8(top, bottom, topAlpha, bottomAlpha, count)
                : Overlay8(top, bottom, topAlpha, bottomAlpha, count);
            return (result.Color, result.Alpha);
        }

        private (Array Color, Half[] Alpha) Blend16(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int count)
        {
            var result = UseApproximatePath ? ApproximateOverlay16(top, bottom, topAlpha, bottomAlpha, count)
                : Overlay16(top, bottom, topAlpha, bottomAlpha, count);
            return (result.Color, result.Alpha);
        }

        private (Half[] Color, Half[] Alpha) BlendBrightness(Half[] top, Half[] bottom, Half[] topAlpha, Half[] bottomAlpha, int count)
        {
            var result = UseApproximatePath ? ApproximateOverlayHdr(top, bottom, topAlpha, bottomAlpha, count)
                : OverlayHdr(top, bottom, topAlpha, bottomAlpha, count);
            return (result.Color, result.Alpha);
        }

        public virtual IEffect WithPictureType<TPicture>() where TPicture : IPicture =>
            ImplementType == EffectImplementType.IPicture ? new ClassicOverlayMixture<TPicture>() : TypedPictureEffect.Create<TPicture>(this);

        public virtual IEffect WithParameters(Dictionary<string, object> parameters) => new ClassicOverlayMixture
        {
            Parameters = parameters
        };
        protected virtual (byte[] Color, Half[] Alpha) Overlay8(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount) =>
            ComputeOverlay8(top, bottom, topAlpha, bottomAlpha, pixelCount);

        private static (byte[] Color, Half[] Alpha) ComputeOverlay8(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount)
        {
            var colors = new byte[pixelCount];
            var alpha = new Half[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                Half weight = bottomAlpha[i] * (Half.One - topAlpha[i]), a = topAlpha[i] + weight;
                alpha[i] = a < (Half)1e-6f ? Half.Zero : a;
                colors[i] = a < (Half)1e-6f ? (byte)0 : (byte)Math.Clamp((top[i] * (float)topAlpha[i] + bottom[i] * (float)weight) / (float)a / 257f, 0, 255);
            }
            return (colors, alpha);
        }

        protected virtual (ushort[] Color, Half[] Alpha) Overlay16(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount) =>
            ComputeOverlay16(top, bottom, topAlpha, bottomAlpha, pixelCount);

        private static (ushort[] Color, Half[] Alpha) ComputeOverlay16(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount)
        {
            var colors = new ushort[pixelCount];
            var alpha = new Half[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                Half weight = bottomAlpha[i] * (Half.One - topAlpha[i]), a = topAlpha[i] + weight;
                alpha[i] = a < (Half)1e-6f ? Half.Zero : a;
                colors[i] = a < (Half)1e-6f ? (ushort)0 : (ushort)Math.Clamp((top[i] * (float)topAlpha[i] + bottom[i] * (float)weight) / (float)a, 0, 65535);
            }
            return (colors, alpha);
        }

        protected virtual (Half[] Color, Half[] Alpha) OverlayHdr(Half[] top, Half[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount) =>
            ComputeOverlayHdr(top, bottom, topAlpha, bottomAlpha, pixelCount);

        private static (Half[] Color, Half[] Alpha) ComputeOverlayHdr(Half[] top, Half[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount)
        {
            var colors = new Half[pixelCount];
            var alpha = new Half[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                alpha[i] = HalfChannelMath.Overlay(topAlpha[i], bottomAlpha[i]);
                colors[i] = HalfChannelMath.Blend(top[i], bottom[i], topAlpha[i], bottomAlpha[i]);
            }
            return (colors, alpha);
        }

        protected virtual (byte[] Color, Half[] Alpha) ApproximateOverlay8(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount) =>
            ComputeOverlay8(top, bottom, topAlpha, bottomAlpha, pixelCount);

        protected virtual (ushort[] Color, Half[] Alpha) ApproximateOverlay16(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount) =>
            ComputeOverlay16(top, bottom, topAlpha, bottomAlpha, pixelCount);

        protected virtual (Half[] Color, Half[] Alpha) ApproximateOverlayHdr(Half[] top, Half[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount) =>
            ComputeOverlayHdr(top, bottom, topAlpha, bottomAlpha, pixelCount);
    }

    /// <summary>
    /// The Render-side provider of the ClassicOverlayMixture.
    /// </summary>
    public class ClassicOverlayMixtureProvider : MixtureProviderBase
    {
        public override IReadOnlyList<Stage> SupportedStages => [Stage.Mixture];

        public override bool IsDeterministic => true;

        public ClassicOverlayMixtureProvider()
        {
            Name = "Classic Overlay";
            SetField("AccuracyMode", "Default");
        }

        public override string TypeName => "ClassicOverlayMixture";
        public override EffectType TypeOfEffect => EffectType.MixtureProvider;
        public override EffectTarget Target => EffectTarget.Mixture;
        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return [Field("AccuracyMode", EffectArgumentFieldType.String | EffectArgumentFieldType.CannotBeDynamic,
                "Default", presetOptions: ["Default", "Accurate", "Approximate"])];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.HwAcceleration, EffectImplementType.IPicture];
    }
}
