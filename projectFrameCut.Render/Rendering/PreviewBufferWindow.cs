namespace projectFrameCut.Render.Rendering;

public sealed class PreviewBufferWindow
{
    private readonly long maxClipBytes;
    private int frameCount;

    public int MaximumFrames { get; }
    public int FrameCount => Volatile.Read(ref frameCount);

    public PreviewBufferWindow(long maxClipBytes, double frameRate)
    {
        this.maxClipBytes = maxClipBytes > 0 ? maxClipBytes : 64L * 1024 * 1024;
        MaximumFrames = (int)Math.Clamp(Math.Ceiling(frameRate * 2) + 1, 1, 4096);
        frameCount = MaximumFrames;
    }

    public static long GetFrameBytes(int width, int height)
    {
        long pixels = checked((long)Math.Max(1, width) * Math.Max(1, height));
        return checked(pixels * 8 + (pixels + 7) / 8);
    }

    public void Observe(int width, int height, long bytes = 0)
    {
        int count = (int)Math.Clamp(maxClipBytes / Math.Max(bytes, GetFrameBytes(width, height)), 1, MaximumFrames);
        Volatile.Write(ref frameCount, Math.Min(FrameCount, count));
    }
}
