using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.VectorContent.Components;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using Point = projectFrameCut.Drawing.Vector.Point;
using projectFrameCut.Render.VectorContent;

namespace projectFrameCut.Render.ClipsAndTracks;

/// <summary>
/// Lightweight IClip wrapper around an <see cref="IVectorComponent"/> so that
/// the editor can manage component layout
/// (drag, resize, snap, reference lines) without knowing about the vector animation domain.
/// </summary>
/// <remarks>
/// Coordinate mapping:
///   centerX = RelativeX * canvasW + BaseX * canvasW
///   centerY = RelativeY * canvasH + BaseY * canvasH
///   TargetX  = centerX - TargetWidth  / 2
///   TargetY  = centerY - TargetHeight / 2
///
/// Reverse:
///   RelativeX = (TargetX + TargetWidth/2 - BaseX * canvasW) / canvasW
///   RelativeY = (TargetY + TargetHeight/2 - BaseX * canvasW) / canvasW
/// </remarks>
public partial class VectorComponentWrapperClip : IClip
{
    private readonly record struct LocalBounds(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => Math.Max(0d, MaxX - MinX);
        public double Height => Math.Max(0d, MaxY - MinY);
        public bool IsValid =>
            double.IsFinite(MinX) && double.IsFinite(MinY) &&
            double.IsFinite(MaxX) && double.IsFinite(MaxY) &&
            Width > 0d && Height > 0d;
    }

    private readonly record struct PixelBounds(double X, double Y, double Width, double Height)
    {
        public bool IsValid =>
            double.IsFinite(X) && double.IsFinite(Y) &&
            double.IsFinite(Width) && double.IsFinite(Height) &&
            Width > 0d && Height > 0d;
    }

    // ── Component reference ──────────────────────────────────

    /// <summary>The wrapped component — never null after construction.</summary>
    [JsonIgnore]
    public IVectorComponent Component { get; }

    /// <summary>Editor-side: cached SVG elements for imported-SVG components.</summary>
    [JsonIgnore]
    public List<VectorCanvasElement>? CachedSvgElements { get; set; }

    /// <summary>Duration in frames. Defaults to 30.</summary>
    [JsonIgnore]
    public uint DurationInFrames { get; set; } = 30;

    /// <summary>Reference width of the parent canvas, used for coordinate mapping.</summary>
    [JsonIgnore]
    public int ParentCanvasWidth { get; set; } = 1920;

    /// <summary>Reference height of the parent canvas, used for coordinate mapping.</summary>
    [JsonIgnore]
    public int ParentCanvasHeight { get; set; } = 1080;

    // ── Parameter key constants ───────────────────────────────

    private const string KeyRelativeX = "RelativeX";
    private const string KeyRelativeY = "RelativeY";
    private const string KeyBaseX = "BaseX";
    private const string KeyBaseY = "BaseY";
    private const string KeyRotation = "Rotation";
    private const string KeyLayerIndex = "LayerIndex";
    private const string KeyStrokeR = "StrokeR";
    private const string KeyStrokeG = "StrokeG";
    private const string KeyStrokeB = "StrokeB";
    private const string KeyStrokeA = "StrokeA";
    private const string KeyFillR = "FillR";
    private const string KeyFillG = "FillG";
    private const string KeyFillB = "FillB";
    private const string KeyFillA = "FillA";
    private const string KeyThickness = "Thickness";

    // ── Parameter helpers ─────────────────────────────────────

    private float GetFloatParam(string key, float defaultValue)
    {
        if (Component.Parameters.TryGetValue(key, out var val) && val is not null)
        {
            return val switch
            {
                float f => f,
                double d => (float)d,
                int i => i,
                uint u => u,
                long l => l,
                ushort us => us,
                decimal m => (float)m,
                JsonElement { ValueKind: JsonValueKind.Number } je => je.GetSingle(),
                JsonElement { ValueKind: JsonValueKind.String } je when float.TryParse(je.GetString(), out var parsed) => parsed,
                _ => defaultValue,
            };
        }
        return defaultValue;
    }

    private void SetFloatParam(string key, float value)
    {
        Component.Parameters[key] = value;
    }

