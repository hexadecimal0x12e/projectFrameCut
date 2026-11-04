using OpenCvSharp;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Numerics;

namespace projectFrameCut.ObjectTrackingPlugin;

internal sealed record TrackingTarget
{
    public TrackingTarget() { }
    public byte[] Png { get; init; } = [];
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public float MatchRatio { get; init; } = 0.75f;
    public int MinInliers { get; init; } = 8;
    public float MinInlierRatio { get; init; } = 0.5f;

    public void Validate()
    {
        if (!float.IsFinite(MatchRatio) || MatchRatio is <= 0 or >= 1
            || MinInliers < 4 || !float.IsFinite(MinInlierRatio) || MinInlierRatio is <= 0 or > 1)
            throw new InvalidDataException("Invalid object matching thresholds.");
        if (Png.Length == 0) return;
        if (ReferenceWidth <= 0 || ReferenceHeight <= 0 || Width <= 0 || Height <= 0
            || X < 0 || Y < 0 || (long)X + Width > ReferenceWidth || (long)Y + Height > ReferenceHeight)
            throw new InvalidDataException("Invalid reference selection.");
    }
}

internal sealed class TrackingEffect : IMultipleOutputEffect
{
    public string FromPlugin => TrackingPlugin.PluginId;
    public string TypeName => TrackingProvider.ProviderType;
    public EffectImplementType ImplementType => EffectImplementType.IPicture;
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public bool IsDeterministic => false;
    public string Name { get; set; } = "Object tracking";
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public Dictionary<string, object> Parameters { get; private init; } = new();
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public bool IsReorderable => true;
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public string? BindedEffectProvidingSystemID { get; set; }
    public IEffect WithParameters(Dictionary<string, object> parameters) => new TrackingEffect() { Parameters = new(parameters) };

