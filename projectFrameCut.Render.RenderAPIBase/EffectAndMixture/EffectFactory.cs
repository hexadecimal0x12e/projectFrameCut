
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

        /// <summary>
        /// Build a effect with default implementation type.
        /// </summary>
        /// <param name="parameters"></param>
        /// <returns></returns>
        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null);
        /// <summary>
        /// Build the specified effect implementation type.
        /// </summary>
        /// <param name="implementType"></param>
        /// <param name="parameters"></param>
        /// <returns></returns>
        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null);

        /// <summary>
        /// Get the supported implement types of this effect.
        /// </summary>
        public EffectImplementType[] SupportsImplementTypes { get; }
        /// <summary>
        /// Gets the default effect implementation type supported by this instance.
        /// </summary>
        /// <remarks>The default implementation type is determined by the first entry in the
        /// SupportsImplementTypes array. If no implementation types are supported, the value is
        /// EffectImplementType.NotSpecified.</remarks>
        public virtual EffectImplementType DefaultImplementType => SupportsImplementTypes.Length > 0 ? SupportsImplementTypes[0] : EffectImplementType.NotSpecified;


    }

    public static class EffectFactoryExtensions
    {
        public static IEffectFactory GetFactory(this IEffect effect, Dictionary<string, IEffectFactory> factories)
        {
            ArgumentNullException.ThrowIfNull(effect);
            ArgumentNullException.ThrowIfNull(factories);
            if (!factories.TryGetValue(effect.TypeName, out var factory))
            {
                if(factories.First(f => f.Value.TypeName.Equals(effect.TypeName, StringComparison.Ordinal)).Value is IEffectFactory foundFactory)
                {
                    return foundFactory;
                }
                throw new InvalidOperationException($"No EffectFactory found for Effect TypeName '{effect.TypeName}'.");
            }
            return factory;
        }


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
