using projectFrameCut.DraftStuff;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Animation;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using Point = projectFrameCut.Drawing.Vector.Point;

namespace projectFrameCut.Render.ClipsAndTracks;

/// <summary>
/// Lightweight IClip wrapper around a <see cref="VectorComponent"/> so that
/// <see cref="InteractableEditor.InteractableEditor"/> can manage component layout
/// (drag, resize, snap, reference lines) without knowing about the storyboard domain.
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
public partial class ComponentClip : IClip
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
    public VectorComponent Component { get; }

    /// <summary>Reference width of the parent canvas, used for coordinate mapping.</summary>
    [JsonIgnore]
    public int ParentCanvasWidth { get; set; } = 1920;

    /// <summary>Reference height of the parent canvas, used for coordinate mapping.</summary>
    [JsonIgnore]
    public int ParentCanvasHeight { get; set; } = 1080;

    // ── IClip identity ───────────────────────────────────────

    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public ClipMode ClipType => ClipMode.VectorCanvasClip;
    public string TypeName => "ComponentClip";

    public Guid Id
    {
        get => Component.Definition.Id;
        init { }
    }

    public string Name
    {
        get => Component.Definition.DisplayName ?? "Unnamed Component";
        init { }
    }

    public string BindedSoundTrack { get; init; } = string.Empty;

    // ── Layer / Z-order ──────────────────────────────────────

    public uint LayerIndex
    {
        get => (uint)Math.Max(0, Component.Definition.LayerIndex);
        init { }
    }

    public uint SubLayerIndex { get; init; } = 0;

    // ── Timeline — component is always visible ───────────────

    public uint StartFrame { get; init; } = 0;

    public uint RelativeStartFrame { get; init; } = 0;

    public uint Duration
    {
        get => Math.Max(1, Component.Storyboard.DurationInFrames);
        set => Component.Storyboard.DurationInFrames = Math.Max(1, value);
    }

    public float FrameTime { get; init; } = 1f / 30f;

    public bool ExtendToWholeDraft { get => true; set { } } //force to extend

    // ── Layout (pixel coordinates, mapped from definition) ───

    private int _targetWidth;
    private int _targetHeight;
    private int _targetX;
    private int _targetY;

    public int TargetWidth
    {
        get => _targetWidth;
        set => _targetWidth = Math.Max(1, value);
    }

    public int TargetHeight
    {
        get => _targetHeight;
        set => _targetHeight = Math.Max(1, value);
    }

    public int TargetX
    {
        get => _targetX;
        set => _targetX = value;
    }

    public int TargetY
    {
        get => _targetY;
        set => _targetY = value;
    }

    // ── Effects / source (minimal — no effects for components) ──

    public EffectAndMixtureJSONStructure[]? Effects { get; init; } = Array.Empty<EffectAndMixtureJSONStructure>();

    [JsonIgnore]
    public IEffect[]? EffectsInstances { get; set; } = Array.Empty<IEffect>();

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

    // ── Construction ─────────────────────────────────────────

    public ComponentClip(VectorComponent component)
    {
        Component = component ?? throw new ArgumentNullException(nameof(component));

        SyncFromDefinition();
    }

    // ── Coordinate sync ──────────────────────────────────────

    /// <summary>
    /// Reads <see cref="VectorComponentDefinition.RelativeX"/> / <see cref="VectorComponentDefinition.RelativeY"/>
    /// and maps them to pixel-space <see cref="TargetX"/> / <see cref="TargetY"/>.
    /// Initialises <see cref="TargetWidth"/> / <see cref="TargetHeight"/> to a sensible default
    /// if they are not already set.
    /// </summary>
    public void SyncFromDefinition()
    {
        var def = Component.Definition;
        double canvasW = Math.Max(1, ParentCanvasWidth);
        double canvasH = Math.Max(1, ParentCanvasHeight);

        if (TryComputeComponentPixelBounds(Component, canvasW, canvasH, out var bounds))
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

        double centerX = def.RelativeX * canvasW + def.BaseX * canvasW;
        double centerY = def.RelativeY * canvasH + def.BaseY * canvasH;

        TargetX = (int)Math.Round(centerX - TargetWidth / 2.0);
        TargetY = (int)Math.Round(centerY - TargetHeight / 2.0);
    }

    /// <summary>
    /// Writes <see cref="TargetX"/> / <see cref="TargetY"/> back to the definition's
    /// <see cref="VectorComponentDefinition.RelativeX"/> / <see cref="VectorComponentDefinition.RelativeY"/>.
    /// </summary>
    public void SyncToDefinition()
    {
        double canvasW = Math.Max(1, ParentCanvasWidth);
        double canvasH = Math.Max(1, ParentCanvasHeight);
        var def = Component.Definition;

        if (!TryComputePrimaryElementLocalBounds(Component, out var localBounds))
        {
            double centerX = TargetX + TargetWidth / 2.0;
            double centerY = TargetY + TargetHeight / 2.0;
            def.RelativeX = (float)Math.Clamp(
                (centerX - def.BaseX * canvasW) / canvasW, 0f, 1f);
            def.RelativeY = (float)Math.Clamp(
                (centerY - def.BaseY * canvasH) / canvasH, 0f, 1f);
            return;
        }

        var currentBounds = ComputePixelBounds(localBounds, def, canvasW, canvasH);
        if (!currentBounds.IsValid)
        {
            return;
        }

        var scaleX = currentBounds.Width > 0d ? TargetWidth / currentBounds.Width : 1d;
        var scaleY = currentBounds.Height > 0d ? TargetHeight / currentBounds.Height : 1d;
        if (!NearlyEqual(scaleX, 1d) || !NearlyEqual(scaleY, 1d))
        {
            ApplyResizeToDefinition(def, localBounds, scaleX, scaleY);
            if (TryComputePrimaryElementLocalBounds(Component, out var resizedBounds))
            {
                localBounds = resizedBounds;
            }
        }

        def.RelativeX = (float)Math.Clamp((TargetX - localBounds.MinX * canvasW) / canvasW, -4f, 4f);
        def.RelativeY = (float)Math.Clamp((TargetY - localBounds.MinY * canvasH) / canvasH, -4f, 4f);
    }

    /// <summary>
    /// Syncs the definition's <see cref="VectorComponentDefinition.LayerIndex"/>
    /// from the clip's <see cref="LayerIndex"/>.
    /// </summary>
    public void SyncLayerToDefinition()
    {
        Component.Definition.LayerIndex = (int)LayerIndex;
    }

    /// <summary>
    /// Syncs the definition's <see cref="VectorComponentDefinition.Rotation"/>
    /// from <paramref name="rotationRadians"/>.
    /// </summary>
    public void SyncRotationToDefinition(float rotationRadians)
    {
        Component.Definition.Rotation = rotationRadians;
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
        bool forceResize,
        IPicture.PicturePixelMode targetPPB)
    {
        int w = Math.Max(1, requiredWidth);
        int h = Math.Max(1, requiredHeight);
        uint duration = Math.Max(1, Component.Storyboard.DurationInFrames);
        uint frame = Math.Min(frameIndex, duration - 1);

        var elements = Component.GetAnimatedElements(frame, duration);
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
    /// Computes the animated pixel bounds for the wrapped component at the specified frame.
    /// The returned rectangle is in parent-canvas coordinates and can be fed directly to
    /// InteractableEditor's TargetX/TargetY/TargetWidth/TargetHeight.
    /// </summary>
    public bool TryComputeAnimatedFrameBounds(
        uint frameIndex,
        uint clipDuration,
        out ClipPositionTuple bounds)
    {
        bounds = default;

        uint duration = Math.Max(1, clipDuration);
        uint frame = Math.Min(frameIndex, duration - 1);
        double canvasW = Math.Max(1, ParentCanvasWidth);
        double canvasH = Math.Max(1, ParentCanvasHeight);

        var elements = Component.GetAnimatedElements(frame, duration);
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

    private static bool NearlyEqual(double a, double b)
        => Math.Abs(a - b) < 0.0001d;

    private static double ScaleCoordinate(double value, double min, double scale)
        => min + (value - min) * scale;

    private static void ApplyResizeToDefinition(
        VectorComponentDefinition def,
        LocalBounds localBounds,
        double scaleX,
        double scaleY)
    {
        scaleX = double.IsFinite(scaleX) && scaleX > 0d ? scaleX : 1d;
        scaleY = double.IsFinite(scaleY) && scaleY > 0d ? scaleY : 1d;

        switch (def.ShapeType)
        {
            case VectorShapeType.Rectangle:
                ScaleShapeParam(def.ShapeParameters, "Width", scaleX);
                ScaleShapeParam(def.ShapeParameters, "Height", scaleY);
                break;
            case VectorShapeType.RoundedRectangle:
                ScaleShapeParam(def.ShapeParameters, "Width", scaleX);
                ScaleShapeParam(def.ShapeParameters, "Height", scaleY);
                ScaleShapeParam(def.ShapeParameters, "CornerRadius", Math.Min(scaleX, scaleY));
                break;
            case VectorShapeType.Ellipse:
                ScaleShapeParam(def.ShapeParameters, "RadiusX", scaleX);
                ScaleShapeParam(def.ShapeParameters, "RadiusY", scaleY);
                break;
            case VectorShapeType.Line:
                ScalePointParams(def.ShapeParameters, localBounds, scaleX, scaleY,
                    ("X1", "Y1"), ("X2", "Y2"));
                break;
            case VectorShapeType.CubicBezier:
                ScalePointParams(def.ShapeParameters, localBounds, scaleX, scaleY,
                    ("X1", "Y1"), ("X2", "Y2"), ("X3", "Y3"), ("X4", "Y4"));
                break;
            case VectorShapeType.QuadraticBezier:
                ScalePointParams(def.ShapeParameters, localBounds, scaleX, scaleY,
                    ("X1", "Y1"), ("X2", "Y2"), ("X3", "Y3"));
                break;
            case VectorShapeType.Arc:
                ScaleShapeParam(def.ShapeParameters, "RadiusX", scaleX);
                ScaleShapeParam(def.ShapeParameters, "RadiusY", scaleY);
                ScaleCoordinateParam(def.ShapeParameters, "CenterX", localBounds.MinX, scaleX);
                ScaleCoordinateParam(def.ShapeParameters, "CenterY", localBounds.MinY, scaleY);
                break;
            case VectorShapeType.Polygon:
            case VectorShapeType.Polyline:
                ScalePoints(def.Points, localBounds, scaleX, scaleY);
                break;
        }
    }

    private static void ScaleShapeParam(Dictionary<string, float>? parameters, string key, double scale)
    {
        if (parameters is null || !parameters.TryGetValue(key, out var value))
        {
            return;
        }

        parameters[key] = (float)Math.Max(0.0001d, value * scale);
    }

    private static void ScaleCoordinateParam(Dictionary<string, float>? parameters, string key, double min, double scale)
    {
        if (parameters is null || !parameters.TryGetValue(key, out var value))
        {
            return;
        }

        parameters[key] = (float)ScaleCoordinate(value, min, scale);
    }

    private static void ScalePointParams(
        Dictionary<string, float>? parameters,
        LocalBounds localBounds,
        double scaleX,
        double scaleY,
        params (string XKey, string YKey)[] keys)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var (xKey, yKey) in keys)
        {
            if (parameters.TryGetValue(xKey, out var x))
            {
                parameters[xKey] = (float)ScaleCoordinate(x, localBounds.MinX, scaleX);
            }

            if (parameters.TryGetValue(yKey, out var y))
            {
                parameters[yKey] = (float)ScaleCoordinate(y, localBounds.MinY, scaleY);
            }
        }
    }

    private static void ScalePoints(List<Point>? points, LocalBounds localBounds, double scaleX, double scaleY)
    {
        if (points is null || points.Count == 0)
        {
            return;
        }

        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i];
            points[i] = new Point(
                (float)ScaleCoordinate(point.X, localBounds.MinX, scaleX),
                (float)ScaleCoordinate(point.Y, localBounds.MinY, scaleY));
        }
    }

    private static bool TryComputeComponentPixelBounds(
        VectorComponent component,
        double canvasW,
        double canvasH,
        out PixelBounds bounds)
    {
        bounds = default;
        var elements = component.BuildElements();
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

    private static bool TryComputePrimaryElementLocalBounds(VectorComponent component, out LocalBounds bounds)
    {
        bounds = default;
        var elements = component.BuildElements();
        return elements.Count > 0 && TryComputeElementLocalBounds(elements[0], out bounds);
    }

    private static PixelBounds ComputePixelBounds(
        LocalBounds localBounds,
        VectorComponentDefinition definition,
        double canvasW,
        double canvasH)
    {
        var elements = new VectorComponent { Definition = definition }.BuildElements();
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

        if (element.UseUniformScale)
        {
            var uniform = Math.Min(canvasW, canvasH);
            var originX = element.BaseX * canvasW + element.RelativeX * uniform;
            var originY = element.BaseY * canvasH + element.RelativeY * uniform;
            return new PixelBounds(
                originX + localBounds.MinX * uniform,
                originY + localBounds.MinY * uniform,
                localBounds.Width * uniform,
                localBounds.Height * uniform);
        }

        var originXNonUniform = element.RelativeX * canvasW;
        var originYNonUniform = element.RelativeY * canvasH;
        return new PixelBounds(
            originXNonUniform + localBounds.MinX * canvasW,
            originYNonUniform + localBounds.MinY * canvasH,
            localBounds.Width * canvasW,
            localBounds.Height * canvasH);
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
        if (points is null)
        {
            return;
        }

        foreach (var point in points)
        {
            include(point.X, point.Y);
        }
    }

    public void ReInit(IPicture.PicturePixelMode targetPPB)
    {
        // ComponentClip has no heavy source to reload; definition is already in memory.
        // Effects are always empty.
    }

    public void Dispose()
    {
        // No unmanaged resources. CachedElements belong to the VectorComponent,
        // which is owned by the ViewModel — do not dispose here.
    }
}


