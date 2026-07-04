using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent;
using TextAlignment = projectFrameCut.Drawing.Text.Entry.TextAlignment;

namespace projectFrameCut.Render.VectorContent.Components;

/// <summary>
/// A text component that integrates <see cref="TextLayoutPipeline"/> rendering
/// into the <see cref="IVectorComponent"/> system.  Text is laid out on a
/// virtual 1920×1080 reference canvas and then positioned via the standard
/// RelativeX/RelativeY component parameters.
///
/// Provider metadata (<c>TextStyleProvider_*</c>) is stored in
/// <see cref="Parameters"/> so that the property panel can reconstruct the
/// <see cref="ITextClipStyleProvider"/> for a rich editing experience.
/// </summary>
public class TextComponent : IVectorComponent
{
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public string TypeName => "Text";
    public string Name { get; set; } = string.Empty;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Dictionary<string, object> Parameters { get; set; } = new();
    public int Index { get; set; }
    public List<VectorAnimationKeyFrame> AnimationFrames { get; set; } = new();
    public IReadOnlyDictionary<string, AnimatableField> AnimatableFields { get; }

    /// <summary>Virtual canvas dimensions used for text layout.</summary>
    private const int RefCanvasW = 1920;
    private const int RefCanvasH = 1080;

    public TextComponent()
    {
        EnsureDefaultParameters();

        var map = new Dictionary<string, AnimatableField>(AnimatableFieldMap.CommonFields);
        foreach (var fieldId in new[] { "FontSize", "CharacterSpacing", "LineSpacing" })
        {
            if (AnimatableFieldMap.ShapeFields.TryGetValue(fieldId, out var field))
                map[fieldId] = field;
        }
        AnimatableFields = map;
    }

