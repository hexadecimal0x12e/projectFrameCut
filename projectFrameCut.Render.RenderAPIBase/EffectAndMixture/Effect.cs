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
using projectFrameCut.Drawing.Base;

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
        /// Never set this in your effect's code, this property has been set when you implement the specific effect interface, such as <see cref="INormalEffect"/>, <see cref="IContinuousEffect"/> and so on.
        /// </remarks>
        public EffectType TypeOfEffect { get; }

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
        /// If set, it <b>should be a Guid</b>.
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
        /// <remarks>
        /// -1 for some effects which do not care about the width and height of the output canvas, such as <see cref="IClipPositionProvider"/>. 
        /// For effects that care about the output canvas size, it should be set to the width of the output canvas when creating the effect, and the effect will do scaling based on the relative width and height when rendering.
        /// </remarks>
        public int RelativeWidth { get; set; }
        /// <summary>
        /// Get the relative height of the effect.
        /// </summary>
        /// <remarks>
        /// -1 for some effects which do not care about the width and height of the output canvas, such as <see cref="IClipPositionProvider"/>. 
        /// For effects that care about the output canvas size, it should be set to the width of the output canvas when creating the effect, and the effect will do scaling based on the relative width and height when rendering.
        /// </remarks>
        public int RelativeHeight { get; set; }

        /// <summary>
        /// Create a new effect with the given parameters.
        /// </summary>
        /// <param name="parameters"></param>
        /// <returns></returns>
        public IEffect WithParameters(Dictionary<string, object> parameters);

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

        /// <summary>
        /// Get the binded EffectGroup's ID
        /// </summary>
        /// <remarks>
        /// <b>DO NOT</b> set this property manually. EffectGroup will do this.
        /// </remarks>
        public string? BindedEffectGroupID { get; set; }
    }

    public interface INormalEffect : IEffect
    {
        EffectType IEffect.TypeOfEffect => EffectType.NormalEffect;

        /// <summary>
        /// Render the effect on the source picture to produce a new picture with the target width and height.
        /// </summary>
        /// <param name="source">The input frame.</param>
        /// <param name="computer">A provided computer for accelerated computing.</param>
        /// <param name="targetWidth">Output canvas' width.</param>
        /// <param name="targetHeight">Output canvas' height.</param>
        /// <returns>the processed frame</returns>
        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight);

        /// <summary>
        /// Generate some process step instead of rendering the picture directly.
        /// Throw a <see cref="NotImplementedException"/> if this effect does not support yielding process step.
        /// </summary>
        /// <param name="source">The input frame.</param>
        /// <param name="targetWidth">Output canvas' width.</param>
        /// <param name="targetHeight">Output canvas' height.</param>
        /// <returns>the processed frame</returns>
        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight);
    }

    public interface IColorAdjustEffect : INormalEffect
    {
        /// <summary>
        /// Adjust the target frame.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="computer"></param>
        /// <returns>the processed frame</returns>
        public IPicture Process(IPicture source, IComputer? computer);
        /// <summary>
        /// Yield a process step for adjusting the frame.
        /// Throw a <see cref="NotImplementedException"/> if this effect does not support yielding process step.
        /// </summary>
        /// <param name="source"></param>
        /// <returns>the processed frame</returns>
        public IPictureProcessStep GetStep(IPicture source);

        IPicture INormalEffect.Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight) => Process(source, computer);
        IPictureProcessStep INormalEffect.GetStep(IPicture source, int targetWidth, int targetHeight) => GetStep(source);

        bool IEffect.Enabled { get => true; set { } }
        int IEffect.RelativeWidth { get => -1; set { } }
        int IEffect.RelativeHeight { get => -1; set { } }
        int IEffect.Index { get => int.MinValue; set => Logger.Log("ColorAdjustment should always be first one to render and it's index should not be changed.", "warn"); }

    }

}
