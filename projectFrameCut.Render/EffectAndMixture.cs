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
        public List<string> ParametersNeeded { get; }
        public Dictionary<string, object> Parameters { get; }
        public Dictionary<string, string> ParametersType { get; }

        public Picture Render(Picture source, IComputer computer);

        public IEffect FromParametersDictionary(Dictionary<string, object> parameters);
    }

    public interface IMixture
    {
        public List<string> ParametersNeeded { get; }
        public Dictionary<string, object> Parameters { get; }
        public Dictionary<string, string> ParametersType { get; }

        public Picture Mix(Picture basePicture, Picture topPicture, IComputer computer);

        public IMixture FromParametersDictionary(Dictionary<string, object> parameters);

    }

    public interface IComputer
    {
        public float[][] Compute(float[][] args);
    }
}