/// <summary>
/// Conversion helpers between <see cref="ComponentClip"/> and <see cref="ClipElementUI"/>
/// so that <see cref="InteractableEditor.InteractableEditor"/> can manage component layouts.
/// </summary>
public static class ComponentClipToClipElementUI
{
    // Shared hidden Border instances — we don't need visible timeline clips,
    // but InteractableEditor requires non-null Clip/LeftHandle/RightHandle.
    private static readonly Border SharedClipBorder = new()
    {
        IsVisible = false,
        WidthRequest = 1,
        HeightRequest = 1,
    };

    private static readonly Border SharedLeftHandle = new()
    {
        IsVisible = false,
        WidthRequest = 1,
        HeightRequest = 1,
    };

    private static readonly Border SharedRightHandle = new()
    {
        IsVisible = false,
        WidthRequest = 1,
        HeightRequest = 1,
    };

    /// <summary>
    /// Converts a collection of <see cref="ComponentClip"/>s into a dictionary
    /// keyed by <see cref="IClip.Id"/>, suitable for passing to
    /// <see cref="InteractableEditor.InteractableEditor.SetClipsFromDraftPage"/>.
    /// </summary>
    public static Dictionary<Guid, ClipElementUI> ToClipElementUIDictionary(
        this IEnumerable<ComponentClip> clips)
    {
        var dict = new Dictionary<Guid, ClipElementUI>();
        foreach (var clip in clips)
        {
            var ui = CreateClipElementUI(clip);
            ui.Effects = clip.EffectsInstances?.ToDictionary(c => c.Id, c => c) ?? new();
            dict[clip.Id] = ui;
        }
        return dict;
    }

