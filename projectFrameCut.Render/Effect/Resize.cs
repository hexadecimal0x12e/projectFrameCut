using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public class ResizeEffect_HwAccel : INormalEffect
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
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;


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

        void IEffect.Initialize()
        {
            Log("Place and Resize effects are deprecated. Consider migrate to IClipPositionProvider.", "warn");
        }


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

            IPicture result;
            if (computer is IResizeComputer resizeComp)
            {
                if (source.BitPerPixel == 16)
                {
                    var r16 = resizeComp.ComputeResizeUshort(r, g, b, a,
                        source.Width, source.Height, destWidth, destHeight);
                    var p = new Picture16bpp(destWidth, destHeight);
                    p.r = r16.R; p.g = r16.G; p.b = r16.B; p.a = r16.A;
                    p.HasAlphaChannel = true;
                    result = p;
                }
                else
                {
                    var r8 = resizeComp.ComputeResizeByte(r, g, b, a,
                        source.Width, source.Height, destWidth, destHeight);
                    var p = new Picture8bpp(destWidth, destHeight);
                    p.r = r8.R; p.g = r8.G; p.b = r8.B; p.a = r8.A;
                    p.HasAlphaChannel = true;
                    result = p;
                }
            }
            else
            {
                var resultArr = computer.Compute(new object[] {
                    r, g, b, a,
                    (float)source.Width, (float)source.Height,
                    (float)destWidth, (float)destHeight
                });

                if (resultArr.Length != 4 ||
                    resultArr[0] is not float[] r_out ||
                    resultArr[1] is not float[] g_out ||
                    resultArr[2] is not float[] b_out ||
                    resultArr[3] is not float[] a_out)
                {
                    throw new InvalidOperationException($"Accelerator doesn't return expected result.");
                }

                if (source.BitPerPixel == 16)
                {
                    var p = new Picture16bpp(destWidth, destHeight);
                    p.r = r_out.Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray();
                    p.g = g_out.Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray();
                    p.b = b_out.Select(v => (ushort)Math.Clamp(v, 0, 65535)).ToArray();
                    p.a = a_out;
                    p.HasAlphaChannel = true;
                    result = p;
                }
                else
                {
                    var p = new Picture8bpp(destWidth, destHeight);
                    p.r = r_out.Select(v => (byte)Math.Clamp(v, 0, 255)).ToArray();
                    p.g = g_out.Select(v => (byte)Math.Clamp(v, 0, 255)).ToArray();
                    p.b = b_out.Select(v => (byte)Math.Clamp(v, 0, 255)).ToArray();
                    p.a = a_out;
                    p.HasAlphaChannel = true;
                    result = p;
                }
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

    }

    public class ResizeEffect_IPicture : INormalEffect
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
        public EffectImplementType ImplementType => EffectImplementType.IPicture;
        public bool IsReorderable => true;


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


        void IEffect.Initialize()
        {
            Log("Place and Resize effects are deprecated. Consider migrate to IClipPositionProvider.", "warn");
        }


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

            return new ResizeEffect_IPicture
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

    }

    public class ResizeEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "Resize";

        public EffectTarget Target => EffectTarget.Video;

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

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.IPicture, EffectImplementType.HwAcceleration, EffectImplementType.IPicture };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            Log("Place and Resize effects are deprecated. Consider migrate to IClipPositionProvider.", "warn");

            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            return implementType switch
            {
                EffectImplementType.IPicture => ResizeEffect_IPicture.FromParametersDictionary(parameters ?? new Dictionary<string, object>()),
                EffectImplementType.HwAcceleration => ResizeEffect_HwAccel.FromParametersDictionary(parameters ?? new Dictionary<string, object>()),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            Log("Place and Resize effects are deprecated. Consider migrate to IClipPositionProvider.", "warn");

            return ResizeEffect_IPicture.FromParametersDictionary(parameters ?? new Dictionary<string, object>());
        }
    }
}
