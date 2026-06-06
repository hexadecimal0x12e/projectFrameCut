using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.Effect
{
    public class MaskApplier : IBindableArgumentEffectOneInputResultGenerator
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string? BindedArgumentProviderID { get; set; }

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.IPicture;
        public string TypeName => "MaskApplier";

        public Dictionary<string, object> Parameters => new Dictionary<string, object>();

        public static List<string> ParametersNeeded { get; } = new List<string>();
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>();

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters) => new MaskApplier();

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture GenerateResult(object source, uint index, IPicture frame, IComputer? computer, int targetWidth, int targetHeight)
        {
            if (source is not BitMaskPicture maskPic)
            {
                return frame;
            }

            return MaskEffect.Process(frame, maskPic);
        }

        public bool IsValueValid(object value) => value is BitMaskPicture;

        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectGroupID { get; set; }

        public string InputAnchorName => "Mask";

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public bool IsContinuous => false;

        public string OutputAnchorName => "Mask";
    }

    public class MaskApplierFactory : IBindableEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "MaskApplier";
        public EffectTarget Target => EffectTarget.Video;
        public List<string> ParametersNeeded => MaskApplier.ParametersNeeded;
        public Dictionary<string, string> ParametersType => MaskApplier.ParametersType;

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.IPicture };

        public string? ID { get; set; }
        public string? BindedInputID { get; set; }
        public string[]? BindedInputIDs { get; set; }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            return Build(SupportsImplementTypes[0], parameters);
        }

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (!SupportsImplementTypes.Contains(implementType))
            {
                throw new ArgumentException($"ImplementType {implementType} is not supported.", nameof(implementType));
            }

            return new MaskApplier { ImplementType = implementType };
        }

        public IEffect BuildWithDefaultType(string? ID, string? BindedInputID, string[]? BindedInputIDs = null, Dictionary<string, object>? parameters = null)
        {
            return new MaskApplier { ImplementType = EffectImplementType.IPicture };
        }

        public IEffect Build(EffectImplementType implementType, string? ID, string? BindedInputID, string[]? BindedInputIDs = null, Dictionary<string, object>? parameters = null)
        {
            return new MaskApplier { ImplementType = implementType == EffectImplementType.NotSpecified ? EffectImplementType.IPicture : implementType };
        }
    }
}
