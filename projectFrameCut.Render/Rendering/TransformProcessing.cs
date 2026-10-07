using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace projectFrameCut.Render.Rendering;

public readonly record struct TransformClipInfo(Guid Id, uint Start, uint Duration, uint Layer, uint SubLayer, IReadOnlyList<EffectProviderJSONStructure> Providers)
{
    public ulong End => (ulong)Start + Duration;
    public static TransformClipInfo FromClip(IClip clip) =>
        new(clip.Id, clip.StartFrame, clip.GetEffectiveDuration(), clip.LayerIndex, clip.SubLayerIndex, clip.EffectProviders ?? []);
}

public sealed record ResolvedTransform(TransformClipInfo Owner, EffectProviderJSONStructure Provider, TransformClipInfo Left, TransformClipInfo? Right, ulong Start, uint Duration)
{
    public bool Contains(uint frame) => Duration > 0 && frame >= Start && frame < Start + Duration;
}

public static class TransformProcessing
{
    public const string SideKey = "TransformSide";
    public const string ModeKey = "TransformInputMode";
    public const string DurationKey = "TransformDuration";
    public const string NextClipKey = "TransformNextClipId";
    public const string OrderKey = "TransformRenderOrder";
    public const string AIKey = "TransformIsAI";
    private static readonly ConditionalWeakTable<IPicture, object> CanvasFrames = new();

    public static T ReadEnum<T>(Dictionary<string, object>? metadata, string key, T fallback = default) where T : struct, Enum =>
        metadata?.TryGetValue(key, out var value) == true && Enum.TryParse<T>(value?.ToString(), out var result) && Enum.IsDefined(result) ? result : fallback;
    public static uint ReadDuration(Dictionary<string, object>? metadata) =>
        metadata?.TryGetValue(DurationKey, out var value) == true && uint.TryParse(value?.ToString(), out var frames) ? frames : 0;
    public static Guid ReadNextClip(Dictionary<string, object>? metadata) =>
        metadata?.TryGetValue(NextClipKey, out var value) == true && Guid.TryParse(value?.ToString(), out var id) ? id : Guid.Empty;
    public static bool IsAI(Dictionary<string, object>? metadata) =>
        metadata?.TryGetValue(AIKey, out var value) == true && bool.TryParse(value?.ToString(), out var ai) && ai;

    public static void Configure(IEffectProvider provider, TransformSide side, TransformInputMode mode, uint duration,
        Guid nextClipId, TransformRenderOrder order, bool isAI = false)
    {
        if (provider.TypeOfEffect != EffectType.Transform || !Enum.IsDefined(side) || !Enum.IsDefined(mode) || !Enum.IsDefined(order) || duration == 0 ||
            mode == TransformInputMode.TwoInput && (side != TransformSide.Right || nextClipId == Guid.Empty))
            throw new ArgumentException("Invalid transform placement.");
        provider.MetaData[SideKey] = side;
        provider.MetaData[ModeKey] = mode;
        provider.MetaData[DurationKey] = duration;
        provider.MetaData[NextClipKey] = nextClipId;
        provider.MetaData[OrderKey] = order;
        provider.MetaData[AIKey] = isAI;
        provider.DisconnectMainInput();
        provider.SetFinalOutputSource(false);
    }

