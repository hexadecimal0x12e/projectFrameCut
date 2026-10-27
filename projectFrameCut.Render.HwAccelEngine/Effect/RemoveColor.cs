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
    public partial class RemoveColorEffect_HwAccel : INormalEffect, IDisposable
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
        public ushort R { get; init; }
        public ushort G { get; init; }
        public ushort B { get; init; }
        public ushort A { get; init; }
        public ushort Tolerance { get; init; } = 0;
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;

        bool IEffect.CanProcessFromCanvas => true;
        public string? BindedEffectProvidingSystemID { get; set; }
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
            {
                "R",
                "ushort"
            },
            {
                "G",
                "ushort"
            },
            {
                "B",
                "ushort"
            },
            {
                "A",
                "ushort"
            },
            {
                "Tolerance",
                "ushort"
            },
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

            var effect = new RemoveColorEffect_HwAccel
            {
                R = DynamicParam.ToUShort(parameters.GetValueOrDefault("R")),
                G = DynamicParam.ToUShort(parameters.GetValueOrDefault("G")),
                B = DynamicParam.ToUShort(parameters.GetValueOrDefault("B")),
                A = DynamicParam.ToUShort(parameters.GetValueOrDefault("A")),
                Tolerance = DynamicParam.ToUShort(parameters.GetValueOrDefault("Tolerance")),
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);
        public IPicture Render(IPicture source, int targetWidth, int targetHeight) => ResidentEffectResult.Download(EffectPictureProcessing.Render(this, source, targetWidth, targetHeight));
    }
}
