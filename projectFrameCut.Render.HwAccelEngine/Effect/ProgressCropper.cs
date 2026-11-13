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
            {
                "StartX",
                "int"
            },
            {
                "StartY",
                "int"
            },
            {
                "Height",
                "int"
            },
            {
                "Width",
                "int"
            },
            {
                "Angle",
                "float"
            },
            {
                "CropList",
                "string"
            },
        };

        public IPicture Render(IPicture source, float progress, int targetWidth, int targetHeight) => ResidentEffectResult.Download(EffectPictureProcessing.Render(this, source, targetWidth, targetHeight, progress));
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
