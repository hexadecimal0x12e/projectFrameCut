using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Effect.ImageSharp;
using projectFrameCut.Render.Effect.HwAccel;
using projectFrameCut.Render.Effect.ProjectFrameCutPicture;
using projectFrameCut.Render.VideoMakeEngine;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.Effect
{
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
            {"StartX", "int" },
            {"StartY", "int" },
        };

        public EffectImplementType[] SupportsImplementTypes => [EffectImplementType.ImageSharp];

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            return implementType switch
            {
                EffectImplementType.ImageSharp => BuildImageSharp(parameters),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        private static IEffect BuildImageSharp(Dictionary<string, object>? parameters)
        {
            parameters ??= new Dictionary<string, object>
            {
                { "StartX", 0 },
                { "StartY", 0 },
            };

            var effect = PlaceEffect_ImageSharp.FromParametersDictionary(parameters);
            return effect;
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            return BuildImageSharp(parameters);
        }
    }

    public class CropEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "Crop";

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
            "Height",
            "Width",
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"StartX", "int" },
            {"StartY", "int" },
            {"Height", "int" },
            {"Width", "int" },
        };

        public EffectImplementType[] SupportsImplementTypes => [EffectImplementType.ImageSharp];

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            return implementType switch
            {
                EffectImplementType.ImageSharp => BuildImageSharp(parameters),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        private static IEffect BuildImageSharp(Dictionary<string, object>? parameters)
        {
            parameters ??= new Dictionary<string, object>
            {
                { "StartX", 0 },
                { "StartY", 0 },
                { "Height", 1 },
                { "Width", 1 },
            };
            var effect = CropEffect_ImageSharp.FromParametersDictionary(parameters);
            return effect;
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            return BuildImageSharp(parameters);
        }
    }

    public class ResizeEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "Resize";

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "Height",
            "Width",
            // "PreserveAspectRatio" is optional by design
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"Height", "int" },
            {"Width", "int" },
            {"PreserveAspectRatio", "bool" },
        };

        public EffectImplementType[] SupportsImplementTypes => [EffectImplementType.ImageSharp, EffectImplementType.HwAcceleration, EffectImplementType.IPicture];

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            return implementType switch
            {
                EffectImplementType.ImageSharp => BuildImageSharp(parameters),
                EffectImplementType.HwAcceleration => BuildHwAccel(parameters),
                EffectImplementType.IPicture => BuildIPicture(parameters),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public bool SupportsImplementType(EffectImplementType implementType) => (int)implementType <= 3;

        private static Dictionary<string, object> EnsureDefaultResizeParameters(Dictionary<string, object>? parameters)
        {
            parameters ??= new Dictionary<string, object>();
            if (!parameters.ContainsKey("Height")) parameters["Height"] = 1;
            if (!parameters.ContainsKey("Width")) parameters["Width"] = 1;
            if (!parameters.ContainsKey("PreserveAspectRatio")) parameters["PreserveAspectRatio"] = true;
            return parameters;
        }

        private static IEffect BuildImageSharp(Dictionary<string, object>? parameters)
        {
            var effect = ResizeEffect_ImageSharp.FromParametersDictionary(EnsureDefaultResizeParameters(parameters));
            return effect;
        }

        private static IEffect BuildHwAccel(Dictionary<string, object>? parameters)
        {
            var effect = ResizeEffect_HwAccel.FromParametersDictionary(EnsureDefaultResizeParameters(parameters));
            return effect;
        }

        private static IEffect BuildIPicture(Dictionary<string, object>? parameters)
        {
            var effect = ResizeEffect_IPicture.FromParametersDictionary(EnsureDefaultResizeParameters(parameters));
            return effect;
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            // Default to ImageSharp to avoid requiring a GPU/Computer.
            return BuildImageSharp(parameters);
        }
    }

    public class RemoveColorEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "RemoveColor";

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "R",
            "G",
            "B",
            "A",
            "Tolerance",
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"R", "ushort" },
            {"G", "ushort" },
            {"B", "ushort" },
            {"A", "ushort" },
            {"Tolerance", "ushort" },
        };

        public EffectImplementType[] SupportsImplementTypes => [EffectImplementType.HwAcceleration];

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            return implementType switch
            {
                EffectImplementType.HwAcceleration => BuildHwAccel(parameters),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        private static IEffect BuildHwAccel(Dictionary<string, object>? parameters)
        {
            parameters ??= new Dictionary<string, object>
            {
                { "R", (ushort)0 },
                { "G", (ushort)0 },
                { "B", (ushort)0 },
                { "A", (ushort)0 },
                { "Tolerance", (ushort)0 },
            };

            var effect = RemoveColorEffect_HwAccel.FromParametersDictionary(parameters);
            return effect;
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            // RemoveColor currently only has HwAcceleration implementation.
            return BuildHwAccel(parameters);
        }
    }

    public class ZoomInContinuousEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "ZoomIn";

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "TargetX",
            "TargetY",
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"TargetX", "int"},
            {"TargetY", "int"},
        };

        public EffectImplementType[] SupportsImplementTypes => [EffectImplementType.ImageSharp];

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            // Continuous effects are currently implemented with ImageSharp processing steps.
            if (implementType != EffectImplementType.ImageSharp)
            {
                throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.");
            }

            return BuildWithDefaultType(parameters);
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            parameters ??= new Dictionary<string, object>();
            if (!parameters.ContainsKey("TargetX")) parameters["TargetX"] = 1;
            if (!parameters.ContainsKey("TargetY")) parameters["TargetY"] = 1;

            return new ZoomInContinuousEffect
            {
                TargetX = Convert.ToInt32(parameters["TargetX"]),
                TargetY = Convert.ToInt32(parameters["TargetY"]),
            };
        }
    }

    public class JitterContinuousEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "Jitter";

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "MaxOffsetX",
            "MaxOffsetY",
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"MaxOffsetX", "int"},
            {"MaxOffsetY", "int"},
            {"Seed", "int"},
        };

        public EffectImplementType[] SupportsImplementTypes => [EffectImplementType.ImageSharp];

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            if (implementType != EffectImplementType.ImageSharp)
            {
                throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.");
            }

            return BuildWithDefaultType(parameters);
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            parameters ??= new Dictionary<string, object>();
            if (!parameters.ContainsKey("MaxOffsetX")) parameters["MaxOffsetX"] = 0;
            if (!parameters.ContainsKey("MaxOffsetY")) parameters["MaxOffsetY"] = 0;
            if (!parameters.ContainsKey("Seed")) parameters["Seed"] = 0;

            return new JitterEffect
            {
                MaxOffsetX = Convert.ToInt32(parameters["MaxOffsetX"]),
                MaxOffsetY = Convert.ToInt32(parameters["MaxOffsetY"]),
                Seed = Convert.ToInt32(parameters["Seed"]),
            };
        }
    }
}
