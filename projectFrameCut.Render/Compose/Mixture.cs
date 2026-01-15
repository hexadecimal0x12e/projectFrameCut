using projectFrameCut.Render;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Render.VideoMakeEngine
{
    public class OverlayMixture : IMixture
    {
        public string TypeName => "Overlay";

        public static List<string> ParametersNeeded = new();

        public static Dictionary<string, string> ParametersType = new();

        public Dictionary<string, object> Parameters => new();

        public static IMixture FromParametersDictionary(Dictionary<string, object> parameters)
        {
            return new OverlayMixture();
        }
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public string? ComputerId => "OverlayComputer";

        public IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer computer)
        {
            var sw = Stopwatch.StartNew();
            OverlayedPictureProcessStack procStack = new OverlayedPictureProcessStack
            {
                BaseSteps = basePicture.ProcessStack,
                TopSteps = topPicture.ProcessStack,
                OperationDisplayName = "Overlay effect",
                Operator = this.GetType(),
                ProcessingFuncStackTrace = new(true),
            };

            if (topPicture.Width != basePicture.Width || topPicture.Height != basePicture.Height)
            {
                topPicture = topPicture.Resize(basePicture.Width, basePicture.Height, false);
            }

            static bool HasValidChannels(IPicture pic)
            {
                if (pic is Picture8bpp p8)
                {
                    if (p8.r is null || p8.g is null || p8.b is null) return false;
                    if (p8.r.Length != p8.Pixels || p8.g.Length != p8.Pixels || p8.b.Length != p8.Pixels) return false;
                    if (p8.hasAlphaChannel && (p8.a is null || p8.a.Length != p8.Pixels)) return false;
                    return true;
                }
                if (pic is Picture16bpp p16)
                {
                    if (p16.r is null || p16.g is null || p16.b is null) return false;
                    if (p16.r.Length != p16.Pixels || p16.g.Length != p16.Pixels || p16.b.Length != p16.Pixels) return false;
                    if (p16.hasAlphaChannel && (p16.a is null || p16.a.Length != p16.Pixels)) return false;
                    return true;
                }
                // Unknown implementation: can't validate, assume ok.
                return true;
            }

            // Defensive: if any picture has corrupted channel buffers (e.g., length != Pixels),
            // normalize it via a roundtrip conversion. This prevents GPU overlay from indexing out of range.
            if (!HasValidChannels(basePicture) || !HasValidChannels(topPicture))
            {
                var baseBpp = (int)basePicture.bitPerPixel;
                var topBpp = (int)topPicture.bitPerPixel;
                try
                {
                    basePicture = basePicture.SaveToSixLaborsImage(baseBpp, saveAlpha: basePicture.hasAlphaChannel)
                        .ToPJFCPicture(baseBpp);
                }
                catch
                {
                    // keep original and let downstream throw with more context
                }

                try
                {
                    topPicture = topPicture.SaveToSixLaborsImage(topBpp, saveAlpha: topPicture.hasAlphaChannel)
                        .ToPJFCPicture(topBpp);
                }
                catch
                {
                    // keep original
                }
            }

            float[] baseR, baseG, baseB, baseA;
            if (basePicture is IPicture<ushort> bp16)
            {
                baseR = new float[bp16.Pixels];
                baseG = new float[bp16.Pixels];
                baseB = new float[bp16.Pixels];
                for (int i = 0; i < bp16.Pixels; i++)
                {
                    baseR[i] = bp16.r[i];
                    baseG[i] = bp16.g[i];
                    baseB[i] = bp16.b[i];
                }
                if (bp16.a is null)
                {
                    baseA = new float[bp16.Pixels];
                    Array.Fill(baseA, 1f);
                }
                else
                {
                    baseA = bp16.a;
                }
            }
            else if (basePicture is IPicture<byte> bp8)
            {
                baseR = new float[bp8.Pixels];
                baseG = new float[bp8.Pixels];
                baseB = new float[bp8.Pixels];
                for (int i = 0; i < bp8.Pixels; i++)
                {
                    baseR[i] = bp8.r[i] * 257.0f;
                    baseG[i] = bp8.g[i] * 257.0f;
                    baseB[i] = bp8.b[i] * 257.0f;
                }
                if (bp8.a is null)
                {
                    baseA = new float[bp8.Pixels];
                    Array.Fill(baseA, 1f);
                }
                else
                {
                    baseA = bp8.a;
                }
            }
            else throw new NotSupportedException();
#if DEBUG
            var id = Guid.NewGuid();
            if (!string.IsNullOrWhiteSpace(IPicture.DiagImagePath))
            {
                LogDiagnostic(
                    $"""
                    Overlay operation {id},
                    base:
                    {basePicture.GetDiagnosticsInfo()}

                    top:
                    {topPicture.GetDiagnosticsInfo()}
                    """
                    );
                basePicture.SaveAsPng16bpp(Path.Combine(IPicture.DiagImagePath, $"_OverlayDiag-{id}-base.png"));
                topPicture.SaveAsPng16bpp(Path.Combine(IPicture.DiagImagePath, $"_OverlayDiag-{id}-top.png"));
            }
#endif
            float[] topR, topG, topB, topA;
            if (topPicture is IPicture<ushort> tp16)
            {
                topR = new float[tp16.Pixels];
                topG = new float[tp16.Pixels];
                topB = new float[tp16.Pixels];
                for (int i = 0; i < tp16.Pixels; i++)
                {
                    topR[i] = tp16.r[i];
                    topG[i] = tp16.g[i];
                    topB[i] = tp16.b[i];
                }
                if (tp16.a is null)
                {
                    topA = new float[tp16.Pixels];
                    Array.Fill(topA, 1f);
                }
                else
                {
                    topA = tp16.a;
                }
            }
            else if (topPicture is IPicture<byte> tp8)
            {
                topR = new float[tp8.Pixels];
                topG = new float[tp8.Pixels];
                topB = new float[tp8.Pixels];
                for (int i = 0; i < tp8.Pixels; i++)
                {
                    topR[i] = tp8.r[i] * 257.0f;
                    topG[i] = tp8.g[i] * 257.0f;
                    topB[i] = tp8.b[i] * 257.0f;
                }
                if (tp8.a is null)
                {
                    topA = new float[tp8.Pixels];
                    Array.Fill(topA, 1f);
                }
                else
                {
                    topA = tp8.a;
                }
            }
            else throw new NotSupportedException();


            var outR = computer.Compute([topR, baseR, topA, baseA]);
            var outG = computer.Compute([topG, baseG, topA, baseA]);
            var outB = computer.Compute([topB, baseB, topA, baseA]);
            float[]? outA;
            if (basePicture.hasAlphaChannel || topPicture.hasAlphaChannel)
            {
                outA = outR[1] as float[];
            }
            else
            {
                outA = null;
            }
            IPicture result;
            if (basePicture is IPicture<byte> && topPicture is IPicture<byte>)
            {
                result = new Picture8bpp(basePicture.Width, basePicture.Height)
                {
                    r = ConvertToByteChannel(outR[0] as float[] ?? throw new InvalidOperationException("Invalid overlay output R")),
                    g = ConvertToByteChannel(outG[0] as float[] ?? throw new InvalidOperationException("Invalid overlay output G")),
                    b = ConvertToByteChannel(outB[0] as float[] ?? throw new InvalidOperationException("Invalid overlay output B")),
                    a = outA,
                    hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel,
                    ProcessStack = new List<PictureProcessStack> { procStack }

                };
            }
            else
            {
                result = new Picture(basePicture.Width, basePicture.Height)
                {
                    r = ConvertToUShortChannel(outR[0] as float[] ?? throw new InvalidOperationException("Invalid overlay output R")),
                    g = ConvertToUShortChannel(outG[0] as float[] ?? throw new InvalidOperationException("Invalid overlay output G")),
                    b = ConvertToUShortChannel(outB[0] as float[] ?? throw new InvalidOperationException("Invalid overlay output B")),
                    a = outA,
                    hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel,
                    ProcessStack = new List<PictureProcessStack> { procStack }
                };
            }
            sw.Stop();
            procStack.Elapsed = sw.Elapsed;

            static byte[] ConvertToByteChannel(float[] src)
            {
                var dst = new byte[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    float v = src[i] / 257.0f;
                    if (v < 0) v = 0;
                    if (v > 255) v = 255;
                    dst[i] = (byte)v;
                }
                return dst;
            }

            static ushort[] ConvertToUShortChannel(float[] src)
            {
                var dst = new ushort[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    float v = src[i];
                    if (v < 0) v = 0;
                    if (v > 65535) v = 65535;
                    dst[i] = (ushort)v;
                }
                return dst;
            }

#if DEBUG
            if (!string.IsNullOrWhiteSpace(IPicture.DiagImagePath))
            {
                LogDiagnostic(
                    $"""
                    Overlay operation {id},
                    result:
                    {result.GetDiagnosticsInfo()}
                    """
                    );
                result.SaveAsPng16bpp(Path.Combine(IPicture.DiagImagePath, $"_OverlayDiag-{id}-result.png"));
                //if (Debugger.IsAttached) Debugger.Break();

            }

            return result;
#endif
            return result;
        }
    }




}
