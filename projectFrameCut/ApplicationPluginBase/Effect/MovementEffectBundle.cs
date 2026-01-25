using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class MovementEffectBundle : IEffectBundle
    {
        private List<string> s_ParametersNeeded = new List<string>
        {
            "StartX",
            "StartY",
            "EndX",
            "EndY",
            "Duration",
        };
        private Dictionary<string, string> s_ParametersType = new Dictionary<string, string>
        {
            {"StartX", "int" },
            {"StartY", "int" },
            {"EndX", "int" },
            {"EndY", "int" },
            {"Duration", "int" },
        };

        public string TypeName => "Movement";

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public bool IsNormalEffect => false;

        public bool IsContinuousEffect => true;

        public bool IsBindableEffect => true;

        public string Id { get; set; }
        public string Name { get; set; }
        public int Index { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "StartX", 0d },
            { "StartY", 0d },
            { "EndX", 0d },
            { "EndY", 0d },
            { "Duration", 0d },

        };

        public List<string> ParametersNeeded => s_ParametersNeeded;

        public Dictionary<string, string> ParametersType => s_ParametersType;

        public IEffectFactory[] Create()
        {
            var id = Guid.NewGuid().ToString();
            var prod = new StraightLineMovementValueProducerFactory()
            {
                ID = id
            };
            var move = new PointPlacerFactory()
            {
                BindedInputID = id
            };
            return [prod, move];
        }

        public PropertyPanelBuilder CreateUI()
        {
            PropertyPanelBuilder ppb = new();
            ppb.AddEntry("StartX", "Start X", Parameters["StartX"].ToString() ?? "0", "The starting X position.");
            ppb.AddEntry("StartY", "Start Y", Parameters["StartY"].ToString() ?? "0", "The starting Y position.");
            ppb.AddEntry("EndX", "End X", Parameters["EndX"].ToString() ?? "0", "The ending X position.");
            ppb.AddEntry("EndY", "End Y", Parameters["EndY"].ToString() ?? "0", "The ending Y position.");
            ppb.AddSlider("Duration", "Duration of movement", 1000, 100, 10000);

            return ppb;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            Parameters[args.Id] = double.Parse(args.Value as string);
            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = Name,
                Description = "Moves an element from a starting position to an ending position over a specified duration.",
                Thumbnail = null,
                VideoThumbnail = null
            };
        }
    }
}
