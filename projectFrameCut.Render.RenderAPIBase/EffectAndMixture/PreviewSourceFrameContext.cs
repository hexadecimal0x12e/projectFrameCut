using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public sealed class PreviewSourceFrameContext : IDisposable
{
    private static readonly AsyncLocal<PreviewSourceFrameContext?> current = new();
    private static readonly AsyncLocal<int> rawDepth = new();
    private readonly PreviewSourceFrameContext? previous;
    private readonly Func<IClip, uint, int, int, IPicture.PicturePixelMode, Func<IPicture>, IPicture> read;

    public static PreviewSourceFrameContext? Current => current.Value;
    public static bool ReadingRaw => rawDepth.Value > 0;

    public PreviewSourceFrameContext(Func<IClip, uint, int, int, IPicture.PicturePixelMode, Func<IPicture>, IPicture> read)
    {
        this.read = read;
        previous = current.Value;
        current.Value = this;
    }

    public IPicture Read(IClip clip, uint frame, int width, int height, IPicture.PicturePixelMode mode, Func<IPicture> compute)
        => ReadingRaw ? compute() : read(clip, frame, width, height, mode, compute);

    public static IDisposable Raw()
    {
        rawDepth.Value++;
        return new RawScope();
    }

    public static IDisposable Suspend()
    {
        var previous = current.Value;
        current.Value = null;
        return new SuspendedScope(previous);
    }

    private sealed class SuspendedScope(PreviewSourceFrameContext? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }

    private sealed class RawScope : IDisposable
    {
        public void Dispose() => rawDepth.Value--;
    }

    public void Dispose() => current.Value = previous;
}
