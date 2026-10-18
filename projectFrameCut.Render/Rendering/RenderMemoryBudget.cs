namespace projectFrameCut.Render.Rendering;

internal static class RenderMemoryBudget
{
    public static long DefaultBytes => Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 3,
        128L * 1024 * 1024, 1024L * 1024 * 1024);

    // Allow for the picture, dependency snapshots and temporary float channels.
    public static long EstimateClipBytes(int width, int height) => checked((long)width * height * 64);

    public static int LimitWorkers(int requested, long clipBytes, long budget)
        => (int)Math.Clamp(budget / Math.Max(1, clipBytes), 1, Math.Max(1, requested));
}
