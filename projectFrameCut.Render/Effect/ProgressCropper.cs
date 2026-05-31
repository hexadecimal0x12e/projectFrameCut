using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Diagnostics;
using System.Text.Json;

namespace projectFrameCut.Render.Effect
{
    public class ProgressCropper_ImageSharp : IContinuousEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Crop";
        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "Crop";
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.ImageSharp;
        public bool YieldProcessStep => true;
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }
        public int StartX { get; init; }
        public int StartY { get; init; }
        public int Height { get; init; }
        public int Width { get; init; }
        public float Angle { get; init; }
        public List<CropData> CropList { get; set; } = new();

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "StartX", StartX },
            { "StartY", StartY },
            { "Height", Height },
            { "Width", Width },
            { "Angle", Angle },
            { "CropList", JsonSerializer.Serialize(CropList) },
        };

        public IPicture Render(IPicture source, uint index, IComputer? computer, int targetWidth, int targetHeight)
        {
            var crop = ResolveCrop(index, targetWidth, targetHeight);
            return new CropProcessStep(crop.StartX, crop.StartY, crop.Width, crop.Height, crop.Angle).Process(source);
        }

        public IEffect WithParameters(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            return new ProgressCropper_ImageSharp
            {
                StartX = Convert.ToInt32(parameters["StartX"]),
                StartY = Convert.ToInt32(parameters["StartY"]),
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
                Angle = parameters.TryGetValue("Angle", out var angleVal) ? Convert.ToSingle(angleVal) : 0f,
                CropList = CropEffectShared.ParseCropList(parameters.TryGetValue("CropList", out var list) ? list : null),
                RelativeWidth = RelativeWidth,
                RelativeHeight = RelativeHeight,
                Name = Name,
                Index = Index,
                Enabled = Enabled,
                StartPoint = StartPoint,
                EndPoint = EndPoint,
            };
        }

        public void Initialize()
        {
            if (CropList is not null && CropList.Count > 1)
            {
                CropList.Sort((a, b) => a.Index.CompareTo(b.Index));
            }
        }

        public IPictureProcessStep GetStep(IPicture source, uint index, int targetWidth, int targetHeight)
        {
            var crop = ResolveCrop(index, targetWidth, targetHeight);
            return new CropProcessStep(crop.StartX, crop.StartY, crop.Width, crop.Height, crop.Angle);
        }

        private CropData ResolveCrop(uint index, int targetWidth, int targetHeight)
        {
            CropData crop = CropList.Count > 0
                ? CropEffectShared.GetCropForProgress(CropList, EffectHelper.GetContinuesEffectProgress(index, StartPoint, EndPoint))
                : new CropData(0, StartX, StartY, Width, Height, Angle);

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

        public int StartX { get; init; }
        public int StartY { get; init; }
        public int Height { get; init; }
        public int Width { get; init; }
        public float Angle { get; init; }
        public List<CropData> CropList { get; set; } = new();

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "StartX", StartX },
            { "StartY", StartY },
            { "Height", Height },
            { "Width", Width },
            { "Angle", Angle },
            { "CropList", JsonSerializer.Serialize(CropList) },
        };

        public string? NeedComputer => "CropComputer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => false;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public string TypeName => "Crop";
        public string? BindedEffectGroupID { get; set; }
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

        public IPicture Render(IPicture source, uint index, IComputer? computer, int targetWidth, int targetHeight)
        {
            int startX = StartX;
            int startY = StartY;
            int width = Width;
            int height = Height;
            float angle = Angle;

            if (CropList.Count > 0)
            {
                var crop = CropEffectShared.GetCropForProgress(CropList, EffectHelper.GetContinuesEffectProgress(index, StartPoint, EndPoint));
                crop = CropEffectShared.Scale(crop, targetWidth, targetHeight, RelativeWidth, RelativeHeight);
                startX = crop.StartX;
                startY = crop.StartY;
                width = crop.Width;
                height = crop.Height;
                angle = crop.Angle;
            }
            else if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                startX = (int)Math.Round((double)StartX * targetWidth / RelativeWidth);
                startY = (int)Math.Round((double)StartY * targetHeight / RelativeHeight);
                width = (int)Math.Round((double)Width * targetWidth / RelativeWidth);
                height = (int)Math.Round((double)Height * targetHeight / RelativeHeight);
            }

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Width and Height must be positive");
            }

            if (Math.Abs(angle) > float.Epsilon)
            {
                return new CropProcessStep(startX, startY, width, height, angle).Process(source);
            }

            var safeRect = CropEffectShared.BuildSafeCropRect(startX, startY, width, height, source.Width, source.Height);
            if (computer is null)
            {
                return EffectHelper.CropPicture(source, safeRect.X, safeRect.Y, safeRect.Width, safeRect.Height, "Crop", typeof(ProgressCropper_HwAccel));
            }

            var sw = Stopwatch.StartNew();
            var (r, g, b, a, sourceHasAlpha) = CropEffectShared.ExtractFloatChannels(source);
            var resultArr = computer.Compute([
                r,
                g,
                b,
                a,
                source.Width,
                source.Height,
                safeRect.X,
                safeRect.Y,
                safeRect.Width,
                safeRect.Height
            ]);

            if (resultArr.Length != 4 ||
                resultArr[0] is not float[] rOut ||
                resultArr[1] is not float[] gOut ||
                resultArr[2] is not float[] bOut ||
                resultArr[3] is not float[] aOut)
            {
                throw new InvalidOperationException("CropComputer did not return expected channel buffers.");
            }

            var result = CropEffectShared.BuildPicture(source, safeRect.Width, safeRect.Height, rOut, gOut, bOut, aOut, sourceHasAlpha);
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
                StartX = Convert.ToInt32(parameters["StartX"]),
                StartY = Convert.ToInt32(parameters["StartY"]),
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
                Angle = parameters.TryGetValue("Angle", out var angleVal) ? Convert.ToSingle(angleVal) : 0f,
                CropList = CropEffectShared.ParseCropList(parameters.TryGetValue("CropList", out var list) ? list : null),
                RelativeWidth = RelativeWidth,
                RelativeHeight = RelativeHeight,
                Name = Name,
                Index = Index,
                Enabled = Enabled,
                StartPoint = StartPoint,
                EndPoint = EndPoint,
            };
        }

        public void Initialize()
        {
            if (CropList is not null && CropList.Count > 1)
            {
                CropList.Sort((a, b) => a.Index.CompareTo(b.Index));
            }
        }

        public IPictureProcessStep GetStep(IPicture source, uint index, int targetWidth, int targetHeight)
        {
            var crop = CropList.Count > 0
                ? CropEffectShared.GetCropForProgress(CropList, EffectHelper.GetContinuesEffectProgress(index, StartPoint, EndPoint))
                : new CropData(0, StartX, StartY, Width, Height, Angle);
            crop = CropEffectShared.Scale(crop, targetWidth, targetHeight, RelativeWidth, RelativeHeight);
            return new CropProcessStep(crop.StartX, crop.StartY, crop.Width, crop.Height, crop.Angle);
        }
    }

    public class CropEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "Crop";

        public EffectTarget Target => EffectTarget.Video;

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
            "Height",
            "Width",
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"StartX", "int" },
            {"StartY", "int" },
            {"Height", "int" },
            {"Width", "int" },
            {"Angle", "float" },
        };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.ImageSharp, EffectImplementType.HwAcceleration, EffectImplementType.IPicture };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            return implementType switch
            {
                EffectImplementType.ImageSharp => CropEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                EffectImplementType.HwAcceleration => CropEffect_HwAccel.FromParametersDictionary(parameters ?? new Dictionary<string, object>()),
                EffectImplementType.IPicture => CropEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters)
        {
            return CropEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>());
        }
    }

    public class ProgressCropperEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "Crop";

        public EffectTarget Target => EffectTarget.Video;

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
            "Height",
            "Width",
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "StartX", "int" },
            { "StartY", "int" },
            { "Height", "int" },
            { "Width", "int" },
            { "Angle", "float" },
            { "CropList", "string" },
        };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.ImageSharp, EffectImplementType.HwAcceleration, EffectImplementType.IPicture };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            return implementType switch
            {
                EffectImplementType.ImageSharp => BuildContinuous(parameters, EffectImplementType.ImageSharp),
                EffectImplementType.HwAcceleration => BuildContinuousHw(parameters),
                EffectImplementType.IPicture => BuildContinuous(parameters, EffectImplementType.IPicture),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters)
        {
            return BuildContinuous(parameters, EffectImplementType.ImageSharp);
        }

        private static IEffect BuildContinuous(Dictionary<string, object>? parameters, EffectImplementType implementType)
        {
            parameters ??= new Dictionary<string, object>();
            if (!parameters.ContainsKey("StartX")) parameters["StartX"] = 0;
            if (!parameters.ContainsKey("StartY")) parameters["StartY"] = 0;
            if (!parameters.ContainsKey("Height")) parameters["Height"] = 1;
            if (!parameters.ContainsKey("Width")) parameters["Width"] = 1;
            if (!parameters.ContainsKey("Angle")) parameters["Angle"] = 0f;
            if (!parameters.ContainsKey("CropList")) parameters["CropList"] = "[]";

            return new ProgressCropper_ImageSharp
            {
                StartX = Convert.ToInt32(parameters["StartX"]),
                StartY = Convert.ToInt32(parameters["StartY"]),
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
                Angle = Convert.ToSingle(parameters["Angle"]),
                CropList = CropEffectShared.ParseCropList(parameters["CropList"]),
                ImplementType = implementType,
            };
        }

        private static IEffect BuildContinuousHw(Dictionary<string, object>? parameters)
        {
            parameters ??= new Dictionary<string, object>();
            if (!parameters.ContainsKey("StartX")) parameters["StartX"] = 0;
            if (!parameters.ContainsKey("StartY")) parameters["StartY"] = 0;
            if (!parameters.ContainsKey("Height")) parameters["Height"] = 1;
            if (!parameters.ContainsKey("Width")) parameters["Width"] = 1;
            if (!parameters.ContainsKey("Angle")) parameters["Angle"] = 0f;
            if (!parameters.ContainsKey("CropList")) parameters["CropList"] = "[]";

            return new ProgressCropper_HwAccel
            {
                StartX = Convert.ToInt32(parameters["StartX"]),
                StartY = Convert.ToInt32(parameters["StartY"]),
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
                Angle = Convert.ToSingle(parameters["Angle"]),
                CropList = CropEffectShared.ParseCropList(parameters["CropList"]),
            };
        }
    }
}
