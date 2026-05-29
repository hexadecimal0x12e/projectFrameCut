using projectFrameCut.Drawing.Processing.Resizing;
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

        public string SourcePath { get; set; }
        [JsonIgnore]
        public IVideoSource source { get; set; }

        void ITransform.Init()
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(SourcePath, nameof(SourcePath));
            source = PluginManager.CreateVideoSource(SourcePath);
        }

        public IPicture GetFrame(IPicture left, IPicture right, double progress, IComputer? computer, int targetWidth, int targetHeight) => source.GetFrame((uint)(progress * source.TotalFrames), false).Resize(targetWidth, targetHeight, true);
    }
}
