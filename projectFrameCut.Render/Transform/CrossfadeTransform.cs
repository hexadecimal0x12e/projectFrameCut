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

        public string TypeName => "Crossfade";

        public string Name { get; init; } = "Crossfade";

        public Guid PreviousClipId { get; init; }

        public Guid NextClipId { get; init; }

        public string? NeedComputer => null;

        public Dictionary<string, object> Parameters { get; set; }

        public List<string> ParametersNeeded => new();

        public Dictionary<string, string> ParametersType => new();

        public Guid BindedLeftClip { get; set; }
        public Guid BindedRightClip { get; set; }
        public uint Duration { get; set; }

        public void Init() { }

        /// <summary>
        /// progress: 0.0 => fully previous, 1.0 => fully next
        /// </summary>
        public IPicture GetFrame(IPicture prevPic, IPicture nextPic, double progress, IComputer? computer, int targetWidth, int targetHeight)
        {
            IPicture? result = null;

            if (prevPic.BitPerPixel == 16 || nextPic.BitPerPixel == 16)
            {
                if (prevPic.ToBitPerPixel(16) is IPicture<ushort> p16 && nextPic.ToBitPerPixel(16) is IPicture<ushort> n16)
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
                    if (p16.HasAlphaChannel || n16.HasAlphaChannel)
                    {
                        outPic.a = new float[total];
                        for (int i = 0; i < total; i++)
                        {
                            float a1 = p16.a is null ? 1f : p16.a[i];
                            float a2 = n16.a is null ? 1f : n16.a[i];
                            outPic.a[i] = Math.Clamp(a1 * wPrev + a2 * wNext, 0f, 1f);
                        }
                        outPic.HasAlphaChannel = true;
                    }

                    result = outPic;
                }
                else
                {
                    throw new NotSupportedException("Invalid pixel format.");
                }
            }
            else
            {
                if (prevPic.ToBitPerPixel(8) is IPicture<byte> p && nextPic.ToBitPerPixel(8) is IPicture<byte> n)
                {
                    int w = p.Width;
                    int h = p.Height;
                    var outPic = new Picture8bpp(w, h)
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
                        outPic.r[i] = (byte)Math.Clamp((int)Math.Round(p.r[i] * wPrev + n.r[i] * wNext), 0, byte.MaxValue);
                        outPic.g[i] = (byte)Math.Clamp((int)Math.Round(p.g[i] * wPrev + n.g[i] * wNext), 0, byte.MaxValue);
                        outPic.b[i] = (byte)Math.Clamp((int)Math.Round(p.b[i] * wPrev + n.b[i] * wNext), 0, byte.MaxValue);
                    }

                    // handle alpha channel if any of inputs has it
                    if (p.HasAlphaChannel || n.HasAlphaChannel)
                    {
                        outPic.a = new float[total];
                        for (int i = 0; i < total; i++)
                        {
                            float a1 = p.a is null ? 1f : p.a[i];
                            float a2 = n.a is null ? 1f : n.a[i];
                            outPic.a[i] = Math.Clamp(a1 * wPrev + a2 * wNext, 0f, 1f);
                        }
                        outPic.HasAlphaChannel = true;
                    }

                    result = outPic;
                }
                else
                {
                    throw new NotSupportedException("Invalid pixel format.");
                }
            }




            if (result == null) throw new InvalidOperationException("Failed to produce frame from CrossfadeTransform");
            return result;
        }
    }
}
