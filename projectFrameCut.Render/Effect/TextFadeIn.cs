using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.Effect
{
    public class TextFadeInContinuousEffect : IContinuousTextEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "TextFadeIn";
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.IPicture;
        public bool YieldProcessStep => false;
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; }

        public int RelativeWidth { get; set; } = -1;
        public int RelativeHeight { get; set; } = -1;
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }
        public bool IsScoped { get; set; }

        public Dictionary<string, object> Parameters => new();

        public TextClipEntry[] Process(TextClipEntry[] source, float progress)
        {
            float alpha = Math.Clamp(progress, 0f, 1f);

            var result = new TextClipEntry[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                result[i] = source[i] with
                {
                    a = (source[i].a ?? 1f) * alpha
                };
            }
            return result;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters)
        {
            return new TextFadeInContinuousEffect
            {
                ImplementType = this.ImplementType,
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

    public class TextFadeInContinuousEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "TextFadeIn";
        public EffectTarget Target => EffectTarget.Video;
        public List<string> ParametersNeeded => new();
        public Dictionary<string, string> ParametersType => new();
        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.IPicture };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
                return BuildWithDefaultType(parameters);

            return implementType switch
            {
                EffectImplementType.IPicture => BuildWithType(implementType, parameters),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            return BuildWithType(EffectImplementType.IPicture, parameters);
        }

        private static IEffect BuildWithType(EffectImplementType implementType, Dictionary<string, object>? parameters)
        {
            return new TextFadeInContinuousEffect
            {
                ImplementType = implementType,
            };
        }
    }
}
