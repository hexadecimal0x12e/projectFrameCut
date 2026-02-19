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
    public class ResizeEffect_ImageSharp : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }


        public int Height { get; init; }
        public int Width { get; init; }
        public bool PreserveAspectRatio { get; init; } = true;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"Height", Height },
            {"Width", Width },
            {"PreserveAspectRatio" , PreserveAspectRatio  },
        };


        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public string? NeedComputer =>null;
        public bool YieldProcessStep => true;
        public EffectImplementType ImplementType => EffectImplementType.ImageSharp;


        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Height",
            "Width",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"Height", "int" },
            {"Width", "int" },
            {"PreserveAspectRatio", "bool" },
        };

        public string TypeName => "Resize";

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            bool preserve = false;
            if (parameters.TryGetValue("PreserveAspectRatio", out var val))
            {
                preserve = Convert.ToBoolean(val);
            }

            return new ResizeEffect_ImageSharp
            {
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
                PreserveAspectRatio = preserve,
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            return GetStep(source, targetWidth, targetHeight).Process(source);
        }


        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            int width = Width;
            int height = Height;

            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                width = Math.Max(1, (int)Math.Round((double)Width * targetWidth / RelativeWidth, MidpointRounding.AwayFromZero));
                height = Math.Max(1, (int)Math.Round((double)Height * targetHeight / RelativeHeight, MidpointRounding.AwayFromZero));
            }
            else
            {
                width = Math.Max(1, width);
                height = Math.Max(1, height);
            }

            return new ResizeProcessStep(width, height, PreserveAspectRatio)
            {
                _origHeight = source.Height,
                _origWidth = source.Width
            };
        }

        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; }
    }

    public class ResizeProcessStep : IPictureProcessStep
    {
        private TimeSpan? _elapsed;
        public string Name => "Resize";
        public Dictionary<string, object?> Properties { get; set; } = new();

        public int Width { get; init; }
        public int Height { get; init; }
        public bool PreserveAspectRatio { get; init; } = true;

        public int _origWidth { get; set; }
        public int _origHeight { get; set; }

        public ResizeProcessStep(int width, int height, bool preserveAspectRatio)
        {
            Width = width;
            Height = height;
            PreserveAspectRatio = preserveAspectRatio;
            Properties = new Dictionary<string, object?>
            {
                { nameof(Width), Width },
                { nameof(Height), Height },
                { nameof(PreserveAspectRatio), PreserveAspectRatio }
            };
        }

        public IPicture Process(IPicture source)
        {
            var sw = Stopwatch.StartNew();
            var img = source.SaveToSixLaborsImage();
            img.Mutate(i => i.Resize(new ResizeOptions
            {
                Size = new Size(Width, Height),
                Mode = PreserveAspectRatio ? ResizeMode.Max : ResizeMode.Stretch
            }));
            IPicture resized = (int)source.bitPerPixel switch
            {
                8 => new Picture8bpp(img),
                16 => new Picture16bpp(img),
                _ => throw new NotSupportedException($"Specific pixel-mode is not supported.")
            };
            sw.Stop();
            _elapsed = sw.Elapsed;

            resized.ProcessStack = source.ProcessStack.Append(GetProcessStack()).ToList();
            return resized;
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            return imgCtx => imgCtx.Resize(new ResizeOptions
            {
                Size = new Size(Width, Height),
                Mode = PreserveAspectRatio ? ResizeMode.Max : ResizeMode.Stretch
            });
        }

        public PictureProcessStack GetProcessStack() => new PictureProcessStack
        {
            Elapsed = _elapsed,
            OperationDisplayName = "Resize",
            Operator = typeof(ResizeProcessStep),
            ProcessingFuncStackTrace = new StackTrace(true),
            StepUsed = this,
            Properties = new Dictionary<string, object>
            {
                { nameof(Width), Width },
                { nameof(Height), Height },
                { nameof(PreserveAspectRatio), PreserveAspectRatio }
            }
        };
    }

    public class ResizeEffect_HwAccel : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string Id { get; set; }


        public int Height { get; init; }
        public int Width { get; init; }
        public bool PreserveAspectRatio { get; init; } = true;
        public string? BindedEffectGroupID { get; set; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"Height", Height },
            {"Width", Width },
            {"PreserveAspectRatio" , PreserveAspectRatio  },
        };


        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public string? NeedComputer => "ResizeComputer";
        public bool YieldProcessStep => false;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;


        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Height",
            "Width",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"Height", "int" },
            {"Width", "int" },
            {"PreserveAspectRatio", "bool" },
        };

        public string TypeName => "Resize";

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            bool preserve = false;
            if (parameters.TryGetValue("PreserveAspectRatio", out var val))
            {
                preserve = Convert.ToBoolean(val);
            }

            return new ResizeEffect_HwAccel
            {
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
                PreserveAspectRatio = preserve,
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            var sw = Stopwatch.StartNew();
            int width = Width;
            int height = Height;

            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                width = Math.Max(1, (int)Math.Round((double)Width * targetWidth / RelativeWidth, MidpointRounding.AwayFromZero));
                height = Math.Max(1, (int)Math.Round((double)Height * targetHeight / RelativeHeight, MidpointRounding.AwayFromZero));
            }
            else
            {
                width = Math.Max(1, width);
                height = Math.Max(1, height);
            }

            int destWidth = width;
            int destHeight = height;

            if (PreserveAspectRatio)
            {
                double sourceRatio = (double)source.Width / source.Height;
                double targetRatio = (double)width / height;

                if (sourceRatio > targetRatio)
                {
                    destHeight = (int)Math.Round(width / sourceRatio, MidpointRounding.AwayFromZero);
                }
                else
                {
                    destWidth = (int)Math.Round(height * sourceRatio, MidpointRounding.AwayFromZero);
                }
                destWidth = Math.Max(1, destWidth);
                destHeight = Math.Max(1, destHeight);
            }

            float[] r, g, b, a;
            if (source is IPicture<ushort> p16)
            {
                r = p16.r.Select(Convert.ToSingle).ToArray();
                g = p16.g.Select(Convert.ToSingle).ToArray();
                b = p16.b.Select(Convert.ToSingle).ToArray();
                a = p16.a ?? Enumerable.Repeat(1f, p16.Pixels).ToArray();
            }
            else if (source is IPicture<byte> p8)
            {
                r = p8.r.Select(Convert.ToSingle).ToArray();
                g = p8.g.Select(Convert.ToSingle).ToArray();
                b = p8.b.Select(Convert.ToSingle).ToArray();
                a = p8.a ?? Enumerable.Repeat(1f, p8.Pixels).ToArray();
            }
            else
            {
                throw new InvalidOperationException($"Source pixel type is not supported.");

            }

            var resultArr = computer.Compute(new object[] {
                r, g, b, a,
                        (float)source.Width, (float)source.Height,
                        (float)destWidth, (float)destHeight
            });


            if (resultArr.Length == 4 &&
                resultArr[0] is float[] r_out &&
                resultArr[1] is float[] g_out &&
                resultArr[2] is float[] b_out &&
                resultArr[3] is float[] a_out)
            {
                IPicture result;
                if (source.bitPerPixel == 16)
                {
                    var p = new Picture16bpp(destWidth, destHeight);
                    p.r = r_out.Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray();
                    p.g = g_out.Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray();
                    p.b = b_out.Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray();
                    p.a = a_out;
                    p.hasAlphaChannel = true;
                    result = p;
                }
                else
                {
                    var p = new Picture8bpp(destWidth, destHeight);
                    p.r = r_out.Select(v => (byte)Math.Clamp(v, 0, 255)).ToArray();
                    p.g = g_out.Select(v => (byte)Math.Clamp(v, 0, 255)).ToArray();
                    p.b = b_out.Select(v => (byte)Math.Clamp(v, 0, 255)).ToArray();
                    p.a = a_out;
                    p.hasAlphaChannel = true;
                    result = p;
                }
                sw.Stop();
                result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
                {
                    Elapsed = sw.Elapsed,
                    OperationDisplayName = $"Resize (GPU)",
                    Operator = typeof(ResizeEffect_HwAccel),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Properties = new Dictionary<string, object>
                            {
                                { "Width", destWidth },
                                { "Height", destHeight },
                                { "PreserveAspectRatio", PreserveAspectRatio }
                            }
                }).ToList();
                return result;
            }
            throw new InvalidOperationException($"Accelerator doesn't return expected result.");
        }

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }
    }

    public class ResizeEffect_IPicture : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string Id { get; set; }


        public int Height { get; init; }
        public int Width { get; init; }
        public bool PreserveAspectRatio { get; init; } = true;
        public string? BindedEffectGroupID { get; set; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"Height", Height },
            {"Width", Width },
            {"PreserveAspectRatio" , PreserveAspectRatio  },
        };


        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public string? NeedComputer => null;
        public bool YieldProcessStep => true;
        public EffectImplementType ImplementType => EffectImplementType.ImageSharp;


        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Height",
            "Width",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"Height", "int" },
            {"Width", "int" },
            {"PreserveAspectRatio", "bool" },
        };

        public string TypeName => "Resize";


        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            bool preserve = false;
            if (parameters.TryGetValue("PreserveAspectRatio", out var val))
            {
                preserve = Convert.ToBoolean(val);
            }

            return new ResizeEffect_ImageSharp
            {
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
                PreserveAspectRatio = preserve,
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            int width = Width;
            int height = Height;

            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                width = Math.Max(1, (int)Math.Round((double)Width * targetWidth / RelativeWidth, MidpointRounding.AwayFromZero));
                height = Math.Max(1, (int)Math.Round((double)Height * targetHeight / RelativeHeight, MidpointRounding.AwayFromZero));
            }
            else
            {
                width = Math.Max(1, width);
                height = Math.Max(1, height);
            }

            return source.Resize(width, height, PreserveAspectRatio);
        }

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }
    }

    public class ResizeEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "Resize";

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "Height",
            "Width",
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"Height", "int" },
            {"Width", "int" },
            {"PreserveAspectRatio", "bool" },
        };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.ImageSharp, EffectImplementType.HwAcceleration, EffectImplementType.IPicture };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            return implementType switch
            {
                EffectImplementType.ImageSharp => ResizeEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>()),
                EffectImplementType.HwAcceleration => ResizeEffect_HwAccel.FromParametersDictionary(parameters ?? new Dictionary<string, object>()),
                EffectImplementType.IPicture => ResizeEffect_IPicture.FromParametersDictionary(parameters ?? new Dictionary<string, object>()),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            return ResizeEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>());
        }
    }
}