    public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        object? Read(string key)
        {
            var value = context.Parameters.TryGetValue(key, out var v) ? v : Parameters.GetValueOrDefault(key);
            return value is Func<object> getter ? getter() : value;
        }
        var value = EffectFieldTypes.ConvertInput(EffectArgumentFieldType.CustomType, Read("Target"));
        var target = value is null ? new TrackingTarget() : value is CustomEffectPortValue custom
            ? TrackingProvider.TargetContract.ReadValue<TrackingTarget>(custom) ?? throw new InvalidDataException("Empty tracking target.")
            : throw new InvalidDataException("Target must be a custom port value.");
        target.Validate();
        if (context.Input is not IPicture picture) throw new ArgumentException("Object tracking requires an input picture.");
        if (target.Png.Length == 0) return Result(null);
        using var reference = Cv2.ImDecode(target.Png, ImreadModes.Grayscale);
        if (reference.Empty() || reference.Width != target.Width || reference.Height != target.Height)
            throw new InvalidDataException("The target PNG does not match its selection dimensions.");
        using var frame = ToGray(picture, context.CancellationToken);
        var corners = Locate(reference, frame, target, context.CancellationToken);
        if (corners is null) return Result(null);
        bool canvas = EffectFieldTypes.ConvertInput(EffectArgumentFieldType.Boolean, Read("CanvasCoordinates")) is not false;
        if (canvas)
        {
            int width = context.RelativeWidth > 0 ? context.RelativeWidth : context.TargetWidth;
            int height = context.RelativeHeight > 0 ? context.RelativeHeight : context.TargetHeight;
            if (width <= 0 || height <= 0) throw new InvalidOperationException("Canvas dimensions are unavailable.");
            var placementValue = EffectFieldTypes.ConvertInput(EffectArgumentFieldType.SizeAndPosition, Read("ImagePlacement"));
            var placement = placementValue is ClipPositionTuple p ? p : new ClipPositionTuple(0, 0, width, height, false);
            if (placement.IsDelta || placement.TargetWidth <= 0 || placement.TargetHeight <= 0 || !float.IsFinite(placement.Rotation))
                throw new ArgumentException("Image placement must have absolute position, positive dimensions and finite rotation.");
            double angle = placement.Rotation * Math.PI / 180;
            for (int i = 0; i < corners.Length; i++)
            {
                double x = corners[i].X / picture.Width * placement.TargetWidth - placement.TargetWidth / 2d;
                double y = corners[i].Y / picture.Height * placement.TargetHeight - placement.TargetHeight / 2d;
                corners[i] = new Point2f((float)(placement.TargetX + placement.TargetWidth / 2d + x * Math.Cos(angle) - y * Math.Sin(angle)),
                    (float)(placement.TargetY + placement.TargetHeight / 2d + x * Math.Sin(angle) + y * Math.Cos(angle)));
            }
        }
        if (corners.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y))) return Result(null);
        double left = Math.Floor(corners.Min(p => p.X)), top = Math.Floor(corners.Min(p => p.Y));
        double right = Math.Ceiling(corners.Max(p => p.X)), bottom = Math.Ceiling(corners.Max(p => p.Y));
        if (left < int.MinValue || top < int.MinValue || right > int.MaxValue || bottom > int.MaxValue
            || right - left > int.MaxValue || bottom - top > int.MaxValue) return Result(null);
        return Result(new ClipPositionTuple((int)left, (int)top, (int)(right - left), (int)(bottom - top), false));
    }

    private static IReadOnlyDictionary<string, object?> Result(ClipPositionTuple? position) => new Dictionary<string, object?>
    {
        ["Position"] = position ?? new ClipPositionTuple(0, 0, 0, 0, false), ["Found"] = position.HasValue,
    };

    public static bool HasFeatures(byte[] png, int minimum)
    {
        using var image = Cv2.ImDecode(png, ImreadModes.Grayscale);
        using var orb = ORB.Create(2000);
        return !image.Empty() && orb.Detect(image).Length >= minimum;
    }

    private static Point2f[]? Locate(Mat reference, Mat frame, TrackingTarget target, CancellationToken token)
    {
        using var orb = ORB.Create(2000);
        using var a = new Mat();
        using var b = new Mat();
        orb.DetectAndCompute(reference, null, out var ka, a);
        token.ThrowIfCancellationRequested();
        orb.DetectAndCompute(frame, null, out var kb, b);
        if (a.Empty() || b.Rows < 2) return null;
        using var matcher = new BFMatcher(NormTypes.Hamming);
        var matches = matcher.KnnMatch(a, b, 2).Where(m => m.Length == 2 && m[0].Distance < target.MatchRatio * m[1].Distance)
            .Select(m => m[0]).OrderBy(m => m.Distance).DistinctBy(m => m.TrainIdx).ToArray();
        token.ThrowIfCancellationRequested();
        if (matches.Length < target.MinInliers) return null;
        using var src = InputArray.Create(matches.Select(m => ka[m.QueryIdx].Pt).ToArray());
        using var dst = InputArray.Create(matches.Select(m => kb[m.TrainIdx].Pt).ToArray());
        using var mask = new Mat();
        using var h = Cv2.FindHomography(src, dst, HomographyMethods.Ransac, 3, mask);
        token.ThrowIfCancellationRequested();
        if (h.Empty()) return null;
        int inliers = Cv2.CountNonZero(mask);
        if (inliers < target.MinInliers || (double)inliers / matches.Length < target.MinInlierRatio) return null;
        var corners = Cv2.PerspectiveTransform(new Point2f[] { new(0, 0), new(reference.Width, 0), new(reference.Width, reference.Height), new(0, reference.Height) }, h);
        if (corners.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y))) return null;
        // Reject folded, degenerate, and implausibly large projections.
        for (int i = 0; i < 4; i++)
        {
            var p = corners[i]; var q = corners[(i + 1) % 4]; var r = corners[(i + 2) % 4];
            if ((q.X - p.X) * (r.Y - q.Y) - (q.Y - p.Y) * (r.X - q.X) <= 0) return null;
        }
        double area = Math.Abs(Cv2.ContourArea(corners));
        if (area < 16 || area > (double)frame.Width * frame.Height * 4
            || corners.Max(p => p.X) <= 0 || corners.Min(p => p.X) >= frame.Width
            || corners.Max(p => p.Y) <= 0 || corners.Min(p => p.Y) >= frame.Height) return null;
        return corners;
    }

    private static Mat ToGray(IPicture picture, CancellationToken token) => picture switch
    {
        IPicture<byte> p => ToGray(p, 255, token),
        IPicture<ushort> p => ToGray(p, 65535, token),
        IPicture<Half> p => ToGray(p, 1, token),
        _ => throw new NotSupportedException($"Unsupported tracking picture: {picture.GetType().Name}"),
    };

    private static Mat ToGray<T>(IPicture<T> picture, double max, CancellationToken token) where T : INumber<T>
    {
        var bytes = new byte[picture.Pixels];
        for (int i = 0; i < bytes.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            double value = (0.299 * double.CreateChecked(picture.r[i]) + 0.587 * double.CreateChecked(picture.g[i])
                + 0.114 * double.CreateChecked(picture.b[i])) * 255 / max;
            bytes[i] = double.IsFinite(value) ? (byte)Math.Clamp(value, 0, 255) : (byte)0;
        }
        var mat = new Mat(picture.Height, picture.Width, MatType.CV_8UC1);
        try { System.Runtime.InteropServices.Marshal.Copy(bytes, 0, mat.Data, bytes.Length); return mat; }
        catch { mat.Dispose(); throw; }
    }
}
