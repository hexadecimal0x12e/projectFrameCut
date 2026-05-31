using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public class RotationEffect_ImageSharp : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Rotation";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        /// <summary>
        /// 旋转角度（度），顺时针为正方向。
        /// </summary>
        public float Angle { get; init; }

        /// <summary>
        /// 是否扩展画布以容纳旋转后的完整图像。
        /// false 时保持原始画布大小（旋转超出部分被裁剪）。
        /// </summary>
        public bool ExpandCanvas { get; init; } = false;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "Angle", Angle },
            { "ExpandCanvas", ExpandCanvas },
        };

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => true;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.ImageSharp;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Angle",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Angle", "float" },
            { "ExpandCanvas", "bool" },
        };

        public string TypeName => "Rotation";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.ImageSharp)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            float angle = Convert.ToSingle(parameters["Angle"]);

            bool expandCanvas = false;
            if (parameters.TryGetValue("ExpandCanvas", out var expandVal))
            {
                expandCanvas = Convert.ToBoolean(expandVal);
            }

            return new RotationEffect_ImageSharp
            {
                Angle = angle,
                ExpandCanvas = expandCanvas,
                ImplementType = implementType,
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            return new RotationProcessStep(Angle, ExpandCanvas);
        }
    }

    public class RotationProcessStep : IPictureProcessStep
    {
        private TimeSpan? _elapsed;
        public string Name => "Rotation";
        public Dictionary<string, object?> Properties { get; set; }

        public float Angle { get; }
        public bool ExpandCanvas { get; }

        public RotationProcessStep(float angle, bool expandCanvas)
        {
            Angle = angle;
            ExpandCanvas = expandCanvas;
            Properties = new Dictionary<string, object?>
            {
                { nameof(Angle), Angle },
                { nameof(ExpandCanvas), ExpandCanvas },
            };
        }

        public IPicture Process(IPicture source)
        {
            var sw = Stopwatch.StartNew();

            int origWidth = source.Width;
            int origHeight = source.Height;

            var img = source.SaveToSixLaborsImage();
            img.Mutate(ctx => ctx.Rotate(Angle));

            if (!ExpandCanvas && (img.Width != origWidth || img.Height != origHeight))
            {
                // 居中裁剪回原始尺寸
                int cropX = Math.Max(0, (img.Width - origWidth) / 2);
                int cropY = Math.Max(0, (img.Height - origHeight) / 2);
                int cropW = Math.Min(img.Width - cropX, origWidth);
                int cropH = Math.Min(img.Height - cropY, origHeight);
                img.Mutate(ctx => ctx.Crop(new Rectangle(cropX, cropY, cropW, cropH)));

                // 若裁剪后仍小于原始尺寸（极少数情况），映射到原始画布中心
                if (img.Width < origWidth || img.Height < origHeight)
                {
                    int padLeft = (origWidth - img.Width) / 2;
                    int padTop = (origHeight - img.Height) / 2;
                    img.Mutate(ctx => ctx.Pad(origWidth, origHeight).Crop(new Rectangle(padLeft, padTop, origWidth, origHeight)));
                }
            }

            IPicture result = (int)source.BitPerPixel switch
            {
                8 => new Picture8bpp(img),
                16 => new Picture16bpp(img),
                _ => throw new NotSupportedException($"Specific pixel-mode is not supported.")
            };

            sw.Stop();
            _elapsed = sw.Elapsed;
            result.ProcessStack = source.ProcessStack.Append(GetProcessStack()).ToList();
            return result;
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            if (ExpandCanvas) return null;
            return ctx => ctx.Rotate(Angle);
        }

        public PictureProcessStack GetProcessStack() => new PictureProcessStack
        {
            Elapsed = _elapsed,
            OperationDisplayName = "Rotation",
            Operator = typeof(RotationProcessStep),
            ProcessingFuncStackTrace = new StackTrace(true),
            StepUsed = this,
            Properties = new Dictionary<string, object>
            {
                { nameof(Angle), Angle },
                { nameof(ExpandCanvas), ExpandCanvas },
            }
        };
    }

    public class RotationEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "Rotation";
        public EffectTarget Target => EffectTarget.Video;
        public List<string> ParametersNeeded { get; } = new List<string> { "Angle" };
        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Angle", "float" },
            { "ExpandCanvas", "bool" },
        };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.ImageSharp, EffectImplementType.IPicture };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }
            return implementType switch
            {
                EffectImplementType.ImageSharp => RotationEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object> { { "Angle", 0f } }, implementType),
                EffectImplementType.IPicture => RotationEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object> { { "Angle", 0f } }, implementType),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            parameters ??= new Dictionary<string, object> { { "Angle", 0f } };
            if (!parameters.ContainsKey("Angle")) parameters["Angle"] = 0f;
            return RotationEffect_ImageSharp.FromParametersDictionary(parameters);
        }
    }
}
