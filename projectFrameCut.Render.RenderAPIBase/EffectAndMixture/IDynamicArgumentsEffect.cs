using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    /// <summary>
    /// Marks an effect that supports bindable dynamic parameters.
    /// The effect keeps its static typed parameter properties (e.g. <c>float Sigma</c>) and exposes a
    /// per-frame getter dictionary that is mounted from outside by the effect builder
    /// (see <see cref="DynamicParam.BuildProviders"/>).
    /// </summary>
    /// <remarks>
    /// Inside <c>Render</c>, effects resolve the live value for each parameter via
    /// <see cref="DynamicParam.Resolve{T}"/>; when no provider is mounted the static value is used,
    /// so unbinded effects behave exactly as before.
    /// </remarks>
    public interface IDynamicArgumentsEffect
    {
        /// <summary>
        /// Per-frame value getters keyed by parameter field id.
        /// Mounted by the effect builder from the provider's <c>__Binding_*</c> parameters;
        /// null when no parameter is bound.
        /// </summary>
        public IReadOnlyDictionary<string, Func<object?>>? DynamicProviders { get; set; }
    }
}
