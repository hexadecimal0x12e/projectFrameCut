using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public class PlaceEffect_ImageSharp : INormalEffect
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

        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; }
    }

    public class PlaceProcessStep : IPictureProcessStep
    {
        private TimeSpan? _elapsed;
        public string Name => "Place";
        public Dictionary<string, object?> Properties { get; set; } = new();

        public int StartX { get; }
        public int StartY { get; }
        public int TargetWidth { get; }
        public int TargetHeight { get; }

        public PlaceProcessStep(int startX, int startY, int targetWidth, int targetHeight)
        {
            StartX = startX;
            StartY = startY;
            TargetWidth = targetWidth;
            TargetHeight = targetHeight;
            Properties = new Dictionary<string, object?>
            {
                { nameof(StartX), StartX },
                { nameof(StartY), StartY },
                { nameof(TargetWidth), TargetWidth },
                { nameof(TargetHeight), TargetHeight }
            };
        }

        public IPicture Process(IPicture source)
        {
            var sw = Stopwatch.StartNew();
            var srcImg = source.SaveToSixLaborsImage();
            var resultImg = new Image<Rgba32>(TargetWidth, TargetHeight, Color.Transparent);
            resultImg.Mutate(ctx => ctx.DrawImage(srcImg, new Point(StartX, StartY), 1f));

            IPicture result = (int)source.bitPerPixel switch
            {
                8 => new Picture8bpp(resultImg),
                16 => new Picture16bpp(resultImg),
                _ => throw new NotSupportedException($"Specific pixel-mode is not supported.")
            };
            sw.Stop();
            _elapsed = sw.Elapsed;

            result.ProcessStack = source.ProcessStack.Append(GetProcessStack()).ToList();
            return result;
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            // Not representable as a single IImageProcessingContext pipeline because
            // placing creates a new target canvas and draws the source into it.
            // Return null so callers know there's no single-context transform.
            return null;
        }

        public PictureProcessStack GetProcessStack() => new PictureProcessStack
        {
            Elapsed = _elapsed,
            OperationDisplayName = "Place",
            Operator = typeof(PlaceProcessStep),
            ProcessingFuncStackTrace = new System.Diagnostics.StackTrace(true),
            StepUsed = this,
            Properties = new Dictionary<string, object>
            {
                { nameof(StartX), StartX },
                { nameof(StartY), StartY },
                { nameof(TargetWidth), TargetWidth },
                { nameof(TargetHeight), TargetHeight }
            }
        };
    }

    public class PlaceEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "Place";

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"StartX", "int"},
            {"StartY", "int"},
        };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.ImageSharp };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            return implementType switch
            {
                EffectImplementType.ImageSharp => BuildWithDefaultType(parameters),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters)
        {
            return PlaceEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>());
        }
    }
}
