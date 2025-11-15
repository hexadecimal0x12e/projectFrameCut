using projectFrameCut.Render;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public interface IEffect
    {
        public string TypeName { get; }
        public Dictionary<string, object> Parameters { get; }

        public Picture Render(Picture source, IComputer computer);
    }

    public interface IMixture
    {
        public Dictionary<string, object> Parameters { get; }

        public Picture Mix(Picture basePicture, Picture topPicture, IComputer computer);

    }

    public interface IComputer
    {
        public float[][] Compute(float[][] args);
    }

    public class EffectAndMixtureJSONStructure
    {
        public bool IsMixture { get; set; } = false;    
        public string TypeName { get; set; } = string.Empty;
        public Dictionary<string, object>? Parameters { get; set; }

    }
}
