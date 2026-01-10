using projectFrameCut.Render;
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
            ArgumentNullException.ThrowIfNull(computer, nameof(computer));

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
                result.ProcessStack += $"Replace color #{R:x2}{G:x2}{B:x2} tol:{Tolerance}\r\n";

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
                result.ProcessStack += $"Replace color #{R:x2}{G:x2}{B:x2} tol:{Tolerance}\r\n";

                return result.Resize(targetWidth, targetHeight, false);
            }
            throw new NotSupportedException();
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
            return GetStep(source, targetHeight, targetHeight).Process(source);

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
        public string? NeedComputer => null;
        public bool YieldProcessStep => true;

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
            return GetStep(source, targetWidth, targetHeight).Process(source);
        }



        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            int width = Width;
            int height = Height;

            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                width = Math.Max(1, (int)Math.Round((double)Width * targetWidth / RelativeWidth));
                height = Math.Max(1, (int)Math.Round((double)Height * targetHeight / RelativeHeight));
            }
            else
            {
                width = Math.Max(1, width);
                height = Math.Max(1, height);
            }

            return new ResizeProcessStep(width, height, PreserveAspectRatio);
        }
    }

    public class ResizeProcessStep : IPictureProcessStep
    {
        public string Name => "Resize";
        public Dictionary<string, object?> Properties { get; set; } = new();

        public int Width { get; init; }
        public int Height { get; init; }
        public bool PreserveAspectRatio { get; init; } = true;

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

            var srcStack = source.ProcessStack ?? string.Empty;
            resized.ProcessStack = $"{srcStack}\r\nResize to {Width}*{Height} preserve:{PreserveAspectRatio}\r\n";
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
    }

    public class PlaceProcessStep : IPictureProcessStep
    {
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
            result.ProcessStack = source.ProcessStack;
            return result;
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            return imgCtx =>
            {
                var size = imgCtx.GetCurrentSize();
                int padX = (CanvasWidth - size.Width) / 2;
                int padY = (CanvasHeight - size.Height) / 2;

                imgCtx.Pad(CanvasWidth, CanvasHeight, Color.Transparent);

                float moveX = StartX - padX;
                float moveY = StartY - padY;

                if (moveX != 0 || moveY != 0)
                {
                    imgCtx.Transform(new AffineTransformBuilder().AppendTranslation(new PointF(moveX, moveY)));
                }

                return imgCtx;
            };
        }

        public string GetProcessStack() => $"Place to ({StartX},{StartY}) with canvas size {CanvasWidth}*{CanvasHeight}\r\n";
    }

    public class CropProcessStep : IPictureProcessStep
    {
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
                _ => throw new NotSupportedException($"Specific pixel-mode is not supported.")
            };

            result.ProcessStack = source.ProcessStack;
            return result;
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            return imgCtx => imgCtx.Crop(new Rectangle(StartX, StartY, Width, Height));
        }

        public string GetProcessStack() => $"Crop from ({StartX},{StartY}) with size {Width}*{Height}\r\n";
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