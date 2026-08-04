using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.HwAccelContracts;
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
        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "Crop";
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.IPicture;
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

        public IPicture Render(IPicture source, float progress, IComputer? computer, int targetWidth, int targetHeight)
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


    public class ProgressCropper_HwAccel : IContinuousEffect
    {
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

        public string? NeedComputer => "CropComputer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;
        public string TypeName => "Crop";
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

        public IPicture Render(IPicture source, float progress, IComputer? computer, int targetWidth, int targetHeight)
        {
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

            if (Math.Abs(angle) > float.Epsilon)
            {
                return CropEffectShared.CropAndProcess(source, startX, startY, width, height, angle);
            }

            if (startX >= source.Width || startY >= source.Height ||
                startX + width <= 0 || startY + height <= 0)
            {
                return CropEffectShared.CreateTransparent(width, height, source.BitPerPixel);
            }

            var safeRect = CropEffectShared.BuildSafeCropRect(startX, startY, width, height, source.Width, source.Height);
            if (computer is null)
            {
                return CropEffect.Process(source, safeRect.X, safeRect.Y, safeRect.Width, safeRect.Height);
            }

            var sw = Stopwatch.StartNew();
            var (r, g, b, a, sourceHasAlpha) = CropEffectShared.ExtractFloatChannels(source);

            FourChannelResult cropResult;
            if (computer is ICropComputer cc)
            {
                cropResult = cc.ComputeCrop(r, g, b, a, source.Width, source.Height,
                    safeRect.X, safeRect.Y, safeRect.Width, safeRect.Height);
            }
            else
            {
                var resultArr = computer.Compute([
                    r, g, b, a,
                    source.Width, source.Height,
                    safeRect.X, safeRect.Y, safeRect.Width, safeRect.Height
                ]);

                if (resultArr.Length != 4 ||
                    resultArr[0] is not float[] rOut ||
                    resultArr[1] is not float[] gOut ||
                    resultArr[2] is not float[] bOut ||
                    resultArr[3] is not float[] aOut)
                {
                    throw new InvalidOperationException("CropComputer did not return expected channel buffers.");
                }

                cropResult = new FourChannelResult(rOut, gOut, bOut, aOut);
            }

            var result = CropEffectShared.BuildPicture(source, safeRect.Width, safeRect.Height,
                cropResult.R, cropResult.G, cropResult.B, cropResult.A, sourceHasAlpha);
            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                Elapsed = sw.Elapsed,
                OperationDisplayName = "Crop (GPU)",
                Operator = typeof(ProgressCropper_HwAccel),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object>
                {
                    { "StartX", safeRect.X },
                    { "StartY", safeRect.Y },
                    { "Width", safeRect.Width },
                    { "Height", safeRect.Height },
                    { "Angle", angle }
                }
            }).ToList();
            return result;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            return new ProgressCropper_HwAccel
            {
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

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            if (!parameters.ContainsKey("StartX")) parameters["StartX"] = 0;
            if (!parameters.ContainsKey("StartY")) parameters["StartY"] = 0;
            if (!parameters.ContainsKey("Height")) parameters["Height"] = 1;
            if (!parameters.ContainsKey("Width")) parameters["Width"] = 1;
            if (!parameters.ContainsKey("Angle")) parameters["Angle"] = 0f;
            if (!parameters.ContainsKey("CropList")) parameters["CropList"] = "[]";

            return implementType switch
            {
                EffectImplementType.HwAcceleration =>
                [
                    new ProgressCropper_HwAccel
                    {
                        StartX = Convert.ToInt32(parameters["StartX"]),
                        StartY = Convert.ToInt32(parameters["StartY"]),
                        Height = Convert.ToInt32(parameters["Height"]),
                        Width = Convert.ToInt32(parameters["Width"]),
                        Angle = Convert.ToSingle(parameters["Angle"]),
                        CropList = CropEffectShared.ParseCropList(parameters["CropList"]),
                    }
                ],
                EffectImplementType.IPicture or EffectImplementType.NotSpecified =>
                [
                    new ProgressCropper_IPicture
                    {
                        StartX = Convert.ToInt32(parameters["StartX"]),
                        StartY = Convert.ToInt32(parameters["StartY"]),
                        Height = Convert.ToInt32(parameters["Height"]),
                        Width = Convert.ToInt32(parameters["Width"]),
                        Angle = Convert.ToSingle(parameters["Angle"]),
                        CropList = CropEffectShared.ParseCropList(parameters["CropList"]),
                        ImplementType = implementType == EffectImplementType.NotSpecified ? EffectImplementType.IPicture : implementType,
                    }
                ],
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }
    }
}
