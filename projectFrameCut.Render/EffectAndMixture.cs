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

        public AcceleratedComputer Computer { get; set; }

        public Picture Render(Picture source);

        public IEffect FromParametersDictionary(Dictionary<string, object> parameters, IAcceleratedComputer computer);
    }

    public interface IMixture
    {
        public List<string> ParametersNeeded { get; }
        public Dictionary<string, object> Parameters { get; }
        public Dictionary<string, string> ParametersType { get; }

        public AcceleratedComputer Computer { get; set; }

        public Picture Mix(Picture basePicture, Picture topPicture);

        public IMixture FromParametersDictionary(Dictionary<string, object> parameters, IAcceleratedComputer computer);

    }
}
