using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Linq;

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
        public bool YieldProcessStep => false;
        public EffectImplementType ImplementType => EffectImplementType.ImageSharp;
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

            var frameImg = frame.SaveToSixLaborsImage().CloneAs<Rgba32>();

            bool sizeMatch = maskPic.Width == frameImg.Width && maskPic.Height == frameImg.Height;

            frameImg.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    int maskRowOffset = y * maskPic.Width;

                    for (int x = 0; x < row.Length; x++)
                    {
                        bool keepPixel;
                        if (sizeMatch)
                        {
                            keepPixel = maskPic.r[maskRowOffset + x];
                        }
                        else
                        {
                            int maskX = (int)((float)x / frameImg.Width * maskPic.Width);
                            int maskY = (int)((float)y / frameImg.Height * maskPic.Height);
                            int maskIndex = maskY * maskPic.Width + maskX;
                            if (maskIndex < maskPic.r.Length)
                                keepPixel = maskPic.r[maskIndex];
                            else
                                keepPixel = true;
                        }

                        if (!keepPixel)
                        {
                            row[x] = new Rgba32(0, 0, 0, 0);
                        }
                    }
                }
            });

            return new Picture8bpp(frameImg);
        }

        public bool IsValueValid(object value) => value is BitMaskPicture;

        public IPictureProcessStep GenerateResultStep(object source, uint index, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }

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
        public List<string> ParametersNeeded => MaskApplier.ParametersNeeded;
        public Dictionary<string, string> ParametersType => MaskApplier.ParametersType;

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.ImageSharp };

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

            if (parameters != null)
            {
                return MaskApplier.FromParametersDictionary(parameters);
            }
            return new MaskApplier();
        }

        public IEffect BuildWithDefaultType(string? ID, string? BindedInputID, string[]? BindedInputIDs = null, Dictionary<string, object>? parameters = null)
        {
            return new MaskApplier();
        }

        public IEffect Build(EffectImplementType implementType, string? ID, string? BindedInputID, string[]? BindedInputIDs = null, Dictionary<string, object>? parameters = null)
        {
            return new MaskApplier();
        }
    }
}
