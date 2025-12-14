using projectFrameCut.Render;
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
        /// <summary>
        /// Indicates which plugin this effect comes from.
        /// </summary>
        public string FromPlugin { get; }
        /// <summary>
        /// Define the type name of the effect. 
        /// </summary>
        public string TypeName { get; }
        /// <summary>
        /// Name of this effect. Most for display purpose.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Parameters of the effect.
        /// </summary>
        public Dictionary<string, object> Parameters { get; }
        /// <summary>
        /// Get or set whether the effect is enabled.
        /// </summary>
        public bool Enabled { get; set; }
        /// <summary>
        /// The index of the effect in the effect stack.
        /// </summary>
        public int Index { get; set; }
        /// <summary>
        /// Indicates which parameters are needed for this effect.
        /// </summary>
        [JsonIgnore]
        public List<string> ParametersNeeded { get; }
        /// <summary>
        /// Indicates the type of each parameter.
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, string> ParametersType { get; }
        /// <summary>
        /// Indicates whether this effect needs a specific computer with the computer which it's ID is <see cref="NeedComputer"/> to run.
        /// Or be null indicates this effect does not need a specific computer.
        /// </summary>
        [JsonIgnore]
        public string? NeedComputer { get; }
        /// <summary>
        /// Create a new effect with the given parameters.
        /// </summary>
        /// <param name="parameters"></param>
        /// <returns></returns>
        public IEffect WithParameters(Dictionary<string, object> parameters);

        /// <summary>
        /// Render the effect on the source picture to produce a new picture with the target width and height.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="computer"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <returns>the processed frame</returns>
        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight);
    }

    public interface IMixture
    {
        /// <summary>
        /// Indicates which plugin this mixture comes from.
        /// </summary>
        public string FromPlugin { get; }
        /// <summary>
        /// Indicate the type name of the mixture. 
        /// </summary>
        public string TypeName { get; }
        /// <summary>
        /// Indicates whether this mixer needs a specific computer with the computer which it's ID is <see cref="NeedComputer"/> to run.
        /// Or be null indicates this mixer does not need a specific computer.
        /// </summary>
        public string? ComputerId { get; }
        /// <summary>
        /// The arguments of the mixture.
        /// </summary>
        public Dictionary<string, object> Parameters { get; }

        /// <summary>
        /// Mix the top picture onto the base picture using this mixture.
        /// </summary>
        /// <param name="basePicture"></param>
        /// <param name="topPicture"></param>
        /// <param name="computer"></param>
        /// <returns>the mixed picture</returns>
        public IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer? computer);
    }

    public interface IComputer
    {
        /// <summary>
        /// Indicates which plugin this computer comes from.
        /// </summary>
        public string FromPlugin { get; }
        /// <summary>
        /// Represents the effect or mixture type name that this computer supports.
        /// </summary>
        public string SupportedEffectOrMixture { get; }
        /// <summary>
        /// Compute the output based on the input arguments.
        /// </summary>
        /// <param name="args">Input data</param>
        /// <returns>output data</returns>
        public object[] Compute(object[] args);
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
