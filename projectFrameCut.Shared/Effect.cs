using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public class Effects
    {
        public EffectType Type { get; init; }
        public object[] Arguments { get; init; }
        public Effects(EffectType type, params object[] arguments)
        {
            Type = type;
            Arguments = arguments;
        }

        public override string ToString()
        {
            return $"Effect Type: {Type}, Arguments: [{string.Join(", ", Arguments.Select(arg => arg?.ToString() ?? "null"))}]";
        }
    }
}
