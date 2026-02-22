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
    public class CrossfadeTransform : ITransform
    {
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";

        public string TypeName => "Crossfade";

        public string Name { get; init; } = "Crossfade";

        public Guid PreviousClipId { get; init; }

        public Guid NextClipId { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public IClip? Previous { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public IClip? Next { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string? NeedComputer => null;

        public Dictionary<string, object> Parameters { get; set; }

        public List<string> ParametersNeeded => new();

        public Dictionary<string, string> ParametersType => new();

        public void Init() { }

        /// <summary>
        /// progress: 0.0 => fully previous, 1.0 => fully next
        /// </summary>
        public IPicture GetFrame(double progress, IComputer? computer, int targetWidth, int targetHeight)
        {
            if (Previous is null) throw new ArgumentNullException(nameof(Previous));
            if (Next is null) throw new ArgumentNullException(nameof(Next));

            progress = Math.Clamp(progress, 0.0, 1.0);

            // sample a small moving window at the end of previous and the start of next
            // Using only the single last/first frame freezes motion during transition.
            int defaultWindow = 8;
            int window = 1;
            try
            {
                int prevDur = (int)Previous.Duration;
                int nextDur = (int)Next.Duration;
                window = Math.Max(1, Math.Min(defaultWindow, Math.Min(prevDur, nextDur)));
            }
            catch { window = 1; }

            int prevStart = Math.Max(0, (int)Previous.Duration - window);
            int prevIndex = prevStart + (int)Math.Round(progress * (window - 1));
            int nextIndex = (int)Math.Round(progress * (window - 1));

            IPicture prevPic = Previous.GetFrameRelativeToStartPointOfSource((uint)prevIndex, targetWidth, targetHeight, true).ToBitPerPixel(16);
            IPicture nextPic = Next.GetFrameRelativeToStartPointOfSource((uint)nextIndex, targetWidth, targetHeight, true).ToBitPerPixel(16);
            prevPic.Disposed = null;
            nextPic.Disposed = null;


            IPicture? result = null;

            if (prevPic is IPicture<ushort> p16 && nextPic is IPicture<ushort> n16)
            {
                int w = p16.Width;
                int h = p16.Height;
                var outPic = new Picture16bpp(w, h)
                {
                    ProcessStack = new List<PictureProcessStack>
                    {
                            new PictureProcessStack { OperationDisplayName = "Crossfade", Operator = this.GetType(), ProcessingFuncStackTrace = new(true), Properties = new Dictionary<string, object> { { "Progress", progress } }  }
                    }
                };
                float wPrev = (float)(1.0 - progress);
                float wNext = (float)progress;
                int total = outPic.Pixels;
                for (int i = 0; i < total; i++)
                {
                    outPic.r[i] = (ushort)Math.Clamp((int)Math.Round(p16.r[i] * wPrev + n16.r[i] * wNext), 0, ushort.MaxValue);
                    outPic.g[i] = (ushort)Math.Clamp((int)Math.Round(p16.g[i] * wPrev + n16.g[i] * wNext), 0, ushort.MaxValue);
                    outPic.b[i] = (ushort)Math.Clamp((int)Math.Round(p16.b[i] * wPrev + n16.b[i] * wNext), 0, ushort.MaxValue);
                }

                // handle alpha channel if any of inputs has it
                if (p16.hasAlphaChannel || n16.hasAlphaChannel)
                {
                    outPic.a = new float[total];
                    for (int i = 0; i < total; i++)
                    {
                        float a1 = p16.a is null ? 1f : p16.a[i];
                        float a2 = n16.a is null ? 1f : n16.a[i];
                        outPic.a[i] = Math.Clamp(a1 * wPrev + a2 * wNext, 0f, 1f);
                    }
                    outPic.hasAlphaChannel = true;
                }

                result = outPic;
            }
            else
            {
                throw new NotSupportedException("Invalid pixel format.");
            }


            if (result == null) throw new InvalidOperationException("Failed to produce frame from CrossfadeTransform");
            return result;
        }
    }
}