    // ── IClip identity ───────────────────────────────────────

    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public ClipMode ClipType => ClipMode.VectorCanvasClip;
    string IClip.TypeName => "ComponentClip";

    public Guid Id
    {
        get => Component.Id;
        init { }
    }

    public string Name
    {
        get => Component.Name ?? "Unnamed Component";
        init { }
    }

    public string BindedSoundTrack { get; init; } = string.Empty;

    // ── Layer / Z-order ──────────────────────────────────────

    public uint LayerIndex
    {
        get => (uint)Math.Max(0, Component.Index);
        init { }
    }

    public uint SubLayerIndex { get; init; } = 0;

    // ── Timeline — component is always visible ───────────────

    public uint StartFrame { get; init; } = 0;

    public uint RelativeStartFrame { get; init; } = 0;

    public uint Duration
    {
        get => Math.Max(1, DurationInFrames);
        set => DurationInFrames = Math.Max(1, value);
    }

    public float FrameTime { get; init; } = 1f / 30f;

    public bool ExtendToWholeDraft { get => true; set { } } //force to extend

    // ── Layout (pixel coordinates, mapped from parameters) ───

    public int TargetWidth
    {
        get;
        set => field = Math.Max(1, value);
    }

    public int TargetHeight
    {
        get;
        set => field = Math.Max(1, value);
    }

    public int TargetX { get; set; }

    public int TargetY { get; set; }

    public float Rotation { get; set; }

    public int StartingX { get; set; }

    public int StartingY { get; set; }

    public bool ShowDefaultClipBorder { get; set; } = true;

    // ── Effects / source (minimal — no effects for components) ──

    public EffectAndMixtureJSONStructure[]? Effects { get; init; } = Array.Empty<EffectAndMixtureJSONStructure>();
    public EffectProviderJSONStructure[]? EffectProviders { get; init; } = Array.Empty<EffectProviderJSONStructure>();

    [JsonIgnore]
    public IEffect[]? EffectsInstances { get; set; } = Array.Empty<IEffect>();
    [JsonIgnore]
    public IEffectProvider[]? EffectProvidersInstances { get; set; } = Array.Empty<IEffectProvider>();

    public string? FilePath
    {
        get => null;
        set { }
    }

    public bool NeedFilePath => false;

    public Dictionary<string, object> ExtraData { get; set; } = new();

    [JsonIgnore]
    public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
    [JsonIgnore]
    public IMixture? MixtureInstance { get; set; }
    [JsonIgnore]
    public ISourceReplacementEffect? AlternativeSource { get; set; }

    // ── Construction ─────────────────────────────────────────

    public VectorComponentWrapperClip(IVectorComponent component) : this(component, 1920, 1080)
    {
    }

    public VectorComponentWrapperClip(IVectorComponent component, int canvasWidth, int canvasHeight)
    {
        Component = component ?? throw new ArgumentNullException(nameof(component));
        ParentCanvasWidth = canvasWidth;
        ParentCanvasHeight = canvasHeight;
        SyncFromDefinition();
    }

    // ── Coordinate sync ──────────────────────────────────────

    /// <summary>
    /// Reads relative transform parameters and maps them to pixel-space
    /// <see cref="TargetX"/> / <see cref="TargetY"/>.
    /// Initialises <see cref="TargetWidth"/> / <see cref="TargetHeight"/> to a sensible default
    /// if they are not already set.
    /// </summary>
    public void SyncFromDefinition()
    {
        double canvasW = Math.Max(1, ParentCanvasWidth);
        double canvasH = Math.Max(1, ParentCanvasHeight);

        if (TryComputeComponentPixelBounds(this, canvasW, canvasH, out var bounds))
        {
            TargetX = (int)Math.Round(bounds.X);
            TargetY = (int)Math.Round(bounds.Y);
            TargetWidth = Math.Max(1, (int)Math.Round(bounds.Width));
            TargetHeight = Math.Max(1, (int)Math.Round(bounds.Height));
            return;
        }

        // Fallback size: 15% of the shorter canvas dimension, at least 20 px
        if (TargetWidth <= 0 || TargetHeight <= 0)
        {
            int defaultSize = (int)Math.Max(20, Math.Min(canvasW, canvasH) * 0.15);
            TargetWidth = defaultSize;
            TargetHeight = defaultSize;
        }

        double relX = GetFloatParam(KeyRelativeX, 0.5f);
        double relY = GetFloatParam(KeyRelativeY, 0.5f);
        double baseX = GetFloatParam(KeyBaseX, 0f);
        double baseY = GetFloatParam(KeyBaseY, 0f);

        double centerX = relX * canvasW + baseX * canvasW;
        double centerY = relY * canvasH + baseY * canvasH;

        TargetX = (int)Math.Round(centerX - TargetWidth / 2.0);
        TargetY = (int)Math.Round(centerY - TargetHeight / 2.0);
    }

