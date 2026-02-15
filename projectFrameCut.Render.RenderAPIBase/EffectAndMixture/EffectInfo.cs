using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public class EffectInfo
    {
        /// <summary>
        /// Indicates which plugin this effect comes from.
        /// </summary>
        public required string FromPlugin { get; init; }
        /// <summary>
        /// Define the type name of the effect. 
        /// </summary>
        /// <remarks>
        /// it SHOULD equals to <see cref="IEffectBundle.TypeName"/>, <see cref="IEffectFactory.TypeName"/> and so on.
        /// </remarks>
        public required string TypeName { get; init; }
        /// <summary>
        /// Name of this effect. Most for display purpose.
        /// </summary>
        public required string Name { get; init; }
        public required string Description { get; init; }

        public required Dictionary<string, EffectParameterInfo> Parameters { get; init; }

        public required EffectType EffectType { get; init; }
    }

    public class EffectParameterInfo
    {
        public string Name { get; init; }
        public string ParameterType { get; init; }
        public object? DefaultValue { get; init; }
    }
}
