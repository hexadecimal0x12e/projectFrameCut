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

        /// <summary>
        /// Begin a render frame: pre-fills the built-in frame/progress sources and clears provider values.
        /// </summary>
        public static void BeginFrame(uint frameIndex, float progress)
        {
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
        }
    }
}
