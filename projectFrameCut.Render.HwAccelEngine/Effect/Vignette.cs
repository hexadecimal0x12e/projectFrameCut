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
    public partial class VignetteEffect_HwAccel : INormalEffect, IDisposable
    {
        private readonly Lock nativeLock = new();
        private bool disposed;
        public void Dispose()
        {
            using var scope = nativeLock.EnterScope();
            if (disposed)
                return;
            disposed = true;
            ReleaseNativeResources();
        }

        partial void ReleaseNativeResources();
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Vignette";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public float Strength { get; init; } = 0.5f;
        public float Radius { get; init; } = 0.65f;
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;

        bool IEffect.CanProcessFromCanvas => true;
        public static List<string> ParametersNeeded { get; } = ["Strength", "Radius"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Strength", "float" },
            { "Radius", "float" }
        };
        public string TypeName => "Vignette";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var effect = new VignetteEffect_HwAccel
            {
                Strength = DynamicParam.ToFloat(parameters.GetValueOrDefault("Strength")),
                Radius = DynamicParam.ToFloat(parameters.GetValueOrDefault("Radius"))
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);
        public IPicture Render(IPicture source, int targetWidth, int targetHeight)
        {
            try
            {
                using var scope = nativeLock.EnterScope();
                ObjectDisposedException.ThrowIf(disposed, this);
                float strength = DynamicParam.Resolve(Parameters.GetValueOrDefault("Strength"), Strength);
                float radius = DynamicParam.Resolve(Parameters.GetValueOrDefault("Radius"), Radius);
                var sw = Stopwatch.StartNew();
                var(r, g, b, a, sourceHasAlpha) = HwAccelEffectHelper.ExtractFloatChannels(source);
                FourChannelResult computeResult;
                computeResult = ComputeVignette(r, g, b, a, source.Width, source.Height, strength, radius);
                var result = HwAccelEffectHelper.BuildPicture(source, source.Width, source.Height, computeResult.R, computeResult.G, computeResult.B, computeResult.A, sourceHasAlpha);
                result = HwAccelEffectHelper.WithBrightness(result, source);
                sw.Stop();
                result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack { Elapsed = sw.Elapsed, OperationDisplayName = "Vignette (GPU)", Operator = typeof(VignetteEffect_HwAccel), ProcessingFuncStackTrace = new StackTrace(true), Properties = new Dictionary<string, object> { { "Strength", strength }, { "Radius", radius } } }).ToList();
                return result;
            }
            catch (Exception ex)
            {
                Log(ex, "Execute Vignette effect", "HwAccelEngine");
                throw;
            }
        }
    }
}
