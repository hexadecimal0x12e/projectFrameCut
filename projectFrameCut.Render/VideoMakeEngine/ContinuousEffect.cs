using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace projectFrameCut.Render.VideoMakeEngine
{
    public class ZoomInContinuousEffect : IContinuousEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public string? NeedComputer => null;
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "ZoomIn";
        public bool YieldProcessStep => true;


        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }


        public int TargetX { get; init; }
        public int TargetY { get; init; }

        public CropEffect Cropper { get; set; } = new();

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"TargetX", TargetX},
            {"TargetY", TargetY},
        };



        public List<string> ParametersNeeded => s_ParametersNeeded;
        public Dictionary<string, string> ParametersType => s_ParametersType;


        public static List<string> s_ParametersNeeded { get; } = new List<string>
        {
            "TargetX",
            "TargetY"
        };

        public static Dictionary<string, string> s_ParametersType { get; } = new Dictionary<string, string>
        {
            {"TargetX","int" },
            {"TargetY","int" },
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
            Cropper.RelativeHeight = RelativeHeight;
            Cropper.RelativeWidth = RelativeWidth;
        }

        public IPictureProcessStep GetStep(IPicture source, uint index, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }
    }

    public class JitterEffect : IContinuousEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public int MaxOffsetX { get; init; }
        public int MaxOffsetY { get; init; }
        public int Seed { get; init; } = 0;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "MaxOffsetX", MaxOffsetX },
            { "MaxOffsetY", MaxOffsetY },
            { "Seed", Seed },
        };

        List<string> IContinuousEffect.ParametersNeeded => ParametersNeeded;
        Dictionary<string, string> IContinuousEffect.ParametersType => ParametersType;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public string? NeedComputer => null;
        public bool YieldProcessStep => true;

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public Random rnd;
        public PlaceEffect placer;

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "MaxOffsetX",
            "MaxOffsetY",
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "MaxOffsetX", "int" },
            { "MaxOffsetY", "int" },
            { "Seed", "int" },
        };

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

            return new JitterEffect
            {
                MaxOffsetX = maxX,
                MaxOffsetY = maxY,
                Seed = seed,
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

            placer = new PlaceEffect
            {
                RelativeWidth = this.RelativeWidth,
                RelativeHeight = this.RelativeHeight,
            };

        }

        /// <summary>
        /// Render single frame with deterministic random offset based on frame index and seed.
        /// </summary>
        public IPicture Render(IPicture source, uint index, IComputer? computer, int targetWidth, int targetHeight)
        {

            int offX = 0, offY = 0;
            if (MaxOffsetX > 0)
            {
                offX = rnd.Next(-MaxOffsetX, MaxOffsetX + 1);
            }
            if (MaxOffsetY > 0)
            {
                offY = rnd.Next(-MaxOffsetY, MaxOffsetY + 1);
            }

            return placer.Place(source, offX, offY, targetWidth, targetHeight);
        }

        public IPictureProcessStep GetStep(IPicture source, uint index, int targetWidth, int targetHeight)
        {
            int offX = 0, offY = 0;
            if (MaxOffsetX > 0)
            {
                offX = rnd.Next(-MaxOffsetX, MaxOffsetX + 1);
            }
            if (MaxOffsetY > 0)
            {
                offY = rnd.Next(-MaxOffsetY, MaxOffsetY + 1);
            }
            return new PlaceProcessStep(offX, offY, targetWidth, targetHeight);
        }
    }
}
