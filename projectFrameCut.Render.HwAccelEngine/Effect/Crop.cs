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
        { "Angle", "CropList" };
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "StartX", "int" },
            { "StartY", "int" },
            { "Height", "int" },
            { "Width", "int" },
            { "Angle", "float" },
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
        public IPicture Render(IPicture source, int targetWidth, int targetHeight)
        {
            try
            {
                using var scope = nativeLock.EnterScope();
                ObjectDisposedException.ThrowIf(disposed, this);
                int cropX = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartX"), StartX);
                int cropY = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartY"), StartY);
                int cropW = DynamicParam.Resolve(Parameters.GetValueOrDefault("Width"), Width);
                int cropH = DynamicParam.Resolve(Parameters.GetValueOrDefault("Height"), Height);
                float cropAngle = DynamicParam.Resolve(Parameters.GetValueOrDefault("Angle"), Angle);
                int startX = cropX;
                int startY = cropY;
                int width = cropW;
                int height = cropH;
                if (width <= 0 || height <= 0)
                {
                    throw new ArgumentException("Width and Height must be positive");
                }

                if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
                {
                    startX = (int)Math.Round((double)cropX * targetWidth / RelativeWidth);
                    startY = (int)Math.Round((double)cropY * targetHeight / RelativeHeight);
                    width = (int)Math.Round((double)cropW * targetWidth / RelativeWidth);
                    height = (int)Math.Round((double)cropH * targetHeight / RelativeHeight);
                }

                if (startX >= source.Width || startY >= source.Height || startX + width <= 0 || startY + height <= 0)
                {
                    return PictureEffectChannels.PreserveHdr(CropEffectShared.CreateTransparent(width, height, source.BitPerPixel), source, new float[checked(width * height)]);
                }

                var safeRect = CropEffectShared.BuildSafeCropRect(startX, startY, width, height, source.Width, source.Height);
                var sw = Stopwatch.StartNew();
                var(r, g, b, a, sourceHasAlpha) = ExtractFloatChannels(source);
                FourChannelResult cropResult;
                cropResult = ComputeCrop(r, g, b, a, source.Width, source.Height, safeRect.X, safeRect.Y, safeRect.Width, safeRect.Height, cropAngle);
                var result = BuildPicture(source, safeRect.Width, safeRect.Height, cropResult.R, cropResult.G, cropResult.B, cropResult.A, sourceHasAlpha || Math.Abs(cropAngle) > float.Epsilon);
                result = HwAccelEffectHelper.WithBrightness(result, source, source is IHDRPicture<ushort> hdr ? ComputeCrop(hdr.Brightness, hdr.Brightness, hdr.Brightness, a, source.Width, source.Height, safeRect.X, safeRect.Y, safeRect.Width, safeRect.Height, cropAngle).R : null);
                sw.Stop();
                result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack { Elapsed = sw.Elapsed, OperationDisplayName = "Crop (GPU)", Operator = typeof(CropEffect_HwAccel), ProcessingFuncStackTrace = new StackTrace(true), Properties = new Dictionary<string, object> { { "StartX", safeRect.X }, { "StartY", safeRect.Y }, { "Width", safeRect.Width }, { "Height", safeRect.Height }, { "Angle", cropAngle } } }).ToList();
                return result;
            }
            catch (Exception ex)
            {
                Log(ex, "Execute Crop effect", "HwAccelEngine");
                throw;
            }
        }

        private static (float[] r, float[] g, float[] b, float[] a, bool sourceHasAlpha) ExtractFloatChannels(IPicture source)
        {
            if (source is IPicture<ushort> p16)
            {
                return (p16.r.Select(Convert.ToSingle).ToArray(), p16.g.Select(Convert.ToSingle).ToArray(), p16.b.Select(Convert.ToSingle).ToArray(), p16.a ?? Enumerable.Repeat(1f, p16.Pixels).ToArray(), p16.HasAlphaChannel && p16.a is not null);
            }

            if (source is IPicture<byte> p8)
            {
                return (p8.r.Select(Convert.ToSingle).ToArray(), p8.g.Select(Convert.ToSingle).ToArray(), p8.b.Select(Convert.ToSingle).ToArray(), p8.a ?? Enumerable.Repeat(1f, p8.Pixels).ToArray(), p8.HasAlphaChannel && p8.a is not null);
            }

            throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
        }

        private static IPicture BuildPicture(IPicture source, int width, int height, float[] r, float[] g, float[] b, float[] a, bool keepAlpha)
        {
            if (source.BitPerPixel == 16)
            {
                var picture = new Picture16bpp(width, height)
                {
                    Tag = source.Tag,
                    HasAlphaChannel = keepAlpha
                };
                picture.r = r.Select(v => (ushort)Math.Clamp(v + 0.5f, 0f, 65535f)).ToArray();
                picture.g = g.Select(v => (ushort)Math.Clamp(v + 0.5f, 0f, 65535f)).ToArray();
                picture.b = b.Select(v => (ushort)Math.Clamp(v + 0.5f, 0f, 65535f)).ToArray();
                picture.a = keepAlpha ? a.Select(v => Math.Clamp(v, 0f, 1f)).ToArray() : null;
                return picture;
            }

            if (source.BitPerPixel == 8)
            {
                var picture = new Picture8bpp(width, height)
                {
                    Tag = source.Tag,
                    HasAlphaChannel = keepAlpha
                };
                picture.r = r.Select(v => (byte)Math.Clamp(v + 0.5f, 0f, 255f)).ToArray();
                picture.g = g.Select(v => (byte)Math.Clamp(v + 0.5f, 0f, 255f)).ToArray();
                picture.b = b.Select(v => (byte)Math.Clamp(v + 0.5f, 0f, 255f)).ToArray();
                picture.a = keepAlpha ? a.Select(v => Math.Clamp(v, 0f, 1f)).ToArray() : null;
                return picture;
            }

            throw new NotSupportedException($"Specific pixel-mode is not supported.");
        }
    }
}
