using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
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
            => ft & (EffectArgumentFieldType)0x3FF;

        /// <summary>
        /// Get the display color for a port type.
        /// </summary>
        public static Color GetTypeColor(EffectArgumentFieldType ft)
        {
            var baseType = GetBaseType(ft);
            return baseType switch
            {
                EffectArgumentFieldType.IPicture => Color.FromArgb("#4CAF50"),    // Green
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
                EffectArgumentFieldType.IPicture => "P",
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
            return baseType switch
            {
                EffectArgumentFieldType.IPicture => "IPicture",
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
            };
        }

        /// <summary>
        /// Check whether two port types are compatible for connection.
        /// Unknown/CustomType are permissive; same base types are compatible;
        /// numeric family (Numeric/Integer/UInt/Long/ULong) is inter-compatible;
        /// SizeAndPosition is compatible with Size and Position;
        /// otherwise strict equality is required.
        /// </summary>
        public static bool IsPortTypeCompatible(EffectArgumentFieldType source, EffectArgumentFieldType target)
        {
            var src = GetBaseType(source);
            var tgt = GetBaseType(target);

            if (src == EffectArgumentFieldType.Unknown || tgt == EffectArgumentFieldType.Unknown)
                return true;
            if (src == EffectArgumentFieldType.CustomType || tgt == EffectArgumentFieldType.CustomType)
                return true;
            if (src == tgt) return true;

            // Numeric family inter-compatibility
            if (IsNumericFamily(src) && IsNumericFamily(tgt))
                return true;

            // SizeAndPosition ↔ Size / Position
            if (src == EffectArgumentFieldType.SizeAndPosition)
                return tgt == EffectArgumentFieldType.Size || tgt == EffectArgumentFieldType.Position;
            if (tgt == EffectArgumentFieldType.SizeAndPosition)
                return src == EffectArgumentFieldType.Size || src == EffectArgumentFieldType.Position;

            return false;
        }

        private static bool IsNumericFamily(EffectArgumentFieldType ft)
        {
            return ft == EffectArgumentFieldType.Numeric
                || ft == EffectArgumentFieldType.Integer
                || ft == EffectArgumentFieldType.UnsignedInteger
                || ft == EffectArgumentFieldType.Long
                || ft == EffectArgumentFieldType.UnsignedLong;
        }
    }
}