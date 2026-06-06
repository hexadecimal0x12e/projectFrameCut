using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Effect;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Shared
{
    public static class PictureProcesser
    {
        public static bool SaveDiagResult = false;
        public static string DiagResultPath = null!;

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static HDRPicture16bpp ToHDRPictureBySignal(this IPicture source, int maximumBrightness = 203)
        {
            var s = source.ToBitPerPixel(16) as IPicture<ushort>;
            if (s is null) throw new InvalidCastException($"Could not cast source to IPicture<ushort>");

            int validMaximumBrightness = Math.Clamp(maximumBrightness, 100, 10000);
            var brightness = new float[s.Pixels];

            for (int i = 0; i < s.Pixels; i++)
            {
                float r = s.r[i] / 65535f;
                float g = s.g[i] / 65535f;
                float b = s.b[i] / 65535f;
                float luma = 0.2627f * r + 0.6780f * g + 0.0593f * b;
                brightness[i] = float.IsFinite(luma) ? Math.Clamp(luma, 0f, 1f) : 0f;
            }

            return new HDRPicture16bpp(s, false)
            {
                r = s.r,
                g = s.g,
                b = s.b,
                a = s.a,
                HasAlphaChannel = s.HasAlphaChannel && s.a is not null,
                Brightness = brightness,
                MaximumBrightness = validMaximumBrightness,
            };
        }
    }
}
