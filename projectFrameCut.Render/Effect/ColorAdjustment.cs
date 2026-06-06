using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace projectFrameCut.Render.Effect
{
    public class ColorAdjustmentEffect_IPicture : IColorAdjustEffect
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
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.IPicture;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Brightness", "Contrast", "Saturation", "Hue", "Gamma",
            "Vibrance", "Temperature", "Invert", "Grayscale", "Opacity"
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"Brightness", "float"}, {"Contrast", "float"}, {"Saturation", "float"},
            {"Hue", "float"}, {"Gamma", "float"}, {"Vibrance", "float"},
            {"Temperature", "float"}, {"Invert", "bool"}, {"Grayscale", "float"}, {"Opacity", "float"}
        };

        public string TypeName => "ColorAdjustment";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.IPicture)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            return new ColorAdjustmentEffect_IPicture
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
            var sw = Stopwatch.StartNew();
            IPicture result = source switch
            {
                IPicture<byte> p8 => ProcessInternal(p8),
                IPicture<ushort> p16 => ProcessInternal(p16),
                _ => throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}"),
            };
            sw.Stop();
            return result;
        }

        private IPicture<byte> ProcessInternal(IPicture<byte> source)
        {
            IPicture<byte> current = source;

            if (Brightness != 1f)
                current = BrightnessEffect.Process(current, Brightness - 1f);
            if (Contrast != 1f)
                current = ContrastEffect.Process(current, Contrast);
            if (Saturation != 1f)
                current = SaturationEffect.Process(current, Saturation);
            if (Hue != 0f)
                current = HueRotationEffect.Process(current, Hue);
            if (Gamma != 1f)
                current = BlurEffect.Process(current, (Gamma - 1f) * 0.5f);
            if (Vibrance != 0f)
                current = SaturationEffect.Process(current, 1f + Vibrance * 0.5f);
            if (Temperature != 0f)
                current = ProcessTemperature(current, Temperature);
            if (Invert)
                current = InvertEffect.Process(current);
            if (Grayscale > 0f)
            {
                if (Grayscale >= 1f)
                    current = GrayscaleEffect.Process(current);
                else
                    current = SaturationEffect.Process(current, 1f - Grayscale);
            }
            if (Opacity < 1f)
                current = OpacityEffect.Process(current, Opacity);

            return current;
        }

        private IPicture<ushort> ProcessInternal(IPicture<ushort> source)
        {
            IPicture<ushort> current = source;

            if (Brightness != 1f)
                current = BrightnessEffect.Process(current, Brightness - 1f);
            if (Contrast != 1f)
                current = ContrastEffect.Process(current, Contrast);
            if (Saturation != 1f)
                current = SaturationEffect.Process(current, Saturation);
            if (Hue != 0f)
                current = HueRotationEffect.Process(current, Hue);
            if (Gamma != 1f)
                current = BlurEffect.Process(current, (Gamma - 1f) * 0.5f);
            if (Vibrance != 0f)
                current = SaturationEffect.Process(current, 1f + Vibrance * 0.5f);
            if (Temperature != 0f)
                current = ProcessTemperature(current, Temperature);
            if (Invert)
                current = InvertEffect.Process(current);
            if (Grayscale > 0f)
            {
                if (Grayscale >= 1f)
                    current = GrayscaleEffect.Process(current);
                else
                    current = SaturationEffect.Process(current, 1f - Grayscale);
            }
            if (Opacity < 1f)
                current = OpacityEffect.Process(current, Opacity);

            return current;
        }

        private static IPicture<byte> ProcessTemperature(IPicture<byte> picture, float temperature)
        {
            int pixels = picture.Pixels;
            var result = new Picture8bpp(picture.Width, picture.Height)
            {
                r = GC.AllocateUninitializedArray<byte>(pixels),
                g = GC.AllocateUninitializedArray<byte>(pixels),
                b = GC.AllocateUninitializedArray<byte>(pixels),
                a = picture.HasAlphaChannel && picture.a != null
                    ? GC.AllocateUninitializedArray<float>(pixels) : null,
                HasAlphaChannel = picture.HasAlphaChannel,
                Tag = picture.Tag,
                ProcessStack = new List<PictureProcessStack>(picture.ProcessStack),
            };

            float rFactor = 1f + temperature * 0.01f;
            float bFactor = 1f - temperature * 0.01f;

            for (int i = 0; i < pixels; i++)
            {
                result.r[i] = (byte)Math.Clamp((int)(picture.r[i] * rFactor + 0.5f), 0, 255);
                result.g[i] = picture.g[i];
                result.b[i] = (byte)Math.Clamp((int)(picture.b[i] * bFactor + 0.5f), 0, 255);
            }

            if (result.a != null && picture.a != null)
                Array.Copy(picture.a, result.a, pixels);

            result.ProcessStack.Add(new PictureProcessStack
            {
                OperationDisplayName = "Temperature",
                Operator = typeof(ColorAdjustmentEffect_IPicture),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object> { { "Temperature", temperature } },
                Elapsed = null,
            });

            return result;
        }

        private static IPicture<ushort> ProcessTemperature(IPicture<ushort> picture, float temperature)
        {
            int pixels = picture.Pixels;
            var result = new Picture16bpp(picture.Width, picture.Height)
            {
                r = GC.AllocateUninitializedArray<ushort>(pixels),
                g = GC.AllocateUninitializedArray<ushort>(pixels),
                b = GC.AllocateUninitializedArray<ushort>(pixels),
                a = picture.HasAlphaChannel && picture.a != null
                    ? GC.AllocateUninitializedArray<float>(pixels) : null,
                HasAlphaChannel = picture.HasAlphaChannel,
                Tag = picture.Tag,
                ProcessStack = new List<PictureProcessStack>(picture.ProcessStack),
            };

            float rFactor = 1f + temperature * 0.01f;
            float bFactor = 1f - temperature * 0.01f;
            const int max16 = 65535;

            for (int i = 0; i < pixels; i++)
            {
                result.r[i] = (ushort)Math.Clamp((int)(picture.r[i] * rFactor + 0.5f), 0, max16);
                result.g[i] = picture.g[i];
                result.b[i] = (ushort)Math.Clamp((int)(picture.b[i] * bFactor + 0.5f), 0, max16);
            }

            if (result.a != null && picture.a != null)
                Array.Copy(picture.a, result.a, pixels);

            result.ProcessStack.Add(new PictureProcessStack
            {
                OperationDisplayName = "Temperature",
                Operator = typeof(ColorAdjustmentEffect_IPicture),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object> { { "Temperature", temperature } },
                Elapsed = null,
            });

            return result;
        }
    }

    public class ColorAdjustmentEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "ColorAdjustment";
        public EffectTarget Target => EffectTarget.ColorAdjustment;
        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "Brightness", "Contrast", "Saturation", "Hue", "Gamma",
            "Vibrance", "Temperature", "Invert", "Grayscale", "Opacity"
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"Brightness", "float"}, {"Contrast", "float"}, {"Saturation", "float"},
            {"Hue", "float"}, {"Gamma", "float"}, {"Vibrance", "float"},
            {"Temperature", "float"}, {"Invert", "bool"}, {"Grayscale", "float"}, {"Opacity", "float"}
        };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.IPicture };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }
            if (implementType != EffectImplementType.IPicture)
            {
                throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.");
            }
            return ColorAdjustmentEffect_IPicture.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType);
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            parameters ??= new Dictionary<string, object>
            {
                { "Brightness", 1f }, { "Contrast", 1f }, { "Saturation", 1f },
                { "Hue", 0f }, { "Gamma", 1f }, { "Vibrance", 0f },
                { "Temperature", 0f }, { "Invert", false }, { "Grayscale", 0f }, { "Opacity", 1f }
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
            return ColorAdjustmentEffect_IPicture.FromParametersDictionary(parameters);
        }
    }
}
