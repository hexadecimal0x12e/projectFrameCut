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
    public partial class FadeOpacityEffect_HwAccel : INormalEffect, IDisposable
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
        public string Name { get; set; } = "FadeOpacity";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public float Opacity { get; init; } = 0.8f;
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;

        bool IEffect.CanProcessFromCanvas => true;
        public static List<string> ParametersNeeded { get; } = ["Opacity"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Opacity", "float" }
        };
        public string TypeName => "FadeOpacity";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var effect = new FadeOpacityEffect_HwAccel
            {
                Opacity = DynamicParam.ToFloat(parameters.GetValueOrDefault("Opacity"))
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
                float opacity = DynamicParam.Resolve(Parameters.GetValueOrDefault("Opacity"), Opacity);
                var sw = Stopwatch.StartNew();
                var(r, g, b, a, sourceHasAlpha) = HwAccelEffectHelper.ExtractFloatChannels(source);
                FourChannelResult computeResult;
                computeResult = ComputeOpacity(r, g, b, a, opacity);
                var result = HwAccelEffectHelper.BuildPicture(source, source.Width, source.Height, computeResult.R, computeResult.G, computeResult.B, computeResult.A, true);
                result = HwAccelEffectHelper.WithBrightness(result, source);
                sw.Stop();
                result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack { Elapsed = sw.Elapsed, OperationDisplayName = "FadeOpacity (GPU)", Operator = typeof(FadeOpacityEffect_HwAccel), ProcessingFuncStackTrace = new StackTrace(true), Properties = new Dictionary<string, object> { { "Opacity", opacity } } }).ToList();
                return result;
            }
            catch (Exception ex)
            {
                Log(ex, "Execute FadeOpacity effect", "HwAccelEngine");
                throw;
            }
        }
    }
}
