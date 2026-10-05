using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Diagnostics;
using System.Text.Json;

namespace projectFrameCut.Render.Effect
{
    public class ProgressCropper_IPicture : IContinuousEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Crop";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "ProgressCrop";
        public EffectImplementType ImplementType => EffectImplementType.IPicture;
        public bool IsReorderable => true;
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;
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

        public IPicture Render(IPicture source, float progress, int targetWidth, int targetHeight)
        {
            int cropX = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartX"), StartX);
            int cropY = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartY"), StartY);
            int cropW = DynamicParam.Resolve(Parameters.GetValueOrDefault("Width"), Width);
            int cropH = DynamicParam.Resolve(Parameters.GetValueOrDefault("Height"), Height);
            float cropAngle = DynamicParam.Resolve(Parameters.GetValueOrDefault("Angle"), Angle);
            var crop = ResolveCrop(progress, targetWidth, targetHeight, cropX, cropY, cropW, cropH, cropAngle);
            return CropEffectShared.CropAndProcess(source, crop.StartX, crop.StartY, crop.Width, crop.Height, crop.Angle);
        }

        public IEffect WithParameters(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            return new ProgressCropper_IPicture
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

        private CropData ResolveCrop(float progress, int targetWidth, int targetHeight, int startX, int startY, int width, int height, float angle)
        {
            CropData crop = CropList.Count > 0
                ? CropEffectShared.GetCropForProgress(CropList, Math.Clamp(progress, 0.0, 1.0))
                : new CropData(0, startX, startY, width, height, angle);

            return CropEffectShared.Scale(crop, targetWidth, targetHeight, RelativeWidth, RelativeHeight);
        }
    }

    /// <summary>
    /// The Render-side provider of the ProgressCrop keyframed crop provider.
    /// </summary>
    public class ProgressCropProvider : EffectProviderBase
    {
        public ProgressCropProvider()
        {
            Name = "ProgressCrop";
            SetField("StartX", 0);
            SetField("StartY", 0);
            SetField("Width", 1280);
            SetField("Height", 720);
            SetField("Angle", 0f);
            SetField("CropList", "[]");
        }

        public override string TypeName => "ProgressCrop";

        public override EffectType TypeOfEffect => EffectType.ContinuousEffect;

        public override EffectTarget Target => EffectTarget.Video | EffectTarget.IsKeyFramed | EffectTarget.IsNotVisibleInNewEffectSelector;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("StartX", EffectArgumentFieldType.Integer, "0"),
                Field("StartY", EffectArgumentFieldType.Integer, "0"),
                Field("Width", EffectArgumentFieldType.Integer, "1280", min: "1"),
                Field("Height", EffectArgumentFieldType.Integer, "720", min: "1"),
                Field("Angle", EffectArgumentFieldType.Numeric, "0", min: "-180", max: "180"),
                Field("CropList", EffectArgumentFieldType.String, "[]", remarks: "Serialized CropData array as JSON string")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.HwAcceleration, EffectImplementType.IPicture];


    }
}
