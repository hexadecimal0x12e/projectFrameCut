using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace projectFrameCut.Render.Effect
{
    public class ColorAdjustmentEffect_IPicture : IColorAdjustEffect, IDynamicArgumentsEffect
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

        public IReadOnlyDictionary<string, Func<object?>>? DynamicProviders { get; set; }

        /// <summary>
        /// A snapshot of the (possibly dynamically resolved) adjustment values for one process call.
        /// </summary>
        private readonly record struct AdjustmentParams(
            float Brightness, float Contrast, float Saturation, float Hue, float Gamma,
            float Vibrance, float Temperature, bool Invert, float Grayscale, float Opacity);

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
        bool IEffect.IsReorderable => false;

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
            var p = new AdjustmentParams(
                DynamicParam.Resolve(DynamicProviders, "Brightness", Brightness),
                DynamicParam.Resolve(DynamicProviders, "Contrast", Contrast),
                DynamicParam.Resolve(DynamicProviders, "Saturation", Saturation),
                DynamicParam.Resolve(DynamicProviders, "Hue", Hue),
                DynamicParam.Resolve(DynamicProviders, "Gamma", Gamma),
                DynamicParam.Resolve(DynamicProviders, "Vibrance", Vibrance),
                DynamicParam.Resolve(DynamicProviders, "Temperature", Temperature),
                DynamicParam.Resolve(DynamicProviders, "Invert", Invert),
                DynamicParam.Resolve(DynamicProviders, "Grayscale", Grayscale),
                DynamicParam.Resolve(DynamicProviders, "Opacity", Opacity));
            var sw = Stopwatch.StartNew();
            IPicture result = source switch
            {
                IPicture<byte> p8 => ProcessInternal(p8, p),
                IPicture<ushort> p16 => ProcessInternal(p16, p),
                _ => throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}"),
            };
            sw.Stop();
            return result;
        }

        private IPicture<byte> ProcessInternal(IPicture<byte> source, in AdjustmentParams p)
        {
            // Fast path: merge simple per-pixel adjustments into a single pass
            // Skip when complex ops (Hue, Gamma, Vibrance) or structural ops (Saturation via HSL) are needed
            bool canMerge = p.Hue == 0f && p.Gamma == 1f && p.Vibrance == 0f && p.Saturation == 1f;
            if (canMerge && HasAnySimpleAdjustment(in p))
            {
                return ProcessMerged8bpp(source, in p);
            }

            IPicture<byte> current = source;

            if (p.Brightness != 1f)
                current = BrightnessEffect.Process(current, p.Brightness - 1f);
            if (p.Contrast != 1f)
                current = ContrastEffect.Process(current, p.Contrast);
            if (p.Saturation != 1f)
                current = SaturationEffect.Process(current, p.Saturation);
            if (p.Hue != 0f)
                current = HueRotationEffect.Process(current, p.Hue);
            if (p.Gamma != 1f)
                current = BlurEffect.Process(current, (p.Gamma - 1f) * 0.5f);
            if (p.Vibrance != 0f)
                current = SaturationEffect.Process(current, 1f + p.Vibrance * 0.5f);
            if (p.Temperature != 0f)
                current = ProcessTemperature(current, p.Temperature);
            if (p.Invert)
                current = InvertEffect.Process(current);
            if (p.Grayscale > 0f)
            {
                if (p.Grayscale >= 1f)
                    current = GrayscaleEffect.Process(current);
                else
                    current = SaturationEffect.Process(current, 1f - p.Grayscale);
            }
            if (p.Opacity < 1f)
                current = OpacityEffect.Process(current, p.Opacity);

            return current;
        }

        private IPicture<ushort> ProcessInternal(IPicture<ushort> source, in AdjustmentParams p)
        {
            bool canMerge = p.Hue == 0f && p.Gamma == 1f && p.Vibrance == 0f && p.Saturation == 1f;
            if (canMerge && HasAnySimpleAdjustment(in p))
            {
                return ProcessMerged16bpp(source, in p);
            }

            IPicture<ushort> current = source;

            if (p.Brightness != 1f)
                current = BrightnessEffect.Process(current, p.Brightness - 1f);
            if (p.Contrast != 1f)
                current = ContrastEffect.Process(current, p.Contrast);
            if (p.Saturation != 1f)
                current = SaturationEffect.Process(current, p.Saturation);
            if (p.Hue != 0f)
                current = HueRotationEffect.Process(current, p.Hue);
            if (p.Gamma != 1f)
                current = BlurEffect.Process(current, (p.Gamma - 1f) * 0.5f);
            if (p.Vibrance != 0f)
                current = SaturationEffect.Process(current, 1f + p.Vibrance * 0.5f);
            if (p.Temperature != 0f)
                current = ProcessTemperature(current, p.Temperature);
            if (p.Invert)
                current = InvertEffect.Process(current);
            if (p.Grayscale > 0f)
            {
                if (p.Grayscale >= 1f)
                    current = GrayscaleEffect.Process(current);
                else
                    current = SaturationEffect.Process(current, 1f - p.Grayscale);
            }
            if (p.Opacity < 1f)
                current = OpacityEffect.Process(current, p.Opacity);

            return current;
        }

        private bool HasAnySimpleAdjustment(in AdjustmentParams p)
        {
            return p.Brightness != 1f || p.Contrast != 1f || p.Temperature != 0f
                || p.Invert || p.Grayscale > 0f || p.Opacity < 1f;
        }

        private IPicture<byte> ProcessMerged8bpp(IPicture<byte> source, in AdjustmentParams p)
        {
            int pixels = source.Pixels;
            var sw = Stopwatch.StartNew();
            var result = new Picture8bpp(source.Width, source.Height)
            {
                r = GC.AllocateUninitializedArray<byte>(pixels),
                g = GC.AllocateUninitializedArray<byte>(pixels),
                b = GC.AllocateUninitializedArray<byte>(pixels),
                a = source.HasAlphaChannel && source.a != null
                    ? GC.AllocateUninitializedArray<float>(pixels) : null,
                HasAlphaChannel = source.HasAlphaChannel,
                Tag = source.Tag,
                ProcessStack = new List<PictureProcessStack>(source.ProcessStack),
            };

            bool hasAlpha = source.HasAlphaChannel && source.a != null;
            bool doBrightness = p.Brightness != 1f;
            bool doContrast = p.Contrast != 1f;
            bool doTemperature = p.Temperature != 0f;
            bool doInvert = p.Invert;
            bool doGrayscale = p.Grayscale > 0f;
            bool doOpacity = p.Opacity < 1f;

            float brightFactor = doBrightness ? p.Brightness : 1f;
            float contrastFactor = doContrast ? p.Contrast : 1f;
            float tempRFactor = doTemperature ? 1f + p.Temperature * 0.01f : 1f;
            float tempBFactor = doTemperature ? 1f - p.Temperature * 0.01f : 1f;
            float grayAmount = p.Grayscale;
            float opacity = p.Opacity;
            const float contrastMid = 128f;
            const float maxVal = 255f;

            for (int i = 0; i < pixels; i++)
            {
                float r = source.r[i];
                float g = source.g[i];
                float b = source.b[i];

                if (doBrightness)
                {
                    float f = brightFactor > 1f ? brightFactor - 1f : brightFactor;
                    if (brightFactor > 1f)
                    {
                        r += (maxVal - r) * f;
                        g += (maxVal - g) * f;
                        b += (maxVal - b) * f;
                    }
                    else
                    {
                        r *= f;
                        g *= f;
                        b *= f;
                    }
                }

                if (doContrast)
                {
                    r = (r - contrastMid) * contrastFactor + contrastMid;
                    g = (g - contrastMid) * contrastFactor + contrastMid;
                    b = (b - contrastMid) * contrastFactor + contrastMid;
                }

                if (doTemperature)
                {
                    r *= tempRFactor;
                    b *= tempBFactor;
                }

                if (doInvert)
                {
                    r = maxVal - r;
                    g = maxVal - g;
                    b = maxVal - b;
                }

                if (doGrayscale)
                {
                    float luma = 0.299f * r + 0.587f * g + 0.114f * b;
                    if (grayAmount >= 1f)
                    {
                        r = g = b = luma;
                    }
                    else
                    {
                        r += (luma - r) * grayAmount;
                        g += (luma - g) * grayAmount;
                        b += (luma - b) * grayAmount;
                    }
                }

                result.r[i] = (byte)Math.Clamp((int)(r + 0.5f), 0, 255);
                result.g[i] = (byte)Math.Clamp((int)(g + 0.5f), 0, 255);
                result.b[i] = (byte)Math.Clamp((int)(b + 0.5f), 0, 255);
            }

            if (hasAlpha)
            {
                if (doOpacity)
                {
                    for (int i = 0; i < pixels; i++)
                        result.a![i] = Math.Clamp(source.a![i] * opacity, 0f, 1f);
                }
                else
                {
                    Array.Copy(source.a!, result.a!, pixels);
                }
            }
            else if (doOpacity)
            {
                for (int i = 0; i < pixels; i++)
                    result.a![i] = opacity;
            }

            sw.Stop();
            result.ProcessStack.Add(new PictureProcessStack
            {
                OperationDisplayName = "ColorAdjustment (Merged 8bpp)",
                Operator = typeof(ColorAdjustmentEffect_IPicture),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object>
                {
                    { "Brightness", p.Brightness }, { "Contrast", p.Contrast }, { "Saturation", p.Saturation },
                    { "Hue", p.Hue }, { "Gamma", p.Gamma }, { "Vibrance", p.Vibrance },
                    { "Temperature", p.Temperature }, { "Invert", p.Invert }, { "Grayscale", p.Grayscale }, { "Opacity", p.Opacity }
                },
                Elapsed = sw.Elapsed,
            });
            return result;
        }

        private IPicture<ushort> ProcessMerged16bpp(IPicture<ushort> source, in AdjustmentParams p)
        {
            int pixels = source.Pixels;
            var sw = Stopwatch.StartNew();
            var result = new Picture16bpp(source.Width, source.Height)
            {
                r = GC.AllocateUninitializedArray<ushort>(pixels),
                g = GC.AllocateUninitializedArray<ushort>(pixels),
                b = GC.AllocateUninitializedArray<ushort>(pixels),
                a = source.HasAlphaChannel && source.a != null
                    ? GC.AllocateUninitializedArray<float>(pixels) : null,
                HasAlphaChannel = source.HasAlphaChannel,
                Tag = source.Tag,
                ProcessStack = new List<PictureProcessStack>(source.ProcessStack),
            };

            bool hasAlpha = source.HasAlphaChannel && source.a != null;
            bool doBrightness = p.Brightness != 1f;
            bool doContrast = p.Contrast != 1f;
            bool doTemperature = p.Temperature != 0f;
            bool doInvert = p.Invert;
            bool doGrayscale = p.Grayscale > 0f;
            bool doOpacity = p.Opacity < 1f;

            float brightFactor = doBrightness ? p.Brightness : 1f;
            float contrastFactor = doContrast ? p.Contrast : 1f;
            float tempRFactor = doTemperature ? 1f + p.Temperature * 0.01f : 1f;
            float tempBFactor = doTemperature ? 1f - p.Temperature * 0.01f : 1f;
            float grayAmount = p.Grayscale;
            float opacity = p.Opacity;
            const float contrastMid = 32768f;
            const float maxVal = 65535f;

            for (int i = 0; i < pixels; i++)
            {
                float r = source.r[i];
                float g = source.g[i];
                float b = source.b[i];

                if (doBrightness)
                {
                    float f = brightFactor > 1f ? brightFactor - 1f : brightFactor;
                    if (brightFactor > 1f)
                    {
                        r += (maxVal - r) * f;
                        g += (maxVal - g) * f;
                        b += (maxVal - b) * f;
                    }
                    else
                    {
                        r *= f;
                        g *= f;
                        b *= f;
                    }
                }

                if (doContrast)
                {
                    r = (r - contrastMid) * contrastFactor + contrastMid;
                    g = (g - contrastMid) * contrastFactor + contrastMid;
                    b = (b - contrastMid) * contrastFactor + contrastMid;
                }

                if (doTemperature)
                {
                    r *= tempRFactor;
                    b *= tempBFactor;
                }

                if (doInvert)
                {
                    r = maxVal - r;
                    g = maxVal - g;
                    b = maxVal - b;
                }

                if (doGrayscale)
                {
                    float luma = 0.299f * r + 0.587f * g + 0.114f * b;
                    if (grayAmount >= 1f)
                    {
                        r = g = b = luma;
                    }
                    else
                    {
                        r += (luma - r) * grayAmount;
                        g += (luma - g) * grayAmount;
                        b += (luma - b) * grayAmount;
                    }
                }

                result.r[i] = (ushort)Math.Clamp((int)(r + 0.5f), 0, 65535);
                result.g[i] = (ushort)Math.Clamp((int)(g + 0.5f), 0, 65535);
                result.b[i] = (ushort)Math.Clamp((int)(b + 0.5f), 0, 65535);
            }

            if (hasAlpha)
            {
                if (doOpacity)
                {
                    for (int i = 0; i < pixels; i++)
                        result.a![i] = Math.Clamp(source.a![i] * opacity, 0f, 1f);
                }
                else
                {
                    Array.Copy(source.a!, result.a!, pixels);
                }
            }
            else if (doOpacity)
            {
                for (int i = 0; i < pixels; i++)
                    result.a![i] = opacity;
            }

            sw.Stop();
            result.ProcessStack.Add(new PictureProcessStack
            {
                OperationDisplayName = "ColorAdjustment (Merged 16bpp)",
                Operator = typeof(ColorAdjustmentEffect_IPicture),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object>
                {
                    { "Brightness", p.Brightness }, { "Contrast", p.Contrast }, { "Saturation", p.Saturation },
                    { "Hue", p.Hue }, { "Gamma", p.Gamma }, { "Vibrance", p.Vibrance },
                    { "Temperature", p.Temperature }, { "Invert", p.Invert }, { "Grayscale", p.Grayscale }, { "Opacity", p.Opacity }
                },
                Elapsed = sw.Elapsed,
            });
            return result;
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

    public class ColorAdjustmentEffect_HwAccel : IColorAdjustEffect, IDynamicArgumentsEffect
    {
        public string Name { get; set; } = "ColorAdjustment";

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
        public IReadOnlyDictionary<string, Func<object?>>? DynamicProviders { get; set; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "Brightness", Brightness }, { "Contrast", Contrast }, { "Saturation", Saturation },
            { "Hue", Hue }, { "Gamma", Gamma }, { "Vibrance", Vibrance },
            { "Temperature", Temperature }, { "Invert", Invert }, { "Grayscale", Grayscale }, { "Opacity", Opacity }
        };

        public string? NeedComputer => "ColorAdjustmentComputer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Brightness", "Contrast", "Saturation", "Hue", "Gamma",
            "Vibrance", "Temperature", "Invert", "Grayscale", "Opacity"
        };
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Brightness", "float" }, { "Contrast", "float" }, { "Saturation", "float" },
            { "Hue", "float" }, { "Gamma", "float" }, { "Vibrance", "float" },
            { "Temperature", "float" }, { "Invert", "bool" }, { "Grayscale", "float" }, { "Opacity", "float" }
        };

        public string TypeName => "ColorAdjustment";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;
        bool IEffect.IsReorderable => false;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            return new ColorAdjustmentEffect_HwAccel
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
                Opacity = Convert.ToSingle(parameters["Opacity"])
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Process(IPicture source, IComputer? computer)
        {
            float brightness = DynamicParam.Resolve(DynamicProviders, "Brightness", Brightness);
            float contrast = DynamicParam.Resolve(DynamicProviders, "Contrast", Contrast);
            float saturation = DynamicParam.Resolve(DynamicProviders, "Saturation", Saturation);
            float hue = DynamicParam.Resolve(DynamicProviders, "Hue", Hue);
            float gamma = DynamicParam.Resolve(DynamicProviders, "Gamma", Gamma);
            float vibrance = DynamicParam.Resolve(DynamicProviders, "Vibrance", Vibrance);
            float temperature = DynamicParam.Resolve(DynamicProviders, "Temperature", Temperature);
            bool invert = DynamicParam.Resolve(DynamicProviders, "Invert", Invert);
            float grayscale = DynamicParam.Resolve(DynamicProviders, "Grayscale", Grayscale);
            float opacity = DynamicParam.Resolve(DynamicProviders, "Opacity", Opacity);

            bool allNoop =
                Math.Abs(brightness - 1f) < float.Epsilon &&
                Math.Abs(contrast - 1f) < float.Epsilon &&
                Math.Abs(saturation - 1f) < float.Epsilon &&
                Math.Abs(hue) < float.Epsilon &&
                Math.Abs(gamma - 1f) < float.Epsilon &&
                Math.Abs(vibrance) < float.Epsilon &&
                Math.Abs(temperature) < float.Epsilon &&
                !invert &&
                Math.Abs(grayscale) < float.Epsilon &&
                Math.Abs(opacity - 1f) < float.Epsilon;

            if (allNoop)
                return source;

            if (computer is null)
                return new ColorAdjustmentEffect_IPicture
                {
                    Brightness = brightness,
                    Contrast = contrast,
                    Saturation = saturation,
                    Hue = hue,
                    Gamma = gamma,
                    Vibrance = vibrance,
                    Temperature = temperature,
                    Invert = invert,
                    Grayscale = grayscale,
                    Opacity = opacity
                }.Process(source, null);

            var sw = Stopwatch.StartNew();
            float maxVal = source.BitPerPixel == 8 ? 255f : 65535f;
            var (r, g, b, a, sourceHasAlpha) = HwAccelEffectHelper.ExtractFloatChannels(source);

            FourChannelResult computeResult;
            if (computer is IColorAdjustmentComputer cac)
            {
                computeResult = cac.ComputeColorAdjustment(
                    r, g, b, a, source.Width, source.Height,
                    brightness, contrast, saturation, hue, gamma,
                    vibrance, temperature, invert, grayscale, opacity, maxVal);
            }
            else
            {
                var resultArr = computer.Compute([
                    r, g, b, a,
                    brightness, contrast, saturation, hue, gamma,
                    vibrance, temperature, invert ? 1f : 0f, grayscale, opacity, maxVal
                ]);

                if (resultArr.Length != 4 ||
                    resultArr[0] is not float[] rOut ||
                    resultArr[1] is not float[] gOut ||
                    resultArr[2] is not float[] bOut ||
                    resultArr[3] is not float[] aOut)
                {
                    throw new InvalidOperationException("ColorAdjustmentComputer did not return expected channel buffers.");
                }

                computeResult = new FourChannelResult(rOut, gOut, bOut, aOut);
            }

            var result = HwAccelEffectHelper.BuildPicture(source, source.Width, source.Height,
                computeResult.R, computeResult.G, computeResult.B, computeResult.A, sourceHasAlpha);
            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                Elapsed = sw.Elapsed,
                OperationDisplayName = "ColorAdjustment (GPU)",
                Operator = typeof(ColorAdjustmentEffect_HwAccel),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object>
                {
                    { "Brightness", brightness }, { "Contrast", contrast }, { "Saturation", saturation },
                    { "Hue", hue }, { "Gamma", gamma }, { "Vibrance", vibrance },
                    { "Temperature", temperature }, { "Invert", invert }, { "Grayscale", grayscale }, { "Opacity", opacity }
                }
            }).ToList();
            return result;
        }
    }

    public class ColorAdjustmentEffectFactory
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

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.IPicture, EffectImplementType.HwAcceleration };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }
            return implementType switch
            {
                EffectImplementType.IPicture => ColorAdjustmentEffect_IPicture.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                EffectImplementType.HwAcceleration => ColorAdjustmentEffect_HwAccel.FromParametersDictionary(parameters ?? new Dictionary<string, object>()),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
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



    /// <summary>
    /// The Render-side provider of the ColorAdjustment effect.
    /// </summary>
    public class ColorAdjustmentEffectProvider : EffectProviderBase
    {
        public ColorAdjustmentEffectProvider()
        {
            Name = "ColorAdjustment";
            Parameters = new Dictionary<string, object>
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
        }

        public override string TypeName => "ColorAdjustment";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.ColorAdjustment | EffectTarget.IsNotVisibleInEffectEditor;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Brightness", EffectArgumentFieldType.Numeric, "1", min: "0", max: "2"),
                Field("Contrast", EffectArgumentFieldType.Numeric, "1", min: "0", max: "3"),
                Field("Saturation", EffectArgumentFieldType.Numeric, "1", min: "0", max: "3"),
                Field("Hue", EffectArgumentFieldType.Numeric, "0", min: "0", max: "360"),
                Field("Gamma", EffectArgumentFieldType.Numeric, "1", min: "0.5", max: "2"),
                Field("Vibrance", EffectArgumentFieldType.Numeric, "0", min: "-1", max: "1"),
                Field("Temperature", EffectArgumentFieldType.Numeric, "0", min: "-100", max: "100"),
                Field("Grayscale", EffectArgumentFieldType.Numeric, "0", min: "0", max: "1"),
                Field("Opacity", EffectArgumentFieldType.Numeric, "1", min: "0", max: "1"),
                Field("Invert", EffectArgumentFieldType.Boolean, "false")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture, EffectImplementType.HwAcceleration];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            return [new ColorAdjustmentEffectFactory().Build(implementType, parameters)];
        }
    }
}