    /// <summary>
    /// Writes <see cref="TargetX"/> / <see cref="TargetY"/> back to the component's
    /// RelativeX / RelativeY parameters.
    /// </summary>
    public void SyncToDefinition()
    {
        double canvasW = Math.Max(1, ParentCanvasWidth);
        double canvasH = Math.Max(1, ParentCanvasHeight);

        if (Component is ComponentGroup group)
        {
            SyncGroupToDefinition(group, canvasW, canvasH);
            return;
        }

        if (!TryComputePrimaryElementLocalBounds(this, out var localBounds))
        {
            double centerX = TargetX + TargetWidth / 2.0;
            double centerY = TargetY + TargetHeight / 2.0;
            double baseX = GetFloatParam(KeyBaseX, 0f);
            double baseY = GetFloatParam(KeyBaseY, 0f);
            SetFloatParam(KeyRelativeX, (float)Math.Clamp(
                (centerX - baseX * canvasW) / canvasW, 0f, 1f));
            SetFloatParam(KeyRelativeY, (float)Math.Clamp(
                (centerY - baseY * canvasH) / canvasH, 0f, 1f));
            return;
        }

        var currentBounds = ComputePixelBounds(localBounds, this, canvasW, canvasH);
        if (!currentBounds.IsValid)
        {
            return;
        }

        var scaleX = currentBounds.Width > 0d ? TargetWidth / currentBounds.Width : 1d;
        var scaleY = currentBounds.Height > 0d ? TargetHeight / currentBounds.Height : 1d;
        if (!NearlyEqual(scaleX, 1d) || !NearlyEqual(scaleY, 1d))
        {
            ApplyResizeToComponent(this, localBounds, scaleX, scaleY);
            if (TryComputePrimaryElementLocalBounds(this, out var resizedBounds))
            {
                localBounds = resizedBounds;
            }
        }

        SetFloatParam(KeyRelativeX, (float)Math.Clamp(
            (TargetX - localBounds.MinX * canvasW) / canvasW, -4f, 4f));
        SetFloatParam(KeyRelativeY, (float)Math.Clamp(
            (TargetY - localBounds.MinY * canvasH) / canvasH, -4f, 4f));
    }

    /// <summary>
    /// Syncs the component's layer index from the clip's <see cref="LayerIndex"/>.
    /// </summary>
    /// <summary>
    /// Writes the editor's <see cref="TargetX"/> / <see cref="TargetY"/> / <see cref="TargetWidth"/> / <see cref="TargetHeight"/>
    /// back to a <see cref="ComponentGroup"/>'s RelativeX/Y/Width/Height parameters.
    ///
    /// Width / Height are stored directly as fractions of the canvas so that
    /// <see cref="SyncFromDefinition"/>'s <see cref="ComponentGroup.ComputeAll"/> produces
    /// pixel bounds that exactly match the editor's clip rect.  This eliminates the
    /// feedback loop that occurred when averaging scaleX and scaleY into a single
    /// uniform scale (which caused the clip to shrink toward the initial size with
    /// each commit cycle — the "jitter" bug).
    /// </summary>
    private void SyncGroupToDefinition(ComponentGroup group, double canvasW, double canvasH)
    {
        double centerX = TargetX + TargetWidth / 2.0;
        double centerY = TargetY + TargetHeight / 2.0;

        // Store the actual pixel rect directly as canvas fractions.
        // ComponentGroup.ComputeAll uses "Width / InitialWidth" as scaleX, so storing
        // the direct fraction makes SyncFromDefinition's pixel bounds exactly match
        // TargetX/TargetY/TargetWidth/TargetHeight — no drift, no feedback loop.
        group.Parameters["RelativeX"] = (float)Math.Clamp(centerX / canvasW, -4f, 4f);
        group.Parameters["RelativeY"] = (float)Math.Clamp(centerY / canvasH, -4f, 4f);
        group.Parameters["Width"] = (float)Math.Max(0.0001d, TargetWidth / canvasW);
        group.Parameters["Height"] = (float)Math.Max(0.0001d, TargetHeight / canvasH);
    }

