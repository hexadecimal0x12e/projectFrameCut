using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    /// <summary>
    /// A value-provider effect that generates a dynamic value for each render frame.
    /// It participates in the render chain (reusing <see cref="EffectType.BindableEffect"/> so the render
    /// loop's switch already reaches it): the render loop writes its generated value into
    /// <see cref="ValueProviderFrameContext"/> keyed by its <see cref="IEffect.Id"/> (which equals the
    /// provider bundle Guid), and consumer effects' bound dynamic parameters read it back.
    /// </summary>
    /// <remarks>
    /// This is the new-system replacement of the legacy <see cref="IBindableArgumentEffectValueProvider"/>.
    /// </remarks>
    public interface IValueProviderEffect : IEffect
    {
        /// <summary>
        /// The display name of the output anchor this provider exposes as a bindable value.
        /// </summary>
        public string OutputAnchorName { get; }

        /// <summary>
        /// Whether the generated value is computed once (and cached across frames) or per frame.
        /// </summary>
        public bool GenerateOnce { get; }

        /// <summary>
        /// Generate the value for the current frame.
        /// </summary>
        /// <param name="frameIndex">The absolute frame index currently being rendered.</param>
        /// <param name="computer">An optional computer for accelerated computing.</param>
        /// <param name="targetWidth">Output canvas' width.</param>
        /// <param name="targetHeight">Output canvas' height.</param>
        /// <returns>The generated value; its exact type depends on the implementation.</returns>
        public object? GenerateValue(uint frameIndex, IComputer? computer, int targetWidth, int targetHeight);

        EffectType IEffect.TypeOfEffect => EffectType.BindableEffect;
    }
}
