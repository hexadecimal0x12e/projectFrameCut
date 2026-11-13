using projectFrameCut.Render.Compose;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Rendering
{
    public static class EffectProcessing
    {
        private static readonly AsyncLocal<AudioSampleSource?> audioSource = new();
        internal static AudioSampleSource? CurrentAudioSource => audioSource.Value;

        public static IDisposable OpenAudioFrame(AudioSampleSource? source) => OpenAudioFrame(source, null);

        public static IDisposable OpenAudioFrame(AudioSampleSource? source, uint? projectFrameCount)
        {
            var scope = new AudioFrameScope(audioSource.Value,
                ValueProviderFrameContext.PushAudioSource(source is null ? null : source.ReadFrame),
                (projectFrameCount ?? source?.TotalProjectFrames ?? ValueProviderFrameContext.ProjectFrameCount
                    ?? source?.GetTimelineFrameCount()) is { } count ? ValueProviderFrameContext.PushProject(count) : null);
            audioSource.Value = source;
            return scope;
        }

        private sealed class AudioFrameScope(AudioSampleSource? previous, IDisposable scope, IDisposable? project) : IDisposable
        {
            public void Dispose() { project?.Dispose(); scope.Dispose(); audioSource.Value = previous; }
        }

    }
}
