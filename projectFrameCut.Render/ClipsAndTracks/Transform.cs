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

        public string Id { get; init; }
        public string Name { get; init; }
        public string BindedSoundTrack { get; init; }
        public uint LayerIndex { get; init; }
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get; init; }
        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }
        public string? FilePath { get; set; }

        public bool NeedFilePath => false;

        public JsonElement? TransformElement { get; set; } = null;

        [JsonIgnore]
        public ITransform? Transform { get; set { field = value; TransformElement = JsonSerializer.SerializeToElement(value); } }

        public void Dispose()
        {
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex) => throw new NotSupportedException("This clip requires a target frame size.");

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize = false)
        {
            throw new NotSupportedException("Use TransformProcesser.");
        }

        public void ReInit()
        {
            if (TransformElement is JsonElement e)
            {
                Transform = PluginManager.CreateTransform(e);
            }
            // If parameters were deserialized as JsonElement, convert them to their actual types
            if (Transform is not null && Transform.Parameters is Dictionary<string, object> paramDict &&
                Transform.ParametersType is Dictionary<string, string> typeDict && paramDict.Count > 0 && typeDict.Count > 0)
            {
                bool needConvert = false;
                foreach (var v in paramDict.Values)
                {
                    if (v is JsonElement)
                    {
                        needConvert = true;
                        break;
                    }
                }

                if (needConvert)
                {
                    try
                    {
                        var converted = EffectArgsHelper.ConvertElementDictToObjectDict(paramDict, Transform.ParametersType);
                        Transform.Parameters = converted;
                    }
                    catch
                    {
                        // swallow exceptions to avoid breaking re-init; leave original parameters if conversion fails
                    }
                }
            }

            Transform?.Init();
        }
    }

}
