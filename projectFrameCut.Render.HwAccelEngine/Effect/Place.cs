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
    public partial class PlaceEffect_HwAccel : INormalEffect, IDisposable
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
        public string Name { get; set; } = "Place";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string Id { get; set; } = string.Empty;
        public int StartX { get; set; }
        public int StartY { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;
        public static List<string> ParametersNeeded { get; } = new List<string>
        { "StartX", "StartY" };
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "StartX", "int" },
            { "StartY", "int" },
        };
        public string TypeName => "Place";
        public string? BindedEffectProvidingSystemID { get; set; }

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

            if (parameters.Count != ParametersNeeded.Count)
            {
                throw new ArgumentException("Too many parameters provided.");
            }

            var effect = new PlaceEffect_HwAccel
            {
                StartX = DynamicParam.ToInt32(parameters.GetValueOrDefault("StartX")),
                StartY = DynamicParam.ToInt32(parameters.GetValueOrDefault("StartY")),
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
                if (targetWidth <= 0 || targetHeight <= 0)
                {
                    throw new ArgumentException("targetWidth and targetHeight must be positive");
                }

                int placeX = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartX"), StartX);
                int placeY = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartY"), StartY);
                int startX = placeX;
                int startY = placeY;
                if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
                {
                    startX = (int)Math.Round((double)startX * targetWidth / RelativeWidth);
                    startY = (int)Math.Round((double)startY * targetHeight / RelativeHeight);
                }

                return RenderWithOffset(source, startX, startY, targetWidth, targetHeight);
            }
            catch (Exception ex)
            {
                Log(ex, "Execute Place effect", "HwAccelEngine");
                throw;
            }
        }

        private IPicture RenderWithOffset(IPicture source, int startX, int startY, int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
            {
                throw new ArgumentException("targetWidth and targetHeight must be positive");
            }

            var sw = Stopwatch.StartNew();
            var(r, g, b, a) = ExtractFloatChannels(source);
            FourChannelResult placeResult;
            placeResult = ComputePlace(r, g, b, a, source.Width, source.Height, startX, startY, targetWidth, targetHeight);
            var result = BuildPicture(source, targetWidth, targetHeight, placeResult.R, placeResult.G, placeResult.B, placeResult.A);
            result = HwAccelEffectHelper.WithBrightness(result, source, source is IHDRPicture<ushort> hdr ? ComputePlace(hdr.Brightness, hdr.Brightness, hdr.Brightness, a, source.Width, source.Height, startX, startY, targetWidth, targetHeight).R : null);
            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack { Elapsed = sw.Elapsed, OperationDisplayName = "Place (GPU)", Operator = typeof(PlaceEffect_HwAccel), ProcessingFuncStackTrace = new StackTrace(true), Properties = new Dictionary<string, object> { { "StartX", startX }, { "StartY", startY }, { "TargetWidth", targetWidth }, { "TargetHeight", targetHeight } } }).ToList();
            return result;
        }

        private static (float[] r, float[] g, float[] b, float[] a) ExtractFloatChannels(IPicture source)
        {
            if (source is IPicture<ushort> p16)
            {
                return (p16.r.Select(Convert.ToSingle).ToArray(), p16.g.Select(Convert.ToSingle).ToArray(), p16.b.Select(Convert.ToSingle).ToArray(), p16.a ?? Enumerable.Repeat(1f, p16.Pixels).ToArray());
            }

            if (source is IPicture<byte> p8)
            {
                return (p8.r.Select(Convert.ToSingle).ToArray(), p8.g.Select(Convert.ToSingle).ToArray(), p8.b.Select(Convert.ToSingle).ToArray(), p8.a ?? Enumerable.Repeat(1f, p8.Pixels).ToArray());
            }

            throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
        }

        private static IPicture BuildPicture(IPicture source, int width, int height, float[] r, float[] g, float[] b, float[] a)
        {
            if (source.BitPerPixel == 16)
            {
                var picture = new Picture16bpp(width, height)
                {
                    Tag = source.Tag,
                    HasAlphaChannel = true,
                };
                picture.r = r.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.g = g.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.b = b.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.a = a.Select(v => Math.Clamp(v, 0f, 1f)).ToArray();
                return picture;
            }

            if (source.BitPerPixel == 8)
            {
                var picture = new Picture8bpp(width, height)
                {
                    Tag = source.Tag,
                    HasAlphaChannel = true,
                };
                picture.r = r.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.g = g.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.b = b.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.a = a.Select(v => Math.Clamp(v, 0f, 1f)).ToArray();
                return picture;
            }

            throw new NotSupportedException($"Specific pixel-mode is not supported.");
        }
    }
}
