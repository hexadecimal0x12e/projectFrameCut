using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.Effect
{
    public class ProgressPlacer : IContinuousClipPositionProvider, IDynamicArgumentsEffect
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "ProgressPlacer";

        public string Name { get; set; }
        public string Id { get; set; }
        public int Index { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        // ProgressPlacer's only parameter is the composite ProgressList; it cannot be expressed as a
        // single Func<T> dynamic value, so the interface is implemented for API uniformity only.
        public IReadOnlyDictionary<string, Func<object?>>? DynamicProviders { get; set; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "ProgressList", JsonSerializer.Serialize(ProgressList) }
        };

        public bool Enabled { get; set; } = true;
        public bool IsReorderable => true;
        public string? BindedEffectGroupID { get; set; }

        public List<ProgressData> ProgressList { get; set; } = new List<ProgressData>();

        public ClipPositionTuple GetPosition(IClip source, uint index, int targetWidth, int targetHeight)
        {
            if (ProgressList is null || ProgressList.Count == 0)
            {
                return source.PositionTuple;
            }

            double progress = GetProgress(source, index);
            var position = GetPositionForProgress(progress);
            position = NormalizePosition(position, source);
            return ScalePosition(position, targetWidth, targetHeight);
        }

        public IEffect WithParameters(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            return new ProgressPlacer
            {
                ProgressList = ParseProgressList(parameters.TryGetValue("ProgressList", out var list) ? list : null)
            };
        }

        void IEffect.Initialize()
        {
            if (ProgressList is null || ProgressList.Count <= 1)
            {
                return;
            }

            ProgressList.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        private double GetProgress(IClip source, uint index)
        {
            uint effectiveDuration = source.GetEffectiveDuration();
            if (effectiveDuration == 0)
            {
                return 1.0;
            }

            uint startFrame = source.StartFrame;
            if (index <= startFrame)
            {
                return 0.0;
            }

            ulong endExclusive = (ulong)startFrame + effectiveDuration;
            if ((ulong)index >= endExclusive)
            {
                return 1.0;
            }

            return (double)(index - startFrame) / effectiveDuration;
        }

        private ClipPositionTuple GetPositionForProgress(double progress)
        {
            if (ProgressList.Count == 1)
            {
                return ProgressList[0].Position;
            }

            if (progress <= ProgressList[0].Index)
            {
                return ProgressList[0].Position;
            }

            int lastIndex = ProgressList.Count - 1;
            if (progress >= ProgressList[lastIndex].Index)
            {
                return ProgressList[lastIndex].Position;
            }

            for (int i = 1; i < ProgressList.Count; i++)
            {
                var current = ProgressList[i];
                if (progress <= current.Index)
                {
                    var previous = ProgressList[i - 1];
                    double span = current.Index - previous.Index;
                    if (span <= 0)
                    {
                        return current.Position;
                    }

                    double t = (progress - previous.Index) / span;
                    return Lerp(previous.Position, current.Position, t);
                }
            }

            return ProgressList[lastIndex].Position;
        }

        private ClipPositionTuple Lerp(ClipPositionTuple from, ClipPositionTuple to, double t)
        {
            int x = (int)Math.Round(from.TargetX + (to.TargetX - from.TargetX) * t);
            int y = (int)Math.Round(from.TargetY + (to.TargetY - from.TargetY) * t);
            int w = (int)Math.Round(from.TargetWidth + (to.TargetWidth - from.TargetWidth) * t);
            int h = (int)Math.Round(from.TargetHeight + (to.TargetHeight - from.TargetHeight) * t);
            bool isDelta = from.IsDelta == to.IsDelta && from.IsDelta;
            return new ClipPositionTuple(x, y, w, h, isDelta);
        }

        private ClipPositionTuple NormalizePosition(ClipPositionTuple position, IClip source)
        {
            if (!position.IsDelta)
            {
                int width = position.TargetWidth <= 0 ? source.TargetWidth : position.TargetWidth;
                int height = position.TargetHeight <= 0 ? source.TargetHeight : position.TargetHeight;
                return new ClipPositionTuple(position.TargetX, position.TargetY, width, height, false);
            }

            return position;
        }

        private ClipPositionTuple ScalePosition(ClipPositionTuple position, int targetWidth, int targetHeight)
        {
            if (RelativeWidth <= 0 || RelativeHeight <= 0 || (RelativeWidth == targetWidth && RelativeHeight == targetHeight))
            {
                return position;
            }

            int x = (int)Math.Round((double)position.TargetX * targetWidth / RelativeWidth);
            int y = (int)Math.Round((double)position.TargetY * targetHeight / RelativeHeight);
            int w = (int)Math.Round((double)position.TargetWidth * targetWidth / RelativeWidth);
            int h = (int)Math.Round((double)position.TargetHeight * targetHeight / RelativeHeight);
            return new ClipPositionTuple(x, y, w, h, position.IsDelta);
        }

        private static List<ProgressData> ParseProgressList(object? value)
        {
            if (value is null)
            {
                return new List<ProgressData>();
            }

            if (value is List<ProgressData> list)
            {
                return new List<ProgressData>(list);
            }

            if (value is ProgressData[] array)
            {
                return new List<ProgressData>(array);
            }

            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    var json = element.GetString();
                    return string.IsNullOrWhiteSpace(json)
                        ? new List<ProgressData>()
                        : JsonSerializer.Deserialize<List<ProgressData>>(json) ?? new List<ProgressData>();
                }

                return JsonSerializer.Deserialize<List<ProgressData>>(element.GetRawText()) ?? new List<ProgressData>();
            }

            if (value is string jsonString)
            {
                return string.IsNullOrWhiteSpace(jsonString)
                    ? new List<ProgressData>()
                    : JsonSerializer.Deserialize<List<ProgressData>>(jsonString) ?? new List<ProgressData>();
            }

            throw new ArgumentException("ProgressList parameter is invalid.", nameof(value));
        }
    }

    public class ProgressPlacerFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "ProgressPlacer";

        public EffectTarget Target => EffectTarget.Video;

        public List<string> ParametersNeeded { get; } = new List<string>();

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "ProgressList", "string" }
        };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.NotSpecified };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            return BuildWithDefaultType(parameters);
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            parameters ??= new Dictionary<string, object>();
            if (!parameters.ContainsKey("ProgressList"))
            {
                parameters["ProgressList"] = "[]";
            }

            return new ProgressPlacer().WithParameters(parameters);
        }
    }

    public record struct ProgressData(double Index, ClipPositionTuple Position);



    /// <summary>
    /// The Render-side provider of the ProgressPlacer keyframed clip-position provider.
    /// </summary>
    public class ProgressPlacerProvider : EffectProviderBase
    {
        public ProgressPlacerProvider()
        {
            Name = "ProgressPlacer";
            Parameters = new Dictionary<string, object>
            {
                { "ProgressList", "[]" }
            };
        }

        public override string TypeName => "ProgressPlacer";

        public override EffectType TypeOfEffect => EffectType.ContinuousClipPositionProvider;

        public override EffectTarget Target => EffectTarget.Video | EffectTarget.IsKeyFramed | EffectTarget.IsNotVisibleInNewEffectSelector;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("ProgressList", EffectArgumentFieldType.String, "[]", remarks: "Serialized ProgressData array as JSON string")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            return [new ProgressPlacerFactory().Build(implementType, parameters)];
        }
    }
}
