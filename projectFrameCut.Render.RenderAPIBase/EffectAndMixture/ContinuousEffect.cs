using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface IContinuousEffect : IEffect
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
        /// Represents the start point of the effect inside this Clip.
        /// </summary>
        public int StartPoint { get; set; }
        /// <summary>
        /// Represents the end point of the effect inside this Clip.
        /// </summary>
        public int EndPoint { get; set; }
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
        /// Render the effect on the source picture to produce a new picture with the target width and height.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="computer"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <returns>the processed frame</returns>
        public IPicture Render(IPicture source, uint index, IComputer? computer, int targetWidth, int targetHeight);

        IPicture IEffect.Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            throw new InvalidOperationException($"Cast this {TypeName} to IContinuousEffect, and call IContinuousEffect.Render().");
        }

        /// <summary>
        /// If you'd like to initialize the effect before use, override it.
        /// </summary>
        public virtual void Initialize()
        {
        }
    }

}
