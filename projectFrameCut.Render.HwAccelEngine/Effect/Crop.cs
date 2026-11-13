using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;
using static projectFrameCut.Shared.Logger;
using System.Diagnostics;

namespace projectFrameCut.Render.HwAccelEngine.Effect
{
    public partial class CropEffect_HwAccel : INormalEffect, IDisposable
    {
        public bool IsDeterministic => true;

        private readonly Lock nativeLock = new();
        private bool disposed;
        public void Dispose()
        {
            using var scope = nativeLock.EnterScope();
            if (disposed)
                return;
            disposed = true;
        }

        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Crop";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public int StartX { get; init; }
        public int StartY { get; init; }
        public int Height { get; init; }
        public int Width { get; init; }
        public float Angle { get; init; }
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;
        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
            "Height",
            "Width",
            "Angle"
        };
        public static List<string> OptionalParameters { get; } = new List<string>
        {
            "Angle",
            "CropList"
        };
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {
                "StartX",
                "int"
            },
            {
                "StartY",
                "int"
            },
            {
                "Height",
                "int"
            },
            {
                "Width",
                "int"
            },
            {
                "Angle",
                "float"
            },
        };
        public string TypeName => "Crop";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.Except(OptionalParameters).All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var unsupportedParameters = parameters.Keys.Except(ParametersNeeded).Except(OptionalParameters).ToList();
            if (unsupportedParameters.Count > 0)
            {
                throw new ArgumentException($"Unsupported parameters: {string.Join(", ", unsupportedParameters)}");
            }

            float angle = parameters.TryGetValue("Angle", out var angleVal) ? DynamicParam.ToFloat(angleVal) : 0f;
            var effect = new CropEffect_HwAccel
            {
                StartX = DynamicParam.ToInt32(parameters.GetValueOrDefault("StartX")),
                StartY = DynamicParam.ToInt32(parameters.GetValueOrDefault("StartY")),
                Height = DynamicParam.ToInt32(parameters.GetValueOrDefault("Height")),
                Width = DynamicParam.ToInt32(parameters.GetValueOrDefault("Width")),
                Angle = angle,
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);
        public IPicture Render(IPicture source, int targetWidth, int targetHeight) => ResidentEffectResult.Download(EffectPictureProcessing.Render(this, source, targetWidth, targetHeight));
    }
}
