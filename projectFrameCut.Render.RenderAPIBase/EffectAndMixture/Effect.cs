using projectFrameCut.Render;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
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
        /// Get the relative width of the effect.
        /// </summary>
        public int RelativeWidth { get; set; }
        /// <summary>
        /// Get the relative height of the effect.
        /// </summary>
        public int RelativeHeight { get; set; }

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

        /// <summary>
        /// If you'd like to initialize the effect before use, override it.
        /// </summary>
        public virtual void Initialize()
        {
        }
    }


}