    public void SyncLayerToDefinition()
    {
        Component.Index = (int)LayerIndex;
    }

    /// <summary>
    /// Syncs the component's rotation parameter from <paramref name="rotationRadians"/>.
    /// </summary>
    public void SyncRotationToDefinition(float rotationRadians)
    {
        SetFloatParam(KeyRotation, rotationRadians);
    }

    // ── IClip core methods ───────────────────────────────────

    /// <summary>
    /// Raster fallback: builds the component's elements for the given frame,
    /// rasterises them via the default rasterizer, and returns a bitmap.
    /// Used when the dynamic preview provider is unavailable.
    /// </summary>
    public IPicture GetFrameRelativeToStartPointOfSource(
        uint frameIndex,
        int requiredWidth,
        int requiredHeight,
        IPicture.PicturePixelMode targetPPB)
    {
        int w = Math.Max(1, requiredWidth);
        int h = Math.Max(1, requiredHeight);
        var elements = BuildElements(Component, CachedSvgElements);
        if (elements is null || elements.Count == 0)
        {
            // Return a transparent placeholder
            return targetPPB.Value switch
            {
                8 => Picture8bpp.GenerateSolidColor(w, h, 0, 0, 0, 0f),
                16 => Picture16bpp.GenerateSolidColor(w, h, 0, 0, 0, 0f),
                _ => Picture8bpp.GenerateSolidColor(w, h, 0, 0, 0, 0f),
            };
        }

        var vectorPicture = new VectorPicture
        {
            Elements = elements,
        };

        return IVectorContentClip.GlobalDefaultRasterizer.Convert(vectorPicture, w, h);
    }

    /// <summary>
    /// Computes the pixel bounds for the wrapped component.
    /// The returned rectangle is in parent-canvas coordinates and can be fed directly to
    /// InteractableEditor's TargetX/TargetY/TargetWidth/TargetHeight.
    /// </summary>
    public bool TryComputeFrameBounds(out ClipPositionTuple bounds)
    {
        bounds = default;

        double canvasW = Math.Max(1, ParentCanvasWidth);
        double canvasH = Math.Max(1, ParentCanvasHeight);

        var elements = BuildElements(Component, CachedSvgElements);
        if (elements is null || elements.Count == 0)
        {
            return false;
        }

        bool found = false;
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        foreach (var element in elements)
        {
            if (!TryComputeElementLocalBounds(element, out var localBounds))
            {
                continue;
            }

            var elementBounds = ComputePixelBounds(localBounds, element, canvasW, canvasH);
            if (!elementBounds.IsValid)
            {
                continue;
            }

            found = true;
            minX = Math.Min(minX, elementBounds.X);
            minY = Math.Min(minY, elementBounds.Y);
            maxX = Math.Max(maxX, elementBounds.X + elementBounds.Width);
            maxY = Math.Max(maxY, elementBounds.Y + elementBounds.Height);
        }

        if (!found)
        {
            return false;
        }

        int x = (int)Math.Round(minX);
        int y = (int)Math.Round(minY);
        int width = Math.Max(1, (int)Math.Round(maxX - minX));
        int height = Math.Max(1, (int)Math.Round(maxY - minY));

        bounds = new(x, y, width, height, false);
        return true;
    }

