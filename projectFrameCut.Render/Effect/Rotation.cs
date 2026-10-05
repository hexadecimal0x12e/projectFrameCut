using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Diagnostics;

namespace projectFrameCut.Render.Effect
{
    public class RotationEffect_IPicture : INormalEffect
    {
        private TimeSpan? _elapsed;

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

        public Dictionary<string, object> Parameters { get; set; } = new();

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.IPicture;
        public bool IsReorderable => true;

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

            var effect = new RotationEffect_IPicture
            {
                Angle = angle,
                ExpandCanvas = expandCanvas,
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, int targetWidth, int targetHeight)
        {
            var sw = Stopwatch.StartNew();

            float angle = DynamicParam.Resolve(Parameters.GetValueOrDefault("Angle"), Angle);
            bool expandCanvas = DynamicParam.Resolve(Parameters.GetValueOrDefault("ExpandCanvas"), ExpandCanvas);
            var result = RotationEffect.Process(source, angle, expandCanvas);

            float radians = angle * MathF.PI / 180f;
            result = PictureEffectChannels.MapHdr(result, source, (x, y) =>
                ((x - result.Width / 2f) * MathF.Cos(radians) - (y - result.Height / 2f) * MathF.Sin(radians) + source.Width / 2f,
                 (x - result.Width / 2f) * MathF.Sin(radians) + (y - result.Height / 2f) * MathF.Cos(radians) + source.Height / 2f));
            sw.Stop();
            _elapsed = sw.Elapsed;
            result.ProcessStack = source.ProcessStack.Append(GetProcessStack(angle, expandCanvas)).ToList();
            return result;
        }

        private PictureProcessStack GetProcessStack(float angle, bool expandCanvas) => new PictureProcessStack
        {
            Elapsed = _elapsed,
            OperationDisplayName = "Rotation",
            Operator = typeof(RotationEffect_IPicture),
            ProcessingFuncStackTrace = new StackTrace(true),

            Properties = new Dictionary<string, object>
            {
                { nameof(Angle), angle },
                { nameof(ExpandCanvas), expandCanvas },
            }
        };
    }

    /// <summary>
    /// The Render-side provider of the Rotation effect.
    /// </summary>
    public class RotationEffectProvider : EffectProviderBase
    {
        public RotationEffectProvider()
        {
            Name = "Rotation";
            SetField("Angle", 0f);
            SetField("ExpandCanvas", false);
        }

        public override string TypeName => "Rotation";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Angle", EffectArgumentFieldType.Numeric, "0"),
                Field("ExpandCanvas", EffectArgumentFieldType.Boolean, "false")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture, EffectImplementType.HwAcceleration];


    }
}
