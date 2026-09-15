using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace projectFrameCut.Render.Effect
{
    public class PointPlacer : IBindableArgumentEffectOneInputResultGenerator
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string TypeName => "PointPlacer";
        public string? BindedArgumentProviderID { get; set; }

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType { get; set; } = EffectImplementType.IPicture;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>();

        public static List<string> ParametersNeeded { get; } = new List<string>();
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>();

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters) => new PointPlacer();

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public IPicture GenerateResult(object source, uint index, IPicture frame, IComputer? computer, int targetWidth, int targetHeight)
        {
            if (source is not Func<double, System.Drawing.Point> func) throw new ArgumentException("Source is not a valid callback function.", nameof(source));
            var prog = EffectHelper.GetContinuesEffectProgress(index, StartPoint, EndPoint);
            var pt = func.Invoke(prog);
            var x = pt.X;
            var y = pt.Y;

            int startX = x, startY = y;
            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                startX = (int)Math.Round((double)startX * targetWidth / RelativeWidth);
                startY = (int)Math.Round((double)startY * targetHeight / RelativeHeight);
            }

            return PlaceEffect.Process(frame, startX, startY, targetWidth, targetHeight);
        }

        public bool IsValueValid(object value)
        {
            return value is Func<double, System.Drawing.Point>;
        }

        public void Initialize() { }

        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectProvidingSystemID { get; set; }

        public string InputAnchorName => "Input";

        public bool IsContinuous => true;

        public string OutputAnchorName => "Point";
    }

    /// <summary>
    /// The Render-side provider of the PointPlacer bindable result generator.
    /// </summary>
    public class PointPlacerProvider : EffectProviderBase
    {
        public PointPlacerProvider()
        {
            Name = "PointPlacer";
        }

        public override string TypeName => "PointPlacer";

        public override EffectType TypeOfEffect => EffectType.BindableEffect;

        public override EffectTarget Target => EffectTarget.Video;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => Array.Empty<EffectArgumentFieldDescriptor>();

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            return [new PointPlacer()];
        }
    }
}
