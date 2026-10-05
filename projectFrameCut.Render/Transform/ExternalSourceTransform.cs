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
    public class ExternalSourceTransform : IContinuousTransform, IDisposable
    {
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";

        public TransformDefinition Definition => TransformDefinition.Clip | TransformDefinition.SupportTwoInput;
        public TransformSide Side { get; set; }

        public string TypeName => "ExternalSourceTransform";

        public string Name { get; init; }
        public Guid BindedLeftClip { get; set; }
        public Guid BindedRightClip { get; set; }
        public uint Duration { get; set; }


        public string SourcePath { get; set; }
        public Dictionary<string, object> Parameters
        {
            get => new() { [nameof(SourcePath)] = SourcePath };
            set { if (value.TryGetValue(nameof(SourcePath), out var path)) SourcePath = path.ToString()!; }
        }
        public Dictionary<string, string> ParametersType => new() { [nameof(SourcePath)] = "string" };
        public List<string> ParametersNeeded => [nameof(SourcePath)];
        [JsonIgnore]
        public IVideoSource source { get; set; }

        void ITransform.Init()
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(SourcePath, nameof(SourcePath));
            source?.Dispose();
            source = PluginManager.CreateVideoSource(SourcePath);
        }

        public void Dispose() => source?.Dispose();

        public IPicture GetFrame(IPicture left, IPicture right, double progress, int targetWidth, int targetHeight) => source.GetFrame((uint)(progress * Math.Max(0, source.TotalFrames - 1))).Resize(targetWidth, targetHeight, true);
    }
}
