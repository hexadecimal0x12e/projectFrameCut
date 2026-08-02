using CommunityToolkit.Maui.Views;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Text.Json.Serialization;

namespace projectFrameCut.ApplicationAPIBase.Effect
{
    /// <summary>
    /// A record that represents a settable field of an effect bundle or text style provider,
    /// which can be used to make programmatic changes to the effect's properties.
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
            /// Results parsed in <see cref="IEffectProvider.HandlePropertyPanelChange(IEffectProvider, PropertyPanelPropertyChangedEventArgs)"/> should be a <see cref="System.Text.Json.JsonDocument"/> like this:
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
            /// Results parsed should be a <see cref="Shared.ClipPositionTuple"/>.
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
            /// Results parsed should be a <see cref="Shared.ClipPositionTuple"/>.
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
            /// Results parsed should be a <see cref="Shared.ClipPositionTuple"/>.
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
            /// Indicate that the field is a custom type, which means the value of the field can be any type, and the handling of the field should be implemented by the provider.
            /// </summary>
            /// <remarks>
            /// Make sure you have a good reason to use this type, describe well in <see cref="EffectBundleSettableFields.Remarks"/> as it will make the effect bundle less portable and harder to use in other contexts.
            /// </remarks>
            CustomType = 1 << 64,
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
