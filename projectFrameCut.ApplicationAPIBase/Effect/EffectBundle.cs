using CommunityToolkit.Maui.Views;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace projectFrameCut.ApplicationAPIBase.Effect
{
    public interface IEffectBundle
    {
        /// <summary>
        /// The ID for input anchor.
        /// </summary>
        public static readonly Guid InputAnchorGUID = new("00000000-0000-0000-0000-000000000000");
        /// <summary>
        /// The ID for output anchor.
        /// </summary>
        public static readonly Guid OutputAnchorGUID = new("ffffffff-ffff-ffff-ffff-ffffffffffff");
        /// <summary>
        /// The Id for any unconnected anchor.
        /// </summary>
        public static readonly Guid NoConnectionGUID = new("00001234-5678-90ab-cdef-012345678900");

        /// <summary>
        /// The TypeName of the EffectGroup.
        /// </summary>
        /// <remarks>
        /// it SHOULD equals to <see cref="IEffect.TypeName"/>, <see cref="IEffectFactory.TypeName"/> and so on.
        /// </remarks>
        public string TypeName { get; }

        /// <summary>
        /// Indicate which plugin this effect comes from, which is used to determine which plugin to use when creating the effect.
        /// </summary>
        public string FromPlugin { get; }

        /// <summary>
        /// Get the type of the effect, which is used to determine how to process this effect.
        /// </summary>
        public EffectType TypeOfEffect { get; }

        /// <summary>
        /// Get the target of the effect, which is used to determine where this effect can be applied.
        /// </summary>
        public EffectTarget Target { get; }

        /// <summary>
        /// Determine whether this effect is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// The id of the EffectGroup.
        /// </summary>
        /// <remarks>
        /// DO NOT set this property manually. It will be set when the effect group is created.
        /// </remarks>
        public Guid Id { get; set; }

        /// <summary>
        /// Get or set the name for the EffectGroup.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The name of the input anchor.
        /// </summary>
        /// <remarks>
        /// Keep it blank while <see cref="IsBindableEffect"/> is false.
        /// </remarks>
        public string InputAnchorDisplayName { get; }
        /// <summary>
        /// The name of the input anchors.
        /// </summary>
        /// <remarks>
        /// Keep it null except <see cref="IsMultiInput"/> is true.
        /// </remarks>
        public string[]? InputAnchorsDisplayName { get; }
        /// <summary>
        /// The name of the output anchor.
        /// </summary>
        /// <remarks>
        /// Keep it blank except the output of this effect is not <see cref="projectFrameCut.Drawing.Base.IPicture"/>
        /// </remarks>
        public string OutputAnchorDisplayName { get; }


        /// <summary>
        /// The ID of the input effect/argument provider this effect is bound to.
        /// </summary>
        /// <remarks>
        /// Use GUID 00000000-0000-0000-0000-000000000000 for Input Anchor, ffffffff-ffff-ffff-ffff-ffffffffffff for Output Anchor.
        /// </remarks>
        public Guid BindedInputId { get; set; }

        /// <summary>
        /// The ID of the next step of the effect.
        /// </summary>
        /// <remarks>
        /// Use GUID 00000000-0000-0000-0000-000000000000 for Input Anchor, ffffffff-ffff-ffff-ffff-ffffffffffff for Output Anchor.
        /// Keep this field blank when <see cref="IsMultiInput"/> is true.
        /// </remarks>
        public Guid BindedOutputId { get; set; }

        /// <summary>
        /// Determine whether this EffectBundle supports multi input.
        /// </summary>
        public bool IsMultiInput { get; }

        /// <summary>
        /// The IDs of the input effects/argument providers this effect is bound to when <see cref="IsMultiInput"/> is true.
        /// </summary>
        public List<Guid>? BindedInputIds { get; set; }

        /// <summary>
        /// The start point of the continuous range (inclusive).
        /// </summary>
        public int StartPoint { get; set; }

        /// <summary>
        /// The end point of the continuous range (inclusive).
        /// </summary>
        public int EndPoint { get; set; }

        /// <summary>
        /// The arguments of the EffectGroup.
        /// </summary>
        public Dictionary<string, object> Parameters { get; set; }

        /// <summary>
        /// Indicates which parameters are needed for this effect.
        /// </summary>
        public List<string> ParametersNeeded { get; }
        /// <summary>
        /// Indicates the type of each parameter.
        /// </summary>
        public Dictionary<string, string> ParametersType { get; }

        /// <summary>
        /// Create the specified effects.
        /// </summary>
        /// <returns>The final effect(s).</returns>
        public IEffectFactory[] Create();

        /// <summary>
        /// Create the Effect property UI.
        /// </summary>
        /// <remarks>
        /// To maintenance a uniform UI style, you'll need to use <see cref="PropertyPanelBuilder"/>.
        /// </remarks>
        public PropertyPanelBuilder CreateUI();

        /// <summary>
        /// Handle the change of the Effect property UI created via <see cref="CreateUI"/>.
        /// </summary>
        /// <remarks>
        /// Default implementation will simply update the parameter with the new value. Override this method if you need custom handling.
        /// </remarks>
        /// <param name="args">The input arguments for the property panel change event.</param>
        /// <returns>The updated parameters after handling the property panel change.</returns>
        public virtual Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            Parameters[args.Id] = args.Value;
            return Parameters;
        }

        /// <summary>
        /// Get the display information of this effect bundle.
        /// </summary>
        /// <param name="locate"></param>
        /// <returns></returns>
        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null);


    }

    public class EffectBundleComparer : IEqualityComparer<IEffectBundle>
    {
        public bool Equals(IEffectBundle? x, IEffectBundle? y)
        {
            if (x is null || y is null) return false;
            return x.Id == y.Id;
        }

        public int GetHashCode([DisallowNull] IEffectBundle obj)
        {
            return obj.Id.GetHashCode();
        }
    }



    public static class EffectBundleExtensions
    {
        /// <summary>
        /// Configures the created factory with the unified properties (Id, Bindings).
        /// </summary>
        /// <param name="bundle">The effect bundle.</param>
        /// <param name="factory">The factory to configure.</param>
        public static void ConfigureFactory(this IEffectBundle bundle, IEffectFactory factory)
        {
            if (factory is IBindableEffectFactory bindableFactory)
            {
                bindableFactory.ID = bundle.Id.ToString();
                bindableFactory.BindedInputID = bundle.BindedInputId.ToString();
                bindableFactory.BindedInputIDs = bundle.BindedInputIds?.Select(x => x.ToString()).ToArray();
            }
        }
    }

    public class EffectBundleDisplayItem
    {
        public required string Name { get; set; }
        public required string Description { get; set; }
        [JsonIgnore]
        public ImageSource? Thumbnail { get; set; }
        [JsonIgnore]
        public MediaSource? VideoThumbnail { get; set; }

        public EffectParameterInfo? Parameters { get; set; }
    }

}
