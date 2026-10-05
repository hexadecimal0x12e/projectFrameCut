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
    public partial class RotationEffect_HwAccel : INormalEffect, IDisposable
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
        public string Name { get; set; } = "Rotation";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public float Angle { get; init; }
        public bool ExpandCanvas { get; init; } = false;
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;
        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Angle"
        };
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Angle", "float" },
            { "ExpandCanvas", "bool" },
        };
        public string TypeName => "Rotation";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            float angle = DynamicParam.ToFloat(parameters.GetValueOrDefault("Angle"));
            bool expandCanvas = false;
            if (parameters.TryGetValue("ExpandCanvas", out var expandVal))
            {
                expandCanvas = DynamicParam.ToBool(expandVal);
            }

            var effect = new RotationEffect_HwAccel
            {
                Angle = angle,
                ExpandCanvas = expandCanvas
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
                float angle = DynamicParam.Resolve(Parameters.GetValueOrDefault("Angle"), Angle);
                bool expandCanvas = DynamicParam.Resolve(Parameters.GetValueOrDefault("ExpandCanvas"), ExpandCanvas);
                if (Math.Abs(angle % 360f) < float.Epsilon)
                    return source;
                float angleRad = angle * MathF.PI / 180f;
                float cos = MathF.Abs(MathF.Cos(angleRad));
                float sin = MathF.Abs(MathF.Sin(angleRad));
                int outW, outH;
                if (expandCanvas)
                {
                    outW = (int)MathF.Ceiling(source.Width * cos + source.Height * sin);
                    outH = (int)MathF.Ceiling(source.Width * sin + source.Height * cos);
                }
                else
                {
                    outW = source.Width;
                    outH = source.Height;
                }

                var sw = Stopwatch.StartNew();
                var(r, g, b, a, sourceHasAlpha) = HwAccelEffectHelper.ExtractFloatChannels(source);
                FourChannelResult computeResult;
                computeResult = ComputeRotation(r, g, b, a, source.Width, source.Height, outW, outH, angle);
                var result = HwAccelEffectHelper.BuildPicture(source, outW, outH, computeResult.R, computeResult.G, computeResult.B, computeResult.A, sourceHasAlpha);
                result = HwAccelEffectHelper.WithBrightness(result, source, source is IHDRPicture<ushort> hdr ? ComputeRotation(hdr.Brightness, hdr.Brightness, hdr.Brightness, a, source.Width, source.Height, outW, outH, angle).R : null);
                sw.Stop();
                result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack { Elapsed = sw.Elapsed, OperationDisplayName = "Rotation (GPU)", Operator = typeof(RotationEffect_HwAccel), ProcessingFuncStackTrace = new StackTrace(true), Properties = new Dictionary<string, object> { { "Angle", angle }, { "ExpandCanvas", expandCanvas } } }).ToList();
                return result;
            }
            catch (Exception ex)
            {
                Log(ex, "Execute Rotation effect", "HwAccelEngine");
                throw;
            }
        }
    }
}
