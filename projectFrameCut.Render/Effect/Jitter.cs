using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.Effect
{
    public class JitterEffect : IContinuousClipPositionProvider
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public EffectImplementType ImplementType { get; } = EffectImplementType.NotSpecified;

        public int MaxOffsetX { get; init; }
        public int MaxOffsetY { get; init; }
        public int Seed { get; init; } = 0;
        public string Direction { get; init; } = Direction_Both;

        public const string Direction_Both = "Both";
        public const string Direction_XOnly = "XOnly";
        public const string Direction_YOnly = "YOnly";

        public Dictionary<string, object> Parameters { get; set; } = new();

        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public string? NeedComputer => ImplementType == EffectImplementType.HwAcceleration ? "PlaceComputer" : null;
        public bool IsReorderable => true;

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public Random rnd = new();

        public static List<string> s_ParametersNeeded { get; } = new List<string>
        {
            "MaxOffsetX",
            "MaxOffsetY",
            "Direction",
        };

        public static Dictionary<string, string> s_ParametersType { get; } = new Dictionary<string, string>
        {
            { "MaxOffsetX", "int" },
            { "MaxOffsetY", "int" },
            { "Seed", "int" },
            { "Direction", "string" },
        };

        public Dictionary<string, string> ParametersType => s_ParametersType;
        public List<string> ParametersNeeded => s_ParametersNeeded;

        public string TypeName => "Jitter";

        public IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            int maxX = DynamicParam.ToInt32(parameters.GetValueOrDefault("MaxOffsetX"));
            int maxY = DynamicParam.ToInt32(parameters.GetValueOrDefault("MaxOffsetY"));
            int seed = 0;
            if (parameters.TryGetValue("Seed", out var s))
            {
                seed = DynamicParam.ToInt32(s);
            }
            string direction = parameters.TryGetValue("Direction", out var d) ? DynamicParam.ToStringValue(d) : Direction_Both;

            var effect = new JitterEffect
            {
                MaxOffsetX = maxX,
                MaxOffsetY = maxY,
                Seed = seed,
                Direction = direction,
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public void Initialize()
        {
            if (Seed != 0)
            {
                rnd = new(Seed);
            }
            else
            {
                rnd = new();
            }
        }


        public ClipPositionTuple GetPosition(IClip source, uint index, int targetWidth, int targetHeight)
        {
            string direction = DynamicParam.Resolve(Parameters.GetValueOrDefault("Direction"), Direction);
            int maxOffsetX = DynamicParam.Resolve(Parameters.GetValueOrDefault("MaxOffsetX"), MaxOffsetX);
            int maxOffsetY = DynamicParam.Resolve(Parameters.GetValueOrDefault("MaxOffsetY"), MaxOffsetY);
            int offX = 0, offY = 0;
            if (direction == Direction_Both || direction == Direction_XOnly)
            {
                if (maxOffsetX > 0)
                {
                    offX = rnd.Next(-maxOffsetX, maxOffsetX + 1);
                }
            }
            if (direction == Direction_Both || direction == Direction_YOnly)
            {
                if (maxOffsetY > 0)
                {
                    offY = rnd.Next(-maxOffsetY, maxOffsetY + 1);
                }
            }
            return new ClipPositionTuple(offX, offY, 0, 0, true);
        }

        public string? BindedEffectProvidingSystemID { get; set; }

    }

    /// <summary>
    /// The Render-side provider of the Jitter continuous effect.
    /// </summary>
    public class JitterEffectProvider : EffectProviderBase
    {
        public JitterEffectProvider()
        {
            Name = "Jitter";
            SetField("MaxOffsetX", 10);
            SetField("MaxOffsetY", 10);
            SetField("Direction", JitterEffect.Direction_Both);
            SetField("Seed", 0);
        }

        public override string TypeName => "Jitter";

        public override EffectType TypeOfEffect => EffectType.ContinuousEffect;

        public override EffectTarget Target => EffectTarget.Video;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("MaxOffsetX", EffectArgumentFieldType.Integer, "10", min: "0"),
                Field("MaxOffsetY", EffectArgumentFieldType.Integer, "10", min: "0"),
                Field("Direction", EffectArgumentFieldType.String, "Both", presetOptions: [JitterEffect.Direction_Both, JitterEffect.Direction_XOnly, JitterEffect.Direction_YOnly]),
                Field("Seed", EffectArgumentFieldType.Integer, "0")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            if (!parameters.ContainsKey("MaxOffsetX")) parameters["MaxOffsetX"] = 0;
            if (!parameters.ContainsKey("MaxOffsetY")) parameters["MaxOffsetY"] = 0;
            if (!parameters.ContainsKey("Seed")) parameters["Seed"] = 0;
            if (!parameters.ContainsKey("Direction")) parameters["Direction"] = JitterEffect.Direction_Both;

            return
            [
                new JitterEffect
                {
                    MaxOffsetX = Convert.ToInt32(parameters["MaxOffsetX"]),
                    MaxOffsetY = Convert.ToInt32(parameters["MaxOffsetY"]),
                    Seed = Convert.ToInt32(parameters["Seed"]),
                    Direction = parameters["Direction"].ToString() ?? JitterEffect.Direction_Both,
                }
            ];
        }
    }
}
