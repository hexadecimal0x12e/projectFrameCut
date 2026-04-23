using System;
using System.Collections.Generic;
using System.Text;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface ISpeedVarianceProvider : IEffect
    {
        /// <summary>
        /// Get the target frame after processing in the processer.
        /// </summary>
        /// <param name="sourceFrame">the frame <b>RELATIVE INSIDE THE CLIP</b></param>
        /// <returns></returns>
        public uint GetTargetFrame(uint sourceFrame);

        /// <summary>
        /// Get the length of the clip should be after processing in the processer. This is used for calculating the length of the final output video. 
        /// </summary>
        /// <remarks>
        /// The source frame for this method is the frame relative to the draft, and the returned length is also relative to the draft. So if the source frame is 100 and the returned length is 150, it means that after processing, the frame 100 in the draft will become frame 150 in the final output video.
        /// </remarks>
        /// <returns>the length of the clip after processing.</returns>
        public uint GetEffectiveLength(uint length);

        EffectType IEffect.TypeOfEffect => EffectType.SpeedVarianceProvider;
        EffectImplementType IEffect.ImplementType => EffectImplementType.NotSpecified;
        bool IEffect.Enabled { get => false; set => throw new InvalidOperationException("Cannot enable a ISpeedVarianceProvider. It should be used within the render system."); } // the simplest way to prevent rendering of ISpeedVarianceProvider is to make it always disabled,
        string? IEffect.NeedComputer => null;
        bool IEffect.YieldProcessStep => false;
        int IEffect.RelativeWidth { get => -1; set => throw new InvalidOperationException("Cannot set RelativeWidth for a ISpeedVarianceProvider."); }
        int IEffect.RelativeHeight { get => -1; set => throw new InvalidOperationException("Cannot set RelativeWidth for a ISpeedVarianceProvider."); }
        int IEffect.Index { get => int.MaxValue; set => throw new InvalidOperationException("Cannot set Index for a ISpeedVarianceProvider."); }
        string? IEffect.BindedEffectGroupID { get => null; set => throw new InvalidOperationException("Cannot set BindedEffectGroupID for a ISpeedVarianceProvider."); }
    }
    /// <summary>
    /// A classic speed variance provider that provides a constant speed ratio. The ratio can be set through the "Ratio" parameter. This is useful for implementing effects like "Fast Forward" or "Slow Motion".
    /// </summary>
    public class ClassicSpeedVarianceProvider : ISpeedVarianceProvider
    {
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";

        public string TypeName => "ClassicSpeedVarianceProvider";

        public string Name { get; set; } = "ClassicSpeedVarianceProvider";
        public string Id { get; set; } = Guid.NewGuid().ToString();

        // Keep mutable IEffect members so this provider can flow through the generic effect pipeline.
        public bool Enabled { get; set; } = true;
        public int RelativeWidth { get; set; } = -1;
        public int RelativeHeight { get; set; } = -1;
        public int Index { get; set; }
        public string? BindedEffectGroupID { get; set; }

        public Dictionary<string, object> Parameters { get; set; } = new();

        public float Ratio { get; set; } = 1f;

        public uint GetTargetFrame(uint sourceFrame)
        {
            double mapped = sourceFrame * GetSanitizedRatio();
            if (mapped <= 0d)
            {
                return 0;
            }

            if (mapped >= uint.MaxValue)
            {
                return uint.MaxValue;
            }

            return (uint)Math.Round(mapped, MidpointRounding.AwayFromZero);
        }

        public uint GetEffectiveLength(uint length)
        {
            if (length == 0)
            {
                return 0;
            }

            double effective = length * GetSanitizedRatio();
            if (effective < 1d)
            {
                return 1;
            }

            if (effective >= uint.MaxValue)
            {
                return uint.MaxValue;
            }

            return (uint)Math.Round(effective, MidpointRounding.AwayFromZero);
        }


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
            return new ClassicSpeedVarianceProvider() { Parameters = parameters };
        }

        private float GetSanitizedRatio()
        {
            if (Ratio <= 0f || float.IsNaN(Ratio) || float.IsInfinity(Ratio))
            {
                return 1f;
            }

            return Ratio;
        }
    }

    public class ClassicSpeedVarianceProviderFactory : IEffectFactory
    {
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";
        public string TypeName => "ClassicSpeedVarianceProvider";
        public EffectTarget Target => EffectTarget.SpeedVariance;

        public List<string> ParametersNeeded { get; } = ["Ratio"];
        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Ratio", "float" }
        };

        public EffectImplementType[] SupportsImplementTypes => [EffectImplementType.NotSpecified];

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType != EffectImplementType.NotSpecified)
            {
                throw new NotSupportedException($"Effect '{TypeName}' only supports implement type '{EffectImplementType.NotSpecified}'.");
            }

            parameters ??= new Dictionary<string, object> { { "Ratio", 1f } };
            if (!parameters.ContainsKey("Ratio"))
            {
                parameters["Ratio"] = 1f;
            }

            var effect = new ClassicSpeedVarianceProvider
            {
                Parameters = parameters
            };
            effect.Initialize();
            return effect;
        }
    }
}
