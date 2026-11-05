namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    /// <summary>
    /// A value provider effect is a special kind of effect that does not produce picture output, but instead provides a dynamic value to other effects. 
    /// <para />
    /// It implements both <see cref="IEffect"/> and <see cref="IEffectArgumentField"/>, allowing it to be used as a source of dynamic parameters for other effects in the rendering pipeline.
    /// </summary>
    public interface IValueProviderEffect : IEffect, IEffectArgumentField
    {
        EffectType IEffect.TypeOfEffect => EffectType.NonIPictureOutputValueProvider;

        bool IEffectArgumentField.IsDynamic => true;
    }

    /// <summary>
    /// A effect computes one or many outputs for full dynamic effect pipeline from one or many input.
    /// </summary>
    public interface IFullDynamicValueProviderEffect : IEffect
    {
        EffectType IEffect.TypeOfEffect => EffectType.FullDynamicValueProviderEffect;

        public record FullDynamicValueProviderEffectOutputResult(FullDynamicValueProviderEffectOutputAnchorState State, string Message);

        public enum FullDynamicValueProviderEffectOutputAnchorState
        {
            Computed,
            NotComputed,
            CannotToCompute,
            Ready,
            Inactive,
        }

        /// <summary>
        /// Computes the value for a specific output port based on the current context and input values. This method is called during the effect execution to retrieve the computed value for the specified output port.
        /// </summary>
        public object? ComputeValue(FullDynamicEffectContext context, string portToCompute);

        /// <summary>
        /// Evaluates the state of all output ports for the current effect execution context. This method provides information about whether each output port has been computed, not computed, or cannot be computed based on the current context and input values.
        /// </summary>
        public IReadOnlyDictionary<string, FullDynamicValueProviderEffectOutputResult> EvaluateState(FullDynamicEffectContext context);

        /// <summary>
        /// Evaluates the types of all output ports for the current effect execution context. This method provides information about the expected data type for each output port based on the current context and input values.
        /// </summary>
        public IReadOnlyDictionary<string, EffectArgumentFieldType> EvaluateOutputPortType(FullDynamicEffectContext context);
    }

    /// <summary>Computes all named outputs once for the current node invocation.</summary>
    public interface IMultipleOutputEffect : IEffect
    {
        public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context);
    }
}
