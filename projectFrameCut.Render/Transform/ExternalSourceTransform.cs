using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.Transform
{
    public class ExternalSourceTransform : IContinuousTransform
    {
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";

        public string TypeName => "ExternalSourceTransform";

        public string Name { get; init; }
        public Guid BindedLeftClip { get; set; }
        public Guid BindedRightClip { get; set; }
        public uint Duration { get; set; }

        public string? NeedComputer => null;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        public List<string> ParametersNeeded => ["SourcePath"];

        public Dictionary<string, string> ParametersType => new Dictionary<string, string> { { "SourcePath", "string" } };

        [JsonIgnore]
        public IVideoSource source { get; set; }

        void ITransform.Init()
        {
            source = PluginManager.CreateVideoSource(Parameters["SourcePath"] as string);
        }

        public IPicture GetFrame(IPicture left, IPicture right, double progress, IComputer? computer, int targetWidth, int targetHeight) => source.GetFrame((uint)(progress * source.TotalFrames), false).Resize(targetWidth, targetHeight, true);
    }
}
