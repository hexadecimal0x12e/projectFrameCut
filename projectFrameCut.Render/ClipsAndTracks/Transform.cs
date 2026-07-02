using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.ClipsAndTracks
{
    public class TransformContainer : IClip
    {
        public ClipMode ClipType => ClipMode.TransformClip;
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";

        public Guid Id { get; init; }
        public string Name { get; init; }
        public string BindedSoundTrack { get; init; }
        public uint LayerIndex { get; init; }
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get; init; }
        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        public string? FilePath { get; set; }
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }

        public bool NeedFilePath => false;

        public JsonElement? TransformElement { get; set; } = null;

        [JsonIgnore]
        public ITransform? Transform
        {
            get
            {
                if (field is null && TransformElement is JsonElement e)
                {
                    field = PluginManager.CreateTransform(e);
                }
                return field;
            }
            set
            {
                field = value;
                TransformElement = value is null ? null : JsonSerializer.SerializeToElement(value, value.GetType());
            }
        }

        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
        public IMixture? MixtureInstance { get; set; }
        public ISourceReplacementEffect? AlternativeSource { get; set; }

        public void Dispose()
        {
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex) => throw new NotSupportedException("Use TransformProcesser.");

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB)
        {
            throw new NotSupportedException("Use TransformProcesser.");
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, IPicture.PicturePixelMode targetPPB)
        {
            throw new NotSupportedException("Use TransformProcesser.");
        }

        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
            if (TransformElement is JsonElement e)
            {
                Transform = PluginManager.CreateTransform(e);
            }

            Transform?.Init();
        }
    }

}
