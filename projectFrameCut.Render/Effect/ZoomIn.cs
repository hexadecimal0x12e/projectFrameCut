using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using projectFrameCut.Render.Plugin;

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
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.ImageSharp;
        public bool YieldProcessStep => true;
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; }


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
                ImplementType = this.ImplementType,
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
            var safeCrop = Rectangle.Intersect(CropRect, new Rectangle(0, 0, source.Width, source.Height));
            if (safeCrop.Width <= 0 || safeCrop.Height <= 0)
            {
                safeCrop = new Rectangle(0, 0, Math.Min(source.Width, 1), Math.Min(source.Height, 1));
            }

            using var cropped = EffectHelper.CropPicture(source, safeCrop.X, safeCrop.Y, safeCrop.Width, safeCrop.Height, "ZoomIn Crop", typeof(ZoomInProcessStep));
            var result = cropped.Resize(TargetWidth, TargetHeight, false);
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

    public class ZoomInContinuousEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "ZoomIn";

        public EffectTarget Target => EffectTarget.Video;

        public List<string> ParametersNeeded => s_ParametersNeeded;
        public static List<string> s_ParametersNeeded { get; } = new List<string>
        {
            "TargetX",
            "TargetY",
        };

        public Dictionary<string, string> ParametersType => s_ParametersType;

        public static Dictionary<string, string> s_ParametersType { get; } = new Dictionary<string, string>
        {
            {"TargetX", "int"},
            {"TargetY", "int"},
        };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.ImageSharp, EffectImplementType.IPicture };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            return implementType switch
            {
                EffectImplementType.ImageSharp => BuildWithType(implementType, parameters),
                EffectImplementType.IPicture => BuildWithType(implementType, parameters),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            return BuildWithType(EffectImplementType.ImageSharp, parameters);
        }

        private static IEffect BuildWithType(EffectImplementType implementType, Dictionary<string, object>? parameters)
        {
            parameters ??= new Dictionary<string, object>();
            if (!parameters.ContainsKey("TargetX")) parameters["TargetX"] = 1;
            if (!parameters.ContainsKey("TargetY")) parameters["TargetY"] = 1;

            return new ZoomInContinuousEffect
            {
                TargetX = Convert.ToInt32(parameters["TargetX"]),
                TargetY = Convert.ToInt32(parameters["TargetY"]),
                ImplementType = implementType,
            };
        }
    }
}
