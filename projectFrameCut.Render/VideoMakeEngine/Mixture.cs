using projectFrameCut.Render;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.VideoMakeEngine
{
    public class OverlayMixture : IMixture
    {
        public static List<string> ParametersNeeded = new();

        public static Dictionary<string, string> ParametersType = new();

        public Dictionary<string, object> Parameters => new();

        public static IMixture FromParametersDictionary(Dictionary<string, object> parameters)
        {
            return new OverlayMixture();
        }
        public static string TempSavePath = "";

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
            //var id = Guid.NewGuid();
            //if (!string.IsNullOrWhiteSpace(TempSavePath))
            //{
            //    LogDiagnostic(
            //        $"""
            //        Overlay operation {id},
            //        base:
            //        {basePicture.GetDiagnosticsInfo()}

            //        top:
            //        {topPicture.GetDiagnosticsInfo()}
            //        """
            //        );
            //    basePicture.SaveAsPng16bpp(Path.Combine(TempSavePath,$"_OverlayDiag-{id}-base.png"));
            //    topPicture.SaveAsPng16bpp(Path.Combine(TempSavePath,$"_OverlayDiag-{id}-top.png"));
            //}
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
                outA = outR[1];
            }
            else
            {
                outA = null;
            }
            IPicture result;
            if (basePicture is IPicture<byte> && topPicture is IPicture<byte>)
            {
                return new Picture8bpp(basePicture.Width, basePicture.Height)
                {
                    r = outR[0].Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                    g = outG[0].Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                    b = outB[0].Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                    a = outA,
                    hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel,
                    ProcessStack = $"Overlayed, \r\nbase:avg R:{baseR.Average(Convert.ToDecimal)} G:{baseG.Average(Convert.ToDecimal)} B:{baseB.Average(Convert.ToDecimal)} A:{baseA?.Average(Convert.ToDecimal) ?? -1}, ProcessStack:\r\n{basePicture.ProcessStack?.Replace("\n", "\n    ")}\r\ntop:avg R:{topR.Average(Convert.ToDecimal)} G:{topG.Average(Convert.ToDecimal)} B:{topB.Average(Convert.ToDecimal)} A:{topA?.Average(Convert.ToDecimal) ?? -1}, ProcessStack:\r\n{topPicture.ProcessStack?.Replace("\n", "\n    ")}"

                };
            }
            else
            {
                return new Picture(basePicture.Width, basePicture.Height)
                {
                    r = outR[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    g = outG[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    b = outB[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    a = outA,
                    hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel,
                    ProcessStack = $"Overlayed, \r\nbase:avg R:{baseR.Average(Convert.ToDecimal)} G:{baseG.Average(Convert.ToDecimal)} B:{baseB.Average(Convert.ToDecimal)} A:{baseA?.Average(Convert.ToDecimal) ?? -1}, ProcessStack:\r\n{basePicture.ProcessStack?.Replace("\n", "\n    ")}\r\ntop:avg R:{topR.Average(Convert.ToDecimal)} G:{topG.Average(Convert.ToDecimal)} B:{topB.Average(Convert.ToDecimal)} A:{topA?.Average(Convert.ToDecimal) ?? -1}, ProcessStack:\r\n{topPicture.ProcessStack?.Replace("\n", "\n    ")}"
                };
            }
            //if (!string.IsNullOrWhiteSpace(TempSavePath))
            //{
            //    LogDiagnostic(
            //        $"""
            //        Overlay operation {id},
            //        result:
            //        {result.GetDiagnosticsInfo()}
            //        """
            //        );
            //    result.SaveAsPng16bpp(Path.Combine(TempSavePath, $"_OverlayDiag-{id}-result.png"));
            //    //if (Debugger.IsAttached) Debugger.Break();

            //}

            //return result;
        }
    }

    public class AddMixture : IMixture
    {
        public Dictionary<string, object> Parameters => new();
        public static AddMixture FromParametersDictionary(Dictionary<string, object> parameters) => new AddMixture();

        public IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer computer)
        {
            // reuse overlay's mixing pipeline but we rely on the provided computer to implement Add semantics
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

            var outR = computer.Compute(new float[][] { topR, baseR, topA, baseA });
            var outG = computer.Compute(new float[][] { topG, baseG, topA, baseA });
            var outB = computer.Compute(new float[][] { topB, baseB, topA, baseA });
            float[] outA;
            if (basePicture.hasAlphaChannel || topPicture.hasAlphaChannel)
            {
                outA = outR[1];
            }
            else
            {
                outA = null;
            }
            if (basePicture is IPicture<byte> && topPicture is IPicture<byte>)
            {
                return new Picture8bpp(basePicture.Width, basePicture.Height)
                {
                    r = outR[0].Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                    g = outG[0].Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                    b = outB[0].Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                    a = outA,
                    hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel
                };
            }
            else
            {
                return new Picture(basePicture.Width, basePicture.Height)
                {
                    r = outR[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    g = outG[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    b = outB[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    a = outA,
                    hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel
                };
            }
        }
    }

    public class MinusMixture : IMixture
    {
        public Dictionary<string, object> Parameters => new();
        public static MinusMixture FromParametersDictionary(Dictionary<string, object> parameters) => new MinusMixture();
        public IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer computer)
        {
            // identical pipeline; rely on computer implementation
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

            var outR = computer.Compute(new float[][] { topR, baseR, topA, baseA });
            var outG = computer.Compute(new float[][] { topG, baseG, topA, baseA });
            var outB = computer.Compute(new float[][] { topB, baseB, topA, baseA });
            float[] outA;
            if (basePicture.hasAlphaChannel || topPicture.hasAlphaChannel)
            {
                outA = outR[1];
            }
            else
            {
                outA = null;
            }
            if (basePicture is IPicture<byte> && topPicture is IPicture<byte>)
            {
                return new Picture8bpp(basePicture.Width, basePicture.Height)
                {
                    r = outR[0].Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                    g = outG[0].Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                    b = outB[0].Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                    a = outA,
                    hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel
                };
            }
            else
            {
                return new Picture(basePicture.Width, basePicture.Height)
                {
                    r = outR[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    g = outG[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    b = outB[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    a = outA,
                    hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel
                };
            }
        }
    }

    public class MultiplyMixture : IMixture
    {
        public Dictionary<string, object> Parameters => new();
        public static MultiplyMixture FromParametersDictionary(Dictionary<string, object> parameters) => new MultiplyMixture();
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

            var outR = computer.Compute(new float[][] { topR, baseR, topA, baseA });
            var outG = computer.Compute(new float[][] { topG, baseG, topA, baseA });
            var outB = computer.Compute(new float[][] { topB, baseB, topA, baseA });
            float[] outA;
            if (basePicture.hasAlphaChannel || topPicture.hasAlphaChannel)
            {
                outA = outR[1];
            }
            else
            {
                outA = null;
            }
            if (basePicture is IPicture<byte> && topPicture is IPicture<byte>)
            {
                return new Picture8bpp(basePicture.Width, basePicture.Height)
                {
                    r = outR[0].Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                    g = outG[0].Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                    b = outB[0].Select(v => (byte)Math.Clamp(v / 257.0f, 0, 255)).ToArray(),
                    a = outA,
                    hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel
                };
            }
            else
            {
                return new Picture(basePicture.Width, basePicture.Height)
                {
                    r = outR[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    g = outG[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    b = outB[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                    a = outA,
                    hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel
                };
            }
        }
    }


}
