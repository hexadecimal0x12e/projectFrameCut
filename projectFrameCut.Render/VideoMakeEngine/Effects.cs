using projectFrameCut.Render;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace projectFrameCut.VideoMakeEngine
{
    public class RemoveColorEffect : IEffect
    {
        public bool Enabled { get; init; } = true;
        public int Index { get; init; }

        public ushort R { get; init; }
        public ushort G { get; init; }
        public ushort B { get; init; }
        public ushort A { get; init; }
        public ushort Tolerance { get; init; } = 0;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "R", R },
            { "G", G },
            { "B", B },
            { "A", A },
            { "Tolerance", Tolerance },
        };

        List<string> IEffect.ParametersNeeded => ParametersNeeded;
        Dictionary<string, string> IEffect.ParametersType => ParametersType;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "R",
            "G",
            "B",
            "A",
            "Tolerance",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "R", "ushort" },
            { "G", "ushort" },
            { "B", "ushort" },
            { "A", "ushort" },
            { "Tolerance", "ushort" },
        };

        public string TypeName => "RemoveColor";
        public static string s_TypeName => "RemoveColor";  

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            if (parameters.Count != ParametersNeeded.Count)
            {
                throw new ArgumentException("Too many parameters provided.");
            }


            return new RemoveColorEffect
            {
                R = Convert.ToUInt16(parameters["R"]),
                G = Convert.ToUInt16(parameters["G"]),
                B = Convert.ToUInt16(parameters["B"]),
                A = Convert.ToUInt16(parameters["A"]),
                Tolerance = Convert.ToUInt16(parameters["Tolerance"]),
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public Picture Render(Picture source, IComputer computer)
        {
            var alpha = computer.Compute([
                source.r.Select(Convert.ToSingle).ToArray(),
                source.g.Select(Convert.ToSingle).ToArray(),
                source.b.Select(Convert.ToSingle).ToArray(),
                source.a ?? Enumerable.Repeat(1f, source.Pixels).ToArray(),
                [(float)R],
                [(float)G],
                [(float)B],
                [(float)Tolerance]
                ])[0];

            var result = new Picture(source)
            {
                r = source.r,
                g = source.g,
                b = source.b,
                a = alpha,
                hasAlphaChannel = true
            };

            for (int i = 0; i < result.Pixels; i++)
            {
                if (result.a[i] == 0)
                {
                    result.r[i] = 0;
                    result.g[i] = 0;
                    result.b[i] = 0;
                    result.a[i] = 0f;
                }
            }

            return result;
        }
    }

   
    
}
