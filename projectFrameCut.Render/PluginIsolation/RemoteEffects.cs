using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Runtime.CompilerServices;

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
        _ => throw new NotSupportedException($"Remote effect kind '{descriptor.EffectType}' is not a picture effect."),
    };
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
            request.DynamicValues = GetDynamicValues();
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
            if (secondLease is not null) secondLease.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (sourceLease is not null) sourceLease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    protected Dictionary<string, IsolationValue> GetDynamicValues()
    {
        var values = new Dictionary<string, IsolationValue>();
        foreach (var id in DynamicProviderIds)
        {
            var value = ValueProviderFrameContext.Get(id);
            if (value is null) continue;
            try { values[id] = IsolationValueConverter.FromObject(value); } catch (NotSupportedException) { }
        }
        return values;
    }

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
            NeedFilePath = clip.NeedFilePath,
            Metadata = metadata,
        };
    }
}
