using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.Effect
{
    public class TextFadeInContinuousEffect : IContinuousTextEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "TextFadeIn";
        public EffectImplementType ImplementType => EffectImplementType.IPicture;
        public bool YieldProcessStep => false;
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; }

        public int RelativeWidth { get; set; } = -1;
        public int RelativeHeight { get; set; } = -1;
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }
        public bool IsScoped { get; set; }

        public Dictionary<string, object> Parameters { get; set; } = new();

        public bool IsReorderable => false;

        public TextEntry[] Process(TextEntry[] source, float progress)
        {
            float alpha = Math.Clamp(progress, 0f, 1f);

            var result = new TextEntry[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                result[i] = source[i] with
                {
                    FillA = source[i].FillA * alpha
                };
            }
            return result;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters)
        {
            return new TextFadeInContinuousEffect
            {
                RelativeWidth = this.RelativeWidth,
                RelativeHeight = this.RelativeHeight,
                Name = this.Name,
                Index = this.Index,
                Enabled = this.Enabled,
                StartPoint = this.StartPoint,
                EndPoint = this.EndPoint,
                IsScoped = this.IsScoped,
            };
        }
    }

    /// <summary>
    /// The Render-side provider of the TextFadeIn continuous text effect.
    /// </summary>
    public class TextFadeInEffectProvider : EffectProviderBase
    {
        public TextFadeInEffectProvider()
        {
            Name = "TextFadeIn";
        }

        public override string TypeName => "TextFadeIn";

        public override EffectType TypeOfEffect => EffectType.ContinuousTextEffect;

        public override EffectTarget Target => EffectTarget.Text;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return Array.Empty<EffectArgumentFieldDescriptor>();
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture];


    }
}
