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
    public partial class SharpenEffect_HwAccel : INormalEffect, IDisposable
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
        public string Name { get; set; } = "Sharpen";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public float Amount { get; init; } = 1f;
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;

        bool IEffect.CanProcessFromCanvas => true;
        public static List<string> ParametersNeeded { get; } = ["Amount"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Amount", "float" }
        };
        public string TypeName => "Sharpen";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var effect = new SharpenEffect_HwAccel
            {
                Amount = DynamicParam.ToFloat(parameters.GetValueOrDefault("Amount"))
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
                float amount = DynamicParam.Resolve(Parameters.GetValueOrDefault("Amount"), Amount);
                var sw = Stopwatch.StartNew();
                var(r, g, b, a, sourceHasAlpha) = HwAccelEffectHelper.ExtractFloatChannels(source);
                FourChannelResult computeResult;
                computeResult = ComputeSharpen(r, g, b, a, source.Width, amount);
                var result = HwAccelEffectHelper.BuildPicture(source, source.Width, source.Height, computeResult.R, computeResult.G, computeResult.B, computeResult.A, sourceHasAlpha);
                result = HwAccelEffectHelper.WithBrightness(result, source);
                sw.Stop();
                result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack { Elapsed = sw.Elapsed, OperationDisplayName = "Sharpen (GPU)", Operator = typeof(SharpenEffect_HwAccel), ProcessingFuncStackTrace = new StackTrace(true), Properties = new Dictionary<string, object> { { "Amount", amount } } }).ToList();
                return result;
            }
            catch (Exception ex)
            {
                Log(ex, "Execute Sharpen effect", "HwAccelEngine");
                throw;
            }
        }
    }
}
