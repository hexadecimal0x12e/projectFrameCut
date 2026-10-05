using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;

namespace projectFrameCut.Render.Transform
{
    /// <summary>
    /// A very simple crossfade transform that linearly blends the last frame of the previous clip
    /// and the first frame of the next clip according to progress (0..1).
    /// This is a minimal implementation for basic transition preview/testing.
    /// </summary>
    public class CrossfadeTransform : IContinuousTransform
    {
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";

        public TransformDefinition Definition => TransformDefinition.Clip | TransformDefinition.SupportTwoInput;
        public TransformSide Side { get; set; }

        public string TypeName => "Crossfade";

        public string Name { get; init; } = "Crossfade";

        public Guid PreviousClipId { get; init; }

        public Guid NextClipId { get; init; }


        public Dictionary<string, object> Parameters { get; set; } = new();

        public List<string> ParametersNeeded => new();

        public Dictionary<string, string> ParametersType => new();

        public Guid BindedLeftClip { get; set; }
        public Guid BindedRightClip { get; set; }
        public uint Duration { get; set; }

        public void Init() { }

        /// <summary>
        /// progress: 0.0 => fully previous, 1.0 => fully next
        /// </summary>
        public IPicture GetFrame(IPicture prevPic, IPicture nextPic, double progress, int targetWidth, int targetHeight)
        {
            float p = (float)Math.Clamp(progress, 0, 1);
            if (prevPic.Width != nextPic.Width || prevPic.Height != nextPic.Height)
                throw new ArgumentException("Crossfade input sizes must match.");
            if (prevPic.BitPerPixel == 16 || nextPic.BitPerPixel == 16)
            {
                var left = (IPicture<ushort>)prevPic.ToBitPerPixel(16);
                var right = (IPicture<ushort>)nextPic.ToBitPerPixel(16);
                var lh = left as IHDRPicture<ushort>;
                var rh = right as IHDRPicture<ushort>;
                float peak = Math.Max(lh?.MaximumBrightness ?? 203, rh?.MaximumBrightness ?? 203);
                Picture16bpp output = lh is not null || rh is not null
                    ? new HDRPicture16bpp(left.Width, left.Height) { MaximumBrightness = peak }
                    : new Picture16bpp(left.Width, left.Height);
                output.a = new float[output.Pixels];
                output.HasAlphaChannel = true;
                for (int i = 0; i < output.Pixels; i++)
                {
                    float a = (left.a?[i] ?? 1) * (1 - p);
                    float b = (right.a?[i] ?? 1) * p;
                    float alpha = a + b;
                    output.a[i] = alpha;
                    output.r[i] = alpha > 0 ? (ushort)Math.Clamp(Math.Round((left.r[i] * a + right.r[i] * b) / alpha), 0, ushort.MaxValue) : (ushort)0;
                    output.g[i] = alpha > 0 ? (ushort)Math.Clamp(Math.Round((left.g[i] * a + right.g[i] * b) / alpha), 0, ushort.MaxValue) : (ushort)0;
                    output.b[i] = alpha > 0 ? (ushort)Math.Clamp(Math.Round((left.b[i] * a + right.b[i] * b) / alpha), 0, ushort.MaxValue) : (ushort)0;
                    if (output is IHDRPicture<ushort> hdr)
                    {
                        float l = lh is not null && i < lh.Brightness.Length ? lh.Brightness[i] * lh.MaximumBrightness : 203;
                        float r = rh is not null && i < rh.Brightness.Length ? rh.Brightness[i] * rh.MaximumBrightness : 203;
                        hdr.Brightness[i] = alpha > 0 ? Math.Clamp((l * a + r * b) / (alpha * peak), 0, 1) : 0;
                    }
                }
                if (!ReferenceEquals(left, prevPic)) left.Dispose();
                if (!ReferenceEquals(right, nextPic)) right.Dispose();
                return output;
            }
            else
            {
                var left = (IPicture<byte>)prevPic;
                var right = (IPicture<byte>)nextPic;
                var output = new Picture8bpp(left.Width, left.Height) { a = new float[left.Pixels], HasAlphaChannel = true };
                for (int i = 0; i < output.Pixels; i++)
                {
                    float a = (left.a?[i] ?? 1) * (1 - p);
                    float b = (right.a?[i] ?? 1) * p;
                    float alpha = a + b;
                    output.a[i] = alpha;
                    output.r[i] = alpha > 0 ? (byte)Math.Clamp(Math.Round((left.r[i] * a + right.r[i] * b) / alpha), 0, byte.MaxValue) : (byte)0;
                    output.g[i] = alpha > 0 ? (byte)Math.Clamp(Math.Round((left.g[i] * a + right.g[i] * b) / alpha), 0, byte.MaxValue) : (byte)0;
                    output.b[i] = alpha > 0 ? (byte)Math.Clamp(Math.Round((left.b[i] * a + right.b[i] * b) / alpha), 0, byte.MaxValue) : (byte)0;
                }
                return output;
            }
        }
    }
}
