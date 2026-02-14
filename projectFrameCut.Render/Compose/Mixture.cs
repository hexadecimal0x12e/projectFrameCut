using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Render.Compose
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

        public IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer? computer, IPicture.PicturePixelMode targetPPB)
        {
            if (computer is null)
            {
                throw new ArgumentNullException(nameof(computer), "OverlayMixture requires a computer.");
            }
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

            var pool = ArrayPool<float>.Shared;
            float[] baseR, baseG, baseB;
            float[]? baseA;
            int basePixels;
            if (basePicture is IPicture<ushort> bp16)
            {
                basePixels = bp16.Pixels;
                baseR = pool.Rent(basePixels);
                baseG = pool.Rent(basePixels);
                baseB = pool.Rent(basePixels);
                for (int i = 0; i < basePixels; i++)
                {
                    baseR[i] = bp16.r[i];
                    baseG[i] = bp16.g[i];
                    baseB[i] = bp16.b[i];
                }
                baseA = bp16.hasAlphaChannel ? bp16.a : null;
            }
            else if (basePicture is IPicture<byte> bp8)
            {
                basePixels = bp8.Pixels;
                baseR = pool.Rent(basePixels);
                baseG = pool.Rent(basePixels);
                baseB = pool.Rent(basePixels);
                for (int i = 0; i < basePixels; i++)
                {
                    baseR[i] = bp8.r[i] * 257.0f;
                    baseG[i] = bp8.g[i] * 257.0f;
                    baseB[i] = bp8.b[i] * 257.0f;
                }
                baseA = bp8.hasAlphaChannel ? bp8.a : null;
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
            float[] topR, topG, topB;
            float[]? topA;
            int topPixels;
            if (topPicture is IPicture<ushort> tp16)
            {
                topPixels = tp16.Pixels;
                topR = pool.Rent(topPixels);
                topG = pool.Rent(topPixels);
                topB = pool.Rent(topPixels);
                for (int i = 0; i < topPixels; i++)
                {
                    topR[i] = tp16.r[i];
                    topG[i] = tp16.g[i];
                    topB[i] = tp16.b[i];
                }
                topA = tp16.hasAlphaChannel ? tp16.a : null;
            }
            else if (topPicture is IPicture<byte> tp8)
            {
                topPixels = tp8.Pixels;
                topR = pool.Rent(topPixels);
                topG = pool.Rent(topPixels);
                topB = pool.Rent(topPixels);
                for (int i = 0; i < topPixels; i++)
                {
                    topR[i] = tp8.r[i] * 257.0f;
                    topG[i] = tp8.g[i] * 257.0f;
                    topB[i] = tp8.b[i] * 257.0f;
                }
                topA = tp8.hasAlphaChannel ? tp8.a : null;
            }
            else throw new NotSupportedException();


            object[]? outR;
            object[]? outG;
            object[]? outB;
            try
            {
                outR = computer.Compute([topR, baseR, topA, baseA, (int)targetPPB, basePixels]);
                outG = computer.Compute([topG, baseG, topA, baseA, (int)targetPPB, basePixels]);
                outB = computer.Compute([topB, baseB, topA, baseA, (int)targetPPB, basePixels]);
            }
            finally
            {
                pool.Return(baseR, clearArray: false);
                pool.Return(baseG, clearArray: false);
                pool.Return(baseB, clearArray: false);
                pool.Return(topR, clearArray: false);
                pool.Return(topG, clearArray: false);
                pool.Return(topB, clearArray: false);
            }
            float[]? outA;
            if (basePicture.hasAlphaChannel || topPicture.hasAlphaChannel)
            {
                outA = outR![1] as float[];
            }
            else
            {
                outA = null;
            }
            IPicture result;
            if ((int)targetPPB == 8)
            {
                result = new Picture8bpp(basePicture.Width, basePicture.Height)
                {
                    r = ConvertToByteChannel(outR![0]),
                    g = ConvertToByteChannel(outG![0]),
                    b = ConvertToByteChannel(outB![0]),
                    a = outA,
                    hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel,
                    ProcessStack = new List<PictureProcessStack> { procStack }

                };
            }
            else
            {
                result = new Picture(basePicture.Width, basePicture.Height)
                {
                    r = ConvertToUShortChannel(outR![0]),
                    g = ConvertToUShortChannel(outG![0]),
                    b = ConvertToUShortChannel(outB![0]),
                    a = outA,
                    hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel,
                    ProcessStack = new List<PictureProcessStack> { procStack }
                };
            }
            sw.Stop();
            procStack.Elapsed = sw.Elapsed;

            static byte[] ConvertToByteChannel(object? src)
            {
                if (src is byte[] b) return b;
                if (src is ushort[] u16)
                {
                    var dstU16 = new byte[u16.Length];
                    for (int i = 0; i < u16.Length; i++)
                    {
                        float v = u16[i] / 257.0f;
                        if (v < 0) v = 0;
                        if (v > 255) v = 255;
                        dstU16[i] = (byte)v;
                    }
                    return dstU16;
                }
                if (src is float[] f)
                {
                    var dst = new byte[f.Length];
                    for (int i = 0; i < f.Length; i++)
                    {
                        float v = f[i] / 257.0f;
                        if (v < 0) v = 0;
                        if (v > 255) v = 255;
                        dst[i] = (byte)v;
                    }
                    return dst;
                }
                throw new InvalidOperationException("Invalid overlay output channel type for byte target");
            }

            static ushort[] ConvertToUShortChannel(object? src)
            {
                if (src is ushort[] u16) return u16;
                if (src is byte[] b)
                {
                    var dstU16 = new ushort[b.Length];
                    for (int i = 0; i < b.Length; i++)
                    {
                        dstU16[i] = (ushort)(b[i] * 257.0f);
                    }
                    return dstU16;
                }
                if (src is float[] f)
                {
                    var dst = new ushort[f.Length];
                    for (int i = 0; i < f.Length; i++)
                    {
                        float v = f[i];
                        if (v < 0) v = 0;
                        if (v > 65535) v = 65535;
                        dst[i] = (ushort)v;
                    }
                    return dst;
                }
                throw new InvalidOperationException("Invalid overlay output channel type for ushort target");
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
#else
            return result;
#endif
        }
    }




}
