using System;
using System.Collections.Generic;

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
        /// <summary>
        /// The built-in binding source id for the current frame index (exposed as <see cref="float"/>).
        /// </summary>
        public const string BuiltInFrameProviderId = "builtin://frame";
        /// <summary>
        /// The built-in binding source id for the current clip progress (0..1, exposed as <see cref="float"/>).
        /// </summary>
        public const string BuiltInProgressProviderId = "builtin://progress";

        [ThreadStatic]
        private static Dictionary<string, object>? _values;

        [ThreadStatic]
        private static Dictionary<object, object>? _computed;

        /// <summary>
        /// Begin a render frame: pre-fills the built-in frame/progress sources and clears provider values.
        /// </summary>
        public static void BeginFrame(uint frameIndex, float progress)
        {
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
        {
            var previous = _values;
            var computed = _computed;
            BeginFrame(frameIndex, progress);
            if (reuseComputed && previous?.GetValueOrDefault(BuiltInFrameProviderId) is float frame && frame == frameIndex
                && previous.GetValueOrDefault(BuiltInProgressProviderId) is float clipProgress && clipProgress == progress)
                _computed = computed;
            return new FrameScope(previous, computed);
        }

        public static IDisposable PushValues(IReadOnlyDictionary<string, object?> values)
        {
            var previous = _values;
            _values = previous is null ? new() : new(previous);
            foreach (var value in values) Set(value.Key, value.Value);
            return new FrameScope(previous, _computed);
        }

        private sealed class FrameScope(Dictionary<string, object>? previous, Dictionary<object, object>? computed) : IDisposable
        {
            public void Dispose()
            {
                _values = previous;
                _computed = computed;
            }
        }

    }
}
