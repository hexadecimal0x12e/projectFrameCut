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
    public class SharpenEffect_IPicture : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Sharpen";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Amount { get; init; } = 1f;
        public Dictionary<string, object> Parameters { get; set; } = new();

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.IPicture;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = ["Amount"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Amount", "float" }
        };

        public string TypeName => "Sharpen";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.IPicture)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var effect = new SharpenEffect_IPicture
            {
                Amount = DynamicParam.ToFloat(parameters.GetValueOrDefault("Amount")),
                ImplementType = implementType
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            float amount = DynamicParam.Resolve(Parameters.GetValueOrDefault("Amount"), Amount);
            return SharpenEffect.Process(source, amount);
        }
    }

    public class SharpenEffect_HwAccel : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Sharpen";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Amount { get; init; } = 1f;
        public Dictionary<string, object> Parameters { get; set; } = new();

        public string? NeedComputer => "SharpenComputer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = ["Amount"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Amount", "float" }
        };

        public string TypeName => "Sharpen";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            var effect = new SharpenEffect_HwAccel
            {
                Amount = DynamicParam.ToFloat(parameters.GetValueOrDefault("Amount"))
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            float amount = DynamicParam.Resolve(Parameters.GetValueOrDefault("Amount"), Amount);
            if (computer is null)
                return SharpenEffect.Process(source, amount);

            var sw = Stopwatch.StartNew();
            var (r, g, b, a, sourceHasAlpha) = HwAccelEffectHelper.ExtractFloatChannels(source);

            FourChannelResult computeResult;
            if (computer is ISharpenComputer sc)
            {
                computeResult = sc.ComputeSharpen(r, g, b, a, source.Width, amount);
            }
            else
            {
                var resultArr = computer.Compute([r, g, b, a, source.Width, amount]);

                if (resultArr.Length != 4 ||
                    resultArr[0] is not float[] rOut ||
                    resultArr[1] is not float[] gOut ||
                    resultArr[2] is not float[] bOut ||
                    resultArr[3] is not float[] aOut)
                {
                    throw new InvalidOperationException("SharpenComputer did not return expected channel buffers.");
                }

                computeResult = new FourChannelResult(rOut, gOut, bOut, aOut);
            }

            var result = HwAccelEffectHelper.BuildPicture(source, source.Width, source.Height,
                computeResult.R, computeResult.G, computeResult.B, computeResult.A, sourceHasAlpha);
            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                Elapsed = sw.Elapsed,
                OperationDisplayName = "Sharpen (GPU)",
                Operator = typeof(SharpenEffect_HwAccel),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object> { { "Amount", amount } }
            }).ToList();
            return result;
        }
    }

    /// <summary>
    /// The Render-side provider of the Sharpen effect.
    /// </summary>
    public class SharpenEffectProvider : EffectProviderBase
    {
        public SharpenEffectProvider()
        {
            Name = "Sharpen";
            SetField("Amount", 1f);
        }

        public override string TypeName => "Sharpen";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Amount", EffectArgumentFieldType.Numeric, "1", min: "0", max: "5")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture, EffectImplementType.HwAcceleration];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                if (!parameters.ContainsKey("Amount")) parameters["Amount"] = 1f;
                return [SharpenEffect_IPicture.FromParametersDictionary(parameters)];
            }
            return implementType switch
            {
                EffectImplementType.IPicture => [SharpenEffect_IPicture.FromParametersDictionary(parameters)],
                EffectImplementType.HwAcceleration => [SharpenEffect_HwAccel.FromParametersDictionary(parameters)],
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }
    }
}
