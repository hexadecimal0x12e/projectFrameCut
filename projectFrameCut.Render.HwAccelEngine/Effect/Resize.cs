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
    public partial class ResizeEffect_HwAccel : INormalEffect, IDisposable
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
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string Id { get; set; }
        public int Height { get; init; }
        public int Width { get; init; }
        public bool PreserveAspectRatio { get; init; } = true;
        public string? BindedEffectProvidingSystemID { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;
        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Height",
            "Width",
        };
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {
                "Height",
                "int"
            },
            {
                "Width",
                "int"
            },
            {
                "PreserveAspectRatio",
                "bool"
            },
        };
        public string TypeName => "Resize";

        void IExtensibleObject.Initialize()
        {
            Log("Place and Resize effects are deprecated. Consider migrate to IClipPositionProvider.", "warn");
        }

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
                preserve = DynamicParam.ToBool(val);
            }

            var effect = new ResizeEffect_HwAccel
            {
                Height = DynamicParam.ToInt32(parameters.GetValueOrDefault("Height")),
                Width = DynamicParam.ToInt32(parameters.GetValueOrDefault("Width")),
                PreserveAspectRatio = preserve,
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);
        public IPicture Render(IPicture source, int targetWidth, int targetHeight) => ResidentEffectResult.Download(EffectPictureProcessing.Render(this, source, targetWidth, targetHeight));
    }
}
