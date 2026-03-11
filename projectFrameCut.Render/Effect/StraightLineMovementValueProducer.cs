using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.Effect
{
    public class StraightLineMovementValueProducer : IBindableArgumentEffectValueProvider
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string? BindedArgumentProviderID { get; set; }

        public int StartX { get; set; }
        public int StartY { get; set; }
        public int EndX { get; set; }
        public int EndY { get; set; }

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => false;
        public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
        public string TypeName => "StraightLineMovementValueProducer";

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "StartX", StartX },
            { "StartY", StartY },
            { "EndX", EndX },
            { "EndY", EndY },
        };

        public static List<string> ParametersNeeded { get; } = StraightLineMovementValueProducerFactory.s_ParametersNeeded;
        public static Dictionary<string, string> ParametersType { get; } = StraightLineMovementValueProducerFactory.s_ParametersType;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            var effect = new StraightLineMovementValueProducer();
            if (parameters.TryGetValue("StartX", out var startX)) effect.StartX = Convert.ToInt32(startX);
            if (parameters.TryGetValue("StartY", out var startY)) effect.StartY = Convert.ToInt32(startY);
            if (parameters.TryGetValue("EndX", out var endX)) effect.EndX = Convert.ToInt32(endX);
            if (parameters.TryGetValue("EndY", out var endY)) effect.EndY = Convert.ToInt32(endY);
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public object GenerateValue(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            return new Func<double, System.Drawing.Point>((progress) =>
            {
                int x = (int)Math.Round(StartX + (EndX - StartX) * progress);
                int y = (int)Math.Round(StartY + (EndY - StartY) * progress);
                return new System.Drawing.Point(x, y);
            });
        }

        public bool IsValueValid(object value) => value is Func<double, System.Drawing.Point>;

        public void Initialize() { }

        public bool GenerateOnce => true;

        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectGroupID { get; set; }

        public string OutputAnchorName => "Point";
    }

    public class StraightLineMovementValueProducerFactory : IBindableEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "StraightLineMovementValueProducer";
        public static List<string> s_ParametersNeeded = new List<string>
        {
            "StartX",
            "StartY",
            "EndX",
            "EndY",
        };

        public static Dictionary<string, string> s_ParametersType = new Dictionary<string, string>
        {
            {"StartX", "int" },
            {"StartY", "int" },
            {"EndX", "int" },
            {"EndY", "int" },
        };
        public List<string> ParametersNeeded => s_ParametersNeeded;
        public Dictionary<string, string> ParametersType => s_ParametersType;

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.NotSpecified };


        public string? ID { get; set; }
        public string? BindedInputID { get; set; }
        public string[]? BindedInputIDs { get; set; }

        public IEffect BuildWithDefaultType(string? ID, string? BindedInputID, string[]? BindedInputIDs = null, Dictionary<string, object>? parameters = null)
        {
            return Build(SupportsImplementTypes[0], ID, BindedInputID, BindedInputIDs, parameters);
        }

        public IEffect Build(EffectImplementType implementType, string? ID, string? BindedInputID, string[]? BindedInputIDs = null, Dictionary<string, object>? parameters = null)
        {
            if (implementType != EffectImplementType.NotSpecified && !SupportsImplementTypes.Contains(implementType))
            {
                throw new ArgumentException($"ImplementType {implementType} is not supported.", nameof(implementType));
            }

            var e = parameters != null ? StraightLineMovementValueProducer.FromParametersDictionary(parameters) : new StraightLineMovementValueProducer();

            if (e is IBindableArgumentEffect be)
            {
                if (ID != null)
                {
                    be.Id = ID;
                }
                else if (parameters != null)
                {
                    throw new InvalidDataException("Invaild source ID.");
                }
                be.BindedArgumentProviderID = null!;
            }
            return e;
        }
    }
}
