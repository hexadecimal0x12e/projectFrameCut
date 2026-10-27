using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace projectFrameCut.Render.Compose
{
    public abstract class BlendModeMixtureBase : IMixture
    {
        public virtual bool IsDeterministic => true;

        public abstract string TypeName { get; }

        protected abstract (ushort[] Color, Half[] Alpha) ComputeBlend(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount);
        private IPicture ResizeTop<TPicture>(TPicture source, int width, int height) where TPicture : IPicture => new EffectPictureResizer(ImplementType).ResizePicture(source, width, height);
        public virtual EffectImplementType ImplementType => EffectImplementType.IPicture;
        public bool IsReorderable => true;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string Name { get; set; }
        public string Id { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public string? BindedEffectProvidingSystemID { get; set; }

        public virtual IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB) => MixInternal(basePicture, topPicture, targetPPB, resizeTopWhenDimensionMismatch: true, topStartX: 0, topStartY: 0, targetWidth: basePicture.Width, targetHeight: basePicture.Height);
        public virtual IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB, int topStartX, int topStartY, int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
                throw new ArgumentException("targetWidth and targetHeight must be positive.");
            return MixInternal(basePicture, topPicture, targetPPB, resizeTopWhenDimensionMismatch: false, topStartX, topStartY, targetWidth, targetHeight);
        }

        public virtual IEffect WithPictureType<TPicture>() where TPicture : IPicture =>
            ImplementType != EffectImplementType.IPicture ? TypedPictureEffect.Create<TPicture>(this) : TypeName switch
            {
                "AddMixture" => new AddMixture<TPicture>(),
                "SubtractMixture" => new SubtractMixture<TPicture>(),
                "MultiplyMixture" => new MultiplyMixture<TPicture>(),
                "ScreenMixture" => new ScreenMixture<TPicture>(),
                "OverlayBlendMixture" => new OverlayBlendMixture<TPicture>(),
                "DarkenMixture" => new DarkenMixture<TPicture>(),
                "LightenMixture" => new LightenMixture<TPicture>(),
                "DifferenceMixture" => new DifferenceMixture<TPicture>(),
                _ => this,
            };

        public virtual IEffect WithParameters(Dictionary<string, object> parameters)
        {
            BlendModeMixtureBase effect = TypeName switch
            {
                "AddMixture" => new AddMixture(),
                "SubtractMixture" => new SubtractMixture(),
                "MultiplyMixture" => new MultiplyMixture(),
                "ScreenMixture" => new ScreenMixture(),
                "OverlayBlendMixture" => new OverlayBlendMixture(),
                "DarkenMixture" => new DarkenMixture(),
                "LightenMixture" => new LightenMixture(),
                "DifferenceMixture" => new DifferenceMixture(),
                _ => throw new NotSupportedException($"Unsupported mixture '{TypeName}'.")
            };
            effect.Parameters = parameters;
            return effect;
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
                OperationDisplayName = "Blend mode effect",
                Operator = GetType(),
                ProcessingFuncStackTrace = ClassicOverlayMixture.CaptureStackTraces ? new(true) : null,
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
                var type = GetType();
                CompositionMode? mode = (type == typeof(AddMixture) || type == typeof(AddMixture<TPicture>)) ? CompositionMode.Add
                    : (type == typeof(SubtractMixture) || type == typeof(SubtractMixture<TPicture>)) ? CompositionMode.Subtract
                    : (type == typeof(MultiplyMixture) || type == typeof(MultiplyMixture<TPicture>)) ? CompositionMode.Multiply
                    : (type == typeof(ScreenMixture) || type == typeof(ScreenMixture<TPicture>)) ? CompositionMode.Screen
                    : (type == typeof(OverlayBlendMixture) || type == typeof(OverlayBlendMixture<TPicture>)) ? CompositionMode.Overlay
                    : (type == typeof(DarkenMixture) || type == typeof(DarkenMixture<TPicture>)) ? CompositionMode.Darken
                    : (type == typeof(LightenMixture) || type == typeof(LightenMixture<TPicture>)) ? CompositionMode.Lighten
                    : (type == typeof(DifferenceMixture) || type == typeof(DifferenceMixture<TPicture>)) ? CompositionMode.Difference : null;
                var result = PictureCompositor.Mix(basePicture, topPicture, targetPPB, topStartX, topStartY,
                    targetWidth, targetHeight, mode ?? CompositionMode.Add, mode is null ? BlendChannels : null);
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

        private (Array Color, Half[] Alpha) BlendChannels(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int count)
        {
            var result = ComputeBlend(top, bottom, topAlpha, bottomAlpha, count);
            return (result.Color, result.Alpha);
        }
    }

    public class AddMixture : BlendModeMixtureBase
    {
        public override string TypeName => "AddMixture";

        protected override (ushort[] Color, Half[] Alpha) ComputeBlend(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new Half[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                Half aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = HalfChannelMath.Overlay(aA, bA);
                if (outAlpha < (Half)1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = Half.Zero;
                }
                else
                {
                    float blended = Math.Min(top[i] + bottom[i], 65535f);
                    float result = (blended * (float)aA + bottom[i] * (float)(bA * (Half.One - aA))) / (float)outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return (outC, outA);
        }
    }

    public class SubtractMixture : BlendModeMixtureBase
    {
        public override string TypeName => "SubtractMixture";

        protected override (ushort[] Color, Half[] Alpha) ComputeBlend(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new Half[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                Half aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = HalfChannelMath.Overlay(aA, bA);
                if (outAlpha < (Half)1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = Half.Zero;
                }
                else
                {
                    float blended = Math.Max(bottom[i] - top[i], 0f);
                    float result = (blended * (float)aA + bottom[i] * (float)(bA * (Half.One - aA))) / (float)outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return (outC, outA);
        }
    }

    public class MultiplyMixture : BlendModeMixtureBase
    {
        public override string TypeName => "MultiplyMixture";

        protected override (ushort[] Color, Half[] Alpha) ComputeBlend(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new Half[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                Half aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = HalfChannelMath.Overlay(aA, bA);
                if (outAlpha < (Half)1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = Half.Zero;
                }
                else
                {
                    float blended = top[i] * bottom[i] / 65535f;
                    float result = (blended * (float)aA + bottom[i] * (float)(bA * (Half.One - aA))) / (float)outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return (outC, outA);
        }
    }

    public class ScreenMixture : BlendModeMixtureBase
    {
        public override string TypeName => "ScreenMixture";

        protected override (ushort[] Color, Half[] Alpha) ComputeBlend(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new Half[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                Half aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = HalfChannelMath.Overlay(aA, bA);
                if (outAlpha < (Half)1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = Half.Zero;
                }
                else
                {
                    float blended = 65535f - (65535f - top[i]) * (65535f - bottom[i]) / 65535f;
                    float result = (blended * (float)aA + bottom[i] * (float)(bA * (Half.One - aA))) / (float)outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return (outC, outA);
        }
    }

    public class OverlayBlendMixture : BlendModeMixtureBase
    {
        public override string TypeName => "OverlayBlendMixture";

        protected override (ushort[] Color, Half[] Alpha) ComputeBlend(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new Half[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                Half aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = HalfChannelMath.Overlay(aA, bA);
                if (outAlpha < (Half)1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = Half.Zero;
                }
                else
                {
                    float blended;
                    if (bottom[i] < 32768f)
                        blended = 2f * top[i] * bottom[i] / 65535f;
                    else
                        blended = 65535f - 2f * (65535f - top[i]) * (65535f - bottom[i]) / 65535f;
                    float result = (blended * (float)aA + bottom[i] * (float)(bA * (Half.One - aA))) / (float)outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return (outC, outA);
        }
    }

    public class DarkenMixture : BlendModeMixtureBase
    {
        public override string TypeName => "DarkenMixture";

        protected override (ushort[] Color, Half[] Alpha) ComputeBlend(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new Half[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                Half aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = HalfChannelMath.Overlay(aA, bA);
                if (outAlpha < (Half)1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = Half.Zero;
                }
                else
                {
                    float blended = Math.Min(top[i], bottom[i]);
                    float result = (blended * (float)aA + bottom[i] * (float)(bA * (Half.One - aA))) / (float)outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return (outC, outA);
        }
    }

    public class LightenMixture : BlendModeMixtureBase
    {
        public override string TypeName => "LightenMixture";

        protected override (ushort[] Color, Half[] Alpha) ComputeBlend(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new Half[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                Half aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = HalfChannelMath.Overlay(aA, bA);
                if (outAlpha < (Half)1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = Half.Zero;
                }
                else
                {
                    float blended = Math.Max(top[i], bottom[i]);
                    float result = (blended * (float)aA + bottom[i] * (float)(bA * (Half.One - aA))) / (float)outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return (outC, outA);
        }
    }

    public class DifferenceMixture : BlendModeMixtureBase
    {
        public override string TypeName => "DifferenceMixture";

        protected override (ushort[] Color, Half[] Alpha) ComputeBlend(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new Half[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                Half aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = HalfChannelMath.Overlay(aA, bA);
                if (outAlpha < (Half)1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = Half.Zero;
                }
                else
                {
                    float blended = Math.Abs(top[i] - bottom[i]);
                    float result = (blended * (float)aA + bottom[i] * (float)(bA * (Half.One - aA))) / (float)outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return (outC, outA);
        }
    }

    /// <summary>
    /// The Render-side provider of the blend-mode mixtures. Each registered type (AddMixture, SubtractMixture, ...)
    /// is an instance with the corresponding <see cref = "MixtureType"/>, whose <see cref = "TypeName"/> is
    /// <c>MixtureType + "Mixture"</c>. The legacy standalone <c>"BlendModeMixture"</c> type (with a selectable
    /// <c>MixtureType</c> parameter) is represented by an instance with <see cref = "ProviderTypeName"/> set.
    /// </summary>
    public class BlendModeMixtureProvider : MixtureProviderBase
    {
        public override IReadOnlyList<Stage> SupportedStages => [Stage.Mixture];

        public override bool IsDeterministic => true;

        public BlendModeMixtureProvider()
        {
            Name = "Blend Mode";
        }

        public string MixtureType { get; init; } = "Add";
        /// <summary>
        /// When set, overrides <see cref = "TypeName"/> (used for the standalone <c>"BlendModeMixture"</c> type).
        /// </summary>
        public string? ProviderTypeName { get; init; }
        public override string TypeName => ProviderTypeName ?? MixtureType + "Mixture";
        public override EffectType TypeOfEffect => EffectType.MixtureProvider;
        public override EffectTarget Target => EffectTarget.Mixture;
        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => ProviderTypeName is "BlendModeMixture" or "OverlayBlend"
            ? [Field("MixtureType", EffectArgumentFieldType.String | EffectArgumentFieldType.CannotBeDynamic,
                MixtureType, presetOptions: ["Add", "Subtract", "Multiply", "Screen", "OverlayBlend", "Darken", "Lighten", "Difference"])] : [];

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.HwAcceleration, EffectImplementType.IPicture];
    }
}
