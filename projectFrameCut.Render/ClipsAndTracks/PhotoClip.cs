using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Effect;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.Plugin;
using System.Text.Json;

namespace projectFrameCut.Render.ClipsAndTracks
{
    public class PhotoClip : IImmutableContentClip
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

        public string? FilePath { get; set; } = string.Empty;
        public bool NeedFilePath => true;
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }

        public bool Use16bpp = false;


        [System.Text.Json.Serialization.JsonIgnore]
        public IPicture? source { get; set; } = null;

        public ClipMode ClipType => ClipMode.PhotoClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public bool IsVector => false;

        public string BindedSoundTrack { get; init; } = "";



        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public EffectProviderJSONStructure[]? EffectProviders { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public IEffectProvider[]? EffectProvidersInstances { get; set; }
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public int StartingX { get; set; }
        public int StartingY { get; set; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
        public IMixture? MixtureInstance { get; set; }
        public ISourceReplacementEffect? AlternativeSource { get; set; }

        public PhotoClip()
        {
           EffectHelper.ResolveClipEffects(this);
        }
        public IPicture GetContent(int targetWidth, int targetHeight, IPicture.PicturePixelMode targetPPB)
        {
            ArgumentNullException.ThrowIfNull(source, $"PhotoClip {Id}'s source is null. Please init it.");

            bool directCropEnabled = ExtraData?.TryGetValue("__Internal_DirectCropEnabled__", out var directCropRaw) == true
                && (directCropRaw is true
                    || directCropRaw is JsonElement { ValueKind: JsonValueKind.True }
                    || bool.TryParse(directCropRaw?.ToString(), out var parsedDirectCrop) && parsedDirectCrop);
            if (directCropEnabled || StartingX > 0 || StartingY > 0)
            {
                var cropper = new CropEffect_HwAccel
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    StartX = StartingX,
                    StartY = StartingY,
                    RelativeWidth = 0,
                    RelativeHeight = 0,
                };
                return cropper.Render(source, PluginManager.CreateComputer(cropper.NeedComputer), targetWidth, targetHeight).ToBitPerPixel(targetPPB);
            }
            else
            {
                return source.Resize(targetWidth, targetHeight, true).ToBitPerPixel(targetPPB);
            }
        }

        void IClip.ReInit(IPicture.PicturePixelMode targetPPB)
        {
            if (FilePath is null) throw new NullReferenceException($"PhotoClip {Id}'s source path is null.");
            source = targetPPB == 16 ? new Picture16bpp(FilePath) : new Picture8bpp(FilePath);
            source.CanBeDisposed = false;
            source.ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    Operator = GetType(),
                    OperationDisplayName = $"Created for PhotoClip {Name} ({Id})",
                    ProcessingFuncStackTrace = null,
                    Properties = new Dictionary<string, object>
                    {
                        { "Path", FilePath }
                    }
                }
            };
           EffectHelper.ResolveClipEffects(this);

        }


        void IDisposable.Dispose()
        {
            source?.CanBeDisposed = false;
            source?.Dispose(true);
        }

    }

}
