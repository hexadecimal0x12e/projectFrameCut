using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace projectFrameCut.Render.PluginIsolation;

internal static class RemoteEffectFactory
{
    public static IEffect Create(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) => descriptor.EffectType switch
    {
        (int)EffectType.NormalEffect when descriptor.IsColorAdjust => new RemoteColorAdjustEffect(session, descriptor),
        (int)EffectType.NormalEffect => new RemoteNormalEffect(session, descriptor),
        (int)EffectType.ContinuousEffect => new RemoteContinuousEffect(session, descriptor),
        (int)EffectType.MixtureProvider => new RemoteMixture(session, descriptor),
        (int)EffectType.SourceReplacement => new RemoteSourceReplacementEffect(session, descriptor),
        (int)EffectType.AudioNormalEffect => new RemoteAudioNormalEffect(session, descriptor),
        (int)EffectType.AudioContinuousEffect => new RemoteAudioContinuousEffect(session, descriptor),
        (int)EffectType.TextEffect => new RemoteTextEffect(session, descriptor),
        (int)EffectType.ContinuousTextEffect => new RemoteContinuousTextEffect(session, descriptor),
        (int)EffectType.SpeedVarianceProvider => new RemoteSpeedVarianceProvider(session, descriptor),
        (int)EffectType.ClipPositionProvider => new RemoteClipPositionProvider(session, descriptor),
        (int)EffectType.ContinuousClipPositionProvider => new RemoteContinuousClipPositionProvider(session, descriptor),
        (int)EffectType.NonIPictureOutputValueProvider => new RemoteValueProviderEffect(session, descriptor),
        _ => throw new NotSupportedException($"Remote effect kind '{descriptor.EffectType}' is not supported."),
    };
}

internal sealed class RemoteAudioNormalEffect(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) : RemoteEffectBase(session, descriptor), IAudioNormalEffect
{
    public IAudioSamples Process(IAudioSamples input) => ProcessAudio(input, 0);
    private IAudioSamples ProcessAudio(IAudioSamples input, float progress) => RemoteEffectInvoke.ProcessAudio(Session, ObjectId, input, progress);
}

internal sealed class RemoteAudioContinuousEffect : RemoteEffectBase, IAudioContinuousEffect
{
    public RemoteAudioContinuousEffect(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) : base(session, descriptor)
    {
        StartPoint = descriptor.StartPoint;
        EndPoint = descriptor.EndPoint;
    }

    public int StartPoint { get; set; }
    public int EndPoint { get; set; }
    public IAudioSamples Process(IAudioSamples input, float index) => RemoteEffectInvoke.ProcessAudio(Session, ObjectId, input, index);
}

internal sealed class RemoteTextEffect(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) : RemoteEffectBase(session, descriptor), ITextEffect
{
    public TextEntry[] Process(TextEntry[] source) => RemoteEffectInvoke.ProcessText(Session, ObjectId, source, 0);
}

internal sealed class RemoteContinuousTextEffect : RemoteEffectBase, IContinuousTextEffect
{
    public RemoteContinuousTextEffect(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) : base(session, descriptor)
    {
        StartPoint = descriptor.StartPoint;
        EndPoint = descriptor.EndPoint;
        IsScoped = descriptor.IsScoped;
    }

    public int StartPoint { get; set; }
    public int EndPoint { get; set; }
    public bool IsScoped { get; set; }
    public TextEntry[] Process(TextEntry[] source, float progress) => RemoteEffectInvoke.ProcessText(Session, ObjectId, source, progress);
}

internal sealed class RemoteSpeedVarianceProvider(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) : RemoteEffectBase(session, descriptor), ISpeedVarianceProvider
{
    public uint GetTargetFrame(uint sourceFrame) => Invoke<IsolationEffectInvokeRequest, IsolationEffectInvokeResponse>(RenderOperation.IsolationMapSpeedFrame, new() { ObjectId = ObjectId, Value = sourceFrame }).Value;
    public uint GetEffectiveLength(uint length) => Invoke<IsolationEffectInvokeRequest, IsolationEffectInvokeResponse>(RenderOperation.IsolationMapSpeedLength, new() { ObjectId = ObjectId, Value = length }).Value;
}

