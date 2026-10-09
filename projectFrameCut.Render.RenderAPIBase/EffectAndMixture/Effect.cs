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
    public interface IEffect : projectFrameCut.Render.RenderAPIBase.Plugins.IExtensibleObject
    {
        /// <summary>
        /// Indicates which plugin this effect comes from.
        /// </summary>
        public new string FromPlugin { get; }
        /// <summary>
        /// Define the type name of the effect. 
        /// </summary>
        /// <remarks>
        /// it SHOULD equals to <see cref="IEffectProvider.TypeName"/> and so on.
        /// </remarks>
        public new string TypeName { get; }
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
        /// This is a <b>REQUIRED</b> property, and it <b>should be a Guid</b>.
        /// </summary>
        /// <remarks>
        /// DO NOT set this property manually. It will be set when the effect is created. 
        /// Manually setting this property may cause unexpected behavior, such as effect instance reference issues.
        /// </remarks>
        public new string Id { get; set; }

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
        /// Get whether this effect is reorderable in the effect stack. If false, the effect will be rendered in a fixed position in the final effect stack.
        /// </summary>
        [JsonIgnore]
        public bool IsReorderable { get; }

        /// <summary>
        /// Gets whether this effect can process a frame after it has been resized to the target canvas size.
        /// </summary>
        [JsonIgnore]
        public bool CanProcessFromCanvas => false;

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
        /// Get the info of this effect. Used in MCP calling in agent.
        /// </summary>
        /// <remarks>
        /// For UI displaying purposes, use the EffectProvider's display information instead.
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
        /// Get the binded effect providing system's ID. 
        /// Blank means this effect is not binded to any effect providing system (i.e. <see cref="IEffectProvider"/>).
        /// </summary>
        /// <remarks>
        /// <b>DO NOT</b> set this property manually. EffectGroup will do this.
        /// </remarks>
        public string? BindedEffectProvidingSystemID { get; set; }

        /// <summary>
        /// Compute a graph node for the current frame. Override for picture-to-value or non-picture primary inputs.
        /// Input pictures and picture parameters are owned by the caller and are valid for this invocation.
        /// </summary>
        public virtual object? Compute(EffectExecutionContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return this switch
            {
                IMultipleOutputEffect multiple => multiple.ComputeOutputs(context),
                IValueProviderEffect value => value.GetGetter()(),
                IClipPositionProvider position when context.Clip is not null => position.GetPosition(context.Clip, context.TargetWidth, context.TargetHeight),
                IContinuousClipPositionProvider position when context.Clip is not null => position.GetPosition(context.Clip, context.FrameIndex,
                    context.TargetWidth, context.TargetHeight, context.RelativeWidth, context.RelativeHeight),
                IContinuousEffect continuous when context.Input is IPicture picture => continuous.Render(picture, context.Progress, context.TargetWidth, context.TargetHeight),
                INormalEffect normal when context.Input is IPicture picture => normal.Render(picture, context.TargetWidth, context.TargetHeight),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support this input. Override Compute to handle it."),
            };
        }
    }

    /// <summary>Computes all named outputs once for the current node invocation.</summary>
    public interface IMultipleOutputEffect : IEffect
    {
        public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context);
    }

    public interface INormalEffect : IEffect
    {
        EffectType IEffect.TypeOfEffect => EffectType.NormalEffect;

        /// <summary>
        /// Render the effect on the source picture to produce a new picture with the target width and height.
        /// </summary>
        /// <param name="source">The input frame.</param>
        /// <param name="targetWidth">Output canvas' width.</param>
        /// <param name="targetHeight">Output canvas' height.</param>
        /// <returns>the processed frame</returns>
        public IPicture Render(IPicture source, int targetWidth, int targetHeight);

    }

    public interface IColorAdjustEffect : INormalEffect
    {
        /// <summary>
        /// Adjust the target frame.
        /// </summary>
        /// <param name="source"></param>
        /// <returns>the processed frame</returns>
        public IPicture Process(IPicture source);
        IPicture INormalEffect.Render(IPicture source, int targetWidth, int targetHeight) => Process(source);

        bool IEffect.Enabled { get => true; set { } }
        int IEffect.RelativeWidth { get => -1; set { } }
        int IEffect.RelativeHeight { get => -1; set { } }
        int IEffect.Index { get => int.MinValue; set { } }

    }

    /// <summary>
    /// A value provider effect is a special kind of effect that does not produce picture output, but instead provides a dynamic value to other effects. 
    /// <para />
    /// It implements both <see cref="IEffect"/> and <see cref="IEffectArgumentField"/>, allowing it to be used as a source of dynamic parameters for other effects in the rendering pipeline.
    /// </summary>
    public interface IValueProviderEffect : IEffect, IEffectArgumentField
    {
        EffectType IEffect.TypeOfEffect => EffectType.NonIPictureOutputValueProvider;

        bool IEffectArgumentField.IsDynamic => true;
    }
}
