using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

namespace projectFrameCut.Render.RenderAPIBase.Context
{
    /// <summary>
    /// Holds per-worker rendering state, flowing with async context via <see cref="AsyncLocal{T}"/>.
    /// Allows external consumers (e.g., UI diagnostics, logging) to query which clip, frame,
    /// and stage a worker thread is currently processing without interfering with other workers.
    /// </summary>
    public class RenderWorkerState
    {
        /// <summary>
        /// The clip currently being processed by this worker, or null if between frames.
        /// </summary>
        public IClip? CurrentClip { get; set; }

        /// <summary>
        /// The frame index currently being processed by this worker.
        /// </summary>
        public uint CurrentFrame { get; set; }

        /// <summary>
        /// The rendering stage this worker is currently in.
        /// </summary>
        public RenderWorkerStage Stage { get; set; }

        /// <summary>
        /// Optional descriptive name for the worker thread.
        /// </summary>
        public string? WorkerName { get; set; }

        /// <inheritdoc />
        public override string ToString()
            => $"[{WorkerName ?? "?"}] Frame {CurrentFrame}, Clip {CurrentClip?.Name ?? CurrentClip?.Id.ToString() ?? "(null)"}, Stage {Stage}";
    }

    /// <summary>
    /// Defines the rendering stage of a worker thread.
    /// </summary>
    public enum RenderWorkerStage
    {
        /// <summary>
        /// No work is currently being performed.
        /// </summary>
        Idle,

        /// <summary>
        /// Decoding / source preparation is in progress.
        /// </summary>
        PreparingSource,

        /// <summary>
        /// Effects are being applied to a clip's frame.
        /// </summary>
        ProcessingEffects,

        /// <summary>
        /// Compositing clips together into the final frame.
        /// </summary>
        Compositing,

        /// <summary>
        /// Writing the rendered frame to the output.
        /// </summary>
        WritingOutput,
    }
}
