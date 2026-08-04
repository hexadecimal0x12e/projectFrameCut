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
        /// Indicates whether the field's value can change at render time while targeting frame changes.
        /// When true, the effect receives <see cref="GetGetter"/> as a <see cref="Func{T}"/> in its <see cref="IEffect.Parameters"/>.
        /// When false, the value is evaluated once at build time and stored as a static value.
        /// </summary>
        public bool IsDynamicAtRenderTime { get; }

        /// <summary>
        /// Get a getter for the field's current value. The getter should be implemented by the effect provider
        /// and should return the current value of the field. For dynamic fields, this returns a closure that
        /// re-evaluates every call; for static fields it returns a constant.
        /// </summary>
        /// <returns>A function that returns the current value of the field.</returns>
        public Func<object> GetGetter();
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
        /// A field that represents a picture, which is a <see cref="projectFrameCut.Drawing.Base.IPicture"/>
        /// </summary>
        IPicture = 1 << 1,

        /// <summary>
        /// An integer (<see langword="int"/>) type field.
        /// </summary>
        Integer = 1 << 2,
        /// <summary>
        /// An unsigned integer (<see langword="uint"/>) type field.
        /// </summary>
        /// <remarks>
        /// Mostly used for parameters that represent frame (projectFrameCut uses uint as frame index).
        /// </remarks>
        UnsignedInteger = 1 << 3,
        /// <summary>
        /// A long (<see langword="long"/>) type field.
        /// </summary>
        Long = 1 << 4,
        /// <summary>
        /// A unsigned long (<see langword="ulong"/>) type field.
        /// </summary>
        UnsignedLong = 1 << 5,
        /// <summary>
        /// A numeric type field indicates a floating-point number, which is mostly represented by <see langword="float"/>.
        /// </summary>
        /// <remarks>
        /// Mostly represent a <see langword="float"/> value. 
        /// Some times it can be a <see langword="double"/> value.
        /// The effect creator will automatically convert the value to the correct type when passing the value to the effect provider.
        /// </remarks>
        Numeric = 1 << 6,

        /// <summary>
        /// A string type field.
        /// </summary>
        /// <remarks>
        /// If the field doesn't have <see cref="Mandatory"/> bit set, the default value will be same as <see cref="IEffectArgumentField.DefaultValue"/>.
        /// </remarks>
        String = 1 << 7,
        /// <summary>
        /// A boolean type field.
        /// </summary>
        /// <remarks>
        /// represents <see langword="true"/> or <see langword="false"/>.
        /// If the field doesn't have <see cref="Mandatory"/> bit set, the default value will be same as <see cref="IEffectArgumentField.DefaultValue"/> parsed to a boolean value, or <see langword="false"/> if the default value is not a valid boolean value.
        /// </remarks>
        Boolean = 1 << 8,
        /// <summary>
        /// A field that represents a 16bit with alpha color value.
        /// </summary>
        /// <remarks>
        /// Results should be a <see cref="Drawing.Base.PictureExtensions.Pixel{T}"/> like this:
        /// the R,G,B values are 16bit/8bit unsigned integers, and A is a float between 0.0 and 1.0, or null for no-alpha mode.
        /// </remarks>
        Color = 1 << 9,

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
        Size = 1 << 10,
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
        Position = 1 << 11,
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
        /// If set, the field will not be visible in the effect panel, which means the user cannot see or modify the field in the effect panel.
        /// </summary>
        NotVisibleInEffectPanel = 1 << 19,
        /// <summary>
        /// Indicate that the field is for internal use only, which means the field is used by the effect provider internally and should not be modified by the user.
        /// </summary>
        InternalUseOnly = 1 << 20,

        /// <summary>
        /// Indicate that the field cannot be a static value.
        /// If set, the field must be a dynamic value, and in Effect UI Panel the static value input will be disabled, and the user must bind a dynamic value to the field.
        /// </summary>
        /// <remarks>
        /// DO NOT Define both <see cref="CanNotBeStatic"/> and <see cref="CannotBeDynamic"/> at the same time, as it will make a exception.
        /// </remarks>
        CanNotBeStatic = 1 << 21,
        /// <summary>
        /// Indicate that the field cannot be a dynamic value.
        /// If set, the field must be a static value, and in Effect UI Panel the dynamic value binding option will be disabled, and the user must set a static value to the field.
        /// </summary>
        /// <remarks>
        /// DO NOT Define both <see cref="CanNotBeStatic"/> and <see cref="CannotBeDynamic"/> at the same time, as it will make a exception.
        /// </remarks>
        CannotBeDynamic = 1 << 22,

        /// <summary>
        /// Indicate that the field supports 8-bit color input.
        /// </summary>
        /// <remarks>
        /// Only meaningful when the field is a <see cref="IPicture"/> or <see cref="Color"/> type field. 
        /// </remarks>
        Supports8BitColor = 1 << 23,
        /// <summary>
        /// Indicate that the field supports 16-bit color input.
        /// </summary>
        /// <remarks>
        /// Only meaningful when the field is a <see cref="IPicture"/> or <see cref="Color"/> type field. 
        /// </remarks>
        Supports16BitColor = 1 << 24,
        /// <summary>
        /// Indicate that the field supports 16-bit color with Brightness channel input.
        /// </summary>
        /// <remarks>
        /// Only meaningful when the field is a <see cref="IPicture"/> type field. 
        /// </remarks>
        SupportsHDR = 1 << 25,

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
        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
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

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public StaticEffectArgumentField(T value, EffectArgumentFieldType fieldType)
        {
            FieldType = fieldType;
            Value = value;
        }

        public StaticEffectArgumentField() { }

        public bool IsDynamicAtRenderTime => false;

        public Func<object> GetGetter() => () => Value;
    }

    /// <summary>
    /// A descriptor for an effect argument field, which is a minimal implementation of the <see cref="IEffectArgumentField"/> interface.
    /// </summary>
    /// <remarks>
    /// When used as a normal effect argument field, the <see cref="DefaultValue"/> property is used as the initial value of the field, and the <see cref="GetGetter"/> method returns a function that always returns the <see cref="DefaultValue"/>.
    /// </remarks>
    public record EffectArgumentFieldDescriptor : IEffectArgumentField
    {
        public string Id { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public string FromPlugin { get; set; } = string.Empty;
        public bool IsDynamic { get; set; } = false;
        public EffectArgumentFieldType FieldType { get; set; } = EffectArgumentFieldType.Unknown;
        public string DefaultValue { get; set; } = string.Empty;
        public string MinValue { get; set; } = string.Empty;
        public string MaxValue { get; set; } = string.Empty;
        public string[]? PresetOptions { get; set; }
        public string? Remarks { get; set; }

        public bool IsDynamicAtRenderTime => false;

        public Func<object> GetGetter()
        {
            return () => DefaultValue;
        }
    }
}
