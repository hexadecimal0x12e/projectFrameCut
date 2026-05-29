using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public class ColorAdjustmentEffect_ImageSharp : IColorAdjustEffect
    {
        public bool Enabled { get; set; } = true;
        public string Name { get; set; } = "ColorAdjustment";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Brightness { get; init; } = 1f;
        public float Contrast { get; init; } = 1f;
        public float Saturation { get; init; } = 1f;
        public float Hue { get; init; } = 0f;
        public float Gamma { get; init; } = 1f;
        public float Vibrance { get; init; } = 0f;
        public float Temperature { get; init; } = 0f;
        public bool Invert { get; init; } = false;
        public float Grayscale { get; init; } = 0f;
        public float Opacity { get; init; } = 1f;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"Brightness", Brightness},
            {"Contrast", Contrast},
            {"Saturation", Saturation},
            {"Hue", Hue},
            {"Gamma", Gamma},
            {"Vibrance", Vibrance},
            {"Temperature", Temperature},
            {"Invert", Invert},
            {"Grayscale", Grayscale},
            {"Opacity", Opacity}
        };

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => true;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.ImageSharp;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Brightness",
            "Contrast",
            "Saturation",
            "Hue",
            "Gamma",
            "Vibrance",
            "Temperature",
            "Invert",
            "Grayscale",
            "Opacity"
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"Brightness", "float"},
            {"Contrast", "float"},
            {"Saturation", "float"},
            {"Hue", "float"},
            {"Gamma", "float"},
            {"Vibrance", "float"},
            {"Temperature", "float"},
            {"Invert", "bool"},
            {"Grayscale", "float"},
            {"Opacity", "float"}
        };

        public string TypeName => "ColorAdjustment";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.ImageSharp)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            return new ColorAdjustmentEffect_ImageSharp
            {
                Brightness = Convert.ToSingle(parameters["Brightness"]),
                Contrast = Convert.ToSingle(parameters["Contrast"]),
                Saturation = Convert.ToSingle(parameters["Saturation"]),
                Hue = Convert.ToSingle(parameters["Hue"]),
                Gamma = Convert.ToSingle(parameters["Gamma"]),
                Vibrance = Convert.ToSingle(parameters["Vibrance"]),
                Temperature = Convert.ToSingle(parameters["Temperature"]),
                Invert = Convert.ToBoolean(parameters["Invert"]),
                Grayscale = Convert.ToSingle(parameters["Grayscale"]),
                Opacity = Convert.ToSingle(parameters["Opacity"]),
                ImplementType = implementType
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Process(IPicture source, IComputer? computer)
        {
            throw new NotImplementedException();
        }

        public IPictureProcessStep GetStep(IPicture source)
        {
            return new ColorAdjustmentProcessStep(Brightness, Contrast, Saturation, Hue, Gamma, Vibrance, Temperature, Invert, Grayscale, Opacity);
        }
    }

    public class ColorAdjustmentProcessStep : IPictureProcessStep
    {
        private TimeSpan? _elapsed;
        public string Name => "ColorAdjustment";
        public Dictionary<string, object?> Properties { get; set; } = new();

        public float Brightness { get; }
        public float Contrast { get; }
        public float Saturation { get; }
        public float Hue { get; }
        public float Gamma { get; }
        public float Vibrance { get; }
        public float Temperature { get; }
        public bool Invert { get; }
        public float Grayscale { get; }
        public float Opacity { get; }

        public ColorAdjustmentProcessStep(float brightness, float contrast, float saturation, float hue, float gamma = 1f, float vibrance = 0f, float temperature = 0f, bool invert = false, float grayscale = 0f, float opacity = 1f)
        {
            Brightness = brightness;
            Contrast = contrast;
            Saturation = saturation;
            Hue = hue;
            Gamma = gamma;
            Vibrance = vibrance;
            Temperature = temperature;
            Invert = invert;
            Grayscale = grayscale;
            Opacity = opacity;
            Properties = new Dictionary<string, object?>
            {
                { nameof(Brightness), Brightness },
                { nameof(Contrast), Contrast },
                { nameof(Saturation), Saturation },
                { nameof(Hue), Hue },
                { nameof(Gamma), Gamma },
                { nameof(Vibrance), Vibrance },
                { nameof(Temperature), Temperature },
                { nameof(Invert), Invert },
                { nameof(Grayscale), Grayscale },
                { nameof(Opacity), Opacity }
            };
        }

        public IPicture Process(IPicture source)
        {
            var sw = Stopwatch.StartNew();
            var img = source.SaveToSixLaborsImage();
            img.Mutate(i =>
            {
                i.Brightness(Brightness);
                i.Contrast(Contrast);
                i.Saturate(Saturation);
                i.Hue(Hue);

                if (Gamma != 1f)
                    i.GaussianBlur(Gamma * 0.5f);

                if (Vibrance != 0f)
                    i.Saturate(1f + Vibrance * 0.5f);

                if (Temperature != 0f)
                    ApplyTemperature(i, Temperature);

                if (Invert)
                    i.Invert();

                if (Grayscale > 0f)
                {
                    if (Grayscale >= 1f)
                        i.Grayscale();
                    else
                        ApplyPartialGrayscale(i, Grayscale);
                }

                if (Opacity < 1f)
                    i.Opacity(Opacity);
            });
            IPicture result = Shared.PictureExtensions.ToPJFCPicture(img, source.BitPerPixel);
            sw.Stop();
            _elapsed = sw.Elapsed;

            result.ProcessStack = source.ProcessStack.Append(GetProcessStack()).ToList();
            return result;
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            return ctx =>
            {
                ctx.Brightness(Brightness);
                ctx.Contrast(Contrast);
                ctx.Saturate(Saturation);
                ctx.Hue(Hue);

                if (Gamma != 1f)
                    ctx.GaussianBlur(Gamma * 0.5f);

                if (Vibrance != 0f)
                    ctx.Saturate(1f + Vibrance * 0.5f);

                if (Temperature != 0f)
                    ApplyTemperature(ctx, Temperature);

                if (Invert)
                    ctx.Invert();

                if (Grayscale > 0f)
                {
                    if (Grayscale >= 1f)
                        ctx.Grayscale();
                    else
                        ApplyPartialGrayscale(ctx, Grayscale);
                }

                if (Opacity < 1f)
                    ctx.Opacity(Opacity);

                return ctx;
            };
        }

        public PictureProcessStack GetProcessStack() => new PictureProcessStack
        {
            Elapsed = _elapsed,
            OperationDisplayName = "ColorAdjustment",
            Operator = typeof(ColorAdjustmentProcessStep),
            ProcessingFuncStackTrace = new StackTrace(true),
            
            Properties = new Dictionary<string, object>
            {
                { nameof(Brightness), Brightness },
                { nameof(Contrast), Contrast },
                { nameof(Saturation), Saturation },
                { nameof(Hue), Hue },
                { nameof(Gamma), Gamma },
                { nameof(Vibrance), Vibrance },
                { nameof(Temperature), Temperature },
                { nameof(Invert), Invert },
                { nameof(Grayscale), Grayscale },
                { nameof(Opacity), Opacity }
            }
        };

        private static void ApplyTemperature(IImageProcessingContext ctx, float temperature)
        {
            float rFactor = 1f + temperature * 0.01f;
            float bFactor = 1f - temperature * 0.01f;
            ctx.Brightness(rFactor > 1f ? rFactor : 1f);
        }

        private static void ApplyPartialGrayscale(IImageProcessingContext ctx, float amount)
        {
            ctx.Saturate(1f - amount);
        }
    }

    public class ColorAdjustmentEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "ColorAdjustment";
        public EffectTarget Target => EffectTarget.ColorAdjustment;
        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "Brightness",
            "Contrast",
            "Saturation",
            "Hue",
            "Gamma",
            "Vibrance",
            "Temperature",
            "Invert",
            "Grayscale",
            "Opacity"
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"Brightness", "float"},
            {"Contrast", "float"},
            {"Saturation", "float"},
            {"Hue", "float"},
            {"Gamma", "float"},
            {"Vibrance", "float"},
            {"Temperature", "float"},
            {"Invert", "bool"},
            {"Grayscale", "float"},
            {"Opacity", "float"}
        };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.ImageSharp };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }
            if (implementType != EffectImplementType.ImageSharp)
            {
                throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.");
            }
            return ColorAdjustmentEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType);
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            parameters ??= new Dictionary<string, object>
            {
                { "Brightness", 1f },
                { "Contrast", 1f },
                { "Saturation", 1f },
                { "Hue", 0f },
                { "Gamma", 1f },
                { "Vibrance", 0f },
                { "Temperature", 0f },
                { "Invert", false },
                { "Grayscale", 0f },
                { "Opacity", 1f }
            };
            if (!parameters.ContainsKey("Brightness")) parameters["Brightness"] = 1f;
            if (!parameters.ContainsKey("Contrast")) parameters["Contrast"] = 1f;
            if (!parameters.ContainsKey("Saturation")) parameters["Saturation"] = 1f;
            if (!parameters.ContainsKey("Hue")) parameters["Hue"] = 0f;
            if (!parameters.ContainsKey("Gamma")) parameters["Gamma"] = 1f;
            if (!parameters.ContainsKey("Vibrance")) parameters["Vibrance"] = 0f;
            if (!parameters.ContainsKey("Temperature")) parameters["Temperature"] = 0f;
            if (!parameters.ContainsKey("Invert")) parameters["Invert"] = false;
            if (!parameters.ContainsKey("Grayscale")) parameters["Grayscale"] = 0f;
            if (!parameters.ContainsKey("Opacity")) parameters["Opacity"] = 1f;
            return ColorAdjustmentEffect_ImageSharp.FromParametersDictionary(parameters);
        }
    }
}