    // ═══════════════════════════════════════════════════════════
    // Static helpers — operate on IVectorComponent + clip data
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Build elements from an <see cref="IVectorComponent"/>.
    /// For SVG-style components (when <paramref name="cachedSvgElements"/> is set),
    /// returns deep-cloned cached elements. For <see cref="ComponentGroup"/>s returns
    /// the flattened children. Otherwise uses <see cref="IVectorComponent.ComputeAll"/>
    /// so components whose <c>Compute</c> returns only a representative element
    /// (e.g. <see cref="TextComponent"/>) still produce all visual elements for
    /// bounds and preview.
    /// </summary>
    public static List<VectorCanvasElement> BuildElements(IVectorComponent component, List<VectorCanvasElement>? cachedSvgElements)
    {
        if (component is ComponentGroup group)
        {
            return group.ComputeAll().ToList();
        }

        if (cachedSvgElements is { Count: > 0 })
        {
            return cachedSvgElements.Select(e => e is ShapeCanvasElement shape ? shape.Clone() : e).ToList();
        }

        return component.ComputeAll().ToList();
    }

    // ── Shape parameter helpers (static) ────────────────────

    private static float GetParam(Dictionary<string, object> parameters, string key, float defaultValue)
    {
        if (parameters.TryGetValue(key, out var val) && val is not null)
        {
            return val switch
            {
                float f => f,
                double d => (float)d,
                int i => i,
                uint u => u,
                long l => l,
                ushort us => us,
                decimal m => (float)m,
                JsonElement { ValueKind: JsonValueKind.Number } je => je.GetSingle(),
                JsonElement { ValueKind: JsonValueKind.String } je when float.TryParse(je.GetString(), out var parsed) => parsed,
                _ => defaultValue,
            };
        }
        return defaultValue;
    }

    private static double ScaleCoordinate(double value, double min, double scale)
        => min + (value - min) * scale;

    private static void ApplyResizeToComponent(
        VectorComponentWrapperClip clip,
        LocalBounds localBounds,
        double scaleX,
        double scaleY)
    {
        var parameters = clip.Component.Parameters;
        string typeName = clip.Component.TypeName;
        scaleX = double.IsFinite(scaleX) && scaleX > 0d ? scaleX : 1d;
        scaleY = double.IsFinite(scaleY) && scaleY > 0d ? scaleY : 1d;

        switch (typeName)
        {
            case "Rectangle":
            case "RoundedRectangle":
                ScaleShapeParam(parameters, "Width", scaleX);
                ScaleShapeParam(parameters, "Height", scaleY);
                if (typeName == "RoundedRectangle")
                    ScaleShapeParam(parameters, "CornerRadius", Math.Min(scaleX, scaleY));
                break;
            case "Ellipse":
                ScaleShapeParam(parameters, "RadiusX", scaleX);
                ScaleShapeParam(parameters, "RadiusY", scaleY);
                break;
            case "Line":
                ScalePointParams(parameters, localBounds, scaleX, scaleY,
                    ("X1", "Y1"), ("X2", "Y2"));
                break;
            case "CubicBezier":
                ScalePointParams(parameters, localBounds, scaleX, scaleY,
                    ("X1", "Y1"), ("X2", "Y2"), ("X3", "Y3"), ("X4", "Y4"));
                break;
            case "QuadraticBezier":
                ScalePointParams(parameters, localBounds, scaleX, scaleY,
                    ("X1", "Y1"), ("X2", "Y2"), ("X3", "Y3"));
                break;
            case "Arc":
                ScaleShapeParam(parameters, "RadiusX", scaleX);
                ScaleShapeParam(parameters, "RadiusY", scaleY);
                ScaleCoordinateParam(parameters, "CenterX", localBounds.MinX, scaleX);
                ScaleCoordinateParam(parameters, "CenterY", localBounds.MinY, scaleY);
                break;
            case "Text":
                ScaleShapeParam(parameters, "FontSize", Math.Min(scaleX, scaleY));
                ScaleShapeParam(parameters, "StrokeThickness", Math.Min(scaleX, scaleY));
                ScaleShapeParam(parameters, "CharacterSpacing", Math.Min(scaleX, scaleY));
                break;
            case "Polygon":
            case "Polyline":
                // Polygon/polyline vertices stored via EditorPoints — handled by the editor
                break;
        }
    }

