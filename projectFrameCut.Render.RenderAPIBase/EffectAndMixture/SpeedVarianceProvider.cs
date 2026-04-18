using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface ISpeedVarianceProvider : IEffect
    {

        /// <summary>
        /// Get the speed ratio for the current progress. 
        /// </summary>
        /// <param name="progress">The progress is a value between 0 and 1 representing the position within the clip.</param>
        /// <returns>The speed ratio for the current progress.</returns>
        public float GetRatio(float progress);

        EffectType IEffect.TypeOfEffect => EffectType.SpeedVarianceProvider;
        EffectImplementType IEffect.ImplementType => EffectImplementType.NotSpecified;
    }

    public class ClassicSpeedVarianceProvider : ISpeedVarianceProvider
    {
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";

        public string TypeName => "ClassicSpeedVarianceProvider";

        public string Name { get; set; }
        public string Id { get; set; }

        public Dictionary<string, object> Parameters { get; set; } = new();

        public bool Enabled { get; set; }
        public int Index { get; set; }

        public string? NeedComputer => null;

        public bool YieldProcessStep => false;

        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectGroupID { get; set; }

        public float Ratio { get; set; } = 1f;

        public float GetRatio(float progress) => Ratio;

        public void Initialize()
        {
            if (Parameters?.TryGetValue("Ratio", out var value) ?? false)
            {
                try 
                { 
                    Ratio = Convert.ToSingle(value); 
                }
                catch { }
            }
        }

        public IEffect WithParameters(Dictionary<string, object> parameters)
        {
            return new ClassicSpeedVarianceProvider();
        }
    }
}