internal sealed class RemoteClipPositionProvider(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) : RemoteEffectBase(session, descriptor), IClipPositionProvider
{
    public ClipPositionTuple GetPosition(IClip source, int targetWidth, int targetHeight) => RemoteEffectInvoke.GetPosition(Session, ObjectId, source, 0, targetWidth, targetHeight);
}

internal sealed class RemoteContinuousClipPositionProvider(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) : RemoteEffectBase(session, descriptor), IContinuousClipPositionProvider
{
    public ClipPositionTuple GetPosition(IClip source, uint index, int targetWidth, int targetHeight) => RemoteEffectInvoke.GetPosition(Session, ObjectId, source, index, targetWidth, targetHeight);
}

internal sealed class RemoteValueProviderEffect : RemoteEffectBase, IValueProviderEffect
{
    private readonly IsolationFieldDescriptor _field;

    public RemoteValueProviderEffect(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) : base(session, descriptor)
        => _field = descriptor.ValueField ?? throw new InvalidDataException("The remote value provider has no field descriptor.");

    string IEffectArgumentField.Id => _field.Id;
    string IEffectArgumentField.TypeName => _field.TypeName;
    string IEffectArgumentField.FromPlugin => _field.FromPlugin;
    public bool IsDynamic => true;
    public EffectArgumentFieldType FieldType => (EffectArgumentFieldType)_field.FieldType;
    public string DefaultValue { get => _field.DefaultValue; set => _field.DefaultValue = value; }
    public string MinValue { get => _field.MinimumValue; set => _field.MinimumValue = value; }
    public string MaxValue { get => _field.MaximumValue; set => _field.MaximumValue = value; }
    public string[]? PresetOptions { get => _field.PresetOptions.ToArray(); set => _field.PresetOptions = value?.ToList() ?? []; }
    public string? Remarks { get => string.IsNullOrEmpty(_field.Remarks) ? null : _field.Remarks; set => _field.Remarks = value ?? string.Empty; }
    public bool IsDynamicAtRenderTime => true;
    public Func<object> GetGetter() => () =>
    {
        var response = Invoke<IsolationEffectInvokeRequest, IsolationEffectInvokeResponse>(RenderOperation.IsolationGetEffectValue, new() { ObjectId = ObjectId });
        return IsolationValueConverter.ToObject(JsonSerializer.Deserialize<IsolationValue>(response.Json)!)!;
    };
}

