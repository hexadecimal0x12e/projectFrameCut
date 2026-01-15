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

namespace projectFrameCut.Render.Effect.ImageSharp
{

    public class PlaceEffect_ImageSharp : IEffect
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



        public string? NeedComputer => null;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => true;
        public EffectImplementType ImplementType => EffectImplementType.ImageSharp;

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


            return new PlaceEffect_ImageSharp
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

    public class CropEffect_ImageSharp : IEffect
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



        public string? NeedComputer => null;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => true;
        public EffectImplementType ImplementType => EffectImplementType.ImageSharp;

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


            return new CropEffect_ImageSharp
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

}