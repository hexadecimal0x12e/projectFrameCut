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
using System.Collections.Concurrent;

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
        /// <remarks>
        /// it SHOULD equals to <see cref="IEffectBundle.TypeName"/>, <see cref="IEffectFactory.TypeName"/> and so on.
        /// </remarks>
        public string TypeName { get; }
        /// <summary>
        /// Get which kind of effect is. 
        /// </summary>
        /// <remarks>
        /// It's for replacing properties <see cref="IsNormalEffect"/>, <see cref="IsContinuousEffect"/> and <see cref="IsBindableArgsEffect"/> and so on, to make it more extendable for future effect types.
        /// For compatibility consideration, the default implementation of this property will check the Is***Effect properties to determine the EffectType. 
        /// It's best to override this property to provide a specific EffectType because of <b>this feature may be removed in the future</b>.
        /// </remarks>
        public virtual EffectType TypeOfEffect => EffectType.NormalEffect;

        /// <summary>
        /// Get how this effect is implemented.
        /// </summary>
        public EffectImplementType ImplementType { get; }

        /// <summary>
        /// Name of this effect. Most for display purpose.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Get the ID of this specific effect instance.
        /// This is a <b>REQUIRED</b> property for any kind of <see cref="IBindableArgumentEffect"/>, but optional for others. 
        /// </summary>
        /// <remarks>
        /// DO NOT set this property manually. It will be set when the effect is created.
        /// If set, it should be a Guid.
        /// </remarks>
        public string Id { get; set; }

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
        /// Indicates whether this effect needs a specific computer with the computer which it's ID is <see cref="NeedComputer"/> to run.
        /// Or be null indicates this effect does not need a specific computer.
        /// </summary>
        [JsonIgnore]
        public string? NeedComputer { get; }
        /// <summary>
        /// Gets a value indicating whether the effect produces a rendered <see cref="IPicture"/> or a un-processed <see cref="IPictureProcessStep"/> to be used in the next step.
        /// </summary>
        [JsonIgnore]
        public bool YieldProcessStep { get; }


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
        /// Generate some process step instead of rendering the picture directly.
        /// Throw a <see cref="NotImplementedException"/> if this effect does not support yielding process step.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="computer"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <returns>the processed frame</returns>
        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight);

        /// <summary>
        /// If you'd like to initialize the effect before use, override it.
        /// </summary>
        public virtual void Initialize()
        {
        }

        /// <summary>
        /// Get the info of this effect. Used in MCP calling in agent.
        /// </summary>
        /// <remarks>
        /// For UI Displaying purpose please use EffectBundle's GetDisplayInfo method instead.
        /// </remarks>
        /// <returns></returns>
        public virtual EffectInfo GetInfo()
        {
            return new EffectInfo
            {
                FromPlugin = FromPlugin,
                TypeName = TypeName,
                Name = Name,
                Description = "No description provided. Try guess it's purpose from TypeName.", // Description is not provided in IEffect, so we set it to a default string.
                Parameters = Parameters.ToDictionary(kv => kv.Key, kv => new EffectParameterInfo { Name = kv.Key, ParameterType = kv.Value.GetType().FullName ?? "unknown", DefaultValue = null }),
                EffectType = TypeOfEffect
            };
        }

        [Obsolete("Consider to use TypeOfEffect instead. This property may be removed in the future.", false)]
        public bool IsNormalEffect => true;
        [Obsolete("Consider to use TypeOfEffect instead. This property may be removed in the future.", false)]
        public bool IsContinuousEffect => false;
        [Obsolete("Consider to use TypeOfEffect instead. This property may be removed in the future.", false)]
        public bool IsBindableArgsEffect => false;

        /// <summary>
        /// Get the binded EffectGroup's ID
        /// </summary>
        /// <remarks>
        /// DO NOT set this property manually. EffectGroup will do this.
        /// </remarks>
        public string? BindedEffectGroupID { get; set; }
    }


}