internal static class RemoteEffectInvoke
{
    public static IAudioSamples ProcessAudio(IPluginIsolationSession session, long objectId, IAudioSamples input, float progress)
    {
        var data = AudioPayloadCodec.Encode(input);
        var lease = session.Payloads.PublishAsync(data, session.PreferredPayloadKind).AsTask().GetAwaiter().GetResult();
        try
        {
            var response = session.InvokeAsync<IsolationEffectInvokeRequest, IsolationAudioSamplesResponse>(RenderOperation.IsolationProcessAudioEffect, new()
            {
                ObjectId = objectId,
                Audio = lease.Reference,
                Progress = progress,
                ChannelCount = input.channelCount,
                SamplePerSecond = input.SamplePerSecond,
                SampleCount = input.SampleCount,
            }).AsTask().GetAwaiter().GetResult();
            try { return AudioPayloadCodec.ReadAsync(response, session.Payloads, default).AsTask().GetAwaiter().GetResult(); }
            finally { session.Payloads.ReleaseAsync(response.Samples).AsTask().GetAwaiter().GetResult(); }
        }
        finally { lease.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    public static TextEntry[] ProcessText(IPluginIsolationSession session, long objectId, TextEntry[] input, float progress)
    {
        var response = session.InvokeAsync<IsolationEffectInvokeRequest, IsolationEffectInvokeResponse>(RenderOperation.IsolationProcessTextEffect,
            new() { ObjectId = objectId, Json = JsonSerializer.Serialize(input), Progress = progress }).AsTask().GetAwaiter().GetResult();
        return JsonSerializer.Deserialize<TextEntry[]>(response.Json) ?? [];
    }

    public static ClipPositionTuple GetPosition(IPluginIsolationSession session, long objectId, IClip clip, uint index, int width, int height)
    {
        var response = session.InvokeAsync<IsolationEffectInvokeRequest, IsolationEffectInvokeResponse>(RenderOperation.IsolationGetClipPosition, new()
        {
            ObjectId = objectId,
            Clip = IsolationClipSnapshotFactory.Create(clip),
            Value = index,
            TargetWidth = width,
            TargetHeight = height,
        }).AsTask().GetAwaiter().GetResult();
        return new(response.TargetX, response.TargetY, response.TargetWidth, response.TargetHeight, response.IsDelta);
    }
}

internal abstract class RemoteEffectBase : IEffect
{
    protected readonly IPluginIsolationSession Session;
    protected readonly long ObjectId;
    protected readonly List<string> DynamicProviderIds;

    protected RemoteEffectBase(IPluginIsolationSession session, IsolationEffectDescriptor descriptor)
    {
        Session = session;
        ObjectId = descriptor.ObjectId;
        DynamicProviderIds = descriptor.DynamicProviderIds;
        FromPlugin = descriptor.FromPlugin;
        TypeName = descriptor.TypeName;
        TypeOfEffect = (EffectType)descriptor.EffectType;
        ImplementType = (EffectImplementType)descriptor.ImplementType;
        Name = descriptor.Name;
        Id = descriptor.InstanceId;
        Enabled = descriptor.Enabled;
        Index = descriptor.Index;
        IsReorderable = descriptor.IsReorderable;
        CanProcessFromCanvas = descriptor.CanProcessFromCanvas;
        NeedComputer = string.IsNullOrWhiteSpace(descriptor.NeedComputer) ? null : descriptor.NeedComputer;
        RelativeWidth = descriptor.RelativeWidth;
        RelativeHeight = descriptor.RelativeHeight;
        Parameters = descriptor.Parameters.ToDictionary(x => x.Key, x => IsolationValueConverter.ToObject(x.Value)!);
    }

    public string FromPlugin { get; }
    public string TypeName { get; }
    public EffectType TypeOfEffect { get; }
    public EffectImplementType ImplementType { get; }
    public string Name { get; set; }
    public string Id { get; set; }
    public Dictionary<string, object> Parameters { get; }
    public bool Enabled { get; set; }
    public int Index { get; set; }
    public bool IsReorderable { get; }
    public bool CanProcessFromCanvas { get; }
    public string? NeedComputer { get; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public string? BindedEffectProvidingSystemID { get; set; }

    public IEffect WithParameters(Dictionary<string, object> parameters)
    {
        var descriptor = Invoke<IsolationCloneEffectRequest, IsolationEffectDescriptor>(RenderOperation.IsolationCloneEffect, new()
        {
            ObjectId = ObjectId,
            Parameters = parameters.ToDictionary(x => x.Key, x => IsolationValueConverter.FromObject(x.Value)),
        });
        return RemoteEffectFactory.Create(Session, descriptor);
    }

    protected IPicture Process(RenderOperation operation, IsolationEffectFrameRequest request, IPicture fallback)
    {
        IsolationPayloadLease? sourceLease = null;
        IsolationPayloadLease? secondLease = null;
        List<IsolationPayloadLease> dynamicLeases = [];
        try
        {
            sourceLease = PicturePayloadCodec.WriteAsync(request.SourcePicture(), Session.Payloads, Session.PreferredPayloadKind).AsTask().GetAwaiter().GetResult();
            request.Source = sourceLease.Reference;
            if (request.SecondPicture() is { } second)
            {
                secondLease = PicturePayloadCodec.WriteAsync(second, Session.Payloads, Session.PreferredPayloadKind).AsTask().GetAwaiter().GetResult();
                request.SecondSource = secondLease.Reference;
            }
            request.ObjectId = ObjectId;
            GetDynamicValues(request, dynamicLeases);
            request.State = CreateState();
            var response = Invoke<IsolationEffectFrameRequest, IsolationPictureResponse>(operation, request);
            try { return PicturePayloadCodec.ReadAsync(response.Picture, Session.Payloads).AsTask().GetAwaiter().GetResult(); }
            finally { Session.Payloads.ReleaseAsync(response.Picture).AsTask().GetAwaiter().GetResult(); }
        }
        catch (Exception ex)
        {
            projectFrameCut.Shared.Logger.Log(ex, $"Run isolated effect '{TypeName}'", this);
            return fallback;
        }
        finally
        {
            foreach (var lease in dynamicLeases) lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (secondLease is not null) secondLease.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (sourceLease is not null) sourceLease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    protected void GetDynamicValues(IsolationEffectFrameRequest request, List<IsolationPayloadLease> leases)
    {
        foreach (var id in DynamicProviderIds)
        {
            var value = ValueProviderFrameContext.Get(id);
            if (value is null) continue;
            if (value is IPicture picture)
            {
                var lease = PicturePayloadCodec.WriteAsync(picture, Session.Payloads, Session.PreferredPayloadKind).AsTask().GetAwaiter().GetResult();
                leases.Add(lease);
                request.DynamicPictures[id] = lease.Reference;
                continue;
            }
            try { request.DynamicValues[id] = IsolationValueConverter.FromObject(value); } catch (NotSupportedException) { }
        }
    }

    protected IsolationEffectMutableState CreateState() => new()
    {
        Name = Name,
        InstanceId = Id,
        Enabled = Enabled,
        Index = Index,
        RelativeWidth = RelativeWidth,
        RelativeHeight = RelativeHeight,
        BoundProviderId = BindedEffectProvidingSystemID ?? string.Empty,
        StartPoint = this switch { IContinuousEffect c => c.StartPoint, IAudioContinuousEffect ac => ac.StartPoint, IContinuousTextEffect tc => tc.StartPoint, _ => 0 },
        EndPoint = this switch { IContinuousEffect c => c.EndPoint, IAudioContinuousEffect ac => ac.EndPoint, IContinuousTextEffect tc => tc.EndPoint, _ => 0 },
        IsScoped = this switch { IContinuousEffect c => c.IsScoped, IContinuousTextEffect tc => tc.IsScoped, _ => false },
        ProjectFrameRate = this is ISourceReplacementEffect s ? s.ProjectFrameRate : 0,
    };

    protected TResponse Invoke<TRequest, TResponse>(RenderOperation operation, TRequest request)
        => Session.InvokeAsync<TRequest, TResponse>(operation, request).AsTask().GetAwaiter().GetResult();
}

internal sealed class RemoteNormalEffect(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) : RemoteEffectBase(session, descriptor), INormalEffect
{
    public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        => Process(RenderOperation.IsolationProcessNormalEffect, IsolationEffectFrameRequestFactory.Create(source, targetWidth, targetHeight), source);
}

internal sealed class RemoteColorAdjustEffect(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) : RemoteEffectBase(session, descriptor), IColorAdjustEffect
{
    public IPicture Process(IPicture source, IComputer? computer)
        => Process(RenderOperation.IsolationProcessNormalEffect, IsolationEffectFrameRequestFactory.Create(source, source.Width, source.Height), source);
}

internal sealed class RemoteContinuousEffect : RemoteEffectBase, IContinuousEffect
{
    public RemoteContinuousEffect(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) : base(session, descriptor)
    {
        StartPoint = descriptor.StartPoint;
        EndPoint = descriptor.EndPoint;
        IsScoped = descriptor.IsScoped;
    }

    public int StartPoint { get; set; }
    public int EndPoint { get; set; }
    public bool IsScoped { get; set; }

    public IPicture Render(IPicture source, float progress, IComputer? computer, int targetWidth, int targetHeight)
    {
        var request = IsolationEffectFrameRequestFactory.Create(source, targetWidth, targetHeight);
        request.Progress = progress;
        return Process(RenderOperation.IsolationProcessContinuousEffect, request, source);
    }
}

internal sealed class RemoteMixture(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) : RemoteEffectBase(session, descriptor), IMixture
{
    public IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer? computer, IPicture.PicturePixelMode targetPPB)
    {
        var request = IsolationEffectFrameRequestFactory.Create(basePicture, basePicture.Width, basePicture.Height, topPicture);
        request.TargetPixelMode = targetPPB.Value;
        return Process(RenderOperation.IsolationProcessMixture, request, basePicture);
    }

    public IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer? computer, IPicture.PicturePixelMode targetPPB, int topStartX, int topStartY, int targetWidth, int targetHeight)
    {
        var request = IsolationEffectFrameRequestFactory.Create(basePicture, targetWidth, targetHeight, topPicture);
        request.TargetPixelMode = targetPPB.Value;
        request.TopStartX = topStartX;
        request.TopStartY = topStartY;
        request.UsePositionedMixture = true;
        return Process(RenderOperation.IsolationProcessMixture, request, basePicture);
    }
}

internal sealed class RemoteSourceReplacementEffect : RemoteEffectBase, ISourceReplacementEffect
{
    public RemoteSourceReplacementEffect(IPluginIsolationSession session, IsolationEffectDescriptor descriptor) : base(session, descriptor)
        => ProjectFrameRate = descriptor.ProjectFrameRate;

    public int ProjectFrameRate { get; set; }

    public bool SupportsSourceReplacement(IClip input, int targetWidth, int targetHeight)
    {
        try
        {
            return Invoke<IsolationSupportsSourceReplacementRequest, IsolationBooleanResponse>(RenderOperation.IsolationSupportsSourceReplacement, new()
            {
                ObjectId = ObjectId,
                Clip = IsolationClipSnapshotFactory.Create(input),
                TargetWidth = targetWidth,
                TargetHeight = targetHeight,
                State = CreateState(),
            }).Value;
        }
        catch { return false; }
    }

    public IPicture Compute(IClip input, IComputer? computer, IPicture source, int targetWidth, int targetHeight, uint targetFrame, IPicture.PicturePixelMode targetPPB)
    {
        var request = IsolationEffectFrameRequestFactory.Create(source, targetWidth, targetHeight);
        request.TargetFrame = targetFrame;
        request.TargetPixelMode = targetPPB.Value;
        request.Clip = IsolationClipSnapshotFactory.Create(input);
        return Process(RenderOperation.IsolationProcessSourceReplacement, request, source);
    }
}

internal static class IsolationEffectFrameRequestFactory
{
    private static readonly ConditionalWeakTable<IsolationEffectFrameRequest, Holder> Pictures = new();

    public static IsolationEffectFrameRequest Create(IPicture source, int targetWidth, int targetHeight, IPicture? second = null)
    {
        var request = new IsolationEffectFrameRequest { TargetWidth = targetWidth, TargetHeight = targetHeight };
        Pictures.Add(request, new(source, second));
        return request;
    }

    public static IPicture SourcePicture(this IsolationEffectFrameRequest request) => Pictures.GetValue(request, _ => throw new InvalidOperationException()).Source;
    public static IPicture? SecondPicture(this IsolationEffectFrameRequest request) => Pictures.GetValue(request, _ => throw new InvalidOperationException()).Second;
    private sealed record Holder(IPicture Source, IPicture? Second);
}

internal static class IsolationClipSnapshotFactory
{
    public static IsolationClipSnapshot Create(IClip clip)
    {
        var metadata = new Dictionary<string, IsolationValue>();
        foreach (var item in clip.ExtraData)
            try { metadata[item.Key] = IsolationValueConverter.FromObject(item.Value); } catch (NotSupportedException) { }
        return new()
        {
            FromPlugin = clip.FromPlugin,
            ClipType = (int)clip.ClipType,
            TypeName = clip.TypeName,
            Id = clip.Id.ToString(),
            Name = clip.Name,
            BoundSoundTrack = clip.BindedSoundTrack,
            LayerIndex = clip.LayerIndex,
            SubLayerIndex = clip.SubLayerIndex,
            StartFrame = clip.StartFrame,
            RelativeStartFrame = clip.RelativeStartFrame,
            Duration = clip.Duration,
            TargetWidth = clip.TargetWidth,
            TargetHeight = clip.TargetHeight,
            TargetX = clip.TargetX,
            TargetY = clip.TargetY,
            StartingX = clip.StartingX,
            StartingY = clip.StartingY,
            FrameTime = clip.FrameTime,
            ExtendToWholeDraft = clip.ExtendToWholeDraft,
            FileName = string.IsNullOrWhiteSpace(clip.FilePath) ? string.Empty : Path.GetFileName(clip.FilePath),
            FilePath = clip.FilePath ?? string.Empty,
            NeedFilePath = clip.NeedFilePath,
            Metadata = metadata,
        };
    }
}
