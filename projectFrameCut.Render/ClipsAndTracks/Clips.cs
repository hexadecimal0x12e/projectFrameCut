using projectFrameCut.Drawing.Text;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Text.Json;
using System.Text.Json.Serialization;
using projectFrameCut.Render.Effect;

namespace projectFrameCut.Render.ClipsAndTracks
{

    public class SolidColorClip : IImmutableContentClip
    {
        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get => 1; init { } }

        public string? filePath { get; } = null;
        public ClipMode ClipType => ClipMode.SolidColorClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public EffectProviderJSONStructure[]? EffectProviders { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public IEffectProvider[]? EffectProvidersInstances { get; set; }
        public bool NeedFilePath => false;
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }

        public string BindedSoundTrack { get; init; } = "";


        string? IClip.FilePath { get => null; set => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

        public ushort R { get; init; }
        public ushort G { get; init; }
        public ushort B { get; init; }
        public float? A { get; init; } = null;

        public bool UseFixedOutputSize { get; init; } = true;
        public int OutputWidth { get; init; } = 1920;
        public int OutputHeight { get; init; } = 1080;

        [JsonIgnore]
        public bool EffectiveUseFixedOutputSize => ResolveConfiguredBool("SolidColorUseFixedOutputSize", UseFixedOutputSize);

        [JsonIgnore]
        public int EffectiveOutputWidth => ResolveConfiguredInt("SolidColorOutputWidth", OutputWidth > 0 ? OutputWidth : targetWidth);

        [JsonIgnore]
        public int EffectiveOutputHeight => ResolveConfiguredInt("SolidColorOutputHeight", OutputHeight > 0 ? OutputHeight : targetHeight);

        [JsonIgnore]
        public bool ShouldUseFixedOutputSize => EffectiveUseFixedOutputSize && TargetWidth <= 0 && TargetHeight <= 0;

        public int targetWidth { get; init; } = 1920;
        public int targetHeight { get; init; } = 1080;
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public int StartingX { get; set; }
        public int StartingY { get; set; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
        public IMixture? MixtureInstance { get; set; }
        public ISourceReplacementEffect? AlternativeSource { get; set; }

        public IPicture GetContent(int targetWidth, int targetHeight, IPicture.PicturePixelMode targetPPB)
        {
            IPicture result = targetPPB.Value switch
            {
                16 => Picture16bpp.GenerateSolidColor(ShouldUseFixedOutputSize ? EffectiveOutputWidth : Math.Max(1, targetWidth), ShouldUseFixedOutputSize ? EffectiveOutputHeight : Math.Max(1, targetHeight), R, G, B, A),
                8 => Picture8bpp.GenerateSolidColor(ShouldUseFixedOutputSize ? EffectiveOutputWidth : Math.Max(1, targetWidth), ShouldUseFixedOutputSize ? EffectiveOutputHeight : Math.Max(1, targetHeight), (byte)(R / 257), (byte)(G / 257), (byte)(B / 257), A),
                _ => throw new NotSupportedException($"Unsupported target pixel mode {targetPPB}.")
            };
            result.CanBeDisposed = false;
            return result;
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex)
            => Picture16bpp.GenerateSolidColor(EffectiveOutputWidth, EffectiveOutputHeight, R, G, B, A);

        public SolidColorClip()
        {
           EffectHelper.ResolveClipEffects(this);

        }

        public void ReInit()
        {
           EffectHelper.ResolveClipEffects(this);

        }

        public void Dispose()
        {

        }

        public uint? GetClipLength() => Duration;


        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
        }

        private int ResolveConfiguredInt(string key, int fallback)
        {
            if (ExtraData != null && ExtraData.TryGetValue(key, out var raw) && raw is not null)
            {
                if (raw is int i)
                {
                    return Math.Max(1, i);
                }

                if (raw is long l)
                {
                    return Math.Max(1, (int)Math.Min(int.MaxValue, l));
                }

                if (raw is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var jn))
                    {
                        return Math.Max(1, jn);
                    }

                    if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var js))
                    {
                        return Math.Max(1, js);
                    }
                }

                if (int.TryParse(raw.ToString(), out var parsed))
                {
                    return Math.Max(1, parsed);
                }
            }

            return Math.Max(1, fallback);
        }

        private bool ResolveConfiguredBool(string key, bool fallback)
        {
            if (ExtraData != null && ExtraData.TryGetValue(key, out var raw) && raw is not null)
            {
                if (raw is bool b)
                {
                    return b;
                }

                if (raw is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.True) return true;
                    if (je.ValueKind == JsonValueKind.False) return false;
                    if (je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var jb)) return jb;
                }

                if (bool.TryParse(raw.ToString(), out var parsed))
                {
                    return parsed;
                }
            }

            return fallback;
        }

    }

    public class MarkingClip : IClip
    {
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public ClipMode ClipType => ClipMode.MarkingClip;

        public Guid Id { get; init; }
        public string Name { get; init; }
        public string BindedSoundTrack { get; init; }
        public uint LayerIndex { get; init; }
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get => 1; init { } }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public EffectProviderJSONStructure[]? EffectProviders { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public IEffectProvider[]? EffectProvidersInstances { get; set; }
        public string? FilePath { get; set; }
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }


        public bool NeedFilePath => false;

        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public int StartingX { get; set; }
        public int StartingY { get; set; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
        public IMixture? MixtureInstance { get; set; }
        public ISourceReplacementEffect? AlternativeSource { get; set; }

        public string? MarkData;
        public Guid MarkID;

        public void Dispose()
        {
        }

        public uint? GetClipLength() => null;

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex)
        {
            throw new NotImplementedException();
        }


        public void ReInit()
        {
        }

        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
            throw new NotImplementedException();
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, IPicture.PicturePixelMode targetPPB)
        {
            throw new NotImplementedException();
        }
    }

}
