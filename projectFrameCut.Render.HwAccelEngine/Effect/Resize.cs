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
            { "Height", "int" },
            { "Width", "int" },
            { "PreserveAspectRatio", "bool" },
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
        public IPicture Render(IPicture source, int targetWidth, int targetHeight)
        {
            try
            {
                using var scope = nativeLock.EnterScope();
                ObjectDisposedException.ThrowIf(disposed, this);
                var sw = Stopwatch.StartNew();
                int resizeWidth = DynamicParam.Resolve(Parameters.GetValueOrDefault("Width"), Width);
                int resizeHeight = DynamicParam.Resolve(Parameters.GetValueOrDefault("Height"), Height);
                bool preserveAspectRatio = DynamicParam.Resolve(Parameters.GetValueOrDefault("PreserveAspectRatio"), PreserveAspectRatio);
                int width = resizeWidth;
                int height = resizeHeight;
                if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
                {
                    width = Math.Max(1, (int)Math.Round((double)resizeWidth * targetWidth / RelativeWidth, MidpointRounding.AwayFromZero));
                    height = Math.Max(1, (int)Math.Round((double)resizeHeight * targetHeight / RelativeHeight, MidpointRounding.AwayFromZero));
                }
                else
                {
                    width = Math.Max(1, width);
                    height = Math.Max(1, height);
                }

                int destWidth = width;
                int destHeight = height;
                if (preserveAspectRatio)
                {
                    double sourceRatio = (double)source.Width / source.Height;
                    double targetRatio = (double)width / height;
                    if (sourceRatio > targetRatio)
                    {
                        destHeight = (int)Math.Round(width / sourceRatio, MidpointRounding.AwayFromZero);
                    }
                    else
                    {
                        destWidth = (int)Math.Round(height * sourceRatio, MidpointRounding.AwayFromZero);
                    }

                    destWidth = Math.Max(1, destWidth);
                    destHeight = Math.Max(1, destHeight);
                }

                float[] r, g, b, a;
                if (source is IPicture<ushort> p16)
                {
                    r = p16.r.Select(Convert.ToSingle).ToArray();
                    g = p16.g.Select(Convert.ToSingle).ToArray();
                    b = p16.b.Select(Convert.ToSingle).ToArray();
                    a = p16.a ?? Enumerable.Repeat(1f, p16.Pixels).ToArray();
                }
                else if (source is IPicture<byte> p8)
                {
                    r = p8.r.Select(Convert.ToSingle).ToArray();
                    g = p8.g.Select(Convert.ToSingle).ToArray();
                    b = p8.b.Select(Convert.ToSingle).ToArray();
                    a = p8.a ?? Enumerable.Repeat(1f, p8.Pixels).ToArray();
                }
                else
                {
                    throw new InvalidOperationException($"Source pixel type is not supported.");
                }

                IPicture result;
                if (source.BitPerPixel == 16)
                {
                    var r16 = ComputeResizeUshort(r, g, b, a, source.Width, source.Height, destWidth, destHeight);
                    var p = new Picture16bpp(destWidth, destHeight);
                    p.r = r16.R;
                    p.g = r16.G;
                    p.b = r16.B;
                    p.a = source.HasAlphaChannel ? r16.A : null;
                    p.HasAlphaChannel = source.HasAlphaChannel;
                    result = p;
                }
                else
                {
                    var r8 = ComputeResizeByte(r, g, b, a, source.Width, source.Height, destWidth, destHeight);
                    var p = new Picture8bpp(destWidth, destHeight);
                    p.r = r8.R;
                    p.g = r8.G;
                    p.b = r8.B;
                    p.a = source.HasAlphaChannel ? r8.A : null;
                    p.HasAlphaChannel = source.HasAlphaChannel;
                    result = p;
                }

                result.Tag = source.Tag;
                result = HwAccelEffectHelper.WithBrightness(result, source, source is IHDRPicture<ushort> hdr ? ComputeResizeFloat(hdr.Brightness, hdr.Brightness, hdr.Brightness, a, source.Width, source.Height, destWidth, destHeight).R : null);
                sw.Stop();
                result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack { Elapsed = sw.Elapsed, OperationDisplayName = $"Resize (GPU)", Operator = typeof(ResizeEffect_HwAccel), ProcessingFuncStackTrace = new StackTrace(true), Properties = new Dictionary<string, object> { { "Width", destWidth }, { "Height", destHeight }, { "PreserveAspectRatio", preserveAspectRatio } } }).ToList();
                return result;
            }
            catch (Exception ex)
            {
                Log(ex, "Execute Resize effect", "HwAccelEngine");
                throw;
            }
        }
    }
}
