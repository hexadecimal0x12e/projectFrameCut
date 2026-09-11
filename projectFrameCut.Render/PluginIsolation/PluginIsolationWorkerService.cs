using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Reflection;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class PluginIsolationWorkerService(
    string authenticationToken,
    int expectedHostProcessId,
    string sessionRoot,
    string authorizedPluginId,
    string pluginDirectory,
    string decryptionKey,
    IIsolationPayloadExchange payloads,
    IIsolationResourceBroker resources,
    NamedPipePluginCommunicationService communication,
    CancellationTokenSource lifetime) : IRenderService, IDisposable
{
    private readonly string _authenticationToken = authenticationToken;
    private readonly int _expectedHostProcessId = expectedHostProcessId;
    private readonly string _sessionRoot = Path.GetFullPath(sessionRoot);
    private readonly string _authorizedPluginId = authorizedPluginId;
    private readonly string _pluginDirectory = Path.GetFullPath(pluginDirectory);
    private readonly string _decryptionKey = decryptionKey;
    private readonly IIsolationPayloadExchange _payloads = payloads;
    private readonly IIsolationResourceBroker _resources = resources;
    private readonly CancellationTokenSource _lifetime = lifetime;
    private readonly NamedPipePluginCommunicationService _communication = communication;
    private readonly ConcurrentDictionary<long, object> _objects = new();
    private long _nextObjectId;
    private IPluginBase? _plugin;
    private bool _authenticated;
    private IsolationPayloadKind _payloadKind = IsolationPayloadKind.SharedMemory;
    private readonly AsyncLocal<Guid?> _currentRequest = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<RemoteLogEntry>> _logs = new();
    private int _logSubscribed;

    public async ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _logSubscribed, 1) == 0) MyLoggerExtensions.OnLog += CaptureLog;
        Logger.Log($"Dispatching isolation operation '{request.Operation}' ({request.RequestId}).");
        _currentRequest.Value = request.RequestId;
        _logs.TryAdd(request.RequestId, new());
        RenderResponseEnvelope response;
        try
        {
            if (request.ProtocolVersion < RenderProtocol.MinimumSupportedVersion || request.ProtocolVersion > RenderProtocol.CurrentVersion)
                response = Failure(request, RenderErrorCode.ProtocolMismatch, "Unsupported render RPC protocol version.");
            else if (!_authenticated && request.Operation != RenderOperation.IsolationNegotiate)
                response = Failure(request, RenderErrorCode.Unauthorized, "The isolation session has not been authenticated.");
            else
                response = request.Operation switch
                {
                    RenderOperation.IsolationNegotiate => Negotiate(request),
                    RenderOperation.IsolationLoadPlugin => await LoadPluginAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationCreateProvider => CreateProvider(request),
                    RenderOperation.IsolationBuildProvider => BuildProvider(request),
                    RenderOperation.IsolationCloneEffect => CloneEffect(request),
                    RenderOperation.IsolationReleaseObject => ReleaseObject(request),
                    RenderOperation.IsolationProcessNormalEffect => await ProcessNormalAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationProcessContinuousEffect => await ProcessContinuousAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationProcessMixture => await ProcessMixtureAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationSupportsSourceReplacement => SupportsSourceReplacement(request),
                    RenderOperation.IsolationProcessSourceReplacement => await ProcessSourceReplacementAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationCreateVideoSource => CreateVideoSource(request),
                    RenderOperation.IsolationInitializeVideoSource => InitializeVideoSource(request),
                    RenderOperation.IsolationReadVideoFrame => await ReadVideoFrameAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationInvokeProjectTool => await InvokeProjectToolAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationUpdateProjectPluginConfiguration => UpdateProjectPluginConfiguration(request),
                    RenderOperation.IsolationCreatePluginChannel => await CreatePluginChannelAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationRegisterPluginChannel => RegisterPluginChannel(request),
                    RenderOperation.IsolationShutdown => Shutdown(request),
                    _ => Failure(request, RenderErrorCode.Unsupported, $"Unsupported isolation operation '{request.Operation}'."),
                };
        }
        catch (Exception ex)
        {
            Logger.Log(ex, $"dispatch isolation operation '{request.Operation}'", this);
            response = new() { RequestId = request.RequestId, Error = new(ex) };
        }
        if (_logs.TryRemove(request.RequestId, out var logs))
            while (logs.TryDequeue(out var log)) response.Logs.Add(log);
        _currentRequest.Value = null;
        Logger.Log($"Isolation operation '{request.Operation}' ({request.RequestId}) completed.");
        return response;
    }

    private async ValueTask<RenderResponseEnvelope> CreatePluginChannelAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationCreatePluginChannelRequest>(envelope);
        if (!string.Equals(request.TargetPluginId, _authorizedPluginId, StringComparison.Ordinal))
            return Failure(envelope, RenderErrorCode.Unauthorized, "The channel target must be the loaded plugin.");
        return Success(envelope, await _communication.CreateListenerAsync(request.SourcePluginId, request.TargetPluginId, cancellationToken).ConfigureAwait(false));
    }

    private RenderResponseEnvelope RegisterPluginChannel(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationRegisterPluginChannelRequest>(envelope);
        if (!string.Equals(request.Descriptor.SourcePluginId, request.SourcePluginId, StringComparison.Ordinal)
            || !string.Equals(request.Descriptor.TargetPluginId, _authorizedPluginId, StringComparison.Ordinal))
            return Failure(envelope, RenderErrorCode.Unauthorized, "The channel participants do not match the isolation session.");
        _communication.RegisterDescriptor(request.Descriptor);
        return Success(envelope, new EmptyResponse());
    }

    private RenderResponseEnvelope Negotiate(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationNegotiateRequest>(envelope);
        var supplied = System.Text.Encoding.UTF8.GetBytes(request.AuthenticationToken);
        var expected = System.Text.Encoding.UTF8.GetBytes(_authenticationToken);
        if (request.ProtocolVersion != PluginIsolationProtocol.CurrentVersion
            || request.HostProcessId != _expectedHostProcessId
            || request.PreferredPayloadKind is not (IsolationPayloadKind.Inline or IsolationPayloadKind.SharedMemory or IsolationPayloadKind.LocalFile)
            || supplied.Length != expected.Length
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(supplied, expected))
            return Failure(envelope, RenderErrorCode.Unauthorized, "Isolation handshake was rejected.");
        _authenticated = true;
        _payloadKind = request.PreferredPayloadKind;
        Logger.Log($"Isolation handshake authenticated for host process {_expectedHostProcessId}; payload kind: {_payloadKind}.");
        return Success(envelope, new IsolationChannelCapabilities
        {
            Platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "unknown",
            PayloadKinds = [IsolationPayloadKind.Inline, IsolationPayloadKind.SharedMemory, IsolationPayloadKind.LocalFile, IsolationPayloadKind.TransferredResource],
            SupportsResourceTransfer = true,
        });
    }

    private async ValueTask<RenderResponseEnvelope> LoadPluginAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        if (_plugin is not null) return Failure(envelope, RenderErrorCode.InvalidRequest, "A plugin is already loaded in this isolation session.");
        var request = Read<IsolationLoadPluginRequest>(envelope);
        if (!string.Equals(request.PluginId, _authorizedPluginId, StringComparison.Ordinal))
            return Failure(envelope, RenderErrorCode.Unauthorized, "The requested plugin does not match the authorized plugin.");
        var encryptedAssemblyPath = Path.Combine(_pluginDirectory, request.PluginId + ".dll.enc");
        Logger.Log($"Loading authorized plugin '{request.PluginId}' from encrypted assembly '{encryptedAssemblyPath}'.");
        var encryptedAssembly = await File.ReadAllBytesAsync(encryptedAssemblyPath, cancellationToken).ConfigureAwait(false);
        var assemblyBytes = FileCryptoService.DecryptToFileWithPassword(_decryptionKey, encryptedAssembly);
        var dependencyRoot = _pluginDirectory;
        ResolveEventHandler resolver = (_, eventArgs) =>
        {
            var name = new AssemblyName(eventArgs.Name).Name;
            var path = Directory.EnumerateFiles(dependencyRoot, name + ".dll", SearchOption.AllDirectories).FirstOrDefault();
            return path is not null ? Assembly.LoadFile(path) : null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolver;
        try
        {
            var assembly = Assembly.Load(assemblyBytes);
            var types = GetLoadableTypes(assembly);
            var loader = types.FirstOrDefault(x => x.Name == "PluginLoader") ?? types.FirstOrDefault(x => x.Name == "AppLevelPluginLoader")
                ?? throw new EntryPointNotFoundException("No PluginLoader was found in the plugin assembly.");
            if (loader.GetMethod("get_PluginAPIVersion", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is not int version
                || version != IPluginBase.CurrentPluginAPIVersion)
                throw new NotSupportedException("The plugin API version is not compatible with this runtime.");
            _plugin = loader.GetMethod("CreateInstance", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, [request.Locale, dependencyRoot]) as IPluginBase
                ?? throw new InvalidDataException("PluginLoader.CreateInstance did not return IPluginBase.");
            if (!string.Equals(_plugin.PluginID, request.PluginId, StringComparison.Ordinal)) throw new InvalidDataException("The loaded plugin id does not match the request.");
            foreach (var item in request.Configuration)
                if (_plugin.Configuration.ContainsKey(item.Key)) _plugin.Configuration[item.Key] = item.Value;
            if (!_plugin.OnLoaded(out var reason)) throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason) ? "Plugin OnLoaded failed." : reason);
        }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }

        return Success(envelope, new IsolationPluginDescriptor
        {
            PluginId = _plugin.PluginID,
            PluginApiVersion = _plugin.PluginAPIVersion,
            Providers = _plugin.EffectProviderProvider.Select(x => (x.Key, Provider: x.Value()))
                .Where(x => IsPictureEffect(x.Provider.TypeOfEffect)
                    && !x.Provider.InFields.Values.Any(f => f.FieldType.HasFlag(EffectArgumentFieldType.CustomType)))
                .Select(x => new IsolationProviderCatalogItem { TypeName = x.Key }).ToList(),
            VideoSources = _plugin.VideoSourceProvider.Select(x => DescribeVideoSource(x.Value)).ToList(),
            ProjectTools = (_plugin as IProjectPluginToolProvider)?.ProjectTools.Keys.ToList() ?? [],
        });
    }

    private async ValueTask<RenderResponseEnvelope> InvokeProjectToolAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationInvokeProjectToolRequest>(envelope);
        var plugin = RequirePlugin() as IProjectPluginToolProvider
            ?? throw new NotSupportedException("The plugin does not provide project tools.");
        if (!plugin.ProjectTools.TryGetValue(request.ToolId, out var tool))
            throw new KeyNotFoundException($"Project tool '{request.ToolId}' was not found.");
        _ = System.Text.Json.JsonDocument.Parse(request.InputJson);
        var output = await tool(request.InputJson, cancellationToken).ConfigureAwait(false);
        return Success(envelope, new IsolationInvokeProjectToolResponse { OutputJson = output ?? string.Empty });
    }

    private RenderResponseEnvelope UpdateProjectPluginConfiguration(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationUpdateProjectPluginConfigurationRequest>(envelope);
        var plugin = RequirePlugin();
        foreach (var item in request.Configuration)
            if (plugin.Configuration.ContainsKey(item.Key)) plugin.Configuration[item.Key] = item.Value;
        return Success(envelope, new EmptyResponse());
    }

    private RenderResponseEnvelope CreateProvider(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationCreateProviderRequest>(envelope);
        var plugin = RequirePlugin();
        if (!plugin.EffectProviderProvider.TryGetValue(request.TypeName, out var factory)) throw new KeyNotFoundException($"Effect provider '{request.TypeName}' was not found.");
        var provider = factory();
        if (!IsPictureEffect(provider.TypeOfEffect)) throw new NotSupportedException("Only picture-output effect providers can run in isolation.");
        return Success(envelope, DescribeProvider(AddObject(provider), provider));
    }

    private RenderResponseEnvelope BuildProvider(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationBuildProviderRequest>(envelope);
        var provider = Require<IEffectProvider>(request.Provider.ObjectId);
        ApplyProviderState(provider, request.Provider);
        var response = new IsolationEffectList();
        foreach (var effect in provider.Build())
        {
            if (!IsPictureEffect(effect.TypeOfEffect)) throw new NotSupportedException("Provider returned a non-picture effect.");
            effect.Initialize();
            response.Effects.Add(DescribeEffect(AddObject(effect), effect, request.Provider));
        }
        return Success(envelope, response);
    }

    private RenderResponseEnvelope CloneEffect(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationCloneEffectRequest>(envelope);
        var source = Require<IEffect>(request.ObjectId);
        var clone = source.WithParameters(request.Parameters.ToDictionary(x => x.Key, x => IsolationValueConverter.ToObject(x.Value)!));
        clone.Initialize();
        return Success(envelope, DescribeEffect(AddObject(clone), clone, null));
    }

    private RenderResponseEnvelope ReleaseObject(RenderRequestEnvelope envelope)
    {
        var id = Read<IsolationReleaseObjectRequest>(envelope).ObjectId;
        if (_objects.TryRemove(id, out var value) && value is IDisposable disposable) disposable.Dispose();
        return Success(envelope, new EmptyResponse());
    }

    private async ValueTask<RenderResponseEnvelope> ProcessNormalAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationEffectFrameRequest>(envelope);
        var effect = Require<INormalEffect>(request.ObjectId);
        using var source = await PicturePayloadCodec.ReadAsync(request.Source, _payloads, cancellationToken).ConfigureAwait(false);
        return await WithFrameContextAsync(envelope, request, () => effect.Render(source, ResolveComputer(effect), request.TargetWidth, request.TargetHeight), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RenderResponseEnvelope> ProcessContinuousAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationEffectFrameRequest>(envelope);
        var effect = Require<IContinuousEffect>(request.ObjectId);
        using var source = await PicturePayloadCodec.ReadAsync(request.Source, _payloads, cancellationToken).ConfigureAwait(false);
        return await WithFrameContextAsync(envelope, request, () => effect.Render(source, request.Progress, ResolveComputer(effect), request.TargetWidth, request.TargetHeight), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RenderResponseEnvelope> ProcessMixtureAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationEffectFrameRequest>(envelope);
        var effect = Require<IMixture>(request.ObjectId);
        using var source = await PicturePayloadCodec.ReadAsync(request.Source, _payloads, cancellationToken).ConfigureAwait(false);
        using var top = await PicturePayloadCodec.ReadAsync(request.SecondSource ?? throw new InvalidDataException("Mixture top picture is missing."), _payloads, cancellationToken).ConfigureAwait(false);
        return await WithFrameContextAsync(envelope, request, () => request.UsePositionedMixture
            ? effect.Mix(source, top, ResolveComputer(effect), request.TargetPixelMode, request.TopStartX, request.TopStartY, request.TargetWidth, request.TargetHeight)
            : effect.Mix(source, top, ResolveComputer(effect), request.TargetPixelMode), cancellationToken).ConfigureAwait(false);
    }

    private RenderResponseEnvelope SupportsSourceReplacement(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationSupportsSourceReplacementRequest>(envelope);
        using var clip = new SnapshotClip(request.Clip);
        return Success(envelope, new IsolationBooleanResponse { Value = Require<ISourceReplacementEffect>(request.ObjectId).SupportsSourceReplacement(clip, request.TargetWidth, request.TargetHeight) });
    }

    private async ValueTask<RenderResponseEnvelope> ProcessSourceReplacementAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationEffectFrameRequest>(envelope);
        var effect = Require<ISourceReplacementEffect>(request.ObjectId);
        using var clip = new SnapshotClip(request.Clip ?? throw new InvalidDataException("Source replacement clip snapshot is missing."));
        using var source = await PicturePayloadCodec.ReadAsync(request.Source, _payloads, cancellationToken).ConfigureAwait(false);
        return await WithFrameContextAsync(envelope, request, () => effect.Compute(clip, ResolveComputer(effect), source, request.TargetWidth, request.TargetHeight, request.TargetFrame, request.TargetPixelMode), cancellationToken).ConfigureAwait(false);
    }

    private RenderResponseEnvelope CreateVideoSource(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationCreateVideoSourceRequest>(envelope);
        var plugin = RequirePlugin();
        if (!plugin.VideoSourceProvider.TryGetValue(request.TypeName, out var prototype)) throw new KeyNotFoundException($"Video source '{request.TypeName}' was not found.");
        var source = prototype.CreateNew(_resources.ResolveReadOnlyFile(request.Source));
        return Success(envelope, DescribeVideoSource(source, AddObject(source)));
    }

    private RenderResponseEnvelope InitializeVideoSource(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationVideoSourceStateRequest>(envelope);
        var source = Require<IVideoSource>(request.ObjectId);
        ApplyVideoState(source, request);
        source.Initialize();
        return Success(envelope, DescribeVideoSource(source, request.ObjectId));
    }

    private async ValueTask<RenderResponseEnvelope> ReadVideoFrameAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationReadVideoFrameRequest>(envelope);
        var source = Require<IVideoSource>(request.State.ObjectId);
        ApplyVideoState(source, request.State);
        IPicture picture;
        if (request.RequestHdr)
        {
            var hdr = source as IHDRVideoSource ?? throw new NotSupportedException("The selected video source does not support HDR output.");
            picture = request.UseRegion
                ? hdr.GetHDRFrame(request.TargetFrame, request.SourceX, request.SourceY, request.SourceWidth, request.SourceHeight, request.TargetWidth, request.TargetHeight, request.HasAlpha)
                : hdr.GetHDRFrame(request.TargetFrame, request.HasAlpha);
        }
        else
        {
            picture = request.UseRegion
                ? source.GetFrame(request.TargetFrame, request.SourceX, request.SourceY, request.SourceWidth, request.SourceHeight, request.TargetWidth, request.TargetHeight)
                : source.GetFrame(request.TargetFrame);
        }
        using (picture)
        {
            var lease = await PicturePayloadCodec.WriteAsync(picture, _payloads, _payloadKind, cancellationToken).ConfigureAwait(false);
            return Success(envelope, new IsolationPictureResponse { Picture = lease.Reference });
        }
    }

    private RenderResponseEnvelope Shutdown(RenderRequestEnvelope envelope)
    {
        Dispose();
        _ = Task.Run(async () =>
        {
            await Task.Delay(100).ConfigureAwait(false);
            try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        });
        return Success(envelope, new EmptyResponse());
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var item in _objects.Values.OfType<IDisposable>())
            try { item.Dispose(); } catch { }
        _objects.Clear();
        try { _plugin?.OnClosing(); } catch { }
        MyLoggerExtensions.OnLog -= CaptureLog;
    }

    private async ValueTask<RenderResponseEnvelope> WithFrameContextAsync(RenderRequestEnvelope envelope, IsolationEffectFrameRequest request, Func<IPicture> render, CancellationToken cancellationToken)
    {
        ValueProviderFrameContext.BeginFrame(request.TargetFrame, request.Progress);
        try
        {
            foreach (var item in request.DynamicValues) ValueProviderFrameContext.Set(item.Key, IsolationValueConverter.ToObject(item.Value));
            using var output = render();
            var lease = await PicturePayloadCodec.WriteAsync(output, _payloads, _payloadKind, cancellationToken).ConfigureAwait(false);
            return Success(envelope, new IsolationPictureResponse { Picture = lease.Reference });
        }
        finally { ValueProviderFrameContext.EndFrame(); }
    }

    private IComputer? ResolveComputer(IEffect effect)
    {
        if (string.IsNullOrWhiteSpace(effect.NeedComputer)) return null;
        var plugin = RequirePlugin();
        return plugin.ComputerProvider.TryGetValue(effect.NeedComputer, out var factory) ? factory() : null;
    }

    private void ApplyProviderState(IEffectProvider provider, IsolationProviderState state)
    {
        provider.Enabled = state.Enabled;
        provider.Id = Guid.TryParse(state.InstanceId, out var id) ? id : Guid.NewGuid();
        provider.Name = state.Name;
        provider.AnchorsBindingState = new(state.AnchorBindings);
        provider.MetaData = state.Metadata.ToDictionary(x => x.Key, x => IsolationValueConverter.ToObject(x.Value)!);
        var fields = new Dictionary<string, IEffectArgumentField>();
        foreach (var item in state.Fields)
        {
            var type = (EffectArgumentFieldType)item.FieldType;
            if (type.HasFlag(EffectArgumentFieldType.CustomType)) throw new NotSupportedException($"Custom field '{item.Id}' cannot cross the isolation boundary.");
            fields[item.Id] = item.IsDynamic
                ? new DynamicEffectParamField(item.Id, type, item.BoundProviderId, IsolationValueConverter.ToObject(item.Value)) { FieldType = type }
                : new StaticEffectArgumentField(IsolationValueConverter.ToObject(item.Value) ?? item.DefaultValue, type);
        }
        provider.Fields = fields;
    }

    private IsolationProviderDescriptor DescribeProvider(long objectId, IEffectProvider provider) => new()
    {
        ObjectId = objectId,
        TypeName = provider.TypeName,
        FromPlugin = provider.FromPlugin,
        EffectType = (int)provider.TypeOfEffect,
        Target = (int)provider.Target,
        Enabled = provider.Enabled,
        InstanceId = provider.Id.ToString(),
        Name = provider.Name,
        InputFields = provider.InFields.Select(x => DescribeField(x.Value)).ToList(),
        OutputField = DescribeField(provider.OutField),
        SupportedImplementTypes = provider.SupportsImplementTypes.Select(x => (int)x).ToList(),
        DefaultImplementType = (int)provider.DefaultImplementType,
    };

    private static IsolationFieldDescriptor DescribeField(IEffectArgumentField field) => new()
    {
        Id = field.Id,
        TypeName = field.TypeName,
        FromPlugin = field.FromPlugin,
        FieldType = (ulong)field.FieldType,
        DefaultValue = field.DefaultValue,
        MinimumValue = field.MinValue,
        MaximumValue = field.MaxValue,
        PresetOptions = field.PresetOptions?.ToList() ?? [],
        Remarks = field.Remarks ?? string.Empty,
        IsDynamic = field.IsDynamic,
        Value = TryValue(field),
    };

    private static IsolationValue TryValue(IEffectArgumentField field)
    {
        try { return IsolationValueConverter.FromObject(field.GetGetter()()); }
        catch { return new() { Kind = IsolationValueKind.String, StringValue = field.DefaultValue }; }
    }

    private static IsolationEffectDescriptor DescribeEffect(long objectId, IEffect effect, IsolationProviderState? provider) => new()
    {
        ObjectId = objectId,
        FromPlugin = effect.FromPlugin,
        TypeName = effect.TypeName,
        EffectType = (int)effect.TypeOfEffect,
        ImplementType = (int)effect.ImplementType,
        Name = effect.Name,
        InstanceId = effect.Id,
        Enabled = effect.Enabled,
        Index = effect.Index,
        IsReorderable = effect.IsReorderable,
        CanProcessFromCanvas = effect.CanProcessFromCanvas,
        NeedComputer = effect.NeedComputer ?? string.Empty,
        RelativeWidth = effect.RelativeWidth,
        RelativeHeight = effect.RelativeHeight,
        StartPoint = effect is IContinuousEffect continuous ? continuous.StartPoint : 0,
        EndPoint = effect is IContinuousEffect continuous2 ? continuous2.EndPoint : 0,
        IsScoped = effect is IContinuousEffect continuous3 && continuous3.IsScoped,
        ProjectFrameRate = effect is ISourceReplacementEffect replacement ? replacement.ProjectFrameRate : 0,
        Parameters = effect.Parameters.Where(x => !DynamicParam.IsDynamicValue(x.Value)).ToDictionary(x => x.Key, x => IsolationValueConverter.FromObject(x.Value)),
        DynamicProviderIds = provider?.Fields.Where(x => x.IsDynamic && !string.IsNullOrWhiteSpace(x.BoundProviderId)).Select(x => x.BoundProviderId).Distinct().ToList() ?? [],
        IsColorAdjust = effect is IColorAdjustEffect,
    };

    private static IsolationVideoSourceDescriptor DescribeVideoSource(IVideoSource source, long objectId = 0) => new()
    {
        ObjectId = objectId,
        TypeName = source.TypeName,
        PreferredExtensions = source.PreferredExtension.ToList(),
        ResultBitsPerPixel = source.ResultBitPerPixel ?? 0,
        HasKnownResultBitsPerPixel = source.ResultBitPerPixel.HasValue,
        TotalFrames = source.TotalFrames,
        Fps = source.Fps,
        Width = source.Width,
        Height = source.Height,
        SupportsHdr = source is IHDRVideoSource,
    };

    private static void ApplyVideoState(IVideoSource source, IsolationVideoSourceStateRequest state)
    {
        source.Index = state.Index;
        source.EnableLock = state.EnableLock;
        source.StrictMode = state.StrictMode;
    }

    private long AddObject(object value)
    {
        var id = Interlocked.Increment(ref _nextObjectId);
        if (!_objects.TryAdd(id, value)) throw new InvalidOperationException("Failed to allocate a remote object id.");
        return id;
    }

    private T Require<T>(long id) where T : class
        => _objects.TryGetValue(id, out var value) && value is T typed ? typed : throw new KeyNotFoundException($"Remote object '{id}' was not found or has the wrong type.");

    private IPluginBase RequirePlugin() => _plugin ?? throw new InvalidOperationException("No plugin has been loaded.");

    private string ResolveSessionDirectory(string locator)
    {
        if (string.IsNullOrWhiteSpace(locator) || Path.IsPathRooted(locator)) throw new InvalidDataException("Dependency directory locator must be relative.");
        var root = _sessionRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root, locator));
        if (!result.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Dependency directory escapes the session root.");
        return result;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }

    private static bool IsPictureEffect(EffectType type) => type is EffectType.NormalEffect or EffectType.ContinuousEffect or EffectType.MixtureProvider or EffectType.SourceReplacement;
    private void CaptureLog(string message, string level)
    {
        if (_currentRequest.Value is Guid requestId && _logs.TryGetValue(requestId, out var logs))
            logs.Enqueue(new() { Level = level, Message = message });
    }
    private static T Read<T>(RenderRequestEnvelope request) => RenderRpcSerializer.Deserialize<T>(request.Payload);
    private static RenderResponseEnvelope Success<T>(RenderRequestEnvelope request, T payload) => new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(payload) };
    private static RenderResponseEnvelope Failure(RenderRequestEnvelope request, RenderErrorCode code, string message) => new() { RequestId = request.RequestId, Error = new() { Code = code, Message = message, Details = message } };
}
