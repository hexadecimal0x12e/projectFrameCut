using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Processing.Composing;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.Effect
{
    /// <summary>
    /// A normal effect that overlays the integer value of the <c>Value</c> parameter as text
    /// on top of the input picture. The value can be a static int or a dynamic binding to a
    /// value-provider effect (e.g. <see cref="IntArithmeticValueProviderEffect"/>); the text
    /// is re-rendered every frame so a changing value is reflected immediately.
    /// </summary>
    public class IntOverlayEffect : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Int Overlay";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public Dictionary<string, object> Parameters { get; set; } = new();

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
        public bool IsReorderable => true;
        public string TypeName => "IntOverlay";
        public string? BindedEffectProvidingSystemID { get; set; }

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            return new IntOverlayEffect
            {
                Parameters = parameters ?? new Dictionary<string, object>(),
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public void Initialize() { }

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (targetWidth <= 0 || targetHeight <= 0)
                throw new ArgumentException("targetWidth and targetHeight must be positive.");

            int value = DynamicParam.Resolve(Parameters.GetValueOrDefault("Value"), 0);
            Log($"IntOverlayEffect {Id}/{Name}'s value: {value} ({Parameters.GetValueOrDefault("B")?.GetType()?.Name ?? "<null>"})");
            string text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Layout the number as text on a canvas matching the target size, rasterize it
            // (transparent background → a 16bpp picture with alpha), then alpha-overlay it.
            var entry = BuildTextEntry(text);
            var ctx = TextLayoutContext.FromCanvas(targetWidth, targetHeight);
            var vectorCanvas = TextLayoutPipeline.LayoutForRender([entry], ctx, targetWidth, targetHeight);
            if (vectorCanvas.Elements.Count == 0)
                return source;

            var overlayPicture = IVectorContentClip.GlobalDefaultRasterizer.Convert(
                vectorCanvas, targetWidth, targetHeight, transparentBackground: true,
                aaMode: IVectorContentClip.GlobalDefaultAntiAliasMode);

            // Unify bit depth so the composer can blend the two pictures.
            IPicture sourceForCompose = source;
            IPicture overlayForCompose = overlayPicture;
            if (source.BitPerPixel != overlayPicture.BitPerPixel)
            {
                var mode = source.BitPerPixel.Value > overlayPicture.BitPerPixel.Value
                    ? source.BitPerPixel
                    : overlayPicture.BitPerPixel;
                if (source.BitPerPixel != mode) sourceForCompose = source.ToBitPerPixel(mode);
                if (overlayPicture.BitPerPixel != mode) overlayForCompose = overlayPicture.ToBitPerPixel(mode);
            }

            // Dispatch to the strongly-typed overload matching the unified bit depth.
            if (sourceForCompose is IPicture<ushort> uBase && overlayForCompose is IPicture<ushort> uTop)
                return PictureComposer.Default.Compose(uBase, uTop, BlendMode.Overlay, 0, 0, targetWidth, targetHeight);
            if (sourceForCompose is IPicture<byte> bBase && overlayForCompose is IPicture<byte> bTop)
                return PictureComposer.Default.Compose(bBase, bTop, BlendMode.Overlay, 0, 0, targetWidth, targetHeight);

            throw new NotSupportedException($"Unsupported picture bit depth for IntOverlay: {sourceForCompose.BitPerPixel}.");
        }

        /// <summary>
        /// Build a single text entry for the given number, using the effect's style parameters.
        /// All length fields are expressed in canvas pixels (see <see cref="TextLayoutContext"/>).
        /// </summary>
        private TextEntry BuildTextEntry(string text)
        {
            float fontSize = DynamicParam.Resolve(Parameters.GetValueOrDefault("FontSize"), 96f);
            float posX = DynamicParam.Resolve(Parameters.GetValueOrDefault("X"), 40f);
            float posY = DynamicParam.Resolve(Parameters.GetValueOrDefault("Y"), 40f);

            return new TextEntry
            {
                Text = text,
                FontName = Parameters.GetValueOrDefault("FontName")?.ToString() ?? "",
                FontStyle = Parameters.GetValueOrDefault("FontStyle")?.ToString() ?? "Regular",
                FontSize = fontSize,
                X = posX,
                Y = posY,
                FillR = ToUShort(Parameters.GetValueOrDefault("FillR"), ushort.MaxValue),
                FillG = ToUShort(Parameters.GetValueOrDefault("FillG"), ushort.MaxValue),
                FillB = ToUShort(Parameters.GetValueOrDefault("FillB"), ushort.MaxValue),
                FillA = ToFloat(Parameters.GetValueOrDefault("FillA"), 1f),
                StrokeR = ToUShort(Parameters.GetValueOrDefault("StrokeR"), 0),
                StrokeG = ToUShort(Parameters.GetValueOrDefault("StrokeG"), 0),
                StrokeB = ToUShort(Parameters.GetValueOrDefault("StrokeB"), 0),
                StrokeA = ToFloat(Parameters.GetValueOrDefault("StrokeA"), 0f),
                StrokeThickness = DynamicParam.Resolve(Parameters.GetValueOrDefault("StrokeThickness"), 2f),
                CharacterSpacing = DynamicParam.Resolve(Parameters.GetValueOrDefault("CharacterSpacing"), 0f),
                LineSpacing = DynamicParam.Resolve(Parameters.GetValueOrDefault("LineSpacing"), 0.3f),
            };
        }

        private static ushort ToUShort(object? raw, ushort fallback)
        {
            if (EffectParamConvert.TryConvertToInt(raw, out var v)) return (ushort)Math.Clamp(v, 0, ushort.MaxValue);
            return fallback;
        }

        private static float ToFloat(object? raw, float fallback)
        {
            if (EffectParamConvert.TryConvertToFloat(raw, out var v)) return v;
            return fallback;
        }
    }

    /// <summary>
    /// The Render-side provider of the <c>IntOverlay</c> effect: a picture input plus an
    /// int <c>Value</c> (static or bindable to a value provider) and text-style parameters.
    /// </summary>
    public class IntOverlayEffectProvider : EffectProviderBase
    {
        public IntOverlayEffectProvider()
        {
            Name = "Int Overlay";
            SetField("Value", 0);
            SetField("FontSize", 96f);
            SetField("X", 40f);
            SetField("Y", 40f);
            SetField("FillR", (float)ushort.MaxValue);
            SetField("FillG", (float)ushort.MaxValue);
            SetField("FillB", (float)ushort.MaxValue);
            SetField("FillA", 1f);
        }

        public override string TypeName => "IntOverlay";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Value", EffectArgumentFieldType.Integer, "0", remarks: "The integer to overlay as text. Can be bound to a value provider."),
                Field("FontSize", EffectArgumentFieldType.Numeric, "96"),
                Field("X", EffectArgumentFieldType.Numeric, "40"),
                Field("Y", EffectArgumentFieldType.Numeric, "40"),
                Field("FillR", EffectArgumentFieldType.Numeric, "65535", min: "0", max: "65535"),
                Field("FillG", EffectArgumentFieldType.Numeric, "65535", min: "0", max: "65535"),
                Field("FillB", EffectArgumentFieldType.Numeric, "65535", min: "0", max: "65535"),
                Field("FillA", EffectArgumentFieldType.Numeric, "1", min: "0", max: "1"),
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            return [IntOverlayEffect.FromParametersDictionary(parameters)];
        }
    }
}
