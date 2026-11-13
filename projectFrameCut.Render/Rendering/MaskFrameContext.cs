using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

namespace projectFrameCut.Render.Rendering;

public sealed class MaskFrameContext : IDisposable
{
    private static readonly AsyncLocal<MaskFrameContext?> current = new();
    private readonly Dictionary<Guid, IPicture> backdrops = new();
    private readonly HashSet<Guid> reading = new();
    private readonly Func<IClip, IPicture>? readBelow;
    private readonly Func<IClip, uint, IPicture?>? readSource;
    private readonly MaskFrameContext? previous;

    public static MaskFrameContext? Current => current.Value;
    public IReadOnlyList<IClip> Clips { get; }
    public uint Frame { get; }
    public int Width { get; }
    public int Height { get; }
    public int ProjectWidth { get; }
    public int ProjectHeight { get; }
    public IPicture.PicturePixelMode PixelMode { get; }
    public CancellationToken Token { get; }

    private MaskFrameContext(IReadOnlyList<IClip> clips, uint frame, int width, int height,
        int projectWidth, int projectHeight, IPicture.PicturePixelMode pixelMode, CancellationToken token,
        Func<IClip, IPicture>? readBelow, Func<IClip, uint, IPicture?>? readSource)
    {
        previous = current.Value;
        Clips = clips;
        Frame = frame;
        Width = width;
        Height = height;
        ProjectWidth = projectWidth > 0 ? projectWidth : width;
        ProjectHeight = projectHeight > 0 ? projectHeight : height;
        PixelMode = pixelMode;
        Token = token;
        this.readBelow = readBelow;
        this.readSource = readSource;
        current.Value = this;
    }

    public static IDisposable? Open(IReadOnlyList<IClip> clips, uint frame, int width, int height,
        int projectWidth, int projectHeight, IPicture.PicturePixelMode pixelMode, CancellationToken token,
        Func<IClip, IPicture>? readBelow = null, Func<IClip, uint, IPicture?>? readSource = null, bool forceNew = false)
    {
        if (!forceNew && !clips.Any(c => c is MaskClip)) return null;
        if (!forceNew && Current is { } parent && parent.Width == width && parent.Height == height
            && parent.ProjectWidth == (projectWidth > 0 ? projectWidth : width)
            && parent.ProjectHeight == (projectHeight > 0 ? projectHeight : height) && parent.PixelMode.Value == pixelMode.Value
            && clips.All(c => parent.Clips.Any(p => p.Id == c.Id))) return null;
        return new MaskFrameContext(clips, frame, width, height, projectWidth, projectHeight, pixelMode, token, readBelow, readSource);
    }

    public IPicture? ReadSource(IClip clip, uint frame)
        => Clips.Any(c => c.Id == clip.Id) ? readSource?.Invoke(clip, frame) : null;

    internal void Publish(IClip clip, IPicture? picture)
    {
        if (clip is not MaskClip || readBelow is not null || picture is null || backdrops.ContainsKey(clip.Id)) return;
        backdrops[clip.Id] = DynamicEffectGraph.CopyPicture(picture);
    }

    public IPicture ReadBelow(IClip clip)
    {
        Token.ThrowIfCancellationRequested();
        if (backdrops.TryGetValue(clip.Id, out var cached)) return DynamicEffectGraph.CopyPicture(cached);
        if (!reading.Add(clip.Id)) throw new InvalidOperationException($"Recursive mask dependency for {clip.Id}.");
        try
        {
            var picture = FrameHashContext.Current is { } context
                ? context.ReadPicture((typeof(MaskFrameContext), clip.Id, Frame, Width, Height, ProjectWidth, ProjectHeight, PixelMode.Value),
                    () => readBelow is not null ? readBelow(clip) : RenderBelow(clip))!
                : readBelow is not null ? readBelow(clip) : RenderBelow(clip);
            backdrops[clip.Id] = picture;
            return DynamicEffectGraph.CopyPicture(picture);
        }
        finally { reading.Remove(clip.Id); }
    }

    private IPicture RenderBelow(IClip clip)
    {
        var lower = Clips.Where(c => ClipLayerOrder.BottomToTop.Compare(c, clip) < 0).ToArray();
        return Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(lower, Frame, Width, Height, PixelMode,
            ProjectWidth, ProjectHeight, initializeClips: false), Frame, Width, Height, (int)PixelMode,
            projectRelativeWidth: ProjectWidth, projectRelativeHeight: ProjectHeight, transparentBackground: true,
            disposeIntermediateFrames: true, cancellationToken: Token, allClips: Clips);
    }

    public void Dispose()
    {
        current.Value = previous;
        foreach (var picture in backdrops.Values) picture.Dispose();
        backdrops.Clear();
    }
}
