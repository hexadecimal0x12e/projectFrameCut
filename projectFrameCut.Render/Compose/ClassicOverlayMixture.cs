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

        private IPicture ResizeTop(IPicture source, int width, int height) => new EffectPictureResizer(ImplementType).ResizePicture(source, width, height);
        public virtual EffectImplementType ImplementType => EffectImplementType.IPicture;
        public string TypeName => "ClassicOverlayMixture";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool IsReorderable => true;
        public string Name { get; set; }
        public string Id { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public string? BindedEffectProvidingSystemID { get; set; }

        public virtual IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB) => MixInternal(basePicture, topPicture, targetPPB, true, 0, 0, basePicture.Width, basePicture.Height);
        public virtual IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB, int topStartX, int topStartY, int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
            {
                throw new ArgumentException("targetWidth and targetHeight must be positive.");
            }

            return MixInternal(basePicture, topPicture, targetPPB, false, topStartX, topStartY, targetWidth, targetHeight);
        }

        private IPicture MixInternal(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB, bool resizeTopWhenDimensionMismatch, int topStartX, int topStartY, int targetWidth, int targetHeight)
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
                    topPicture = resizedTop = ResizeTop(topPicture, targetWidth, targetHeight);
                    topStartX = topStartY = 0;
                }
                bool custom = GetType() != typeof(ClassicOverlayMixture);
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

        private (Array Color, float[] Alpha) Blend8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int count)
        {
            var result = EnableApproximatePath ? ApproximateOverlay8(top, bottom, topAlpha, bottomAlpha, count)
                : Overlay8(top, bottom, topAlpha, bottomAlpha, count);
            return (result.Color, result.Alpha);
        }

        private (Array Color, float[] Alpha) Blend16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int count)
        {
            var result = EnableApproximatePath ? ApproximateOverlay16(top, bottom, topAlpha, bottomAlpha, count)
                : Overlay16(top, bottom, topAlpha, bottomAlpha, count);
            return (result.Color, result.Alpha);
        }

        private (Array Color, float[] Alpha) BlendBrightness(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int count)
        {
            var result = EnableApproximatePath ? ApproximateOverlayHdr(top, bottom, topAlpha, bottomAlpha, count)
                : OverlayHdr(top, bottom, topAlpha, bottomAlpha, count);
            return (result.Color, result.Alpha);
        }

        public virtual IEffect WithParameters(Dictionary<string, object> parameters) => new ClassicOverlayMixture
        {
            Parameters = parameters
        };
        protected virtual (byte[] Color, float[] Alpha) Overlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new byte[pixelCount];
            var outA = new float[pixelCount];
            OverlayBlend8Scalar(top, bottom, topAlpha, bottomAlpha, outC, outA, pixelCount);
            return (outC, outA);
        }

        protected virtual (ushort[] Color, float[] Alpha) Overlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new float[pixelCount];
            OverlayBlend16Simd(top, bottom, topAlpha, bottomAlpha, outC, outA, pixelCount);
            return (outC, outA);
        }

        protected virtual (float[] Color, float[] Alpha) OverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new float[pixelCount];
            var outA = new float[pixelCount];
            OverlayBlendFloat(top, bottom, topAlpha, bottomAlpha, outC, outA, pixelCount);
            return (outC, outA);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void OverlayBlend8Scalar(float[] a, float[] b, float[] aAlpha, float[] bAlpha, byte[] outC, float[] outA, int pixelCount)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = 0f;
                }
                else
                {
                    float aC = a[i] * aA / outAlpha;
                    float bC = b[i] * bA * (1f - aA) / outAlpha;
                    float outColor = aC + bC;
                    outC[i] = (byte)Math.Clamp(outColor / 257f, 0f, 255f);
                    outA[i] = outAlpha;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void OverlayBlend16Simd(float[] a, float[] b, float[] aAlpha, float[] bAlpha, ushort[] outC, float[] outA, int pixelCount)
        {
            int simdWidth = Vector<float>.Count;
            var one = Vector<float>.One;
            var zero = Vector<float>.Zero;
            var epsilon = new Vector<float>(1e-6f);
            var max16 = new Vector<float>(65535f);
            int i = 0;
            for (; i <= pixelCount - simdWidth; i += simdWidth)
            {
                var vA = new Vector<float>(a, i);
                var vB = new Vector<float>(b, i);
                var vAa = new Vector<float>(aAlpha, i);
                var vBa = new Vector<float>(bAlpha, i);
                var vOutAlpha = vAa + vBa * (one - vAa);
                var vAC = vA * vAa;
                var vBC = vB * vBa * (one - vAa);
                var vOutColor = (vAC + vBC) / vOutAlpha;
                vOutColor = Vector.Min(Vector.Max(vOutColor, zero), max16);
                var mask = Vector.GreaterThanOrEqual(vOutAlpha, epsilon);
                vOutColor = Vector.ConditionalSelect(mask, vOutColor, zero);
                vOutAlpha = Vector.ConditionalSelect(mask, vOutAlpha, zero);
                // Write back: store alpha via CopyTo, convert color via scalar per-element
                vOutAlpha.CopyTo(outA, i);
                for (int j = 0; j < simdWidth; j++)
                    outC[i + j] = (ushort)vOutColor[j];
            }

            // Remainder: scalar fallback
            for (; i < pixelCount; i++)
            {
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = 0f;
                }
                else
                {
                    float aC = a[i] * aA / outAlpha;
                    float bC = b[i] * bA * (1f - aA) / outAlpha;
                    outC[i] = (ushort)Math.Clamp(aC + bC, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void OverlayBlendFloat(float[] a, float[] b, float[] aAlpha, float[] bAlpha, float[] outC, float[] outA, int pixelCount)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f)
                {
                    outC[i] = 0f;
                    outA[i] = 0f;
                }
                else
                {
                    float aC = a[i] * aA / outAlpha;
                    float bC = b[i] * bA * (1f - aA) / outAlpha;
                    outC[i] = Math.Clamp(aC + bC, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }
        }

        protected virtual (byte[] Color, float[] Alpha) ApproximateOverlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new byte[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = 0f;
                }
                else
                {
                    float aC = top[i] * aA / outAlpha, bC = bottom[i] * bA * (1f - aA) / outAlpha;
                    outC[i] = (byte)Math.Clamp((aC + bC) / 257f, 0f, 255f);
                    outA[i] = outAlpha;
                }
            }

            return (outC, outA);
        }

        protected virtual (ushort[] Color, float[] Alpha) ApproximateOverlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = 0f;
                }
                else
                {
                    float aC = top[i] * aA / outAlpha, bC = bottom[i] * bA * (1f - aA) / outAlpha;
                    outC[i] = (ushort)Math.Clamp(aC + bC, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return (outC, outA);
        }

        protected virtual (float[] Color, float[] Alpha) ApproximateOverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new float[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f)
                {
                    outC[i] = 0f;
                    outA[i] = 0f;
                }
                else
                {
                    float aC = top[i] * aA / outAlpha, bC = bottom[i] * bA * (1f - aA) / outAlpha;
                    outC[i] = Math.Clamp(aC + bC, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return (outC, outA);
        }
    }

    /// <summary>
    /// The Render-side provider of the ClassicOverlayMixture.
    /// </summary>
    public class ClassicOverlayMixtureProvider : EffectProviderBase
    {
        public override bool IsDeterministic => true;

        public ClassicOverlayMixtureProvider()
        {
            Name = "Classic Overlay";
            SetField("AccuracyMode", "Accurate");
        }

        public override string TypeName => "ClassicOverlayMixture";
        public override EffectType TypeOfEffect => EffectType.MixtureProvider;
        public override EffectTarget Target => EffectTarget.Mixture;
        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() =>
            new Dictionary<string, EffectArgumentFieldDescriptor>();

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return [Field("AccuracyMode", EffectArgumentFieldType.String | EffectArgumentFieldType.CannotBeDynamic,
                "Accurate", presetOptions: ["Accurate", "Approximate"])];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.HwAcceleration, EffectImplementType.IPicture];
    }
}