    private void EnsureDefaultParameters()
    {
        Parameters ??= new();

        // ── Text content ──
        Parameters.TryAdd("Text", "Text");
        Parameters.TryAdd("FontName", "");
        Parameters.TryAdd("FontStyle", "Regular");
        Parameters.TryAdd("FontSize", 120f);
        Parameters.TryAdd("TextAlignment", (int)TextAlignment.Left);
        Parameters.TryAdd("CharacterSpacing", 0f);
        Parameters.TryAdd("LineSpacing", 0.3f);
        Parameters.TryAdd("StrokeThickness", 2f);

        // ── Provider metadata (for property-panel reconstruction) ──
        Parameters.TryAdd("TextStyleProvider_FromPlugin", InternalPluginBase.InternalPluginBaseID);
        Parameters.TryAdd("TextStyleProvider_TypeName", "Basic");

        // ── Position / transform (standard across all components) ──
        Parameters.TryAdd("RelativeX", 0.5f);
        Parameters.TryAdd("RelativeY", 0.5f);
        Parameters.TryAdd("Rotation", 0f);
        Parameters.TryAdd("LayerIndex", 0);

        // ── Fill (white by default for text) ──
        Parameters.TryAdd("FillR", (float)ushort.MaxValue);
        Parameters.TryAdd("FillG", (float)ushort.MaxValue);
        Parameters.TryAdd("FillB", (float)ushort.MaxValue);
        Parameters.TryAdd("FillA", 1f);

        // ── Stroke (transparent by default) ──
        Parameters.TryAdd("StrokeR", 0f);
        Parameters.TryAdd("StrokeG", 0f);
        Parameters.TryAdd("StrokeB", 0f);
        Parameters.TryAdd("StrokeA", 0f);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Rendering
    // ═══════════════════════════════════════════════════════════════

    public VectorCanvasElement Compute(float normalizedProgress)
    {
        EnsureDefaultParameters();

        var ctx = TextLayoutContext.FromCanvas(RefCanvasW, RefCanvasH);
        var entries = BuildTextEntries(ctx, normalizedProgress);
        if (entries.Count == 0)
            return CreateEmptyElement();

        var picture = TextLayoutPipeline.LayoutForRender(entries, ctx, RefCanvasW, RefCanvasH);

        var element = picture.Elements.Count > 0
            ? picture.Elements[0]
            : CreateEmptyElement();

        ApplyPosition(element, normalizedProgress);
        return element;
    }

    public IEnumerable<VectorCanvasElement> ComputeAll(float normalizedProgress)
    {
        EnsureDefaultParameters();

        var ctx = TextLayoutContext.FromCanvas(RefCanvasW, RefCanvasH);
        var entries = BuildTextEntries(ctx, normalizedProgress);
        if (entries.Count == 0)
            yield break;

        var picture = TextLayoutPipeline.LayoutForRender(entries, ctx, RefCanvasW, RefCanvasH);

        float relX = AnimationFrames.EvaluateField("RelativeX", normalizedProgress, Parameters.GetFloat("RelativeX", 0.5f));
        float relY = AnimationFrames.EvaluateField("RelativeY", normalizedProgress, Parameters.GetFloat("RelativeY", 0.5f));
        float rot  = AnimationFrames.EvaluateField("Rotation", normalizedProgress, Parameters.GetFloat("Rotation", 0f));
        int layer  = (int)AnimationFrames.EvaluateField("LayerIndex", normalizedProgress, Parameters.GetFloat("LayerIndex", Index));

        foreach (var element in picture.Elements)
        {
            // Store component position in BaseX/Y, NOT RelativeX/Y
            // (RelativeX/Y carry the typesetting-engine cursor position)
            element.BaseX      = relX;
            element.BaseY      = relY;
            element.Rotation   = rot;
            element.LayerIndex = layer;
            yield return element;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private void ApplyPosition(VectorCanvasElement element, float progress)
    {
        // GlyphCanvasElement uses UseUniformScale=true; its screen origin is:
        //   originX = BaseX * canvasW + RelativeX * uniform
        // RelativeX is set by the typesetting engine to the glyph's cursor
        // position — we MUST NOT overwrite it.  Instead we store the component
        // position in BaseX/BaseY so the two offsets compose correctly.
        element.BaseX      = AnimationFrames.EvaluateField("RelativeX", progress, Parameters.GetFloat("RelativeX", 0.5f));
        element.BaseY      = AnimationFrames.EvaluateField("RelativeY", progress, Parameters.GetFloat("RelativeY", 0.5f));
        element.Rotation   = AnimationFrames.EvaluateField("Rotation", progress, Parameters.GetFloat("Rotation", 0f));
        element.LayerIndex = (int)AnimationFrames.EvaluateField("LayerIndex", progress, Parameters.GetFloat("LayerIndex", Index));
    }

    private static VectorCanvasElement CreateEmptyElement() =>
        ShapeCanvasElement.DrawRectangle(0, 0);

    private List<TextEntry> BuildTextEntries(TextLayoutContext ctx, float progress)
    {
        string text = Parameters.TryGetValue("Text", out var rawText)
            ? rawText?.ToString() ?? "Text"
            : "Text";
        if (string.IsNullOrEmpty(text))
            return new List<TextEntry>();

        float fontSize = AnimationFrames.EvaluateField("FontSize", progress, Parameters.GetFloat("FontSize", 120f));

        var entry = new TextEntry
        {
            Text = text,
            FontName = Parameters.TryGetValue("FontName", out var fn) ? fn?.ToString() ?? "" : "",
            FontStyle = Parameters.TryGetValue("FontStyle", out var fs) ? fs?.ToString() ?? "Regular" : "Regular",
            FontSize = fontSize,
            X = 0f,
            Y = 0f,
            FillR = (ushort)Math.Clamp(AnimationFrames.EvaluateField("FillR", progress, Parameters.GetFloat("FillR", ushort.MaxValue)), 0f, 65535f),
            FillG = (ushort)Math.Clamp(AnimationFrames.EvaluateField("FillG", progress, Parameters.GetFloat("FillG", ushort.MaxValue)), 0f, 65535f),
            FillB = (ushort)Math.Clamp(AnimationFrames.EvaluateField("FillB", progress, Parameters.GetFloat("FillB", ushort.MaxValue)), 0f, 65535f),
            FillA = AnimationFrames.EvaluateField("FillA", progress, Parameters.GetFloat("FillA", 1f)),
            StrokeR = (ushort)Math.Clamp(AnimationFrames.EvaluateField("StrokeR", progress, Parameters.GetFloat("StrokeR", 0f)), 0f, 65535f),
            StrokeG = (ushort)Math.Clamp(AnimationFrames.EvaluateField("StrokeG", progress, Parameters.GetFloat("StrokeG", 0f)), 0f, 65535f),
            StrokeB = (ushort)Math.Clamp(AnimationFrames.EvaluateField("StrokeB", progress, Parameters.GetFloat("StrokeB", 0f)), 0f, 65535f),
            StrokeA = AnimationFrames.EvaluateField("StrokeA", progress, Parameters.GetFloat("StrokeA", 0f)),
            StrokeThickness = Parameters.GetFloat("StrokeThickness", 2f),
            CharacterSpacing = AnimationFrames.EvaluateField("CharacterSpacing", progress, Parameters.GetFloat("CharacterSpacing", 0f)),
            LineSpacing = AnimationFrames.EvaluateField("LineSpacing", progress, Parameters.GetFloat("LineSpacing", 0.3f)),
            Alignment = (TextAlignment)(int)Parameters.GetFloat("TextAlignment", 0f),
        };

        return new List<TextEntry> { entry };
    }
}
