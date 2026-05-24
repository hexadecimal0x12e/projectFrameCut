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
        public bool YieldProcessStep => true;
        public EffectImplementType ImplementType { get; set; } = EffectImplementType.ImageSharp;

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

            return EffectHelper.PlacePicture(frame, startX, startY, targetWidth, targetHeight, "PointPlacer", GetType());
        }

        public IPictureProcessStep GenerateResultStep(object source, uint index, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }


        public bool IsValueValid(object value)
        {
            return value is Func<double, System.Drawing.Point>;
        }

        public void Initialize() { }

        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectGroupID { get; set; }

        public string InputAnchorName => "Input";

        public bool IsContinuous => true;

        public string OutputAnchorName => "Point";
    }

    public class PointPlacerFactory : IBindableEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "PointPlacer";
        public EffectTarget Target => EffectTarget.Video;
        public List<string> ParametersNeeded => PointPlacer.ParametersNeeded;
        public Dictionary<string, string> ParametersType => PointPlacer.ParametersType;

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.ImageSharp, EffectImplementType.IPicture };


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

            var e = parameters != null ? PointPlacer.FromParametersDictionary(parameters) : new PointPlacer();

            if (e is PointPlacer pointPlacer)
            {
                pointPlacer.ImplementType = implementType == EffectImplementType.NotSpecified ? EffectImplementType.ImageSharp : implementType;
            }

            if (e is IBindableArgumentEffect be)
            {
                be.Id = Guid.NewGuid().ToString();

                if (BindedInputID != null)
                {
                    be.BindedArgumentProviderID = BindedInputID;
                }
                else if (parameters != null)
                {
                    throw new InvalidDataException("Invaild source ID.");
                }
            }
            return e;
        }
    }
}
