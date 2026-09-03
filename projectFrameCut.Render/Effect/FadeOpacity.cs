using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public class FadeOpacityEffect_IPicture : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "FadeOpacity";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Opacity { get; init; } = 0.8f;
        public Dictionary<string, object> Parameters { get; set; } = new();

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.IPicture;
        public bool IsReorderable => true;
        bool IEffect.CanProcessFromCanvas => true;

        public static List<string> ParametersNeeded { get; } = ["Opacity"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Opacity", "float" }
        };

        public string TypeName => "FadeOpacity";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.IPicture)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var effect = new FadeOpacityEffect_IPicture
            {
                Opacity = DynamicParam.ToFloat(parameters.GetValueOrDefault("Opacity")),
                ImplementType = implementType
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            float opacity = DynamicParam.Resolve(Parameters.GetValueOrDefault("Opacity"), Opacity);
            return OpacityEffect.Process(source, opacity);
        }
    }

    public class FadeOpacityEffect_HwAccel : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "FadeOpacity";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Opacity { get; init; } = 0.8f;
        public Dictionary<string, object> Parameters { get; set; } = new();

        public string? NeedComputer => "OpacityComputer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;
        bool IEffect.CanProcessFromCanvas => true;

        public static List<string> ParametersNeeded { get; } = ["Opacity"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Opacity", "float" }
        };

        public string TypeName => "FadeOpacity";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            var effect = new FadeOpacityEffect_HwAccel
            {
                Opacity = DynamicParam.ToFloat(parameters.GetValueOrDefault("Opacity"))
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            float opacity = DynamicParam.Resolve(Parameters.GetValueOrDefault("Opacity"), Opacity);
            if (computer is null)
                return OpacityEffect.Process(source, opacity);

            var sw = Stopwatch.StartNew();
            var (r, g, b, a, sourceHasAlpha) = HwAccelEffectHelper.ExtractFloatChannels(source);

            FourChannelResult computeResult;
            if (computer is IOpacityComputer oc)
            {
                computeResult = oc.ComputeOpacity(r, g, b, a, opacity);
            }
            else
            {
                var resultArr = computer.Compute([r, g, b, a, opacity]);

                if (resultArr.Length != 4 ||
                    resultArr[0] is not float[] rOut ||
                    resultArr[1] is not float[] gOut ||
                    resultArr[2] is not float[] bOut ||
                    resultArr[3] is not float[] aOut)
                {
                    throw new InvalidOperationException("OpacityComputer did not return expected channel buffers.");
                }

                computeResult = new FourChannelResult(rOut, gOut, bOut, aOut);
            }

            var result = HwAccelEffectHelper.BuildPicture(source, source.Width, source.Height,
                computeResult.R, computeResult.G, computeResult.B, computeResult.A, sourceHasAlpha);
            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                Elapsed = sw.Elapsed,
                OperationDisplayName = "FadeOpacity (GPU)",
                Operator = typeof(FadeOpacityEffect_HwAccel),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object> { { "Opacity", opacity } }
            }).ToList();
            return result;
        }
    }

    /// <summary>
    /// The Render-side provider of the FadeOpacity effect.
    /// </summary>
    public class FadeOpacityEffectProvider : EffectProviderBase
    {
        public FadeOpacityEffectProvider()
        {
            Name = "FadeOpacity";
            SetField("Opacity", 0.8f);
        }

        public override string TypeName => "FadeOpacity";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Opacity", EffectArgumentFieldType.Numeric, "0.8", min: "0", max: "1")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture, EffectImplementType.HwAcceleration];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                if (!parameters.ContainsKey("Opacity")) parameters["Opacity"] = 0.8f;
                return [FadeOpacityEffect_IPicture.FromParametersDictionary(parameters)];
            }
            return implementType switch
            {
                EffectImplementType.IPicture => [FadeOpacityEffect_IPicture.FromParametersDictionary(parameters)],
                EffectImplementType.HwAcceleration => [FadeOpacityEffect_HwAccel.FromParametersDictionary(parameters)],
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }
    }
}