    private static void ScaleShapeParam(Dictionary<string, object>? parameters, string key, double scale)
    {
        if (parameters is null || !parameters.TryGetValue(key, out var raw)) return;
        float value = raw switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            uint u => u,
            long l => l,
            ushort us => us,
            decimal m => (float)m,
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetSingle(),
            JsonElement { ValueKind: JsonValueKind.String } je when float.TryParse(je.GetString(), out var parsed) => parsed,
            _ => 0f,
        };
        parameters[key] = (float)Math.Max(0.0001d, value * scale);
    }

    private static void ScaleCoordinateParam(Dictionary<string, object>? parameters, string key, double min, double scale)
    {
        if (parameters is null || !parameters.TryGetValue(key, out var raw)) return;
        float value = raw switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            uint u => u,
            long l => l,
            ushort us => us,
            decimal m => (float)m,
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetSingle(),
            JsonElement { ValueKind: JsonValueKind.String } je when float.TryParse(je.GetString(), out var parsed) => parsed,
            _ => 0f,
        };
        parameters[key] = (float)ScaleCoordinate(value, min, scale);
    }

    private static void ScalePointParams(
        Dictionary<string, object>? parameters,
        LocalBounds localBounds,
        double scaleX,
        double scaleY,
        params (string XKey, string YKey)[] keys)
    {
        if (parameters is null) return;

        foreach (var (xKey, yKey) in keys)
        {
            if (parameters.TryGetValue(xKey, out var rawX))
            {
                float x = rawX switch
                {
                    float f => f,
                    double d => (float)d,
                    int i => i,
                    uint u => u,
                    long l => l,
                    ushort us => us,
                    decimal m => (float)m,
                    JsonElement { ValueKind: JsonValueKind.Number } je => je.GetSingle(),
                    JsonElement { ValueKind: JsonValueKind.String } je when float.TryParse(je.GetString(), out var parsed) => parsed,
                    _ => 0f,
                };
                parameters[xKey] = (float)ScaleCoordinate(x, localBounds.MinX, scaleX);
            }

            if (parameters.TryGetValue(yKey, out var rawY))
            {
                float y = rawY switch
                {
                    float f => f,
                    double d => (float)d,
                    int i => i,
                    uint u => u,
                    long l => l,
                    ushort us => us,
                    decimal m => (float)m,
                    JsonElement { ValueKind: JsonValueKind.Number } je => je.GetSingle(),
                    JsonElement { ValueKind: JsonValueKind.String } je when float.TryParse(je.GetString(), out var parsed) => parsed,
                    _ => 0f,
                };
                parameters[yKey] = (float)ScaleCoordinate(y, localBounds.MinY, scaleY);
            }
        }
    }

    // ── Bounds computation helpers ───────────────────────────

    private static bool TryComputeComponentPixelBounds(
        VectorComponentWrapperClip clip,
        double canvasW,
        double canvasH,
        out PixelBounds bounds)
    {
        bounds = default;
        var elements = BuildElements(clip.Component, clip.CachedSvgElements);
        if (elements.Count == 0)
        {
            return false;
        }

        bool found = false;
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        foreach (var element in elements)
        {
            if (!TryComputeElementLocalBounds(element, out var localBounds))
            {
                continue;
            }

            var elementBounds = ComputePixelBounds(localBounds, element, canvasW, canvasH);
            if (!elementBounds.IsValid)
            {
                continue;
            }

            found = true;
            minX = Math.Min(minX, elementBounds.X);
            minY = Math.Min(minY, elementBounds.Y);
            maxX = Math.Max(maxX, elementBounds.X + elementBounds.Width);
            maxY = Math.Max(maxY, elementBounds.Y + elementBounds.Height);
        }

        if (!found)
        {
            return false;
        }

        bounds = new PixelBounds(minX, minY, maxX - minX, maxY - minY);
        return bounds.IsValid;
    }

    private static bool TryComputePrimaryElementLocalBounds(VectorComponentWrapperClip clip, out LocalBounds bounds)
    {
        bounds = default;
        var elements = BuildElements(clip.Component, clip.CachedSvgElements);
        return elements.Count > 0 && TryComputeElementLocalBounds(elements[0], out bounds);
    }

    private static PixelBounds ComputePixelBounds(
        LocalBounds localBounds,
        VectorComponentWrapperClip clip,
        double canvasW,
        double canvasH)
    {
        var elements = BuildElements(clip.Component, clip.CachedSvgElements);
        return elements.Count == 0 ? default : ComputePixelBounds(localBounds, elements[0], canvasW, canvasH);
    }

    private static PixelBounds ComputePixelBounds(
        LocalBounds localBounds,
        VectorCanvasElement element,
        double canvasW,
        double canvasH)
    {
        if (!localBounds.IsValid)
        {
            return default;
        }

        var transformed = new VectorViewportElement(element, (float)canvasW, (float)canvasH, 0, 0, (float)canvasW, (float)canvasH);
        if (!TryComputeElementLocalBounds(transformed, out var rotatedBounds)) return default;
        return new PixelBounds(
            (transformed.RelativeX + rotatedBounds.MinX) * canvasW,
            (transformed.RelativeY + rotatedBounds.MinY) * canvasH,
            rotatedBounds.Width * canvasW, rotatedBounds.Height * canvasH);
    }

    private static bool TryComputeElementLocalBounds(VectorCanvasElement element, out LocalBounds bounds)
    {
        bounds = default;
        var segments = element.Draw();
        if (segments is null || segments.Length == 0)
        {
            return false;
        }

        bool found = false;
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        void Include(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                return;
            }

            found = true;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        foreach (var segment in segments)
        {
            switch (segment)
            {
                case StraightLineVectorSegment s:
                    Include(s.X1, s.Y1);
                    Include(s.X2, s.Y2);
                    break;
                case RoundedRectangleVectorSegment s:
                    Include(s.X, s.Y);
                    Include(s.X + s.Width, s.Y + s.Height);
                    break;
                case RectangleVectorSegment s:
                    Include(s.X, s.Y);
                    Include(s.X + s.Width, s.Y + s.Height);
                    break;
                case EllipseVectorSegment s:
                    Include(s.X - s.RadiusX, s.Y - s.RadiusY);
                    Include(s.X + s.RadiusX, s.Y + s.RadiusY);
                    break;
                case CubicBezierVectorSegment s:
                    Include(s.X1, s.Y1);
                    Include(s.X2, s.Y2);
                    Include(s.X3, s.Y3);
                    Include(s.X4, s.Y4);
                    break;
                case QuadraticBezierVectorSegment s:
                    Include(s.X1, s.Y1);
                    Include(s.X2, s.Y2);
                    Include(s.X3, s.Y3);
                    break;
                case ArcVectorSegment s:
                    Include(s.X - s.RadiusX, s.Y - s.RadiusY);
                    Include(s.X + s.RadiusX, s.Y + s.RadiusY);
                    break;
                case PolygonVectorSegment s:
                    IncludePoints(s.Points, Include);
                    if (s.Holes is not null)
                    {
                        foreach (var hole in s.Holes)
                        {
                            IncludePoints(hole, Include);
                        }
                    }
                    break;
                case PolylineVectorSegment s:
                    IncludePoints(s.Points, Include);
                    break;
            }
        }

        if (!found)
        {
            return false;
        }

        bounds = new LocalBounds(minX, minY, maxX, maxY);
        return bounds.IsValid;
    }

    private static void IncludePoints(Point[]? points, Action<double, double> include)
    {
        if (points is null) return;
        foreach (var point in points)
        {
            include(point.X, point.Y);
        }
    }

    private static bool NearlyEqual(double a, double b)
        => Math.Abs(a - b) < 0.0001d;

    // ═══════════════════════════════════════════════════════════

    public void ReInit(IPicture.PicturePixelMode targetPPB)
    {
        // VectorComponentWrapperClip has no heavy source to reload;
        // component definition is already in memory. Effects are always empty.
    }

    public void Dispose()
    {
            projectFrameCut.Render.Effect.EffectHelper.ReleaseClipEffects(this);
        // No unmanaged resources.
    }

}
