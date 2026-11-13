using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Plugin;
using Color = Microsoft.Maui.Graphics.Color;

namespace projectFrameCut.DraftStuff
{
    /// <summary>
    /// Static helper for port type classification, compatibility checking, and visual styling.
    /// </summary>
    public static class PortTypeHelper
    {
        /// <summary>
        /// Strip high-bit flags (HasMinValue, HasMaxValue, Mandatory, etc.) to get the base type.
        /// </summary>
        public static EffectArgumentFieldType GetBaseType(EffectArgumentFieldType ft)
            => ft.IsHwAccelPicture() ? EffectArgumentFieldType.IPicture : ft.BaseType();

        /// <summary>
        /// Get the display color for a port type.
        /// </summary>
        public static Color GetTypeColor(EffectArgumentFieldType ft)
        {
            var baseType = GetBaseType(ft);
            return baseType switch
            {
                EffectArgumentFieldType.IPicture => Color.FromArgb("#4CAF50"),    // Green
                EffectArgumentFieldType.VectorPicture => Color.FromArgb("#A6C436"),
                EffectArgumentFieldType.AudioSample => Color.FromArgb("#E0B04B"),
                EffectArgumentFieldType.SpeedVariance => Color.FromArgb("#26A69A"),
                EffectArgumentFieldType.Numeric => Color.FromArgb("#1E90FF"),      // Blue
                EffectArgumentFieldType.Integer => Color.FromArgb("#4169E1"),      // Royal Blue
                EffectArgumentFieldType.UnsignedInteger => Color.FromArgb("#87CEEB"), // Sky Blue
                EffectArgumentFieldType.Long => Color.FromArgb("#0000CD"),         // Medium Blue
                EffectArgumentFieldType.Boolean => Color.FromArgb("#FF9800"),      // Orange
                EffectArgumentFieldType.String => Color.FromArgb("#9B59B6"),       // Purple
                EffectArgumentFieldType.Color => Color.FromArgb("#00BCD4"),        // Cyan
                EffectArgumentFieldType.Size => Color.FromArgb("#008080"),         // Teal
                EffectArgumentFieldType.Position => Color.FromArgb("#E91E63"),     // Pink
                EffectArgumentFieldType.SizeAndPosition => Color.FromArgb("#E040FB"), // Magenta
                _ => Color.FromArgb("#808080"),                                   // Gray
            };
        }

        /// <summary>
        /// Get a single-character glyph for a port type.
        /// </summary>
        public static string GetTypeGlyph(EffectArgumentFieldType ft)
        {
            var baseType = GetBaseType(ft);
            return baseType switch
            {
                EffectArgumentFieldType.Any => "*",
                EffectArgumentFieldType.IPicture => "P",
                EffectArgumentFieldType.VectorPicture => "V",
                EffectArgumentFieldType.AudioSample => "A",
                EffectArgumentFieldType.SpeedVariance => "T",
                EffectArgumentFieldType.Numeric => "N",
                EffectArgumentFieldType.Integer => "I",
                EffectArgumentFieldType.UnsignedInteger => "U",
                EffectArgumentFieldType.Long => "L",
                EffectArgumentFieldType.Boolean => "B",
                EffectArgumentFieldType.String => "S",
                EffectArgumentFieldType.Color => "C",
                EffectArgumentFieldType.Size => "D",
                EffectArgumentFieldType.Position => "X",
                EffectArgumentFieldType.SizeAndPosition => "R",
                _ => "?",
            };
        }

        /// <summary>
        /// Get a human-readable name for a port type.
        /// </summary>
        public static string HumanizeTypeName(EffectArgumentFieldType ft)
        {
            var baseType = GetBaseType(ft);
            return PluginManager.GetLocalizationItem("Effect_PortType_" + baseType, baseType switch
            {
                EffectArgumentFieldType.Any => "Any",
                EffectArgumentFieldType.CustomType => "Custom",
                EffectArgumentFieldType.IPicture => "IPicture",
                EffectArgumentFieldType.VectorPicture => Localized.Effect_VectorContentType,
                EffectArgumentFieldType.AudioSample => "AudioSample",
                EffectArgumentFieldType.SpeedVariance => "Speed provider",
                EffectArgumentFieldType.Numeric => "Numeric",
                EffectArgumentFieldType.Integer => "Integer",
                EffectArgumentFieldType.UnsignedInteger => "UInt",
                EffectArgumentFieldType.Long => "Long",
                EffectArgumentFieldType.UnsignedLong => "ULong",
                EffectArgumentFieldType.Boolean => "Boolean",
                EffectArgumentFieldType.String => "String",
                EffectArgumentFieldType.Color => "Color",
                EffectArgumentFieldType.Size => "Size",
                EffectArgumentFieldType.Position => "Position",
                EffectArgumentFieldType.SizeAndPosition => "Size&Pos",
                _ => "?",
            });
        }

        public static string HumanizeTypeName(EffectArgumentFieldType ft, EffectPortTypeContract? contract) =>
            ft.BaseType() == EffectArgumentFieldType.CustomType && contract is not null ? contract.DisplayName : HumanizeTypeName(ft);

        /// <summary>
        /// Check whether two port types are compatible for connection.
        /// Custom ports require matching contracts; same base types are compatible;
        /// numeric family (Numeric/Integer/UInt/Long/ULong) is inter-compatible;
        /// SizeAndPosition is compatible with Size and Position;
        /// otherwise strict equality is required.
        /// </summary>
        public static bool IsPortTypeCompatible(EffectArgumentFieldType source, EffectArgumentFieldType target)
            => EffectFieldTypes.AreCompatible(source, target);

        public static bool IsPortTypeCompatible(EffectArgumentFieldType source, EffectArgumentFieldType target,
            EffectPortTypeContract? sourceContract, EffectPortTypeContract? targetContract)
            => EffectFieldTypes.AreCompatible(source, target, sourceContract, targetContract);
    }
}
