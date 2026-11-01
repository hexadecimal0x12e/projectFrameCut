using System.Collections.Concurrent;
using System.Diagnostics;
using projectFrameCut.Render.RenderAPIBase.Context;

namespace projectFrameCut.Render.Rendering;

public sealed class RenderComposer(VideoBuilder? builder) : RenderComposerBase
{
    private readonly ConcurrentDictionary<uint, RenderCompositionFrame> outputs = new();
    private long outputBytes;
    private bool composeInTimelineOrder = true;
    public bool ComposeInTimelineOrder
    {
        get => composeInTimelineOrder;
        set
        {
            if (IsInitialized) throw new InvalidOperationException("Composition order cannot change after initialization.");
            composeInTimelineOrder = value;
        }
    }
    protected override bool Ordered => ComposeInTimelineOrder;
    protected override bool SeparateOutput => true;
    protected override long OutputBufferedBytes => Interlocked.Read(ref outputBytes);
    protected override int PendingOutputs => outputs.Count;

    protected override void BeginOutput(CancellationToken token)
    {
        if (builder is not null)
        {
            if (!Ordered) builder.BeginBufferedOutput(Options.StartFrame, Options.Duration);
            token.ThrowIfCancellationRequested();
            if (Cover is not null) builder.Writer.Append(Cover);
        }
        Log($"[Composer] Standard output, composition order={(Ordered ? "timeline" : "ready frames")}.");
    }

    protected override void StoreFrame(RenderCompositionFrame frame, IPicture? picture, CancellationToken token)
    {
        if (picture is null) throw new InvalidOperationException("The composed picture is missing.");
        if (Ordered || builder is null)
        {
            WriteFrame(frame, picture, token);
            return;
        }
        bool submitted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            outputs[frame.FrameIndex] = frame;
            Interlocked.Add(ref outputBytes, FrameBytes);
            builder.Append(frame.FrameIndex, picture);
            submitted = true;
            while (builder.TryWriteNextBufferedFrame(token, out uint index, out var elapsed))
            {
                if (!outputs.TryRemove(index, out var output))
                    throw new InvalidDataException($"Unexpected buffered output frame {index}.");
                Interlocked.Add(ref outputBytes, -FrameBytes);
                try
                {
                    NotifyWritten(output, elapsed);
                    if (output.Diagnostics is not null) output.Diagnostics.Succeeded = true;
                }
                finally { output.Dispose(); CollectGarbage(); }
            }
        }
        finally
        {
            if (!submitted)
            {
                builder.DiscardBufferedFrame(frame.FrameIndex);
                picture.Dispose();
                if (outputs.TryRemove(frame.FrameIndex, out _)) Interlocked.Add(ref outputBytes, -FrameBytes);
                frame.Dispose();
            }
        }
    }

    private long FrameBytes => checked((long)Options.TargetWidth * Options.TargetHeight * (Options.UseHDR ? 10 : 8));

    private void WriteFrame(RenderCompositionFrame frame, IPicture picture, CancellationToken token)
    {
        bool written = false;
        long started = Stopwatch.GetTimestamp();
        try
        {
            token.ThrowIfCancellationRequested();
            IRenderContext.CurrentFrameBuffer = picture;
            if (builder is not null)
            {
                IRenderContext.SetWorkerState(frame.FrameIndex, RenderWorkerStage.WritingOutput, "Render composer");
                using var preview = builder.CapturePreviewFrame(picture);
                using (frame.Diagnostics?.Measure("Encode/write output")) builder.Writer.Append(picture);
                written = true;
                builder.NotifyFrameWritten(frame.FrameIndex, preview ?? picture);
            }
            NotifyWritten(frame, Stopwatch.GetElapsedTime(started));
            if (frame.Diagnostics is not null) frame.Diagnostics.Succeeded = true;
        }
        finally
        {
            if (!written || builder?.DisposeFrameAfterEachWrite != false) picture.Dispose();
            frame.Dispose();
            CollectGarbage();
        }
    }

    private void CollectGarbage()
    {
        if (Options.GCOption == 2) GC.Collect(2, GCCollectionMode.Forced, true, true);
        else if (Options.GCOption == 1 && builder?.DoGCAfterEachWrite != true) GC.Collect();
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var output in outputs.Values)
        {
            builder?.DiscardBufferedFrame(output.FrameIndex);
            output.Dispose();
        }
        outputs.Clear();
        Interlocked.Exchange(ref outputBytes, 0);
    }
}
