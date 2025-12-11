using projectFrameCut.Render;
using projectFrameCut.Render.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public interface IEffect
    {
        public string FromPlugin { get; }
        public string TypeName { get; }
        public string Name { get; set; }
        public Dictionary<string, object> Parameters { get; }
        public bool Enabled { get; set; }
        public int Index { get; set; }      
        [JsonIgnore]
        public List<string> ParametersNeeded { get; }
        [JsonIgnore]
        public Dictionary<string, string> ParametersType { get; }
        [JsonIgnore]
        public string? NeedComputer { get; }

        public IEffect WithParameters(Dictionary<string, object> parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight);
    }

    public interface IMixture
    {
        public string FromPlugin { get; }

        public string? ComputerId { get; }

        public Dictionary<string, object> Parameters { get; }

        public IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer? computer);
    }

    public interface IComputer
    {
        public string FromPlugin { get; }
        public float[][] Compute(float[][] args);
    }

    public class EffectAndMixtureJSONStructure
    {
        public bool IsMixture { get; set; } = false;
        public string FromPlugin { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;    
        public string TypeName { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public int Index { get; set; } = 1;
        public Dictionary<string, object>? Parameters { get; set; }
    }


}
