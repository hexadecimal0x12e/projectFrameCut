using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Effect
{


    /// <summary>
    /// The Render-side provider of the Place effect.
    /// </summary>
    public class PlaceEffect_IPicture : INormalEffect
    {

        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Place";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string Id { get; set; } = string.Empty;

        public int StartX { get; set; }
        public int StartY { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.IPicture;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY"
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "StartX", "int" },
            { "StartY", "int" },
        };

        public string TypeName => "Place";
        public string? BindedEffectProvidingSystemID { get; set; }

        void IExtensibleObject.Initialize()
        {
            Log("Place and Resize effects are deprecated. Consider migrate to IClipPositionProvider.", "warn");
        }

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            if (parameters.Count != ParametersNeeded.Count)
            {
                throw new ArgumentException("Too many parameters provided.");
            }

            var effect = new PlaceEffect_IPicture
            {
                StartX = DynamicParam.ToInt32(parameters.GetValueOrDefault("StartX")),
                StartY = DynamicParam.ToInt32(parameters.GetValueOrDefault("StartY")),
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
            {
                throw new ArgumentException("targetWidth and targetHeight must be positive");
            }

            int placeX = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartX"), StartX);
            int placeY = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartY"), StartY);
            int startX = placeX;
            int startY = placeY;
            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                startX = (int)Math.Round((double)startX * targetWidth / RelativeWidth);
                startY = (int)Math.Round((double)startY * targetHeight / RelativeHeight);
            }

            return PictureEffectChannels.MapHdr(projectFrameCut.Drawing.Effect.PlaceEffect.Process(source, startX, startY, targetWidth, targetHeight), source, (x, y) => (x - startX, y - startY));
        }

    }

    public class PlaceEffectProvider : EffectProviderBase
    {
        public PlaceEffectProvider()
        {
            Name = "Place";
            SetField("StartX", 0);
            SetField("StartY", 0);
        }

        public override string TypeName => "Place";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video | EffectTarget.IsNotVisibleInEffectEditor | EffectTarget.IsNotVisibleInNewEffectSelector;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("StartX", EffectArgumentFieldType.Integer, "0"),
                Field("StartY", EffectArgumentFieldType.Integer, "0")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.HwAcceleration, EffectImplementType.IPicture];


    }
}
