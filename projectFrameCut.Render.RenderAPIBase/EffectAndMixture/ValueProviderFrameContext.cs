using System;
using System.Collections.Generic;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    /// <summary>
    /// Per-frame value store for bindable dynamic parameters.
    /// During a render frame, value-provider effects write their generated value keyed by their
    /// <see cref="IEffect.Id"/> (which equals the provider bundle Guid), and consumer effects'
    /// bound dynamic parameter getters read it back via <see cref="Get"/>.
    /// </summary>
    /// <remarks>
    /// The storage is <see cref="ThreadStatic"/> so parallel rendering workers never see each other's
    /// values; the provider and its consumers of the same clip/frame run in the same thread and loop,
    /// so the values stay consistent.
    /// </remarks>
    public static class ValueProviderFrameContext
    {
        [ThreadStatic] private static IClip? clip;
        public static IClip? CurrentClip => clip;

        public static IDisposable PushClip(IClip? value)
        {
            var scope = new ClipScope(clip);
            clip = value;
            return scope;
        }

        private sealed class ClipScope(IClip? previous) : IDisposable
        {
            public void Dispose() => clip = previous;
        }
        /// <summary>
        /// The built-in binding source id for the current frame index (exposed as <see cref="float"/>).
        /// </summary>
        public const string BuiltInFrameProviderId = "builtin://frame";
        /// <summary>
        /// The built-in binding source id for the current clip progress (0..1, exposed as <see cref="float"/>).
        /// </summary>
        public const string BuiltInProgressProviderId = "builtin://progress";

        public const string BuiltInAudioSampleProviderId = "builtin://audio-sample";

        private static readonly AsyncLocal<Func<uint, CancellationToken, IAudioSamples<float>>?> audio = new();
        private static Func<uint, CancellationToken, IAudioSamples<float>>? audioSource
        {
            get => audio.Value;
            set => audio.Value = value;
        }
        [ThreadStatic] private static uint currentFrame;

        public static bool IsBuiltInSource(string? id) => id is BuiltInFrameProviderId or BuiltInProgressProviderId or BuiltInAudioSampleProviderId;

        public static bool SupportsAudioSample(Stage stage) => stage is Stage.Picture or Stage.VectorPicture
            or Stage.Text or Stage.VectorComponent or Stage.Mixture;

        public static IDisposable PushAudioSource(Func<uint, CancellationToken, projectFrameCut.Shared.IAudioSamples<float>>? source)
        {
            var scope = new AudioScope(audioSource);
            audioSource = source;
            return scope;
        }

        private sealed class AudioScope(Func<uint, CancellationToken, projectFrameCut.Shared.IAudioSamples<float>>? previous) : IDisposable
        {
            public void Dispose() => audioSource = previous;
        }

        [ThreadStatic]
        private static Dictionary<string, object>? _values;

        [ThreadStatic]
        private static Dictionary<object, object>? _computed;

        [ThreadStatic]
        private static CancellationToken _cancellationToken;
        public static CancellationToken CancellationToken => _cancellationToken;

        [ThreadStatic]
        private static Action<IEffect, object?>? _nodeObserver;
        public static Action<IEffect, object?>? NodeObserver => _nodeObserver;

        public static IDisposable ObserveNodes(Action<IEffect, object?> observer)
        {
            var scope = new NodeObserverScope(_nodeObserver);
            _nodeObserver = observer;
            return scope;
        }

        private sealed class NodeObserverScope(Action<IEffect, object?>? previous) : IDisposable
        {
            public void Dispose() => _nodeObserver = previous;
        }

        /// <summary>
        /// Begin a render frame: pre-fills the built-in frame/progress sources and clears provider values.
        /// </summary>
        public static void BeginFrame(uint frameIndex, float progress)
        {
            currentFrame = frameIndex;
            _computed = new(ReferenceEqualityComparer.Instance);
            _values = new Dictionary<string, object>(8)
            {
                [BuiltInFrameProviderId] = (float)frameIndex,
                [BuiltInProgressProviderId] = progress,
            };
        }

        /// <summary>
        /// Begin a render frame with only the frame index (progress defaults to 0).
        /// </summary>
        public static void BeginFrame(uint frameIndex)
        {
            BeginFrame(frameIndex, 0f);
        }

        /// <summary>
        /// Store a value-provider effect's generated value for the current frame, keyed by its <see cref="IEffect.Id"/>.
        /// </summary>
        public static void Set(string key, object? value)
        {
            _values ??= new Dictionary<string, object>(4);
            if (value is null) { _values.Remove(key); return; }
            _values[key] = value;
        }

        /// <summary>
        /// Read the current frame value for a binding source id, or null when unavailable.
        /// </summary>
        public static object? Get(string key)
        {
            if (_values is not null && _values.TryGetValue(key, out var value)) return value;
            if (key == BuiltInAudioSampleProviderId && audioSource is not null)
            {
                var samples = audioSource(currentFrame, _cancellationToken);
                Set(key, samples);
                return samples;
            }
            return null;
        }

        /// <summary>
        /// End the render frame and release the thread-local storage.
        /// </summary>
        public static void EndFrame()
        {
            _values = null;
            _computed = null;
        }

        public static T GetOrCompute<T>(object key, Func<T> compute) where T : class
        {
            if (_values is null) return compute();
            _computed ??= new(ReferenceEqualityComparer.Instance);
            if (_computed.TryGetValue(key, out var value)) return (T)value;
            var result = compute();
            _computed[key] = result;
            return result;
        }

        public static IDisposable PushFrame(uint frameIndex, float progress) => PushFrame(frameIndex, progress, false);

        public static IDisposable PushFrame(uint frameIndex, float progress, bool reuseComputed)
            => PushFrame(frameIndex, progress, reuseComputed, _cancellationToken);

        public static IDisposable PushFrame(uint frameIndex, float progress, bool reuseComputed, CancellationToken cancellationToken)
        {
            var previous = _values;
            var computed = _computed;
            var previousToken = _cancellationToken;
            var previousFrame = currentFrame;
            _cancellationToken = cancellationToken;
            BeginFrame(frameIndex, progress);
            if (reuseComputed && previous?.GetValueOrDefault(BuiltInFrameProviderId) is float frame && frame == frameIndex
                && previous.GetValueOrDefault(BuiltInProgressProviderId) is float clipProgress && clipProgress == progress)
                _computed = computed;
            return new FrameScope(previous, computed, previousToken, previousFrame);
        }

        public static IDisposable PushValues(IReadOnlyDictionary<string, object?> values)
        {
            var previous = _values;
            _values = previous is null ? new() : new(previous);
            foreach (var value in values) Set(value.Key, value.Value);
            return new FrameScope(previous, _computed, _cancellationToken, currentFrame);
        }

        private sealed class FrameScope(Dictionary<string, object>? previous, Dictionary<object, object>? computed, CancellationToken cancellationToken, uint frame) : IDisposable
        {
            public void Dispose()
            {
                _values = previous;
                _computed = computed;
                _cancellationToken = cancellationToken;
                currentFrame = frame;
            }
        }

    }
}
