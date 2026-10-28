using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Rendering;

namespace projectFrameCut.LivePreview;

internal sealed class DynamicPreviewClipPool(long maxBytes, int maxFrames)
{
    // The store guards slot metadata; each buffer guards its pixel readers and writer.
    private readonly List<Slot> slots = [];
    private long frameBytes;
    public uint LastFrame { get; private set; }

    public Slot? Find(uint frame)
    {
        foreach (var slot in slots)
            if (slot.Entry?.FrameIndex == frame) return slot;
        return null;
    }

    public Slot? Reserve(DynamicPreviewCachedClip entry, uint currentFrame, bool preload)
    {
        frameBytes = Math.Max(frameBytes, PreviewBufferWindow.GetFrameBytes(entry.Width, entry.Height));
        int capacity = (int)Math.Clamp(maxBytes / frameBytes, 1, maxFrames);
        Trim(capacity, currentFrame);

        var slot = Find(entry.FrameIndex) ?? slots.FirstOrDefault(s => s.Entry is null)
            ?? slots.Where(s => s.Entry!.FrameIndex < currentFrame).MinBy(s => s.Entry!.FrameIndex);
        if (slot is null && slots.Count < capacity)
        {
            slot = new();
            slots.Add(slot);
        }
        if (slot is null)
        {
            var last = slots.MaxBy(s => s.Entry!.FrameIndex);
            if (!preload || last!.Entry!.FrameIndex > entry.FrameIndex) slot = last;
        }
        if (slot is null) return null;
        Invalidate(slot);
        slot.Entry = entry;
        LastFrame = Math.Max(LastFrame, entry.FrameIndex);
        return slot;
    }

    public void SetFrameCount(int count, uint currentFrame)
    {
        maxFrames = Math.Max(1, count);
        Trim(maxFrames, currentFrame);
    }

    private void Trim(int capacity, uint currentFrame)
    {
        while (slots.Count > capacity)
        {
            var old = slots.FirstOrDefault(s => s.Entry is null)
                ?? slots.Where(s => s.Entry!.FrameIndex < currentFrame).MinBy(s => s.Entry!.FrameIndex)
                ?? slots.MaxBy(s => s.Entry!.FrameIndex)!;
            Invalidate(old);
            slots.Remove(old);
        }
    }

    public void ResetWindow(uint start, uint end)
    {
        foreach (var slot in slots)
            if (slot.Entry is { } entry && (entry.FrameIndex < start || entry.FrameIndex > end)) Invalidate(slot);
        LastFrame = Math.Max(start, slots.Select(s => s.Entry?.FrameIndex ?? 0).DefaultIfEmpty().Max());
    }

    public void RemoveUnprepared()
    {
        foreach (var slot in slots)
            if (slot.Source?.IsValueCreated != true) Invalidate(slot);
    }

    private static void Invalidate(Slot slot)
    {
        Interlocked.Increment(ref slot.Generation);
        slot.Entry = null;
        slot.Source = null;
    }

    internal sealed class Slot
    {
        public DynamicPreviewCachedClip? Entry;
        public Lazy<PreviewFrameSource>? Source;
        public PreviewFrameBuffer Buffer { get; } = new();
        public long Generation;
    }
}
