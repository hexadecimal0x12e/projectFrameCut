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
    public partial class ColorAdjustmentEffect_HwAccel : IColorAdjustEffect, IDisposable
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
        public string Name { get; set; } = "ColorAdjustment";
        public float Brightness { get; init; } = 1f;
        public float Contrast { get; init; } = 1f;
        public float Saturation { get; init; } = 1f;
        public float Hue { get; init; } = 0f;
        public float Gamma { get; init; } = 1f;
        public float Vibrance { get; init; } = 0f;
        public float Temperature { get; init; } = 0f;
        public bool Invert { get; init; } = false;
        public float Grayscale { get; init; } = 0f;
        public float Opacity { get; init; } = 1f;
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Brightness",
            "Contrast",
            "Saturation",
            "Hue",
            "Gamma",
            "Vibrance",
            "Temperature",
            "Invert",
            "Grayscale",
            "Opacity"
        };
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Brightness", "float" },
            { "Contrast", "float" },
            { "Saturation", "float" },
            { "Hue", "float" },
            { "Gamma", "float" },
            { "Vibrance", "float" },
            { "Temperature", "float" },
            { "Invert", "bool" },
            { "Grayscale", "float" },
            { "Opacity", "float" }
        };
        public string TypeName => "ColorAdjustment";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        bool IEffect.IsReorderable => false;

        bool IEffect.CanProcessFromCanvas => true;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var effect = new ColorAdjustmentEffect_HwAccel
            {
                Brightness = DynamicParam.ToFloat(parameters.GetValueOrDefault("Brightness")),
                Contrast = DynamicParam.ToFloat(parameters.GetValueOrDefault("Contrast")),
                Saturation = DynamicParam.ToFloat(parameters.GetValueOrDefault("Saturation")),
                Hue = DynamicParam.ToFloat(parameters.GetValueOrDefault("Hue")),
                Gamma = DynamicParam.ToFloat(parameters.GetValueOrDefault("Gamma")),
                Vibrance = DynamicParam.ToFloat(parameters.GetValueOrDefault("Vibrance")),
                Temperature = DynamicParam.ToFloat(parameters.GetValueOrDefault("Temperature")),
                Invert = DynamicParam.ToBool(parameters.GetValueOrDefault("Invert")),
                Grayscale = DynamicParam.ToFloat(parameters.GetValueOrDefault("Grayscale")),
                Opacity = DynamicParam.ToFloat(parameters.GetValueOrDefault("Opacity"))
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);
        public IPicture Process(IPicture source)
        {
            try
            {
                using var scope = nativeLock.EnterScope();
                ObjectDisposedException.ThrowIf(disposed, this);
                float brightness = DynamicParam.Resolve(Parameters.GetValueOrDefault("Brightness"), Brightness);
                float contrast = DynamicParam.Resolve(Parameters.GetValueOrDefault("Contrast"), Contrast);
                float saturation = DynamicParam.Resolve(Parameters.GetValueOrDefault("Saturation"), Saturation);
                float hue = DynamicParam.Resolve(Parameters.GetValueOrDefault("Hue"), Hue);
                float gamma = DynamicParam.Resolve(Parameters.GetValueOrDefault("Gamma"), Gamma);
                float vibrance = DynamicParam.Resolve(Parameters.GetValueOrDefault("Vibrance"), Vibrance);
                float temperature = DynamicParam.Resolve(Parameters.GetValueOrDefault("Temperature"), Temperature);
                bool invert = DynamicParam.Resolve(Parameters.GetValueOrDefault("Invert"), Invert);
                float grayscale = DynamicParam.Resolve(Parameters.GetValueOrDefault("Grayscale"), Grayscale);
                float opacity = DynamicParam.Resolve(Parameters.GetValueOrDefault("Opacity"), Opacity);
                bool allNoop = Math.Abs(brightness - 1f) < float.Epsilon && Math.Abs(contrast - 1f) < float.Epsilon && Math.Abs(saturation - 1f) < float.Epsilon && Math.Abs(hue) < float.Epsilon && Math.Abs(gamma - 1f) < float.Epsilon && Math.Abs(vibrance) < float.Epsilon && Math.Abs(temperature) < float.Epsilon && !invert && Math.Abs(grayscale) < float.Epsilon && Math.Abs(opacity - 1f) < float.Epsilon;
                if (allNoop)
                    return source;
                var sw = Stopwatch.StartNew();
                float maxVal = source.BitPerPixel == 8 ? 255f : 65535f;
                var(r, g, b, a, sourceHasAlpha) = HwAccelEffectHelper.ExtractFloatChannels(source);
                FourChannelResult computeResult;
                computeResult = ComputeColorAdjustment(r, g, b, a, source.Width, source.Height, brightness, contrast, saturation, hue, gamma, vibrance, temperature, invert, grayscale, opacity, maxVal);
                var result = HwAccelEffectHelper.BuildPicture(source, source.Width, source.Height, computeResult.R, computeResult.G, computeResult.B, computeResult.A, sourceHasAlpha || opacity < 1f);
                result = HwAccelEffectHelper.WithBrightness(result, source);
                sw.Stop();
                result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack { Elapsed = sw.Elapsed, OperationDisplayName = "ColorAdjustment (GPU)", Operator = typeof(ColorAdjustmentEffect_HwAccel), ProcessingFuncStackTrace = new StackTrace(true), Properties = new Dictionary<string, object> { { "Brightness", brightness }, { "Contrast", contrast }, { "Saturation", saturation }, { "Hue", hue }, { "Gamma", gamma }, { "Vibrance", vibrance }, { "Temperature", temperature }, { "Invert", invert }, { "Grayscale", grayscale }, { "Opacity", opacity } } }).ToList();
                return result;
            }
            catch (Exception ex)
            {
                Log(ex, "Execute ColorAdjustment effect", "HwAccelEngine");
                throw;
            }
        }
    }
}