    /// <summary>
    /// Creates a single <see cref="ClipElementUI"/> from a <see cref="ComponentClip"/>.
    /// </summary>
    public static ClipElementUI CreateClipElementUI(ComponentClip clip)
    {
        return new ClipElementUI
        {
            Id = clip.Id,
            Clip = SharedClipBorder,
            LeftHandle = SharedLeftHandle,
            RightHandle = SharedRightHandle,
            DisplayName = clip.Name,
            ClipType = ClipMode.VectorCanvasClip,
            FromPlugin = clip.FromPlugin,
            TypeName = "ComponentClip",
            TargetX = clip.TargetX,
            TargetY = clip.TargetY,
            TargetWidth = clip.TargetWidth,
            TargetHeight = clip.TargetHeight,
            IsMoveable = true,
            IsHorizontalResizable = true,
            IsVerticalResizable = true,
            ShouldDisplayInUI = true,
            CanSnapWhilePlacing = true,
            CanSnapWhileResizing = true,
            layoutX = 0,
            layoutY = 0,
            origLength = clip.Duration,
            SubLayerIndex = (int)clip.LayerIndex,
        };
    }

    /// <summary>
    /// Synchronises the layout properties from a <see cref="ClipElementUI"/>
    /// (modified by InteractableEditor) back to the <see cref="ComponentClip"/>.
    /// </summary>
    public static void SyncToComponentClip(this ClipElementUI ui, ComponentClip clip)
    {
        clip.TargetX = ui.TargetX;
        clip.TargetY = ui.TargetY;
        clip.TargetWidth = ui.TargetWidth;
        clip.TargetHeight = ui.TargetHeight;
    }
}

/// <summary>
/// A simple wrapper to allow a <see cref="ComponentClip"/> to have dynamic position information.
/// </summary>
public class DynamicPositionProviderEffect : IContinuousClipPositionProvider
{
    Func<uint, ClipPositionTuple>? _callback = null;

    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

    public string TypeName => "DynamicPositionProviderEffect";

    public string Name { get; set; }
    public string Id { get; set; }

    public Dictionary<string, object> Parameters => new();

    public bool Enabled { get => true; set { } }
    public int Index { get; set; }

    public bool IsReorderable => false;

    public string? BindedEffectGroupID { get; set; }

    public DynamicPositionProviderEffect(Func<uint, ClipPositionTuple> callback)
    {
        Name = $"DynamicPositionProviderEffect #{callback.GetHashCode()}";
        Id = $"dppe@{callback.GetHashCode()}";
        _callback = callback;
    }

    public ClipPositionTuple GetPosition(IClip source, uint index, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(_callback, "Position callback");
        return _callback(index);
    }

    public IEffect WithParameters(Dictionary<string, object> parameters)
    {
        throw new NotImplementedException("This effect does not support parameters.");
    }
}
