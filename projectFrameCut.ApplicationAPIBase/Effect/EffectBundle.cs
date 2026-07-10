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
        /// Get the settable fields of this effect bundle.
        /// Can be used to make programmatic changes to the effect bundle's properties.
        /// </summary>
        public Dictionary<string, EffectBundleSettableFields> SettableFields { get; }

        /// <summary>
        /// Handle the change of the settable fields of this effect bundle.
        /// </summary>
        /// <param name="field">the field that is being changed</param>
        /// <param name="value">the new value for the field</param>
        /// <param name="feedback">feedback message for the change, can be used to provide error messages or other information</param>
        /// <returns>true if the change was successful, false otherwise</returns>
        public bool HandleSettableFieldsChange(EffectBundleSettableFields field, object value, out string feedback);

        /// <summary>
        /// Get the display information of this effect bundle.
        /// </summary>
        /// <param name="locate"></param>
        /// <returns></returns>
        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null);


    }

    /// <summary>
    /// A record that represents a settable field of an effect bundle, 
    /// which can be used to make programmatic changes to the effect bundle's properties.
    /// </summary>
    public record EffectBundleSettableFields
    {
        /// <summary>
        /// A unique identifier for the settable field, used to identify the field when handling changes.
        /// </summary>
        public required string Id { get; init; }
        /// <summary>
        /// The display name of the settable field, used for UI display purposes.
        /// </summary>
        public required string DisplayName { get; init; }
        /// <summary>
        /// A brief description of the settable field, used for UI display purposes.
        /// </summary>
        public required string Description { get; init; }
        /// <summary>
        /// The type of the settable field, used to determine how to handle the field's value.
        /// </summary>
        public required FieldType ValueType { get; init; }
        /// <summary>
        /// The default value of the settable field, used to initialize the field's value when creating a new effect bundle.
        /// </summary>
        public required string DefaultValue { get; init; }
        /// <summary>
        /// The minimum value of the settable field, used to validate the field's value.
        /// </summary>
        public required string MinValue { get; init; }
        /// <summary>
        /// The maximum value of the settable field, used to validate the field's value.
        /// </summary>
        public required string MaxValue { get; init; }

        /// <summary>
        /// A list of preset options for the enum type settable field, used to provide a set of enum options for the field.
        /// </summary>
        public string[]? PresetOptions { get; init; } = null;
        /// <summary>
        /// A simple remark or note about the settable field, used for agentic working.
        /// </summary>
        public string? Remarks { get; init; } = "";


        /// <summary>
        /// Determine the type of the <see cref="EffectBundleSettableFields"/>
        /// </summary>
        [Flags]
        public enum FieldType
        {
            /// <summary>
            /// The type of the field is unknown or not specified.
            /// </summary>
            Unknown = 0,
            /// <summary>
            /// An integer type field.
            /// </summary>
            Integer = 1 << 1,
            /// <summary>
            /// An unsigned integer type field.
            /// </summary>
            /// <remarks>
            /// Mostly used for parameters that represent frame (projectFrameCut uses uint as frame index).
            /// </remarks>
            UnsignedInteger = 1 << 2,
            /// <summary>
            /// A numeric type field. 
            /// </summary>
            /// <remarks>
            /// Mostly represent float or double values.
            /// </remarks>
            Numeric = 1 << 3,
            /// <summary>
            /// A string type field.
            /// </summary>
            String = 1 << 4,
            /// <summary>
            /// A boolean type field.
            /// </summary>
            /// <remarks>
            /// True or false
            /// </remarks>
            Boolean = 1 << 5,
            /// <summary>
            /// A field that represents an enumeration type.
            /// </summary>
            /// <remarks>
            /// The available values of the field are listed under <see cref="EffectBundleSettableFields.PresetOptions"/>. The value of the field should be one of the available options.
            /// </remarks>
            Enum = 1 << 6,
            /// <summary>
            /// A field that represents a keyframe data in <see cref="KeyFrameStepInfo"/>.
            /// </summary>
            KeyFrames = 1 << 7,
            /// <summary>
            /// A field that represents a 16bit with alpha color value.
            /// </summary>
            /// <remarks>
            /// Results parsed in <see cref="IEffectBundle.HandleSettableFieldsChange"/> should be a <see cref="System.Text.Json.JsonDocument"/> like this:
            /// <code>
            /// {
            ///     "r": 65535,
            ///     "g": 65535,
            ///     "b": 65535,
            ///     "a": 1.0
            /// }
            /// </code>
            /// 
            /// the R,G,B values are 16bit unsigned integers, and A is a float between 0.0 and 1.0, or null for no-alpha mode.
            /// See more details in <see cref="projectFrameCut.Drawing.Base.IPicture"/>.
            /// </remarks>
            Color = 1 << 8,
            /// <summary>
            /// A size type field, which is a pair of width and height values.
            /// </summary>
            /// <remarks>
            /// Results parsed in <see cref="IEffectBundle.HandleSettableFieldsChange"/> should be a <see cref="Shared.ClipPositionTuple"/>.
            /// Only the <see cref="Shared.ClipPositionTuple.TargetWidth"/> and <see cref="Shared.ClipPositionTuple.TargetHeight"/> properties are used.
            /// <br />
            /// Width and Height are both integers, and should be positive values.
            /// Other properties of <see cref="Shared.ClipPositionTuple"/> are unset and should be ignored.
            /// </remarks>
            Size = 1 << 9,
            /// <summary>
            /// A position type field, which is a pair of X and Y values.
            /// </summary>
            /// <remarks>
            /// Results parsed in <see cref="IEffectBundle.HandleSettableFieldsChange"/> should be a <see cref="Shared.ClipPositionTuple"/>.
            /// Only the <see cref="Shared.ClipPositionTuple.TargetX"/> and <see cref="Shared.ClipPositionTuple.TargetY"/> properties are used.
            /// <br />
            /// X and Y are both integers, and should be positive values, starting from the top-left corner of the canvas.
            /// Other properties of <see cref="Shared.ClipPositionTuple"/> are unset and should be ignored.
            /// </remarks>
            Position = 1 << 10,

            /// <summary>
            /// A field that represents both size and position, which is a combination of <see cref="Size"/> and <see cref="Position"/>.
            /// </summary>
            /// <remarks>
            /// Results parsed in <see cref="IEffectBundle.HandleSettableFieldsChange"/> should be a <see cref="Shared.ClipPositionTuple"/>.
            /// <br />
            /// All fields except <see cref="Shared.ClipPositionTuple.IsDelta"/> of <see cref="Shared.ClipPositionTuple"/> are used to present the size and position of the effect.
            /// </remarks>
            SizeAndPosition = Size | Position,

            /// <summary>
            /// Indicate that the field is a mandatory field, which means it must be set before the effect can be applied.
            /// </summary>
            Mandatory = 1 << 16,
            /// <summary>
            /// Indicate that the field has a minimum value, which means the value of the field should not be less than the specified minimum value.
            /// </summary>
            HasMinValue = 1 << 17,
            /// <summary>
            /// Indicate that the field has a maximum value, which means the value of the field should not be greater than the specified maximum value.
            /// </summary>
            HasMaxValue = 1 << 18,

            /// <summary>
            /// Indicate that the field is a custom type, which means the value of the field can be any type, and the handling of the field should be implemented in <see cref="IEffectBundle.HandleSettableFieldsChange"/>.
            /// </summary>
            /// <remarks>
            /// Make sure you have a good reason to use this type, describe well in <see cref="EffectBundleSettableFields.Remarks"/> as it will make the effect bundle less portable and harder to use in other contexts.
            /// </remarks>
            CustomType = 1 << 64,
        }
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
