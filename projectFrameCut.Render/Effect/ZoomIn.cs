using projectFrameCut.Drawing.Effect;
using projectFrameCut.Drawing.Processing.Cropping;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Diagnostics;

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
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.IPicture;
        public bool IsReorderable => true;
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; }


        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }
        public bool IsScoped { get; set; }
        public int TargetX { get; init; }
        public int TargetY { get; init; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"TargetX", TargetX},
            {"TargetY", TargetY},
        };


        public IPicture Render(IPicture source, float progress, IComputer? computer, int targetWidth, int targetHeight)
        {
            double clampedProgress = Math.Clamp(progress, 0.0, 1.0);

            int currentWidth = (int)Math.Round(source.Width + (TargetX - source.Width) * clampedProgress);
            int currentHeight = (int)Math.Round(source.Height + (TargetY - source.Height) * progress);
            if (currentWidth < 1) currentWidth = 1;
            if (currentHeight < 1) currentHeight = 1;

            if (currentWidth > source.Width) currentWidth = source.Width;
            if (currentHeight > source.Height) currentHeight = source.Height;

            int startX = Math.Max(0, (source.Width - currentWidth) / 2);
            int startY = Math.Max(0, (source.Height - currentHeight) / 2);
            var cropped = CropEffect.Process(source, startX, startY, currentWidth, currentHeight);
            var result = cropped.Resize(targetWidth, targetHeight, preserveAspect: false);
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
                IsScoped = this.IsScoped,
            };
        }

        public void Initialize()
        {
        }
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

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.IPicture };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            return implementType switch
            {
                EffectImplementType.IPicture => BuildWithType(implementType, parameters),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            return BuildWithType(EffectImplementType.IPicture, parameters);
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
