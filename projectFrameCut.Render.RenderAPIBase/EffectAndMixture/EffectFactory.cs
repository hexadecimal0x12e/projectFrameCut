
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface IEffectFactory
    {
        /// <summary>
        /// Indicates which plugin this effect comes from.
        /// </summary>
        public string FromPlugin { get; }
        /// <summary>
        /// Define the type name of the effect. 
        /// </summary>
        public string TypeName { get; }
        /// <summary>
        /// Indicates which parameters are needed for this effect.
        /// </summary>
        public List<string> ParametersNeeded { get; }
        /// <summary>
        /// Indicates the type of each parameter.
        /// </summary>
        public Dictionary<string, string> ParametersType { get; }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null);

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null);

        public EffectImplementType[] SupportsImplementTypes { get; }


    }

    public static class EffectFactoryExtensions
    {
        /// <summary>
        /// Build an <see cref="IEffect"/> for the specified <see cref="EffectImplementType"/> and call <see cref="IEffect.Initialize"/> before returning it.
        /// </summary>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="InvalidOperationException">Thrown when the created effect doesn't match the requested implement type.</exception>
        public static IEffect BuildAndInitialize(this IEffectFactory factory, EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            ArgumentNullException.ThrowIfNull(factory);
            if(!factory.SupportsImplementTypes.Contains(implementType))
            {
                throw new InvalidOperationException($"EffectFactory '{factory.TypeName}' does not support ImplementType '{implementType}'.");
            }
            var effect = factory.Build(implementType, parameters);
            if (implementType != EffectImplementType.NotSpecified && effect.ImplementType != implementType)
            {
                throw new InvalidOperationException($"EffectFactory '{factory.TypeName}' returned ImplementType '{effect.ImplementType}', but '{implementType}' was requested.");
            }

            effect.Initialize();
            return effect;
        }

        /// <summary>
        /// Build an <see cref="IContinuousEffect"/> via <see cref="IEffectFactory.BuildWithDefaultType"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="InvalidOperationException">Thrown when the created effect isn't a <see cref="IContinuousEffect"/>.</exception>
        public static IContinuousEffect BuildContinuousWithDefaultType(this IEffectFactory factory, Dictionary<string, object>? parameters = null)
        {
            ArgumentNullException.ThrowIfNull(factory);
            var effect = factory.BuildWithDefaultType(parameters);
            return effect as IContinuousEffect
                ?? throw new InvalidOperationException($"EffectFactory '{factory.TypeName}' created '{effect.GetType().FullName}', which is not an IContinuousEffect.");
        }

        /// <summary>
        /// Build an <see cref="IContinuousEffect"/> for the specified <see cref="EffectImplementType"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="InvalidOperationException">Thrown when the created effect isn't a <see cref="IContinuousEffect"/>.</exception>
        public static IContinuousEffect BuildContinuous(this IEffectFactory factory, EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            ArgumentNullException.ThrowIfNull(factory);
            var effect = factory.Build(implementType, parameters);
            return effect as IContinuousEffect
                ?? throw new InvalidOperationException($"EffectFactory '{factory.TypeName}' created '{effect.GetType().FullName}', which is not an IContinuousEffect.");
        }

        /// <summary>
        /// Build an <see cref="IContinuousEffect"/> for the specified <see cref="EffectImplementType"/> and call <see cref="IEffect.Initialize"/> before returning it.
        /// </summary>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="InvalidOperationException">Thrown when the created effect isn't a <see cref="IContinuousEffect"/> or doesn't match the requested implement type.</exception>
        public static IContinuousEffect BuildContinuousAndInitialize(this IEffectFactory factory, EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            var effect = factory.BuildAndInitialize(implementType, parameters);
            return effect as IContinuousEffect
                ?? throw new InvalidOperationException($"EffectFactory '{factory.TypeName}' created '{effect.GetType().FullName}', which is not an IContinuousEffect.");
        }
    }
}
