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
        public static string TempSavePath = "";
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";
        public string? ComputerId => "OverlayComputer";

        [DebuggerStepThrough()]
        public IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer computer)
        {
            float[] baseR, baseG, baseB, baseA;
            if (basePicture is IPicture<ushort> bp16)
            {
                baseR = bp16.r.Select(Convert.ToSingle).ToArray();
                baseG = bp16.g.Select(Convert.ToSingle).ToArray();
                baseB = bp16.b.Select(Convert.ToSingle).ToArray();
                baseA = bp16.a ?? Enumerable.Repeat(1f, bp16.Pixels).ToArray();
            }
            else if (basePicture is IPicture<byte> bp8)
            {
                baseR = bp8.r.Select(v => v * 257.0f).ToArray();
                baseG = bp8.g.Select(v => v * 257.0f).ToArray();
                baseB = bp8.b.Select(v => v * 257.0f).ToArray();
                baseA = bp8.a ?? Enumerable.Repeat(1f, bp8.Pixels).ToArray();
            }
            else throw new NotSupportedException();
#if DEBUG
            var id = Guid.NewGuid();
            if (!string.IsNullOrWhiteSpace(TempSavePath))
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
                basePicture.SaveAsPng16bpp(Path.Combine(TempSavePath, $"_OverlayDiag-{id}-base.png"));
                topPicture.SaveAsPng16bpp(Path.Combine(TempSavePath, $"_OverlayDiag-{id}-top.png"));
            }
#endif
            float[] topR, topG, topB, topA;
            if (topPicture is IPicture<ushort> tp16)
            {
                topR = tp16.r.Select(Convert.ToSingle).ToArray();
                topG = tp16.g.Select(Convert.ToSingle).ToArray();
                topB = tp16.b.Select(Convert.ToSingle).ToArray();
                topA = tp16.a ?? Enumerable.Repeat(1f, tp16.Pixels).ToArray();
            }
            else if (topPicture is IPicture<byte> tp8)
            {
                topR = tp8.r.Select(v => v * 257.0f).ToArray();
                topG = tp8.g.Select(v => v * 257.0f).ToArray();
                topB = tp8.b.Select(v => v * 257.0f).ToArray();
                topA = tp8.a ?? Enumerable.Repeat(1f, tp8.Pixels).ToArray();
            }
            else throw new NotSupportedException();


            var outR = computer.Compute([topR, baseR, topA, baseA]);
            var outG = computer.Compute([topG, baseG, topA, baseA]);
            var outB = computer.Compute([topB, baseB, topA, baseA]);
            float[] outA;
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
#if DEBUG
                result =
#else
                return 
#endif
                    new Picture8bpp(basePicture.Width, basePicture.Height)
                    {
                        r = (outR[0] as float[]).Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                        g = (outG[0] as float[]).Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                        b = (outB[0] as float[]).Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                        a = outA,
                        hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel,
                        ProcessStack = $"Overlayed, \r\nbase:avg R:{baseR.Average(Convert.ToDecimal)} G:{baseG.Average(Convert.ToDecimal)} B:{baseB.Average(Convert.ToDecimal)} A:{baseA?.Average(Convert.ToDecimal) ?? -1}, ProcessStack:\r\n{basePicture.ProcessStack?.Replace("\n", "\n    ")}\r\ntop:avg R:{topR.Average(Convert.ToDecimal)} G:{topG.Average(Convert.ToDecimal)} B:{topB.Average(Convert.ToDecimal)} A:{topA?.Average(Convert.ToDecimal) ?? -1}, ProcessStack:\r\n{topPicture.ProcessStack?.Replace("\n", "\n    ")}",
                        filePath  = $"base:{basePicture.filePath}, top:{topPicture.filePath}"

                    };
            }
            else
            {
#if DEBUG
                result =
#else
                return 
#endif
                new Picture(basePicture.Width, basePicture.Height)
                {
                    r = (outR[0] as float[]).Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    g = (outG[0] as float[]).Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    b = (outB[0] as float[]).Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    a = outA,
                    hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel,
                    ProcessStack = $"Overlayed, \r\nbase:avg R:{baseR.Average(Convert.ToDecimal)} G:{baseG.Average(Convert.ToDecimal)} B:{baseB.Average(Convert.ToDecimal)} A:{baseA?.Average(Convert.ToDecimal) ?? -1}, ProcessStack:\r\n{basePicture.ProcessStack?.Replace("\n", "\n    ")}\r\ntop:avg R:{topR.Average(Convert.ToDecimal)} G:{topG.Average(Convert.ToDecimal)} B:{topB.Average(Convert.ToDecimal)} A:{topA?.Average(Convert.ToDecimal) ?? -1}, ProcessStack:\r\n{topPicture.ProcessStack?.Replace("\n", "\n    ")}",
                    filePath = $"base:{basePicture.filePath}, top:{topPicture.filePath}"

                };
            }
#if DEBUG
            if (!string.IsNullOrWhiteSpace(TempSavePath))
            {
                LogDiagnostic(
                    $"""
                    Overlay operation {id},
                    result:
                    {result.GetDiagnosticsInfo()}
                    """
                    );
                result.SaveAsPng16bpp(Path.Combine(TempSavePath, $"_OverlayDiag-{id}-result.png"));
                //if (Debugger.IsAttached) Debugger.Break();

            }

            return result;
#endif
        }
    }




}
