using projectFrameCut.Render;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace projectFrameCut.Render.VideoMakeEngine
{
    public class RemoveColorEffect : IEffect
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

        List<string> IEffect.ParametersNeeded => ParametersNeeded;
        Dictionary<string, string> IEffect.ParametersType => ParametersType;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public string NeedComputer => "RemoveColorComputer";
        public bool YieldProcessStep => false;


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


            return new RemoveColorEffect
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
                        Operator = typeof(RemoveColorEffect),
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
                    Operator = typeof(RemoveColorEffect),
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

    public class PlaceEffect : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }


        public int StartX { get; set; }
        public int StartY { get; set; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"StartX", StartX},
            {"StartY", StartY},
        };

        List<string> IEffect.ParametersNeeded => ParametersNeeded;
        Dictionary<string, string> IEffect.ParametersType => ParametersType;
        public string? NeedComputer => null;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => true;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY"
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"StartX","int" },
            {"StartY","int" },
        };

        public string TypeName => "Place";

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


            return new PlaceEffect
            {
                StartX = Convert.ToInt32(parameters["StartX"]),
                StartY = Convert.ToInt32(parameters["StartY"]),
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
            => Place(source, StartX, StartY, targetWidth, targetHeight);

        public IPicture Place(IPicture source, int startX, int startY, int targetWidth, int targetHeight)
        {
            return GetStep(source, targetWidth, targetHeight).Process(source);

        }

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
            {
                throw new ArgumentException("targetWidth and targetHeight must be positive");
            }
            int startX = StartX, startY = StartY;
            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                startX = (int)Math.Round((double)startX * targetWidth / RelativeWidth);
                startY = (int)Math.Round((double)startY * targetHeight / RelativeHeight);
            }

            return new PlaceProcessStep(startX, startY, targetWidth, targetHeight);
        }
    }

    public class CropEffect : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }


        public int StartX { get; init; }
        public int StartY { get; init; }
        public int Height { get; init; }
        public int Width { get; init; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"StartX", StartX },
            {"StartY", StartY },
            {"Height", Height },
            {"Width", Width },
        };

        List<string> IEffect.ParametersNeeded => ParametersNeeded;
        Dictionary<string, string> IEffect.ParametersType => ParametersType;
        public string? NeedComputer => null;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => true;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
            "Height",
            "Width",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"StartX", "int" },
            {"StartY", "int" },
            {"Height", "int" },
            {"Width", "int" },
        };

        public string TypeName => "Crop";

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


            return new CropEffect
            {
                StartX = Convert.ToInt32(parameters["StartX"]),
                StartY = Convert.ToInt32(parameters["StartY"]),
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
            => Crop(source, StartX, StartY, Width, Height, targetWidth, targetHeight);


        //[DebuggerStepThrough()]
        public IPicture Crop(IPicture source, int startX, int startY, int width, int height, int targetWidth, int targetHeight)
        {
            return GetStep(source, targetWidth, targetHeight).Process(source);
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            return imgCtx => imgCtx.Crop(new Rectangle(StartX, StartY, Width, Height));
        }

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            int startX = StartX, startY = StartY, width = Width, height = Height;
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Width and Height must be positive");
            }
            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                startX = (int)Math.Round((double)StartX * targetWidth / RelativeWidth);
                startY = (int)Math.Round((double)StartY * targetHeight / RelativeHeight);
                width = (int)Math.Round((double)Width * targetWidth / RelativeWidth);
                height = (int)Math.Round((double)Height * targetHeight / RelativeHeight);
            }


            return new CropProcessStep(startX, startY, width, height);
        }
    }

    public class ResizeEffect : IEffect
    {
        public static bool UseHWAccel { get; set; } = true;

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

        List<string> IEffect.ParametersNeeded => ParametersNeeded;
        Dictionary<string, string> IEffect.ParametersType => ParametersType;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public string? NeedComputer => UseHWAccel ? "ResizeComputer" : null;
        public bool YieldProcessStep => !UseHWAccel;

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

            return new ResizeEffect
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
            if (computer != null && !YieldProcessStep)
            {
                try
                {
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
                        goto CPU_FALLBACK;
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
                            Operator = typeof(ResizeProcessStep),
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
                catch (Exception)
                {
                    // Fallback to CPU on any error
                }
            }

        CPU_FALLBACK:
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
                Size = new SixLabors.ImageSharp.Size(Width, Height),
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
                Size = new SixLabors.ImageSharp.Size(Width, Height),
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

    public class PlaceProcessStep : IPictureProcessStep
    {
        private TimeSpan? _elapsed;
        public string Name => "Place";
        public Dictionary<string, object?> Properties { get; set; } = new();

        public int StartX { get; }
        public int StartY { get; }
        public int CanvasWidth { get; }
        public int CanvasHeight { get; }

        public PlaceProcessStep(int startX, int startY, int canvasWidth, int canvasHeight)
        {
            StartX = startX;
            StartY = startY;
            CanvasWidth = canvasWidth;
            CanvasHeight = canvasHeight;
            Properties = new Dictionary<string, object?>
            {
                { nameof(StartX), StartX },
                { nameof(StartY), StartY },
                { nameof(CanvasWidth), CanvasWidth },
                { nameof(CanvasHeight), CanvasHeight }
            };
        }

        public IPicture Process(IPicture source)
        {
            var sw = Stopwatch.StartNew();
            var srcImg = source.SaveToSixLaborsImage();
            Image resultImg;
            if (source.bitPerPixel == 16)
            {
                var canvas = new Image<Rgba64>(CanvasWidth, CanvasHeight);
                canvas.Mutate(x => x.DrawImage(srcImg, new Point(StartX, StartY), 1f));
                resultImg = canvas;
            }
            else
            {
                var canvas = new Image<Rgba32>(CanvasWidth, CanvasHeight);
                canvas.Mutate(x => x.DrawImage(srcImg, new Point(StartX, StartY), 1f));
                resultImg = canvas;
            }

            IPicture result = (int)source.bitPerPixel switch
            {
                8 => new Picture8bpp(resultImg),
                16 => new Picture16bpp(resultImg),
                _ => throw new NotSupportedException($"Specific pixel-mode is not supported.")
            };
            sw.Stop();
            _elapsed = sw.Elapsed;
            result.ProcessStack = source.ProcessStack;
            return result;
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            // IMPORTANT: Keep behavior identical to Process(), which renders onto a new
            // transparent canvas and draws the image at (StartX, StartY).
            // The previous Pad+Transform fast-path introduced edge-dependent offsets.
            return null;
        }

        public PictureProcessStack GetProcessStack() => new PictureProcessStack
        {
            Elapsed = _elapsed,
            OperationDisplayName = "Place",
            Operator = typeof(PlaceProcessStep),
            ProcessingFuncStackTrace = new StackTrace(true),
            StepUsed = this,
            Properties = new Dictionary<string, object>
            {
                { nameof(StartX), StartX },
                { nameof(StartY), StartY },
                { nameof(CanvasWidth), CanvasWidth },
                { nameof(CanvasHeight), CanvasHeight }
            }
        };
    }

    public class CropProcessStep : IPictureProcessStep
    {
        private TimeSpan? _elapsed;
        public string Name => "Crop";
        public Dictionary<string, object?> Properties { get; set; } = new();

        public int StartX { get; init; }
        public int StartY { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        public CropProcessStep(int startX, int startY, int width, int height)
        {
            StartX = startX;
            StartY = startY;
            Width = width;
            Height = height;
            Properties = new Dictionary<string, object?>
            {
                { nameof(StartX), StartX },
                { nameof(StartY), StartY },
                { nameof(Width), Width },
                { nameof(Height), Height }
            };
        }

        public IPicture Process(IPicture source)
        {
            var sw = Stopwatch.StartNew();

            if (Width <= 0 || Height <= 0)
            {
                throw new ArgumentException("Width and Height must be positive");
            }
            var rect = new Rectangle(StartX, StartY, Width, Height);
            var resultImg = source.SaveToSixLaborsImage().Clone(x => x.Crop(rect));

            IPicture result = (int)source.bitPerPixel switch
            {
                8 => new Picture8bpp(resultImg),
                16 => new Picture16bpp(resultImg),
            };

            sw.Stop();
            _elapsed = sw.Elapsed;

            result.ProcessStack = source.ProcessStack;
            return result;
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            return imgCtx => imgCtx.Crop(new Rectangle(StartX, StartY, Width, Height));
        }

        public PictureProcessStack GetProcessStack() => new PictureProcessStack
        {
            Elapsed = _elapsed,
            OperationDisplayName = "Crop",
            Operator = typeof(CropProcessStep),
            ProcessingFuncStackTrace = new StackTrace(true),
            StepUsed = this,
            Properties = new Dictionary<string, object>
            {
                { nameof(StartX), StartX },
                { nameof(StartY), StartY },
                { nameof(Width), Width },
                { nameof(Height), Height }
            }
        };
    }

    /*
    public class EffectBase : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; init; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"", },
        };

        List<string> IEffect.ParametersNeeded => ParametersNeeded;
        Dictionary<string, string> IEffect.ParametersType => ParametersType;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"","" },
        };

        public string TypeName => "";
        public static string s_TypeName => "";

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


            return new EffectBase
            {

            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public Picture Render(Picture source, IComputer computer, int targetWidth, int targetHeight)
        {

        }
    }
    */

}