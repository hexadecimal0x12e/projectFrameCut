using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Effect
{


    public class ResizeEffect_IPicture : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string Id { get; set; }


        public int Height { get; init; }
        public int Width { get; init; }
        public bool PreserveAspectRatio { get; init; } = true;
        public string? BindedEffectProvidingSystemID { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();


        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.IPicture;
        public bool IsReorderable => true;


        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Height",
            "Width",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"Height", "int" },
            {"Width", "int" },
            {"PreserveAspectRatio", "bool" },
        };

        public string TypeName => "Resize";


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

            bool preserve = false;
            if (parameters.TryGetValue("PreserveAspectRatio", out var val))
            {
                preserve = DynamicParam.ToBool(val);
            }

            var effect = new ResizeEffect_IPicture
            {
                Height = DynamicParam.ToInt32(parameters.GetValueOrDefault("Height")),
                Width = DynamicParam.ToInt32(parameters.GetValueOrDefault("Width")),
                PreserveAspectRatio = preserve,
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, int targetWidth, int targetHeight)
        {
            int resizeWidth = DynamicParam.Resolve(Parameters.GetValueOrDefault("Width"), Width);
            int resizeHeight = DynamicParam.Resolve(Parameters.GetValueOrDefault("Height"), Height);
            bool preserveAspectRatio = DynamicParam.Resolve(Parameters.GetValueOrDefault("PreserveAspectRatio"), PreserveAspectRatio);
            int width = resizeWidth;
            int height = resizeHeight;

            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                width = Math.Max(1, (int)Math.Round((double)resizeWidth * targetWidth / RelativeWidth, MidpointRounding.AwayFromZero));
                height = Math.Max(1, (int)Math.Round((double)resizeHeight * targetHeight / RelativeHeight, MidpointRounding.AwayFromZero));
            }
            else
            {
                width = Math.Max(1, width);
                height = Math.Max(1, height);
            }

            var resizer = new BilinearPictureResizer();
            return source switch
            {
                IHDRPicture<ushort> hdr => resizer.Resize(hdr, width, height, preserveAspectRatio),
                IPicture<ushort> p16 => resizer.Resize(p16, width, height, preserveAspectRatio),
                IPicture<byte> p8 => resizer.Resize(p8, width, height, preserveAspectRatio),
                _ => throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}")
            };
        }

    }

    /// <summary>
    /// The Render-side provider of the Resize effect.
    /// </summary>
    public class ResizeEffectProvider : EffectProviderBase
    {
        public ResizeEffectProvider()
        {
            Name = "Resize";
            SetField("Width", 1920);
            SetField("Height", 1080);
            SetField("PreserveAspectRatio", true);
        }

        public override string TypeName => "Resize";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video | EffectTarget.IsNotVisibleInEffectEditor | EffectTarget.IsNotVisibleInNewEffectSelector;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Width", EffectArgumentFieldType.Integer, "1920", min: "1"),
                Field("Height", EffectArgumentFieldType.Integer, "1080", min: "1"),
                Field("PreserveAspectRatio", EffectArgumentFieldType.Boolean, "true")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture, EffectImplementType.HwAcceleration];


    }
}
