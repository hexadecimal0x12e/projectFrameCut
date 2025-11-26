using projectFrameCut.Render;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
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

        public Picture Mix(Picture basePicture, Picture topPicture, IComputer computer)
        {


            var outR = computer.Compute([basePicture.r.Select(Convert.ToSingle).ToArray(), topPicture.r.Select(Convert.ToSingle).ToArray(), basePicture.a, topPicture.a]);
            var outG = computer.Compute([basePicture.g.Select(Convert.ToSingle).ToArray(), topPicture.g.Select(Convert.ToSingle).ToArray(), basePicture.a, topPicture.a]);
            var outB = computer.Compute([basePicture.b.Select(Convert.ToSingle).ToArray(), topPicture.b.Select(Convert.ToSingle).ToArray(), basePicture.a, topPicture.a]);
            float[] outA;
            if (basePicture.hasAlphaChannel || topPicture.hasAlphaChannel)
            {
                List<KeyValuePair<float, Tuple<float, float>>> alphas = new();
                for (int i = 0; i < outB[1].Length; i++)
                {
                    alphas.Add(new(outR[1][i], new(outG[1][i], outB[1][i])));
                }
                outA = alphas.Select((k) => (k.Key + k.Value.Item1 + k.Value.Item2) / 3).ToArray();
            }
            else
            {
                outA = null;
            }

            return new Picture(basePicture)
            {
                r = outR[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                g = outG[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                b = outB[0].Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray(),
                a = outA,
                Width = basePicture.Width,
                Height = basePicture.Height,
                hasAlphaChannel = basePicture.hasAlphaChannel || topPicture.hasAlphaChannel
            };

        }
    }


}
