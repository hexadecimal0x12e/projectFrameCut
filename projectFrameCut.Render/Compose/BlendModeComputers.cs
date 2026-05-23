using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;

namespace projectFrameCut.Render.Compose
{
    public class OverlayComputer : IComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "Overlay";

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0];
            var b = (float[])args[1];
            var aAlpha = (float[])args[2];
            var bAlpha = (float[])args[3];
            int bitDepth = Convert.ToInt32(args[4]);
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;

            if (bitDepth == 8)
            {
                var outC = new byte[pixelCount];
                var outA = new float[pixelCount];

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
                        outC[i] = (byte)Math.Clamp(outColor, 0f, 255f);
                        outA[i] = outAlpha;
                    }
                }

                return [outC, outA];
            }
            else // 16-bit
            {
                var outC = new ushort[pixelCount];
                var outA = new float[pixelCount];

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
                        outC[i] = (ushort)Math.Clamp(outColor, 0f, 65535f);
                        outA[i] = outAlpha;
                    }
                }

                return [outC, outA];
            }
        }
    }

    public class ApproximateComputer : IComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "OverlayApproximate";

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0];
            var b = (float[])args[1];
            var aAlpha = (float[])args[2];
            var bAlpha = (float[])args[3];
            int bitDepth = Convert.ToInt32(args[4]);
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;

            if (bitDepth == 8)
            {
                var outC = new byte[pixelCount];
                var outA = new float[pixelCount];

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
                        outC[i] = (byte)Math.Clamp(outColor, 0f, 255f);
                        outA[i] = outAlpha;
                    }
                }

                return [outC, outA];
            }
            else // 16-bit
            {
                var outC = new ushort[pixelCount];
                var outA = new float[pixelCount];

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
                        outC[i] = (ushort)Math.Clamp(outColor, 0f, 65535f);
                        outA[i] = outAlpha;
                    }
                }

                return [outC, outA];
            }
        }
    }

    public class AddComputer : IComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "AddMixture";

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0];
            var b = (float[])args[1];
            var aAlpha = (float[])args[2];
            var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;

            var outC = new float[pixelCount];
            var outA = new float[pixelCount];

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
                    float blended = Math.Min(a[i] + b[i], 65535f);
                    float result = (blended * aA + b[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return [outC, outA];
        }
    }

    public class SubtractComputer : IComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "SubtractMixture";

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0];
            var b = (float[])args[1];
            var aAlpha = (float[])args[2];
            var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;

            var outC = new float[pixelCount];
            var outA = new float[pixelCount];

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
                    float blended = Math.Max(b[i] - a[i], 0f);
                    float result = (blended * aA + b[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return [outC, outA];
        }
    }

    public class MultiplyComputer : IComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "MultiplyMixture";

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0];
            var b = (float[])args[1];
            var aAlpha = (float[])args[2];
            var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;

            var outC = new float[pixelCount];
            var outA = new float[pixelCount];

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
                    float blended = a[i] * b[i] / 65535f;
                    float result = (blended * aA + b[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return [outC, outA];
        }
    }

    public class ScreenComputer : IComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "ScreenMixture";

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0];
            var b = (float[])args[1];
            var aAlpha = (float[])args[2];
            var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;

            var outC = new float[pixelCount];
            var outA = new float[pixelCount];

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
                    float blended = 65535f - (65535f - a[i]) * (65535f - b[i]) / 65535f;
                    float result = (blended * aA + b[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return [outC, outA];
        }
    }

    public class OverlayBlendComputer : IComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "OverlayBlendMixture";

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0];
            var b = (float[])args[1];
            var aAlpha = (float[])args[2];
            var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;

            var outC = new float[pixelCount];
            var outA = new float[pixelCount];

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
                    float blended;
                    if (b[i] < 32768f)
                        blended = 2f * a[i] * b[i] / 65535f;
                    else
                        blended = 65535f - 2f * (65535f - a[i]) * (65535f - b[i]) / 65535f;

                    float result = (blended * aA + b[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return [outC, outA];
        }
    }

    public class DarkenComputer : IComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "DarkenMixture";

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0];
            var b = (float[])args[1];
            var aAlpha = (float[])args[2];
            var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;

            var outC = new float[pixelCount];
            var outA = new float[pixelCount];

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
                    float blended = Math.Min(a[i], b[i]);
                    float result = (blended * aA + b[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return [outC, outA];
        }
    }

    public class LightenComputer : IComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "LightenMixture";

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0];
            var b = (float[])args[1];
            var aAlpha = (float[])args[2];
            var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;

            var outC = new float[pixelCount];
            var outA = new float[pixelCount];

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
                    float blended = Math.Max(a[i], b[i]);
                    float result = (blended * aA + b[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return [outC, outA];
        }
    }

    public class DifferenceComputer : IComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "DifferenceMixture";

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0];
            var b = (float[])args[1];
            var aAlpha = (float[])args[2];
            var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;

            var outC = new float[pixelCount];
            var outA = new float[pixelCount];

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
                    float blended = Math.Abs(a[i] - b[i]);
                    float result = (blended * aA + b[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = Math.Clamp(result, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }

            return [outC, outA];
        }
    }
}
