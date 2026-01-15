using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using projectFrameCut.Render;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect.HwAccel
{
    public class RemoveColorEffect_HwAccel : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }


        public ushort R { get; init; }
        public ushort G { get; init; }
        public ushort B { get; init; }
        public ushort A { get; init; }
        public ushort Tolerance { get; init; } = 0;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "R", R },
            { "G", G },
            { "B", B },
            { "A", A },
            { "Tolerance", Tolerance },
        };



        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public string NeedComputer => "RemoveColorComputer";
        public bool YieldProcessStep => false;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;


        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "R",
            "G",
            "B",
            "A",
            "Tolerance",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "R", "ushort" },
            { "G", "ushort" },
            { "B", "ushort" },
            { "A", "ushort" },
            { "Tolerance", "ushort" },
        };

        public string TypeName => "RemoveColor";

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            if (parameters.Count != ParametersNeeded.Count)
            {
                throw new ArgumentException("Too many parameters provided.");
            }


            return new RemoveColorEffect_HwAccel
            {
                R = Convert.ToUInt16(parameters["R"]),
                G = Convert.ToUInt16(parameters["G"]),
                B = Convert.ToUInt16(parameters["B"]),
                A = Convert.ToUInt16(parameters["A"]),
                Tolerance = Convert.ToUInt16(parameters["Tolerance"]),
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            var sw = Stopwatch.StartNew();
            ArgumentNullException.ThrowIfNull(computer, nameof(computer));
            float[] r, g, b, a;
            if (source is IPicture<ushort> p16)
            {
                r = new float[p16.Pixels];
                g = new float[p16.Pixels];
                b = new float[p16.Pixels];
                for (int i = 0; i < p16.Pixels; i++)
                {
                    r[i] = p16.r[i];
                    g[i] = p16.g[i];
                    b[i] = p16.b[i];
                }
                if (p16.a is null)
                {
                    a = new float[p16.Pixels];
                    Array.Fill(a, 1f);
                }
                else
                {
                    a = p16.a;
                }
            }
            else if (source is IPicture<byte> p8)
            {
                r = new float[p8.Pixels];
                g = new float[p8.Pixels];
                b = new float[p8.Pixels];
                for (int i = 0; i < p8.Pixels; i++)
                {
                    r[i] = p8.r[i];
                    g[i] = p8.g[i];
                    b[i] = p8.b[i];
                }
                if (p8.a is null)
                {
                    a = new float[p8.Pixels];
                    Array.Fill(a, 1f);
                }
                else
                {
                    a = p8.a;
                }
            }
            else
            {
                throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
            }

            var alphaArr = computer.Compute([
                r,
                g,
                b,
                a,
                (float)R,
                (float)G,
                (float)B,
                (float)Tolerance
                ]);

            if (alphaArr[0] is not float[] alpha) throw new InvalidOperationException("The output data from computer is invaild.");

            if (source is IPicture<ushort> p16_out)
            {
                p16_out.SetAlpha(true);
                var result = new Picture(p16_out)
                {
                    r = p16_out.r,
                    g = p16_out.g,
                    b = p16_out.b,
                    a = alpha,
                    hasAlphaChannel = true
                };
                for (int i = 0; i < result.Pixels; i++)
                {
                    if (result.a[i] == 0)
                    {
                        result.r[i] = 0;
                        result.g[i] = 0;
                        result.b[i] = 0;
                        result.a[i] = 0f;
                    }
                }
                result.ProcessStack = source.ProcessStack.Concat(new List<PictureProcessStack>
                {
                    new PictureProcessStack
                    {
                        OperationDisplayName = $"Replace color",
                        Operator = typeof(RemoveColorEffect_HwAccel),
                        ProcessingFuncStackTrace = new StackTrace(true),
                        Properties = new Dictionary<string, object>
                        {
                            { "R", R },
                            { "G", G },
                            { "B", B },
                            { "A", A },
                            { "Tolerance", Tolerance },
                        }
                    }
                }).ToList();

                return result.Resize(targetWidth, targetHeight, false);
            }
            else if (source is Picture8bpp p8_out)
            {
                p8_out.SetAlpha(true);
                var result = new Picture8bpp(p8_out)
                {
                    r = p8_out.r,
                    g = p8_out.g,
                    b = p8_out.b,
                    a = alpha,
                    hasAlphaChannel = true
                };
                for (int i = 0; i < result.Pixels; i++)
                {
                    if (result.a[i] == 0)
                    {
                        result.r[i] = 0;
                        result.g[i] = 0;
                        result.b[i] = 0;
                        result.a[i] = 0f;
                    }
                }
                sw.Stop();

                result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
                {
                    OperationDisplayName = $"Replace color",
                    Operator = typeof(RemoveColorEffect_HwAccel),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "R", R },
                        { "G", G },
                        { "B", B },
                        { "A", A },
                        { "Tolerance", Tolerance },
                    },
                    Elapsed = sw.Elapsed
                }).ToList();

                return result.Resize(targetWidth, targetHeight, false);
            }
            throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");

        }

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }
    }

    public class ResizeEffect_HwAccel : IEffect
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
        public string? NeedComputer => "ResizeComputer";
        public bool YieldProcessStep => false;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;


        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Height",
            "Width",
            //"PreserveAspectRatio"
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
            // 1. Calculate target dimensions (Aspect Ratio Logic)
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
                // Logic matching ImageSharp ResizeMode.Max
                double sourceRatio = (double)source.Width / source.Height;
                double targetRatio = (double)width / height;

                if (sourceRatio > targetRatio)
                {
                    // Fit to width
                    destHeight = (int)Math.Round(width / sourceRatio, MidpointRounding.AwayFromZero);
                }
                else
                {
                    // Fit to height
                    destWidth = (int)Math.Round(height * sourceRatio, MidpointRounding.AwayFromZero);
                }
                destWidth = Math.Max(1, destWidth);
                destHeight = Math.Max(1, destHeight);
            }

            // 2. Prepare Data
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

            // 3. Compute
            // Computer should handle: [r, g, b, a, srcW, srcH, dstW, dstH]
            var resultArr = computer.Compute([
                r, g, b, a,
                        (float)source.Width, (float)source.Height,
                        (float)destWidth, (float)destHeight
            ]);


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


}
