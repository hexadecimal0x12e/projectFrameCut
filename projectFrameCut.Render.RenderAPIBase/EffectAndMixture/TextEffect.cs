using projectFrameCut.Drawing.Text.Entry;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface ITextEffect : IEffect
    {
        /// <summary>
        /// Process the input text clip's entries.
        /// </summary>
        /// <returns>the updated entries.</returns>
        public TextEntry[] Process(TextEntry[] source);

        string? IEffect.NeedComputer => null;
        EffectType IEffect.TypeOfEffect => EffectType.TextEffect;
    }
    public interface IContinuousTextEffect : IEffect
    {
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }
        public bool IsScoped { get; set; }

        /// <summary>
        /// Process the input text clip's entries.
        /// </summary>
        /// <param name="source">The input text entries.</param>
        /// <param name="progress">A value between 0 and 1 indicating the current progress of the effect.</param>
        /// <returns>the updated entries.</returns>
        public TextEntry[] Process(TextEntry[] source, float progress);

        string? IEffect.NeedComputer => null;
        EffectType IEffect.TypeOfEffect => EffectType.ContinuousTextEffect;
    }
}
