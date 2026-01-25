using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using projectFrameCut.Render.Effect.ImageSharp;

namespace projectFrameCut.Render.Effect
{
    public class ZoomInContinuousEffect : IContinuousEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public string? NeedComputer => null;
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "ZoomIn";
        public EffectImplementType ImplementType => EffectImplementType.ImageSharp;
        public bool YieldProcessStep => true;
        public string? BindedEffectGroupID { get; set; }


        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }


        public int TargetX { get; init; }
        public int TargetY { get; init; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"TargetX", TargetX},
            {"TargetY", TargetY},
        };



        public IPicture Render(IPicture source, uint index, IComputer? computer, int targetWidth, int targetHeight)
        {
            int localIndex = (int)index - StartPoint;
            double totalFrames = (double)(EndPoint - StartPoint);
            double progress = totalFrames <= 0 ? 0.0 : (double)localIndex / totalFrames;
            if (progress < 0.0) progress = 0.0;
            if (progress > 1.0) progress = 1.0;

            int currentWidth = (int)Math.Round(source.Width + (TargetX - source.Width) * progress);
            int currentHeight = (int)Math.Round(source.Height + (TargetY - source.Height) * progress);
            if (currentWidth < 1) currentWidth = 1;
            if (currentHeight < 1) currentHeight = 1;

            // Crop requires the rectangle to be fully inside the source bounds.
            // If TargetX/TargetY are larger than the source, the interpolation may exceed bounds.
            if (currentWidth > source.Width) currentWidth = source.Width;
            if (currentHeight > source.Height) currentHeight = source.Height;

            int startX = Math.Max(0, (source.Width - currentWidth) / 2);
            int startY = Math.Max(0, (source.Height - currentHeight) / 2);
            var rect = new Rectangle(startX, startY, currentWidth, currentHeight);
            var resultImg = source.SaveToSixLaborsImage().Clone(x => x.Crop(rect).Resize(targetWidth, targetHeight));

            IPicture result = (int)source.bitPerPixel switch
            {
                8 => new Picture8bpp(resultImg),
                16 => new Picture16bpp(resultImg),
                _ => throw new NotSupportedException($"Specific pixel-mode is not supported.")
            };
            return result;
        }


        public IEffect WithParameters(Dictionary<string, object> parameters)
        {
            return new ZoomInContinuousEffect
            {
                TargetX = (int)parameters["TargetX"],
                TargetY = (int)parameters["TargetY"],
                RelativeWidth = this.RelativeWidth,
                RelativeHeight = this.RelativeHeight,
                Name = this.Name,
                Index = this.Index,
                Enabled = this.Enabled,
                StartPoint = this.StartPoint,
                EndPoint = this.EndPoint,
            };
        }

        public void Initialize()
        {
        }

        public IPictureProcessStep GetStep(IPicture source, uint index, int targetWidth, int targetHeight)
        {
            int localIndex = (int)index - StartPoint;
            double totalFrames = (double)(EndPoint - StartPoint);
            double progress = totalFrames <= 0 ? 0.0 : (double)localIndex / totalFrames;
            if (progress < 0.0) progress = 0.0;
            if (progress > 1.0) progress = 1.0;

            int currentWidth = (int)Math.Round(source.Width + (TargetX - source.Width) * progress);
            int currentHeight = (int)Math.Round(source.Height + (TargetY - source.Height) * progress);
            if (currentWidth < 1) currentWidth = 1;
            if (currentHeight < 1) currentHeight = 1;

            // Keep crop rect within bounds to avoid ImageSharp ArgumentException.
            if (currentWidth > source.Width) currentWidth = source.Width;
            if (currentHeight > source.Height) currentHeight = source.Height;

            int startX = Math.Max(0, (source.Width - currentWidth) / 2);
            int startY = Math.Max(0, (source.Height - currentHeight) / 2);
            var rect = new Rectangle(startX, startY, currentWidth, currentHeight);

            return new ZoomInProcessStep(rect, targetWidth, targetHeight);
        }
    }

    public class ZoomInProcessStep : IPictureProcessStep
    {
        private TimeSpan? _elapsed;
        public string Name => "ZoomIn";
        public Dictionary<string, object?> Properties { get; set; } = new();

        public Rectangle CropRect { get; }
        public int TargetWidth { get; }
        public int TargetHeight { get; }

        public ZoomInProcessStep(Rectangle cropRect, int targetWidth, int targetHeight)
        {
            CropRect = cropRect;
            TargetWidth = targetWidth;
            TargetHeight = targetHeight;
            Properties = new Dictionary<string, object?>
            {
                { nameof(CropRect), CropRect },
                { nameof(TargetWidth), TargetWidth },
                { nameof(TargetHeight), TargetHeight }
            };
        }

        public IPicture Process(IPicture source)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var resultImg = source.SaveToSixLaborsImage().Clone(x =>
            {
                var originalSize = x.GetCurrentSize();
                var safeCrop = Rectangle.Intersect(CropRect, new Rectangle(0, 0, originalSize.Width, originalSize.Height));
                if (safeCrop.Width <= 0 || safeCrop.Height <= 0)
                {
                    safeCrop = new Rectangle(0, 0, Math.Min(originalSize.Width, 1), Math.Min(originalSize.Height, 1));
                }
                x.Crop(safeCrop).Resize(TargetWidth, TargetHeight);
            });

            IPicture result = (int)source.bitPerPixel switch
            {
                8 => new Picture8bpp(resultImg),
                16 => new Picture16bpp(resultImg),
                _ => throw new NotSupportedException($"Specific pixel-mode is not supported.")
            };
            sw.Stop();
            _elapsed = sw.Elapsed;

            result.ProcessStack = new List<PictureProcessStack>(source.ProcessStack) { GetProcessStack() };
            return result;
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            return x =>
            {
                var originalSize = x.GetCurrentSize();
                var safeCrop = Rectangle.Intersect(CropRect, new Rectangle(0, 0, originalSize.Width, originalSize.Height));
                if (safeCrop.Width <= 0 || safeCrop.Height <= 0)
                {
                    safeCrop = new Rectangle(0, 0, Math.Min(originalSize.Width, 1), Math.Min(originalSize.Height, 1));
                }
                return x.Crop(safeCrop).Resize(TargetWidth, TargetHeight);
            };
        }

        public PictureProcessStack GetProcessStack() => new PictureProcessStack
        {
            Elapsed = _elapsed,
            OperationDisplayName = "ZoomIn",
            Operator = typeof(ZoomInProcessStep),
            ProcessingFuncStackTrace = new System.Diagnostics.StackTrace(true),
            StepUsed = this,
            Properties = new Dictionary<string, object>
            {
                { nameof(CropRect), CropRect },
                { nameof(TargetWidth), TargetWidth },
                { nameof(TargetHeight), TargetHeight }
            }
        };
    }

    public class JitterEffect : IContinuousEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public EffectImplementType ImplementType => EffectImplementType.ImageSharp;

        public int MaxOffsetX { get; init; }
        public int MaxOffsetY { get; init; }
        public int Seed { get; init; } = 0;
        public string Direction { get; init; } = Direction_Both;

        public const string Direction_Both = "Both";
        public const string Direction_XOnly = "XOnly";
        public const string Direction_YOnly = "YOnly";

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "MaxOffsetX", MaxOffsetX },
            { "MaxOffsetY", MaxOffsetY },
            { "Seed", Seed },
            { "Direction", Direction },
        };

        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public string? NeedComputer => null;
        public bool YieldProcessStep => true;

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public Random rnd;

        public static List<string> s_ParametersNeeded { get; } = new List<string>
        {
            "MaxOffsetX",
            "MaxOffsetY",
            "Direction",
        };

        public static Dictionary<string, string> s_ParametersType { get; } = new Dictionary<string, string>
        {
            { "MaxOffsetX", "int" },
            { "MaxOffsetY", "int" },
            { "Seed", "int" },
            { "Direction", "string" },
        };

        public Dictionary<string, string> ParametersType => s_ParametersType;
        public List<string> ParametersNeeded => s_ParametersNeeded;

        public string TypeName => "Jitter";

        public IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            int maxX = Convert.ToInt32(parameters["MaxOffsetX"]);
            int maxY = Convert.ToInt32(parameters["MaxOffsetY"]);
            int seed = 0;
            if (parameters.TryGetValue("Seed", out var s))
            {
                seed = Convert.ToInt32(s);
            }
            string direction = parameters.TryGetValue("Direction", out var d) ? d.ToString() : Direction_Both;

            return new JitterEffect
            {
                MaxOffsetX = maxX,
                MaxOffsetY = maxY,
                Seed = seed,
                Direction = direction,
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public void Initialize()
        {
            if (Seed != 0)
            {
                rnd = new(Seed);
            }
            else
            {
                rnd = new();
            }
        }

        /// <summary>
        /// Render single frame with deterministic random offset based on frame index and seed.
        /// </summary>
        public IPicture Render(IPicture source, uint index, IComputer? computer, int targetWidth, int targetHeight)
        {

            int offX = 0, offY = 0;
            if (Direction == Direction_Both || Direction == Direction_XOnly)
            {
                if (MaxOffsetX > 0)
                {
                    offX = rnd.Next(-MaxOffsetX, MaxOffsetX + 1);
                }
            }
            if (Direction == Direction_Both || Direction == Direction_YOnly)
            {
                if (MaxOffsetY > 0)
                {
                    offY = rnd.Next(-MaxOffsetY, MaxOffsetY + 1);
                }
            }

            return new PlaceProcessStep(offX, offY, targetWidth, targetHeight).Process(source);
        }

        public IPictureProcessStep GetStep(IPicture source, uint index, int targetWidth, int targetHeight)
        {
            int offX = 0, offY = 0;
            if (Direction == Direction_Both || Direction == Direction_XOnly)
            {
                if (MaxOffsetX > 0)
                {
                    offX = rnd.Next(-MaxOffsetX, MaxOffsetX + 1);
                }
            }
            if (Direction == Direction_Both || Direction == Direction_YOnly)
            {
                if (MaxOffsetY > 0)
                {
                    offY = rnd.Next(-MaxOffsetY, MaxOffsetY + 1);
                }
            }
            return new PlaceProcessStep(offX, offY, targetWidth, targetHeight);
        }

        public string? BindedEffectGroupID { get; set; }

    }

}
