namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface IEffectArgumentField
    {
        /// <summary>
        /// The id of the field, which is used to identify the field in the effect provider.
        /// </summary>
        public string Id { get; }
        /// <summary>
        /// Indicates the type name of the field, which is used to determine how to process the field.
        /// </summary>
        public string TypeName { get; }
        /// <summary>
        /// Indicate which plugin this field comes from, which is used to determine which plugin to use when creating the field.
        /// </summary>
        public string FromPlugin { get; }
        /// <summary>
        /// Indicate whether the field is dynamic, which means the field can be changed at runtime and the effect provider should handle the change accordingly.
        /// </summary>
        public bool IsDynamic { get; }
        /// <summary>
        /// Determine the type of the field, which is used to determine how to process the field.
        /// </summary>
        public EffectArgumentFieldType FieldType { get; }

        /// <summary>
        /// The default value of the settable field, used to initialize the field's value when creating a new effect bundle.
        /// </summary>
        public string DefaultValue { get; set; }
        /// <summary>
        /// The minimum value of the settable field, used to validate the field's value.
        /// </summary>
        public string MinValue { get; set; }
        /// <summary>
        /// The maximum value of the settable field, used to validate the field's value.
        /// </summary>
        public string MaxValue { get; set; }

        /// <summary>
        /// A list of preset options for the enum type settable field, used to provide a set of enum options for the field.
        /// </summary>
        public string[]? PresetOptions { get; set; }
        /// <summary>
        /// A simple remark or note about the settable field, used for agentic working.
        /// </summary>
        public string? Remarks { get; set; }

        /// <summary>
        /// Get a lazy getter for the field's value, which is used to get the current value of the field. The getter should be implemented by the effect provider and should return the current value of the field.
        /// </summary>
        /// <returns>The lazy getter for the field's value.</returns>
        public Lazy<object> GetGetter();
    }

    /// <summary>
    /// Determine the type of the <see cref="IEffectArgumentField"/>
    /// </summary>
    [Flags]
    public enum EffectArgumentFieldType
    {
        /// <summary>
        /// The type of the field is unknown or not specified.
        /// </summary>
        Unknown = 0,
        /// <summary>
        /// An integer (int) type field.
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
        /// A field that represents a 16bit with alpha color value.
        /// </summary>
        /// <remarks>
        /// Results should be a <see cref="Drawing.Base.PictureExtensions.Pixel{T}"/> like this:
        /// the R,G,B values are 16bit/8bit unsigned integers, and A is a float between 0.0 and 1.0, or null for no-alpha mode.
        /// </remarks>
        Color = 1 << 6,
        /// <summary>
        /// A size type field, which is a pair of width and height values.
        /// </summary>
        /// <remarks>
        /// Results should be a <see cref="Shared.ClipPositionTuple"/>.
        /// Only the <see cref="Shared.ClipPositionTuple.TargetWidth"/> and <see cref="Shared.ClipPositionTuple.TargetHeight"/> properties are used.
        /// <br />
        /// Width and Height are both integers, and should be positive values.
        /// Other properties of <see cref="Shared.ClipPositionTuple"/> are unset and should be ignored.
        /// </remarks>
        Size = 1 << 7,
        /// <summary>
        /// A position type field, which is a pair of X and Y values.
        /// </summary>
        /// <remarks>
        /// Results should be a <see cref="Shared.ClipPositionTuple"/>.
        /// Only the <see cref="Shared.ClipPositionTuple.TargetX"/> and <see cref="Shared.ClipPositionTuple.TargetY"/> properties are used.
        /// <br />
        /// X and Y are both integers, and should be positive values, starting from the top-left corner of the canvas.
        /// Other properties of <see cref="Shared.ClipPositionTuple"/> are unset and should be ignored.
        /// </remarks>
        Position = 1 << 8,

        /// <summary>
        /// A field that represents both size and position, which is a combination of <see cref="Size"/> and <see cref="Position"/>.
        /// </summary>
        /// <remarks>
        /// Results should be a <see cref="Shared.ClipPositionTuple"/>.
        /// <br />
        /// All fields except <see cref="Shared.ClipPositionTuple.IsDelta"/> of <see cref="Shared.ClipPositionTuple"/> are used to present the size and position of the effect.
        /// </remarks>
        SizeAndPosition = Size | Position,

        /// <summary>
        /// A field that represents a picture, which is a <see cref="projectFrameCut.Drawing.Base.IPicture"/>
        /// </summary>
        IPicture = 1 << 9,

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
        /// Indicate that the field is a custom type, which means the value of the field can be any type, and the handling of the field should be implemented in <see cref="IEffectProvider.Build"/>.
        /// </summary>
        /// <remarks>
        /// Make sure you have a good reason to use this type, describe well in <see cref="IEffectArgumentField.Remarks"/> as it will make the effect bundle less portable and harder to use in other contexts.
        /// </remarks>
        CustomType = 1 << 64,
    }

    /// <summary>
    /// A static effect argument field that holds a value of type <see cref="object"/>. 
    /// This class is used to represent a static value for an effect argument field, which is not dynamic and does not change at runtime.
    /// </summary>
    public record StaticEffectArgumentField : StaticEffectArgumentField<object>
    {
        public StaticEffectArgumentField(object value, EffectArgumentFieldType fieldType) : base(value, fieldType)
        {
        }

        public StaticEffectArgumentField() : base()
        {
        }
    }

    /// <summary>
    /// A static effect argument field that holds a value of type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public record StaticEffectArgumentField<T> : IEffectArgumentField where T : notnull
    {
        public string Id { get; set; } = "Static value";

        public string TypeName => "StaticEffectArgumentField";

        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";

        public bool IsDynamic => false;

        /// <summary>
        /// Determine the type of the field, which is used to determine how to process the field.
        /// </summary>
        public required EffectArgumentFieldType FieldType { get; set; }

        /// <summary>
        /// The value of the static effect argument field. This property is required and must be set when creating an instance of <see cref="StaticEffectArgumentField{T}"/>.
        /// </summary>
        public required T Value { get; set; }

        public string DefaultValue { get; set; } = default(T)?.ToString() ?? string.Empty;
        public string MinValue { get; set; } = default(T)?.ToString() ?? string.Empty;
        public string MaxValue { get; set; } = default(T)?.ToString() ?? string.Empty;
        public string[]? PresetOptions { get; set; }
        public string? Remarks { get; set; }

        public StaticEffectArgumentField(T value, EffectArgumentFieldType fieldType)
        {
            FieldType = fieldType;
            Value = value;
        }

        public StaticEffectArgumentField() { }

        public Lazy<object> GetGetter() => new(Value);
    }
}