    public static void ValidateProviders(IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        var transforms = providers.Values.Where(p => p.TypeOfEffect == EffectType.Transform).ToArray();
        foreach (var p in transforms)
        {
            if (!p.MetaData.ContainsKey(SideKey) || !p.MetaData.ContainsKey(ModeKey) ||
                ReadEnum(p.MetaData, SideKey, (TransformSide)(-1)) == (TransformSide)(-1) ||
                ReadEnum(p.MetaData, ModeKey, (TransformInputMode)(-1)) == (TransformInputMode)(-1) || ReadDuration(p.MetaData) == 0 ||
                ReadEnum<TransformInputMode>(p.MetaData, ModeKey) == TransformInputMode.TwoInput &&
                (ReadEnum<TransformSide>(p.MetaData, SideKey) != TransformSide.Right || ReadNextClip(p.MetaData) == Guid.Empty))
                throw new InvalidOperationException($"Invalid placement for transform provider {p.Id}.");
        }
        if (transforms.GroupBy(p => ReadEnum<TransformSide>(p.MetaData, SideKey)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Only one transform is allowed on each clip edge.");
    }

    public static EffectProviderJSONStructure[]? CopySingleInputs(EffectProviderJSONStructure[]? providers)
    {
        if (providers is null) return null;
        var copies = providers.Where(p => p.MetaData?.ContainsKey(SideKey) != true ||
            ReadEnum<TransformInputMode>(p.MetaData, ModeKey) == TransformInputMode.OneInput)
            .Select(p => JsonSerializer.SerializeToElement(p).Deserialize<EffectProviderJSONStructure>()!).ToArray();
        foreach (var p in copies.Where(p => p.MetaData?.ContainsKey(SideKey) == true)) p.Id = Guid.NewGuid();
        return copies;
    }

    public static bool IsCanvasFrame(IPicture frame) => CanvasFrames.TryGetValue(frame, out _);
    public static void MarkCanvasFrame(IPicture frame) => CanvasFrames.GetValue(frame, static _ => new object());

    private static IPicture CopyPicture(IPicture picture) => picture is IHDRPicture<ushort> hdr
        ? new HDRPicture16bpp(hdr.Width, hdr.Height)
        {
            r = hdr.r.Take(hdr.Pixels).ToArray(), g = hdr.g.Take(hdr.Pixels).ToArray(), b = hdr.b.Take(hdr.Pixels).ToArray(),
            a = hdr.HasAlphaChannel ? hdr.a?.Take(hdr.Pixels).ToArray() : null, HasAlphaChannel = hdr.HasAlphaChannel,
            Brightness = hdr.Brightness.ToArray(), MaximumBrightness = hdr.MaximumBrightness,
            ProcessStack = hdr.ProcessStack.ToList(), Tag = hdr.Tag
        }
        : picture.Clone();

    public sealed class Index
    {
        private readonly Dictionary<Guid, TransformClipInfo> clips;
        private readonly Dictionary<(Guid, TransformSide), (TransformClipInfo Owner, EffectProviderJSONStructure Provider)> edges = new();

        public Index(IReadOnlyList<TransformClipInfo> source)
        {
            clips = source.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var clip in clips.Values)
            {
                foreach (var provider in clip.Providers.Where(p => p.MetaData?.ContainsKey(SideKey) == true))
                {
                    Add(clip.Id, ReadEnum<TransformSide>(provider.MetaData, SideKey), clip, provider);
                    if (ReadEnum<TransformInputMode>(provider.MetaData, ModeKey) == TransformInputMode.TwoInput)
                        Add(ReadNextClip(provider.MetaData), TransformSide.Left, clip, provider);
                }
            }
        }

        private void Add(Guid clipId, TransformSide side, TransformClipInfo owner, EffectProviderJSONStructure provider)
        {
            if (clipId == Guid.Empty || !clips.ContainsKey(clipId)) return;
            if (!edges.TryAdd((clipId, side), (owner, provider)))
                throw new InvalidOperationException($"Multiple transforms occupy clip {clipId}/{side}.");
        }

        public (TransformClipInfo Owner, EffectProviderJSONStructure Provider)? Find(Guid clipId, TransformSide side) =>
            edges.TryGetValue((clipId, side), out var edge) ? edge : null;

        public ResolvedTransform? Resolve(Guid clipId, TransformSide side)
        {
            if (Find(clipId, side) is not { } edge || !edge.Provider.Enabled) return null;
            var left = edge.Owner;
            var metadata = edge.Provider.MetaData;
            uint requested = ReadDuration(metadata);
            if (left.Duration == 0 || requested == 0) return null;
            if (ReadEnum<TransformInputMode>(metadata, ModeKey) == TransformInputMode.OneInput)
            {
                var ownSide = ReadEnum<TransformSide>(metadata, SideKey);
                uint duration = Math.Min(requested, Capacity(left, ownSide));
                return new(left, edge.Provider, left, null, ownSide == TransformSide.Left ? left.Start : left.End - duration, duration);
            }
            if (!clips.TryGetValue(ReadNextClip(metadata), out var right) || !Connected(left, right)) return null;
            uint length = (uint)Math.Min(requested, Math.Min((ulong)Capacity(left, TransformSide.Right) * 2 + 1,
                (ulong)Capacity(right, TransformSide.Left) * 2));
            return new(left, edge.Provider, left, right, (ulong)right.Start - length / 2, length);
        }

        private static bool Connected(TransformClipInfo left, TransformClipInfo right) =>
            right.Id != left.Id && right.Duration > 0 && left.Layer == right.Layer && left.SubLayer == right.SubLayer && left.End == right.Start;

        private uint Capacity(TransformClipInfo clip, TransformSide side)
        {
            var opposite = side == TransformSide.Left ? TransformSide.Right : TransformSide.Left;
            if (Find(clip.Id, opposite) is not { } other) return clip.Duration;
            var metadata = other.Provider.MetaData;
            bool dual = ReadEnum<TransformInputMode>(metadata, ModeKey) == TransformInputMode.TwoInput;
            if (dual && (!clips.TryGetValue(ReadNextClip(metadata), out var right) || !Connected(other.Owner, right)))
                return clip.Duration;
            uint duration = ReadDuration(metadata);
            uint reserved = !dual ? duration : opposite == TransformSide.Left ? duration / 2 + duration % 2 : duration / 2;
            return clip.Duration - Math.Min(reserved, opposite == TransformSide.Left ? clip.Duration / 2 : clip.Duration / 2 + clip.Duration % 2);
        }
    }

    public static (TransformClipInfo Owner, EffectProviderJSONStructure Provider)? Find(IReadOnlyList<TransformClipInfo> clips, Guid id, TransformSide side) =>
        new Index(clips).Find(id, side);
    public static ResolvedTransform? Resolve(IReadOnlyList<TransformClipInfo> clips, Guid id, TransformSide side) =>
        new Index(clips).Resolve(id, side);

    public static bool HasActiveTransform(IClip clip, IReadOnlyList<IClip> clips, uint frame)
    {
        var index = new Index(clips.Select(TransformClipInfo.FromClip).ToArray());
        return Enum.GetValues<TransformSide>().Any(side => index.Resolve(clip.Id, side)?.Contains(frame) == true);
    }

    public static IPicture RenderCanvas(IClip clip, IReadOnlyList<IClip> clips, uint frame, int width, int height,
        int projectWidth, int projectHeight, IPicture.PicturePixelMode pixelMode)
    {
        if (TryRender(clip, clips, frame, width, height, projectWidth, projectHeight, pixelMode, out var result)) return result!;
        return RenderInput(clip, frame, width, height, projectWidth, projectHeight, pixelMode);
    }

    public static bool TryRender(IClip clip, IReadOnlyList<IClip> clips, uint frameIndex, int width, int height,
        int projectWidth, int projectHeight, IPicture.PicturePixelMode pixelMode, out IPicture? result,
        int sdrBrightness = 203, bool autoCenterImplicitClip = true, bool initializeClips = true, IPicture? preparedSource = null)
    {
        result = null;
        var index = new Index(clips.Select(TransformClipInfo.FromClip).ToArray());
        foreach (var side in Enum.GetValues<TransformSide>())
        {
            var resolved = index.Resolve(clip.Id, side);
            if (resolved is null || !resolved.Contains(frameIndex)) continue;
            IPicture? transformed = null;
            try
            {
                var owner = clips.First(c => c.Id == resolved.Owner.Id);
                var neighbor = resolved.Right is { } r ? clips.First(c => c.Id == r.Id) : null;
                bool before = ReadEnum<TransformRenderOrder>(resolved.Provider.MetaData, OrderKey) == TransformRenderOrder.BeforeEffects;
                int inputWidth = before ? OutputDimension(clip.TargetWidth, projectWidth, width) : width;
                int inputHeight = before ? OutputDimension(clip.TargetHeight, projectHeight, height) : height;
                using var current = before
                    ? RenderRawInput(clip, frameIndex, inputWidth, inputHeight, pixelMode, sdrBrightness, initializeClips, preparedSource, align: false)
                    : null;
                if (current is not null)
                {
                    inputWidth = current.Width;
                    inputHeight = current.Height;
                }
                using var left = before
                    ? owner.Id == clip.Id ? CopyPicture(current!) : RenderRawInput(owner, frameIndex, inputWidth, inputHeight, pixelMode, sdrBrightness, initializeClips, null)
                    : RenderInput(owner, frameIndex, width, height, projectWidth, projectHeight, pixelMode, sdrBrightness, autoCenterImplicitClip, initializeClips, owner.Id == clip.Id ? preparedSource : null);
                using var right = neighbor is null ? null : before
                    ? neighbor.Id == clip.Id ? CopyPicture(current!) : RenderRawInput(neighbor, frameIndex, inputWidth, inputHeight, pixelMode, sdrBrightness, initializeClips, null)
                    : RenderInput(neighbor, frameIndex, width, height, projectWidth, projectHeight, pixelMode, sdrBrightness, autoCenterImplicitClip, initializeClips, neighbor.Id == clip.Id ? preparedSource : null);
                var transform = owner.EffectsInstances?.OfType<ITransform>().SingleOrDefault(e => e.Enabled &&
                    e.BindedEffectProvidingSystemID == resolved.Provider.Id.ToString())
                    ?? throw new InvalidOperationException($"Missing effect instance for transform {resolved.Provider.Id}.");
                float ownerProgress = owner.GetEffectiveDuration() == 0 ? 0 :
                    Math.Clamp(((float)frameIndex - owner.StartFrame) / owner.GetEffectiveDuration(), 0, 1);
                using (ValueProviderFrameContext.PushFrame(frameIndex, ownerProgress))
                    transformed = ProcessFrames(left, right, transform,
                        ReadEnum<TransformInputMode>(resolved.Provider.MetaData, ModeKey),
                        Progress(frameIndex, resolved.Start, resolved.Duration),
                        ReadEnum<TransformSide>(resolved.Provider.MetaData, SideKey), inputWidth, inputHeight);
                if (ReferenceEquals(transformed, left) || ReferenceEquals(transformed, right)) transformed = CopyPicture(transformed);
                if (before)
                {
                    result = Timeline.MixtureLayers([new OneFrame(frameIndex, clip, transformed, resolveEffects: false)],
                        frameIndex, width, height, (int)pixelMode, autoCenterImplicitClip: autoCenterImplicitClip,
                        projectRelativeWidth: projectWidth, projectRelativeHeight: projectHeight, transparentBackground: true, disposeIntermediateFrames: true);
                    if (!ReferenceEquals(result, transformed)) transformed.Dispose();
                    transformed = null;
                }
                else
                {
                    result = transformed;
                    transformed = null;
                }
                if (IsAI(resolved.Provider.MetaData))
                {
                    var marked = EffectProcessing.ProcessAIWatermark(result, frameIndex);
                    if (!ReferenceEquals(marked, result)) result.Dispose();
                    result = marked;
                }
                MarkCanvasFrame(result);
                return true;
            }
            catch (Exception ex)
            {
                transformed?.Dispose();
                result?.Dispose();
                result = null;
                Logger.Log(ex, $"Transform {resolved.Provider.Id} on clip {clip.Id}, frame {frameIndex}; using normal picture", nameof(TransformProcessing));
                return false;
            }
        }
        return false;
    }

    private static int OutputDimension(int dimension, int relative, int output) =>
        dimension <= 0 ? Math.Max(1, output) : relative <= 0 ? dimension :
            Math.Max(1, (int)Math.Round((double)dimension * output / relative));

    private static IPicture RenderRawInput(IClip clip, uint frame, int width, int height,
        IPicture.PicturePixelMode pixelMode, int sdrBrightness, bool initializeClips, IPicture? preparedSource, bool align = true)
    {
        uint clamped = ClampFrame(clip, frame);
        if (initializeClips && preparedSource is null)
        {
            clip.ReInit(pixelMode);
            EffectHelper.ResolveClipEffects(clip);
        }
        using var context = ValueProviderFrameContext.PushFrame(clamped,
            Math.Clamp(((float)clamped - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1));
        uint actual = clip.GetRelativeFrameIndex(clamped) ?? 0;
        var source = preparedSource is null ? clip.GetFrameRelativeToStartPointOfSource(actual, width, height, pixelMode) : CopyPicture(preparedSource);
        try
        {
            if (preparedSource is null && clip.AlternativeSource is ISourceReplacementEffect replacement && replacement.SupportsSourceReplacement(clip, width, height))
            {
                var replaced = replacement.Compute(clip, source, width, height, actual, pixelMode);
                if (!ReferenceEquals(source, replaced)) source.Dispose();
                source = replaced;
            }
            if (preparedSource is null && clip.ExtraData.TryGetValue("IsAI", out var ai) && bool.TryParse(ai?.ToString(), out var isAI) && isAI)
            {
                var marked = EffectProcessing.ProcessAIWatermark(source, clamped);
                if (!ReferenceEquals(marked, source)) source.Dispose();
                source = marked;
            }
            var resized = !align || source.Width == width && source.Height == height ? source : source is IHDRPicture<ushort> hdrSource
                ? hdrSource.Resize(width, height, false) : source.Resize(width, height, false);
            if (!ReferenceEquals(resized, source)) source.Dispose();
            source = resized;
            if (pixelMode == IPicture.PicturePixelMode.UShortPicture && source is not IHDRPicture<ushort>)
            {
                var hdr = HDRPicture16bpp.ToHDRPictureBySignal(source, Brightness(clip, sdrBrightness));
                if (!ReferenceEquals(hdr, source)) source.Dispose();
                source = hdr;
            }
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static uint ClampFrame(IClip clip, uint frame) =>
        (uint)Math.Clamp((ulong)frame, clip.StartFrame, (ulong)clip.StartFrame + Math.Max(1u, clip.GetEffectiveDuration()) - 1);
    private static int Brightness(IClip clip, int fallback) =>
        clip.ExtraData.TryGetValue("HDRBrightness", out var value) && int.TryParse(value.ToString(), out var brightness) ? brightness : fallback;

    private static IPicture RenderInput(IClip clip, uint frame, int width, int height, int projectWidth, int projectHeight,
        IPicture.PicturePixelMode pixelMode, int sdrBrightness = 203, bool autoCenterImplicitClip = true, bool initializeClips = true, IPicture? preparedSource = null)
    {
        uint clamped = ClampFrame(clip, frame);
        using var context = ValueProviderFrameContext.PushFrame(clamped,
            Math.Clamp(((float)clamped - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1));
        var frames = preparedSource is not null ? new[] { new OneFrame(clamped, clip, CopyPicture(preparedSource), resolveEffects: false) }
            : Timeline.GetFramesInOneFrame([clip], clamped, width, height, pixelMode,
                projectWidth, projectHeight, applyTransforms: false, initializeClips: initializeClips).ToArray();
        try
        {
            if (pixelMode == IPicture.PicturePixelMode.UShortPicture)
            {
                for (int i = 0; i < frames.Length; i++)
                {
                    if (frames[i].Clip is IHDRPicture<ushort>) continue;
                    var source = frames[i].Clip;
                    var hdr = HDRPicture16bpp.ToHDRPictureBySignal(source, Brightness(clip, sdrBrightness));
                    frames[i] = new OneFrame(clamped, clip, hdr, resolveEffects: false);
                    if (!ReferenceEquals(source, hdr)) source.Dispose();
                }
            }
            var result = Timeline.MixtureLayers(frames, clamped, width, height, (int)pixelMode, autoCenterImplicitClip: autoCenterImplicitClip,
                projectRelativeWidth: projectWidth, projectRelativeHeight: projectHeight, transparentBackground: true, disposeIntermediateFrames: true);
            return frames.Any(item => ReferenceEquals(item.Clip, result)) ? CopyPicture(result) : result;
        }
        finally
        {
            foreach (var item in frames) item.Clip.Dispose();
        }
    }

    public static float Progress(uint frame, ulong start, uint duration) =>
        duration <= 1 ? 0 : Math.Clamp(((float)frame - start) / (duration - 1), 0, 1);

    public static IPicture ProcessFrames(IPicture left, IPicture? right, ITransform transform,
        TransformInputMode mode, float progress, TransformSide side, int width, int height)
    {
        var flag = mode == TransformInputMode.OneInput ? TransformDefinition.SupportOneInput : TransformDefinition.SupportTwoInput;
        if (!transform.Definition.HasFlag(TransformDefinition.Clip) || !transform.Definition.HasFlag(flag))
            throw new NotSupportedException($"Transform {transform.TypeName} does not support {mode} pictures.");
        if (mode == TransformInputMode.TwoInput) ArgumentNullException.ThrowIfNull(right);
        return transform.Render(left, mode == TransformInputMode.OneInput ? null : right, Math.Clamp(progress, 0, 1), side, width, height);
    }

    public static IAudioSamples ProcessSamples(IAudioSamples left, IAudioSamples? right, ITransform transform,
        TransformInputMode mode, long sampleOffset, long durationSamples, TransformSide side)
    {
        var required = TransformDefinition.Audio | (mode == TransformInputMode.OneInput ? TransformDefinition.SupportOneInput : TransformDefinition.SupportTwoInput);
        if (!transform.Definition.HasFlag(required))
            throw new NotSupportedException($"Transform {transform.TypeName} does not support {mode} audio.");
        if (mode == TransformInputMode.TwoInput) ArgumentNullException.ThrowIfNull(right);
        return transform.Render(left, mode == TransformInputMode.OneInput ? null : right, sampleOffset, durationSamples, side);
    }
}
