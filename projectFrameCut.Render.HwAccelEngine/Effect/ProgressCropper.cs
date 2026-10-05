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
    public partial class ProgressCropper_HwAccel : IContinuousEffect, IDisposable
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
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }
        public bool IsScoped { get; set; }
        public int StartX { get; init; }
        public int StartY { get; init; }
        public int Height { get; init; }
        public int Width { get; init; }
        public float Angle { get; init; }
        public List<CropData> CropList { get; set; } = new();
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;
        public string TypeName => "ProgressCrop";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;
        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
            "Height",
            "Width"
        };
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "StartX", "int" },
            { "StartY", "int" },
            { "Height", "int" },
            { "Width", "int" },
            { "Angle", "float" },
            { "CropList", "string" },
        };

        public IPicture Render(IPicture source, float progress, int targetWidth, int targetHeight)
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
                float angle = cropAngle;
                if (CropList.Count > 0)
                {
                    var crop = CropEffectShared.GetCropForProgress(CropList, Math.Clamp(progress, 0.0, 1.0));
                    crop = CropEffectShared.Scale(crop, targetWidth, targetHeight, RelativeWidth, RelativeHeight);
                    startX = crop.StartX;
                    startY = crop.StartY;
                    width = crop.Width;
                    height = crop.Height;
                    angle = crop.Angle;
                }
                else if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
                {
                    startX = (int)Math.Round((double)cropX * targetWidth / RelativeWidth);
                    startY = (int)Math.Round((double)cropY * targetHeight / RelativeHeight);
                    width = (int)Math.Round((double)cropW * targetWidth / RelativeWidth);
                    height = (int)Math.Round((double)cropH * targetHeight / RelativeHeight);
                }

                if (width <= 0 || height <= 0)
                {
                    throw new ArgumentException("Width and Height must be positive");
                }

                if (startX >= source.Width || startY >= source.Height || startX + width <= 0 || startY + height <= 0)
                {
                    return PictureEffectChannels.PreserveHdr(CropEffectShared.CreateTransparent(width, height, source.BitPerPixel), source, new float[checked(width * height)]);
                }

                var safeRect = CropEffectShared.BuildSafeCropRect(startX, startY, width, height, source.Width, source.Height);
                var sw = Stopwatch.StartNew();
                var(r, g, b, a, sourceHasAlpha) = CropEffectShared.ExtractFloatChannels(source);
                FourChannelResult cropResult;
                cropResult = ComputeCrop(r, g, b, a, source.Width, source.Height, safeRect.X, safeRect.Y, safeRect.Width, safeRect.Height, angle);
                var result = CropEffectShared.BuildPicture(source, safeRect.Width, safeRect.Height, cropResult.R, cropResult.G, cropResult.B, cropResult.A, sourceHasAlpha || Math.Abs(angle) > float.Epsilon);
                result = HwAccelEffectHelper.WithBrightness(result, source, source is IHDRPicture<ushort> hdr ? ComputeCrop(hdr.Brightness, hdr.Brightness, hdr.Brightness, a, source.Width, source.Height, safeRect.X, safeRect.Y, safeRect.Width, safeRect.Height, angle).R : null);
                sw.Stop();
                result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack { Elapsed = sw.Elapsed, OperationDisplayName = "Crop (GPU)", Operator = typeof(ProgressCropper_HwAccel), ProcessingFuncStackTrace = new StackTrace(true), Properties = new Dictionary<string, object> { { "StartX", safeRect.X }, { "StartY", safeRect.Y }, { "Width", safeRect.Width }, { "Height", safeRect.Height }, { "Angle", angle } } }).ToList();
                return result;
            }
            catch (Exception ex)
            {
                Log(ex, "Execute ProgressCropper effect", "HwAccelEngine");
                throw;
            }
        }

        public IEffect WithParameters(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            return new ProgressCropper_HwAccel
            {
                Parameters = parameters,
                StartX = DynamicParam.ToInt32(parameters.GetValueOrDefault("StartX")),
                StartY = DynamicParam.ToInt32(parameters.GetValueOrDefault("StartY")),
                Height = DynamicParam.ToInt32(parameters.GetValueOrDefault("Height")),
                Width = DynamicParam.ToInt32(parameters.GetValueOrDefault("Width")),
                Angle = parameters.TryGetValue("Angle", out var angleVal) ? DynamicParam.ToFloat(angleVal) : 0f,
                CropList = CropEffectShared.ParseCropList(parameters.TryGetValue("CropList", out var list) ? list : null),
                RelativeWidth = RelativeWidth,
                RelativeHeight = RelativeHeight,
                Name = Name,
                Index = Index,
                Enabled = Enabled,
                StartPoint = StartPoint,
                EndPoint = EndPoint,
                IsScoped = IsScoped,
            };
        }

        public void Initialize()
        {
            if (CropList is not null && CropList.Count > 1)
            {
                CropList.Sort((a, b) => a.Index.CompareTo(b.Index));
            }
        }
    }
}
