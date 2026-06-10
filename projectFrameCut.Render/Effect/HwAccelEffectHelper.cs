using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.Effect
{
    public static class HwAccelEffectHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static (float[] r, float[] g, float[] b, float[] a, bool sourceHasAlpha) ExtractFloatChannels(IPicture source)
        {
            if (source is IPicture<ushort> p16)
            {
                int pixels = p16.Pixels;
                var rOut = GC.AllocateUninitializedArray<float>(pixels);
                var gOut = GC.AllocateUninitializedArray<float>(pixels);
                var bOut = GC.AllocateUninitializedArray<float>(pixels);
                var aOut = GC.AllocateUninitializedArray<float>(pixels);

                var srcR = p16.r;
                var srcG = p16.g;
                var srcB = p16.b;
                for (int i = 0; i < pixels; i++)
                {
                    rOut[i] = srcR[i];
                    gOut[i] = srcG[i];
                    bOut[i] = srcB[i];
                }

                bool hasAlpha = p16.HasAlphaChannel && p16.a is not null;
                if (hasAlpha)
                {
                    var srcA = p16.a;
                    for (int i = 0; i < pixels; i++)
                        aOut[i] = srcA[i];
                }
                else
                {
                    aOut.AsSpan().Fill(1f);
                }

                return (rOut, gOut, bOut, aOut, hasAlpha);
            }

            if (source is IPicture<byte> p8)
            {
                int pixels = p8.Pixels;
                var rOut = GC.AllocateUninitializedArray<float>(pixels);
                var gOut = GC.AllocateUninitializedArray<float>(pixels);
                var bOut = GC.AllocateUninitializedArray<float>(pixels);
                var aOut = GC.AllocateUninitializedArray<float>(pixels);

                var srcR = p8.r;
                var srcG = p8.g;
                var srcB = p8.b;
                for (int i = 0; i < pixels; i++)
                {
                    rOut[i] = srcR[i];
                    gOut[i] = srcG[i];
                    bOut[i] = srcB[i];
                }

                bool hasAlpha = p8.HasAlphaChannel && p8.a is not null;
                if (hasAlpha)
                {
                    var srcA = p8.a;
                    for (int i = 0; i < pixels; i++)
                        aOut[i] = srcA[i];
                }
                else
                {
                    aOut.AsSpan().Fill(1f);
                }

                return (rOut, gOut, bOut, aOut, hasAlpha);
            }

            throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static IPicture BuildPicture(IPicture source, int width, int height, float[] r, float[] g, float[] b, float[] a, bool keepAlpha)
        {
            int pixels = width * height;

            if (source.BitPerPixel == 16)
            {
                var picture = new Picture16bpp(width, height)
                {
                    Tag = source.Tag,
                    HasAlphaChannel = keepAlpha
                };
                var rOut = GC.AllocateUninitializedArray<ushort>(pixels);
                var gOut = GC.AllocateUninitializedArray<ushort>(pixels);
                var bOut = GC.AllocateUninitializedArray<ushort>(pixels);
                for (int i = 0; i < pixels; i++)
                {
                    rOut[i] = (ushort)Math.Clamp(r[i], 0f, 65535f);
                    gOut[i] = (ushort)Math.Clamp(g[i], 0f, 65535f);
                    bOut[i] = (ushort)Math.Clamp(b[i], 0f, 65535f);
                }
                picture.r = rOut;
                picture.g = gOut;
                picture.b = bOut;

                if (keepAlpha)
                {
                    var aOut = GC.AllocateUninitializedArray<float>(pixels);
                    for (int i = 0; i < pixels; i++)
                        aOut[i] = Math.Clamp(a[i], 0f, 1f);
                    picture.a = aOut;
                }
                return picture;
            }

            if (source.BitPerPixel == 8)
            {
                var picture = new Picture8bpp(width, height)
                {
                    Tag = source.Tag,
                    HasAlphaChannel = keepAlpha
                };
                var rOut = GC.AllocateUninitializedArray<byte>(pixels);
                var gOut = GC.AllocateUninitializedArray<byte>(pixels);
                var bOut = GC.AllocateUninitializedArray<byte>(pixels);
                for (int i = 0; i < pixels; i++)
                {
                    rOut[i] = (byte)Math.Clamp(r[i], 0f, 255f);
                    gOut[i] = (byte)Math.Clamp(g[i], 0f, 255f);
                    bOut[i] = (byte)Math.Clamp(b[i], 0f, 255f);
                }
                picture.r = rOut;
                picture.g = gOut;
                picture.b = bOut;

                if (keepAlpha)
                {
                    var aOut = GC.AllocateUninitializedArray<float>(pixels);
                    for (int i = 0; i < pixels; i++)
                        aOut[i] = Math.Clamp(a[i], 0f, 1f);
                    picture.a = aOut;
                }
                return picture;
            }

            throw new NotSupportedException($"Specific pixel-mode is not supported.");
        }
    }
}
