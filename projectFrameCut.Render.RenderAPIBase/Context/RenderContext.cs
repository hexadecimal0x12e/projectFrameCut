using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Sources;

namespace projectFrameCut.Render.RenderAPIBase.Context
{


    /// <summary>
    /// A context for rendering operations, providing access to the current rendering state and settings. This abstract class serves as a base for specific render context implementations, allowing for the management of rendering progress, working draft clips, and frame rendering.
    /// </summary>
    public interface IRenderContext
    {
        /// <summary>
        /// A static property that holds the current render context. This property is used to access the current rendering state and settings.
        /// </summary>
        public static IRenderContext? Current = null;

        /// <summary>
        /// Gets the progress of the rendering operation as a double value between 0.0 and 1.0, where 0.0 represents no progress and 1.0 represents completion.
        /// </summary>
        public double Progress { get; }

        /// <summary>
        /// Gets the array of clips that are currently being worked on in the rendering operation. Each clip represents a segment of media that is part of the final output.
        /// </summary>
        public IClip[] Clips { get; }

        /// <summary>
        /// Renders a specific frame of the video based on the provided frame index. This method allows for rendering individual frames, which can be useful for previewing or processing specific parts of the video.
        /// </summary>
        public IPicture? RenderSpecificFrame(uint frameIndex, CancellationToken token);

        /// <summary>
        /// Gets the composed audio source that represents the final audio output of the rendering operation. This property provides access to the combined audio from all clips and effects applied during rendering.
        /// </summary>
        public IAudioSource ComposedAudio { get; }

        /// <summary>
        /// AsyncLocal that holds per-worker rendering state.
        /// Each execution context (thread or async flow) gets its own value,
        /// allowing safe per-thread tracking of which clip, frame, and stage
        /// is being processed without interfering with other workers.
        /// </summary>
        private static readonly AsyncLocal<RenderWorkerState?> _workerState = new();

        /// <summary>
        /// Gets or sets the current worker's rendering state.
        /// This value is per execution context (flows with async/await),
        /// so each worker can report its own status without interfering with others.
        /// Returns null when called outside of a worker thread.
        /// When setting to null, the per-thread state is cleared.
        /// </summary>
        public static RenderWorkerState? WorkerState
        {
            get => _workerState.Value;
            set => _workerState.Value = value;
        }

        /// <summary>
        /// Convenience helper: sets the per-thread worker state in one call.
        /// Returns the same state instance so it can be used in an expression.
        /// </summary>
        public static RenderWorkerState SetWorkerState(uint frame, RenderWorkerStage stage, string? workerName = null, IClip? clip = null)
        {
            var state = _workerState.Value;
            if (state is null)
            {
                state = new RenderWorkerState();
                _workerState.Value = state;
            }
            state.CurrentFrame = frame;
            state.Stage = stage;
            state.WorkerName = workerName ?? state.WorkerName;
            state.CurrentClip = clip ?? state.CurrentClip;
            return state;
        }

        /// <summary>
        /// Clears the current thread's worker state.
        /// </summary>
        public static void ClearWorkerState()
        {
            _workerState.Value = null;
        }

    }
}
