using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public abstract class MockBindableEffectFactoryBase<T> : IBindableEffectFactory where T : MockBindableEffectBase, new()
    {
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";
        public string TypeName => typeof(T).Name;
        public List<string> ParametersNeeded => new();
        public Dictionary<string, string> ParametersType => new();
        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.Custom1 };

        public string? ID { get; set; }
        public string? BindedInputID { get; set; }
        public string[]? BindedInputIDs { get; set; }

        public IEffect Build(EffectImplementType implementType, string? ID, string? BindedInputID, string[]? BindedInputIDs = null, Dictionary<string, object>? parameters = null)
        {
             var effect = new T();
             effect.Id = ID ?? this.ID ?? Guid.NewGuid().ToString();
             effect.BindedArgumentProviderID = BindedInputID ?? this.BindedInputID;

             if (effect is IBindableArgumentEffectManyToOneValueProcesser manyToOne)
             {
                 manyToOne.BindedArgumentProviderIDs = BindedInputIDs ?? this.BindedInputIDs ?? Array.Empty<string>();
             }
             if (effect is IBindableArgumentEffectManyInputResultGenerator manyInput)
             {
                 manyInput.BindedArgumentProviderIDs = BindedInputIDs ?? this.BindedInputIDs ?? Array.Empty<string>();
             }

             if (parameters != null)
             {
                 foreach (var kvp in parameters)
                 {
                     effect.Parameters[kvp.Key] = kvp.Value;
                 }
             }
             effect.Initialize();
             return effect;
        }

        public IEffect BuildWithDefaultType(string? ID, string? BindedInputID, string[]? BindedInputIDs = null, Dictionary<string, object>? parameters = null)
        {
            return Build(EffectImplementType.Custom1, ID, BindedInputID, BindedInputIDs, parameters);
        }
    }

    public class MockValueProviderFactory : MockBindableEffectFactoryBase<MockValueProvider> { }
    public class MockOneToOneProcessorFactory : MockBindableEffectFactoryBase<MockOneToOneProcessor> { }
    public class MockManyToOneProcessorFactory : MockBindableEffectFactoryBase<MockManyToOneProcessor> { }
    public class MockOneInputResultGeneratorFactory : MockBindableEffectFactoryBase<MockOneInputResultGenerator> { }
    public class MockManyInputResultGeneratorFactory : MockBindableEffectFactoryBase<MockManyInputResultGenerator> { }
}
