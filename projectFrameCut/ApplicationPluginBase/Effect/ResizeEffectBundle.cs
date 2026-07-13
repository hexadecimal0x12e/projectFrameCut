using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class ResizeEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Resize";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "Width", 1920 },
            { "Height", 1080 },
            { "PreserveAspectRatio", true },
        };

        private static readonly Dictionary<string, EffectBundleSettableFields> s_settableFields = new()
        {
            { "Width", EffectBundleHelper.IntField("Width", "Width", "Output width", 1920, 1) },
            { "Height", EffectBundleHelper.IntField("Height", "Height", "Output height", 1080, 1) },
            { "PreserveAspectRatio", EffectBundleHelper.BoolField("PreserveAspectRatio", "Preserve Aspect Ratio", "Maintain aspect ratio when resizing", true) }
        };

        public List<string> ParametersNeeded => ResizeEffect_IPicture.ParametersNeeded;
        public Dictionary<string, string> ParametersType => ResizeEffect_IPicture.ParametersType;
        public Dictionary<string, EffectBundleSettableFields> SettableFields => s_settableFields;

        public string TypeName => "Resize";
        public bool IsNormalEffect => true;
        public bool IsContinuousEffect => false;
        public bool IsBindableEffect => false;
        public EffectType TypeOfEffect => EffectType.NormalEffect;
        public EffectTarget Target => EffectTarget.Video | EffectTarget.IsNotVisibleInEffectEditor | EffectTarget.IsNotVisibleInNewEffectSelector;

        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;
        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;
        public List<Guid>? BindedInputIds { get; set; }
        public bool IsMultiInput => false;
        public string InputAnchorDisplayName => string.Empty;
        public string[]? InputAnchorsDisplayName => null;
        public string OutputAnchorDisplayName => string.Empty;

        public bool Enabled { get; set; } = true;
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public IEffectFactory[] Create()
        {
            var factory = new ResizeEffectFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            int width = Math.Max(1, EffectBundleHelper.GetInt(Parameters, "Width", 1920));
            int height = Math.Max(1, EffectBundleHelper.GetInt(Parameters, "Height", 1080));
            bool preserveAspectRatio = EffectBundleHelper.GetBool(Parameters, "PreserveAspectRatio", true);

            var panel = new PropertyPanelBuilder();
            panel.AddPositionTupleInputBox("resize", new SingleLineLabel(EffectBundleHelper.L("_OutputSize", "Output Size")), PositionTupleMode.WH, (0, 0, width, height));
            panel.AddCheckbox(
                "PreserveAspectRatio",
                EffectBundleHelper.L("Effect_Resize_PreserveAspectRatio", "Preserve Aspect Ratio"),
                preserveAspectRatio);

            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            switch (args.Id)
            {
                case "resize_W":
                    if (EffectBundleHelper.TrySetInt(Parameters, "Width", args.Value))
                        Parameters["Width"] = Math.Max(1, EffectBundleHelper.GetInt(Parameters, "Width", 1920));
                    break;
                case "resize_H":
                    if (EffectBundleHelper.TrySetInt(Parameters, "Height", args.Value))
                        Parameters["Height"] = Math.Max(1, EffectBundleHelper.GetInt(Parameters, "Height", 1080));
                    break;
                case "PreserveAspectRatio":
                    EffectBundleHelper.TrySetBool(Parameters, "PreserveAspectRatio", args.Value);
                    break;
            }

            return Parameters;
        }

        public bool HandleSettableFieldsChange(EffectBundleSettableFields field, object value, out string feedback)
        {
            return EffectBundleHelper.HandleSettableFieldChange(Parameters, field, value, out feedback);
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleHelper.L("DisplayName_Effect_Resize", "Resize"),
                Description = EffectBundleHelper.L("Description_Effect_Resize", "Resize the frame output width and height."),
                Thumbnail = ImageSource.FromFile(FileSystemService.GetAppPackageFileSync("EffectSample", "resize.png"))
            };
        }
    }
}