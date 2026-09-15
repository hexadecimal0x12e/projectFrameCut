using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
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
    CancellationTokenSource lifetime,
    Func<IsolationLoadPluginRequest, IPluginBase>? externalPluginFactory = null) : IRenderService, IDisposable
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
    private readonly Func<IsolationLoadPluginRequest, IPluginBase>? _externalPluginFactory = externalPluginFactory;
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
                    RenderOperation.IsolationBuildProvider => await BuildProviderAsync(request, cancellationToken).ConfigureAwait(false),
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
                    RenderOperation.IsolationPluginProjectLoad => ProjectLifecycle(request, static (plugin, project) => plugin.OnProjectLoad(project)),
                    RenderOperation.IsolationPluginProjectSave => ProjectLifecycle(request, static (plugin, project) => plugin.OnProjectSave(project)),
                    RenderOperation.IsolationPluginProjectClose => ProjectLifecycle(request, static (plugin, project) => plugin.OnProjectClose(project)),
                    RenderOperation.IsolationCreateAudioSource => CreateAudioSource(request),
                    RenderOperation.IsolationInitializeAudioSource => InitializeAudioSource(request),
                    RenderOperation.IsolationReadAudioSamples => await ReadAudioSamplesAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationCreateVideoWriter => CreateVideoWriter(request),
                    RenderOperation.IsolationInitializeVideoWriter => InitializeVideoWriter(request),
                    RenderOperation.IsolationAppendVideoWriterFrame => await AppendVideoWriterFrameAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationFinishVideoWriter => FinishVideoWriter(request),
                    RenderOperation.IsolationVideoWriterSupportsCodec => VideoWriterSupportsCodec(request),
                    RenderOperation.IsolationCreateTransform => CreateTransform(request),
                    RenderOperation.IsolationInitializeTransform => InitializeTransform(request),
                    RenderOperation.IsolationProcessTransform => await ProcessTransformAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationCreateComputer => CreateComputer(request),
                    RenderOperation.IsolationCompute => await ComputeAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationCreateClip => CreateClip(request),
                    RenderOperation.IsolationReadClipFrame => await ReadClipFrameAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationReinitializeClip => ReinitializeClip(request),
                    RenderOperation.IsolationCreateSoundTrack => CreateSoundTrack(request),
                    RenderOperation.IsolationReadSoundTrackSamples => await ReadSoundTrackSamplesAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationReinitializeSoundTrack => ReinitializeSoundTrack(request),
                    RenderOperation.IsolationCreateVectorComponent => CreateVectorComponent(request),
                    RenderOperation.IsolationComputeVectorComponent => ComputeVectorComponent(request),
                    RenderOperation.IsolationProcessAudioEffect => await ProcessAudioEffectAsync(request, cancellationToken).ConfigureAwait(false),
                    RenderOperation.IsolationProcessTextEffect => ProcessTextEffect(request),
                    RenderOperation.IsolationMapSpeedFrame => MapSpeed(request, false),
                    RenderOperation.IsolationMapSpeedLength => MapSpeed(request, true),
                    RenderOperation.IsolationGetClipPosition => GetClipPosition(request),
                    RenderOperation.IsolationGetEffectValue => GetEffectValue(request),
                    RenderOperation.IsolationShutdown => Shutdown(request),
                    _ => UnsupportedOperation(request),
                };
        }
        catch (Exception ex)
        {
            if (_externalPluginFactory is not null && ex is InvalidDataException or KeyNotFoundException)
                _lifetime.Cancel();
            Logger.Log(ex, $"dispatch isolation operation '{request.Operation}'", this);
            response = new() { RequestId = request.RequestId, Error = new(ex) };
        }
        if (_logs.TryRemove(request.RequestId, out var logs))
            while (logs.TryDequeue(out var log)) response.Logs.Add(log);
        _currentRequest.Value = null;
        Logger.Log($"Isolation operation '{request.Operation}' ({request.RequestId}) completed.");
        return response;
    }

    private RenderResponseEnvelope UnsupportedOperation(RenderRequestEnvelope request)
    {
        if (_externalPluginFactory is not null) _lifetime.Cancel();
        return Failure(request, RenderErrorCode.Unsupported, $"Unsupported isolation operation '{request.Operation}'.");
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
        if (_externalPluginFactory is not null)
        {
            _plugin = _externalPluginFactory(request) ?? throw new InvalidDataException("The external backend factory returned null.");
        }
        else
        {
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
            }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
        }

        if (!string.Equals(_plugin.PluginID, request.PluginId, StringComparison.Ordinal)) throw new InvalidDataException("The loaded plugin id does not match the request.");
        if (_plugin.PluginAPIVersion != IPluginBase.CurrentPluginAPIVersion) throw new NotSupportedException("The plugin API version is not compatible with this runtime.");
        foreach (var item in request.Configuration)
            if (_plugin.Configuration.ContainsKey(item.Key)) _plugin.Configuration[item.Key] = item.Value;
        if (!_plugin.OnLoaded(out var reason)) throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason) ? "Plugin OnLoaded failed." : reason);
        if (_externalPluginFactory is not null)
        {
            foreach (var item in _plugin.EffectProviderProvider)
            {
                var provider = item.Value();
                if (!IsSupportedExternalEffect(provider.TypeOfEffect))
                    throw new NotSupportedException($"External effect provider '{item.Key}' uses unsupported effect type '{provider.TypeOfEffect}'.");
                var custom = provider.InFields.Values.FirstOrDefault(x => x.FieldType.HasFlag(EffectArgumentFieldType.CustomType));
                if (custom is not null) throw new NotSupportedException($"External effect provider '{item.Key}' field '{custom.Id}' uses unsupported CustomType '{custom.TypeName}'.");
                if (provider.OutField.FieldType.HasFlag(EffectArgumentFieldType.CustomType))
                    throw new NotSupportedException($"External effect provider '{item.Key}' output field '{provider.OutField.Id}' uses unsupported CustomType '{provider.OutField.TypeName}'.");
            }
        }

        return Success(envelope, new IsolationPluginDescriptor
        {
            PluginId = _plugin.PluginID,
            PluginApiVersion = _plugin.PluginAPIVersion,
            Providers = _plugin.EffectProviderProvider.Select(x => (x.Key, Provider: x.Value()))
                .Where(x => (_externalPluginFactory is not null || IsPictureEffect(x.Provider.TypeOfEffect))
                    && !x.Provider.InFields.Values.Any(f => f.FieldType.HasFlag(EffectArgumentFieldType.CustomType)))
                .Select(x => new IsolationProviderCatalogItem { TypeName = x.Key }).ToList(),
            VideoSources = _plugin.VideoSourceProvider.Select(x => DescribeVideoSource(x.Value)).ToList(),
            ProjectTools = (_plugin as IProjectPluginToolProvider)?.ProjectTools.Keys.ToList() ?? [],
            PluginApiMinorVersion = _plugin.PluginAPIMinorVersion,
            Name = _plugin.Name,
            Author = _plugin.Author,
            Description = _plugin.Description,
            Version = _plugin.Version.ToString(),
            AuthorUrl = _plugin.AuthorUrl,
            PublishingUrl = _plugin.PublishingUrl ?? string.Empty,
            Properties = new(_plugin.Properties),
            Localization = _plugin.LocalizationProvider.ToDictionary(x => x.Key, x => new IsolationStringMap { Values = new(x.Value) }),
            Configuration = new(_plugin.Configuration),
            ConfigurationDisplayStrings = _plugin.ConfigurationDisplayString.ToDictionary(x => x.Key, x => new IsolationStringMap { Values = new(x.Value) }),
            SoundTracks = _plugin.SoundTrackProvider.Keys.ToList(),
            Transforms = _plugin.TransformProvider.Keys.ToList(),
            Computers = _plugin.ComputerProvider.Keys.ToList(),
            AudioSources = _plugin.AudioSourceProvider.Select(x => new IsolationAudioSourceCatalogItem
            {
                TypeName = x.Key,
                PreferredExtensions = [],
            }).ToList(),
            VideoWriters = _plugin.VideoWriterProvider.Keys.ToList(),
            ProvidesClips = Implements(nameof(IPluginBase.ClipCreator)),
            ProvidesVectorComponents = Implements(nameof(IPluginBase.VectComponentCreator)),
        });

        bool Implements(string name) => _plugin.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(x => x.Name == name);
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
        return Success(envelope, DescribeProvider(AddObject(provider), provider));
    }

    private async ValueTask<RenderResponseEnvelope> BuildProviderAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationBuildProviderRequest>(envelope);
        var provider = Require<IEffectProvider>(request.Provider.ObjectId);
        var pictures = new Dictionary<string, IPicture>();
        var retained = false;
        try
        {
            foreach (var field in request.Provider.Fields.Where(x => x.PictureValue is not null))
                pictures[field.Id] = await PicturePayloadCodec.ReadAsync(field.PictureValue!, _payloads, cancellationToken).ConfigureAwait(false);
            ApplyProviderState(provider, request.Provider, pictures);
            var response = new IsolationEffectList();
            foreach (var effect in provider.Build())
            {
                effect.Initialize();
                response.Effects.Add(DescribeEffect(AddObject(effect), effect, request.Provider));
            }
            foreach (var picture in pictures.Values) AddObject(picture);
            retained = true;
            return Success(envelope, response);
        }
        finally
        {
            if (!retained)
                foreach (var picture in pictures.Values) picture.Dispose();
        }
    }

    private RenderResponseEnvelope CreateVectorComponent(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationVectorComponentRequest>(envelope);
        using var json = System.Text.Json.JsonDocument.Parse(request.Json);
        var component = RequirePlugin().VectComponentCreator(json.RootElement);
        return Success(envelope, DescribeVectorComponent(component, AddObject(component)));
    }

    private RenderResponseEnvelope ComputeVectorComponent(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationVectorComponentRequest>(envelope);
        var component = Require<IVectorComponent>(request.ObjectId);
        component.Name = request.Name;
        component.Id = Guid.Parse(request.InstanceId);
        component.Index = request.Index;
        foreach (var item in request.Parameters)
            component.Parameters[item.Key] = IsolationValueConverter.ToObject(item.Value)!;
        component.AnimationFrames = System.Text.Json.JsonSerializer.Deserialize<List<VectorAnimationKeyFrame>>(request.AnimationFramesJson) ?? [];
        var response = new IsolationVectorElementList();
        foreach (var element in component.ComputeAll(request.Progress))
        {
            var target = new IsolationVectorElement
            {
                RelativeX = element.RelativeX,
                RelativeY = element.RelativeY,
                BaseX = element.BaseX,
                BaseY = element.BaseY,
                LayerIndex = element.LayerIndex,
                Rotation = element.Rotation,
                UseUniformScale = element.UseUniformScale,
            };
            foreach (var segment in element.Draw())
            {
                if (!IsBuiltInVectorSegment(segment.GetType()))
                    throw new NotSupportedException($"Vector component '{component.TypeName}' returned unsupported segment type '{segment.GetType().FullName}'.");
                target.Segments.Add(new() { TypeName = segment.GetType().Name, Json = System.Text.Json.JsonSerializer.Serialize(segment, segment.GetType()) });
            }
            response.Elements.Add(target);
        }
        return Success(envelope, response);
    }

    private async ValueTask<RenderResponseEnvelope> ProcessAudioEffectAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationEffectInvokeRequest>(envelope);
        var inputDescriptor = new IsolationAudioSamplesResponse
        {
            Samples = request.Audio ?? throw new InvalidDataException("Audio effect input is missing."),
            ChannelCount = request.ChannelCount,
            SamplePerSecond = request.SamplePerSecond,
            SampleCount = request.SampleCount,
        };
        var input = await AudioPayloadCodec.ReadAsync(inputDescriptor, _payloads, cancellationToken).ConfigureAwait(false);
        var output = Require<IEffect>(request.ObjectId) switch
        {
            IAudioNormalEffect effect => effect.Process(input),
            IAudioContinuousEffect effect => effect.Process(input, request.Progress),
            _ => throw new NotSupportedException("The remote effect is not an audio effect."),
        };
        var lease = await _payloads.PublishAsync(AudioPayloadCodec.Encode(output), _payloadKind, cancellationToken).ConfigureAwait(false);
        return Success(envelope, new IsolationAudioSamplesResponse
        {
            Samples = lease.Reference,
            ChannelCount = output.channelCount,
            SamplePerSecond = output.SamplePerSecond,
            SampleCount = output.SampleCount,
        });
    }

    private RenderResponseEnvelope ProcessTextEffect(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationEffectInvokeRequest>(envelope);
        var input = System.Text.Json.JsonSerializer.Deserialize<TextEntry[]>(request.Json) ?? [];
        var output = Require<IEffect>(request.ObjectId) switch
        {
            ITextEffect effect => effect.Process(input),
            IContinuousTextEffect effect => effect.Process(input, request.Progress),
            _ => throw new NotSupportedException("The remote effect is not a text effect."),
        };
        return Success(envelope, new IsolationEffectInvokeResponse { Json = System.Text.Json.JsonSerializer.Serialize(output) });
    }

    private RenderResponseEnvelope MapSpeed(RenderRequestEnvelope envelope, bool length)
    {
        var request = Read<IsolationEffectInvokeRequest>(envelope);
        var effect = Require<ISpeedVarianceProvider>(request.ObjectId);
        return Success(envelope, new IsolationEffectInvokeResponse { Value = length ? effect.GetEffectiveLength(request.Value) : effect.GetTargetFrame(request.Value) });
    }

    private RenderResponseEnvelope GetClipPosition(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationEffectInvokeRequest>(envelope);
        using var clip = new SnapshotClip(request.Clip ?? throw new InvalidDataException("Clip position input is missing."));
        var position = Require<IEffect>(request.ObjectId) switch
        {
            IClipPositionProvider effect => effect.GetPosition(clip, request.TargetWidth, request.TargetHeight),
            IContinuousClipPositionProvider effect => effect.GetPosition(clip, request.Value, request.TargetWidth, request.TargetHeight),
            _ => throw new NotSupportedException("The remote effect is not a clip position provider."),
        };
        return Success(envelope, new IsolationEffectInvokeResponse
        {
            TargetX = position.TargetX,
            TargetY = position.TargetY,
            TargetWidth = position.TargetWidth,
            TargetHeight = position.TargetHeight,
            IsDelta = position.IsDelta,
        });
    }

    private RenderResponseEnvelope GetEffectValue(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationEffectInvokeRequest>(envelope);
        var field = Require<IValueProviderEffect>(request.ObjectId);
        return Success(envelope, new IsolationEffectInvokeResponse { Json = System.Text.Json.JsonSerializer.Serialize(IsolationValueConverter.FromObject(field.GetGetter()())) });
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
        ApplyEffectState(effect, request.State);
        using var source = await PicturePayloadCodec.ReadAsync(request.Source, _payloads, cancellationToken).ConfigureAwait(false);
        return await WithFrameContextAsync(envelope, request, () => effect.Render(source, ResolveComputer(effect), request.TargetWidth, request.TargetHeight), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RenderResponseEnvelope> ProcessContinuousAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationEffectFrameRequest>(envelope);
        var effect = Require<IContinuousEffect>(request.ObjectId);
        ApplyEffectState(effect, request.State);
        using var source = await PicturePayloadCodec.ReadAsync(request.Source, _payloads, cancellationToken).ConfigureAwait(false);
        return await WithFrameContextAsync(envelope, request, () => effect.Render(source, request.Progress, ResolveComputer(effect), request.TargetWidth, request.TargetHeight), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RenderResponseEnvelope> ProcessMixtureAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationEffectFrameRequest>(envelope);
        var effect = Require<IMixture>(request.ObjectId);
        ApplyEffectState(effect, request.State);
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
        var effect = Require<ISourceReplacementEffect>(request.ObjectId);
        ApplyEffectState(effect, request.State);
        return Success(envelope, new IsolationBooleanResponse { Value = effect.SupportsSourceReplacement(clip, request.TargetWidth, request.TargetHeight) });
    }

    private async ValueTask<RenderResponseEnvelope> ProcessSourceReplacementAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationEffectFrameRequest>(envelope);
        var effect = Require<ISourceReplacementEffect>(request.ObjectId);
        ApplyEffectState(effect, request.State);
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

    private RenderResponseEnvelope CreateAudioSource(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationCreateAudioSourceRequest>(envelope);
        if (!RequirePlugin().AudioSourceProvider.TryGetValue(request.TypeName, out var factory))
            throw new KeyNotFoundException($"Audio source '{request.TypeName}' was not found.");
        var source = factory(request.Source);
        return Success(envelope, DescribeAudioSource(AddObject(source), request.TypeName, source));
    }

    private RenderResponseEnvelope InitializeAudioSource(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationReleaseObjectRequest>(envelope);
        var source = Require<IAudioSource>(request.ObjectId);
        source.Initialize();
        return Success(envelope, DescribeAudioSource(request.ObjectId, string.Empty, source));
    }

    private async ValueTask<RenderResponseEnvelope> ReadAudioSamplesAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationReadAudioSamplesRequest>(envelope);
        var samples = Require<IAudioSource>(request.ObjectId).GetSample(request.StartIndex, request.Count);
        var data = AudioPayloadCodec.Encode(samples);
        var lease = await _payloads.PublishAsync(data, _payloadKind, cancellationToken).ConfigureAwait(false);
        return Success(envelope, new IsolationAudioSamplesResponse
        {
            Samples = lease.Reference,
            ChannelCount = samples.channelCount,
            SamplePerSecond = samples.SamplePerSecond,
            SampleCount = samples.SampleCount,
        });
    }

    private static IsolationAudioSourceDescriptor DescribeAudioSource(long id, string typeName, IAudioSource source) => new()
    {
        ObjectId = id,
        TypeName = typeName,
        PreferredExtensions = source.PreferredExtension.ToList(),
        Duration = source.Duration,
        ChannelCount = source.ChannelCount,
        SamplePerSecond = source.SamplePerSecond,
    };

    private RenderResponseEnvelope CreateVideoWriter(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationCreateVideoWriterRequest>(envelope);
        if (!RequirePlugin().VideoWriterProvider.TryGetValue(request.TypeName, out var factory))
            throw new KeyNotFoundException($"Video writer '{request.TypeName}' was not found.");
        var writer = factory(request.FactoryArgument);
        return Success(envelope, DescribeVideoWriter(AddObject(writer), writer));
    }

    private RenderResponseEnvelope InitializeVideoWriter(RenderRequestEnvelope envelope)
    {
        var state = Read<IsolationVideoWriterState>(envelope);
        var writer = Require<IVideoWriter>(state.ObjectId);
        ApplyVideoWriterState(writer, state);
        writer.Initialize();
        return Success(envelope, DescribeVideoWriter(state.ObjectId, writer));
    }

    private async ValueTask<RenderResponseEnvelope> AppendVideoWriterFrameAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationVideoWriterFrameRequest>(envelope);
        var writer = Require<IVideoWriter>(request.State.ObjectId);
        ApplyVideoWriterState(writer, request.State);
        using var picture = await PicturePayloadCodec.ReadAsync(request.Picture, _payloads, cancellationToken).ConfigureAwait(false);
        writer.Append(picture);
        return Success(envelope, DescribeVideoWriter(request.State.ObjectId, writer));
    }

    private RenderResponseEnvelope FinishVideoWriter(RenderRequestEnvelope envelope)
    {
        var state = Read<IsolationVideoWriterState>(envelope);
        var writer = Require<IVideoWriter>(state.ObjectId);
        ApplyVideoWriterState(writer, state);
        writer.Finish();
        return Success(envelope, DescribeVideoWriter(state.ObjectId, writer));
    }

    private RenderResponseEnvelope VideoWriterSupportsCodec(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationVideoWriterCodecRequest>(envelope);
        return Success(envelope, new IsolationBooleanResponse { Value = Require<IVideoWriter>(request.ObjectId).SupportCodec(request.CodecName) });
    }

    private static void ApplyVideoWriterState(IVideoWriter writer, IsolationVideoWriterState state)
    {
        writer.Width = state.Width;
        writer.Height = state.Height;
        writer.OutputPath = state.OutputPath;
        writer.FramePerSecond = state.FramePerSecond;
        writer.CodecName = state.CodecName;
        writer.PixelFormat = state.PixelFormat;
        writer.BitRate = state.BitRate;
        writer.PreferToSpeed = state.PreferToSpeed;
        writer.Metadata = state.Metadata;
    }

    private static IsolationVideoWriterState DescribeVideoWriter(long id, IVideoWriter writer) => new()
    {
        ObjectId = id,
        Width = writer.Width,
        Height = writer.Height,
        OutputPath = writer.OutputPath,
        FramePerSecond = writer.FramePerSecond,
        CodecName = writer.CodecName,
        PixelFormat = writer.PixelFormat,
        BitRate = writer.BitRate,
        PreferToSpeed = writer.PreferToSpeed,
        Metadata = writer.Metadata ?? [],
        DurationWritten = writer.DurationWritten,
        HasTargetPixelMode = writer.TargetPPB.HasValue,
        TargetPixelMode = writer.TargetPPB.HasValue ? (int)writer.TargetPPB.Value : 0,
    };

    private RenderResponseEnvelope CreateTransform(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationCreateTransformRequest>(envelope);
        ITransform transform;
        if (!string.IsNullOrWhiteSpace(request.Json))
        {
            using var json = System.Text.Json.JsonDocument.Parse(request.Json);
            transform = RequirePlugin().TransformCreator(json.RootElement.Clone());
        }
        else
        {
            if (!RequirePlugin().TransformProvider.TryGetValue(request.TypeName, out var factory))
                throw new KeyNotFoundException($"Transform '{request.TypeName}' was not found.");
            transform = factory(Guid.Parse(request.LeftClipId), Guid.Parse(request.RightClipId));
        }
        return Success(envelope, DescribeTransform(AddObject(transform), transform));
    }

    private RenderResponseEnvelope InitializeTransform(RenderRequestEnvelope envelope)
    {
        var state = Read<IsolationTransformState>(envelope);
        var transform = Require<ITransform>(state.ObjectId);
        ApplyTransformState(transform, state);
        transform.Init();
        return Success(envelope, DescribeTransform(state.ObjectId, transform));
    }

    private async ValueTask<RenderResponseEnvelope> ProcessTransformAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationTransformFrameRequest>(envelope);
        var transform = Require<ITransform>(request.State.ObjectId);
        ApplyTransformState(transform, request.State);
        using var input = await PicturePayloadCodec.ReadAsync(request.Input, _payloads, cancellationToken).ConfigureAwait(false);
        using var second = request.SecondInput is null ? null : await PicturePayloadCodec.ReadAsync(request.SecondInput, _payloads, cancellationToken).ConfigureAwait(false);
        var computer = string.IsNullOrWhiteSpace(transform.NeedComputer) ? null :
            RequirePlugin().ComputerProvider.TryGetValue(transform.NeedComputer, out var factory) ? factory() : null;
        using var output = second is null
            ? (transform as IOneInputSingleFrameTransform ?? throw new NotSupportedException("The transform does not accept one input."))
                .GetFrame(input, request.Progress, computer, request.TargetWidth, request.TargetHeight)
            : request.HasProgress
                ? (transform as IContinuousTransform ?? throw new NotSupportedException("The transform does not accept continuous progress."))
                    .GetFrame(input, second, request.Progress, computer, request.TargetWidth, request.TargetHeight)
                : (transform as ISingleFrameTransform ?? throw new NotSupportedException("The transform does not accept two single-frame inputs."))
                    .GetFrame(input, second, computer, request.TargetWidth, request.TargetHeight);
        var lease = await PicturePayloadCodec.WriteAsync(output, _payloads, _payloadKind, cancellationToken).ConfigureAwait(false);
        return Success(envelope, new IsolationPictureResponse { Picture = lease.Reference });
    }

    private static void ApplyTransformState(ITransform transform, IsolationTransformState state)
    {
        transform.BindedLeftClip = Guid.Parse(state.LeftClipId);
        transform.BindedRightClip = Guid.Parse(state.RightClipId);
        transform.Duration = state.Duration;
    }

    private static IsolationTransformState DescribeTransform(long id, ITransform transform) => new()
    {
        ObjectId = id,
        FromPlugin = transform.FromPlugin,
        TypeName = transform.TypeName,
        TransformType = (int)transform.TransformType,
        Name = transform.Name,
        LeftClipId = transform.BindedLeftClip.ToString(),
        RightClipId = transform.BindedRightClip.ToString(),
        Duration = transform.Duration,
        NeedComputer = transform.NeedComputer ?? string.Empty,
    };

    private RenderResponseEnvelope CreateComputer(RenderRequestEnvelope envelope)
    {
        var typeName = Read<IsolationProviderCatalogItem>(envelope).TypeName;
        if (!RequirePlugin().ComputerProvider.TryGetValue(typeName, out var factory))
            throw new KeyNotFoundException($"Computer '{typeName}' was not found.");
        var computer = factory();
        return Success(envelope, new IsolationComputerDescriptor
        {
            ObjectId = AddObject(computer),
            TypeName = typeName,
            FromPlugin = computer.FromPlugin,
            SupportedEffectOrMixture = computer.SupportedEffectOrMixture,
        });
    }

    private async ValueTask<RenderResponseEnvelope> ComputeAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationComputeRequest>(envelope);
        var inputs = new object?[request.Arguments.Count];
        var pictures = new List<IPicture>();
        try
        {
            for (var i = 0; i < request.Arguments.Count; i++)
            {
                var argument = request.Arguments[i];
                if (argument.Picture is not null)
                {
                    var picture = await PicturePayloadCodec.ReadAsync(argument.Picture, _payloads, cancellationToken).ConfigureAwait(false);
                    pictures.Add(picture);
                    inputs[i] = picture;
                }
                else if (argument.Value is not null) inputs[i] = IsolationValueConverter.ToObject(argument.Value);
                else throw new InvalidDataException("A compute argument contains no supported value.");
            }
            var output = Require<IComputer>(request.ObjectId).Compute(inputs!);
            var response = new IsolationComputeResponse();
            foreach (var item in output)
            {
                if (item is IPicture picture)
                {
                    using (picture)
                    {
                        var lease = await PicturePayloadCodec.WriteAsync(picture, _payloads, _payloadKind, cancellationToken).ConfigureAwait(false);
                        response.Results.Add(new() { Picture = lease.Reference });
                    }
                }
                else response.Results.Add(new() { Value = IsolationValueConverter.FromObject(item) });
            }
            return Success(envelope, response);
        }
        finally
        {
            foreach (var picture in pictures) picture.Dispose();
        }
    }

    private RenderResponseEnvelope CreateClip(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationCreateSerializedObjectRequest>(envelope);
        using var document = System.Text.Json.JsonDocument.Parse(request.Json);
        var clip = RequirePlugin().ClipCreator(document.RootElement.Clone());
        return Success(envelope, DescribeClip(AddObject(clip), clip));
    }

    private async ValueTask<RenderResponseEnvelope> ReadClipFrameAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationClipFrameRequest>(envelope);
        var clip = Require<IClip>(request.Clip.ObjectId);
        ApplyClipState(clip, request.Clip.State);
        using var picture = clip.GetFrameRelativeToStartPointOfSource(request.FrameIndex, request.Width, request.Height,
            (IPicture.PicturePixelMode)request.PixelMode);
        var lease = await PicturePayloadCodec.WriteAsync(picture, _payloads, _payloadKind, cancellationToken).ConfigureAwait(false);
        return Success(envelope, new IsolationPictureResponse { Picture = lease.Reference });
    }

    private RenderResponseEnvelope ReinitializeClip(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationClipFrameRequest>(envelope);
        var clip = Require<IClip>(request.Clip.ObjectId);
        ApplyClipState(clip, request.Clip.State);
        clip.ReInit((IPicture.PicturePixelMode)request.PixelMode);
        return Success(envelope, DescribeClip(request.Clip.ObjectId, clip));
    }

    private RenderResponseEnvelope CreateSoundTrack(RenderRequestEnvelope envelope)
    {
        var request = Read<IsolationCreateSerializedObjectRequest>(envelope);
        ISoundTrack track;
        if (!string.IsNullOrWhiteSpace(request.Json))
        {
            using var document = System.Text.Json.JsonDocument.Parse(request.Json);
            track = RequirePlugin().SoundTrackCreator(document.RootElement.Clone());
        }
        else
        {
            if (!RequirePlugin().SoundTrackProvider.TryGetValue(request.TypeName, out var factory))
                throw new KeyNotFoundException($"Sound track '{request.TypeName}' was not found.");
            track = factory(request.FirstId, request.SecondId);
        }
        return Success(envelope, DescribeSoundTrack(AddObject(track), track));
    }

    private async ValueTask<RenderResponseEnvelope> ReadSoundTrackSamplesAsync(RenderRequestEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = Read<IsolationSoundTrackSamplesRequest>(envelope);
        var track = Require<ISoundTrack>(request.Track.ObjectId);
        ApplySoundTrackState(track, request.Track);
        var samples = track.GetAudioSamplesRelatedToStartPointOfSource(request.StartIndex, request.Length);
        var lease = await _payloads.PublishAsync(AudioPayloadCodec.Encode(samples), _payloadKind, cancellationToken).ConfigureAwait(false);
        return Success(envelope, new IsolationAudioSamplesResponse
        {
            Samples = lease.Reference,
            ChannelCount = samples.channelCount,
            SamplePerSecond = samples.SamplePerSecond,
            SampleCount = samples.SampleCount,
        });
    }

    private RenderResponseEnvelope ReinitializeSoundTrack(RenderRequestEnvelope envelope)
    {
        var state = Read<IsolationSoundTrackDescriptor>(envelope);
        var track = Require<ISoundTrack>(state.ObjectId);
        ApplySoundTrackState(track, state);
        track.ReInit();
        return Success(envelope, DescribeSoundTrack(state.ObjectId, track));
    }

    private static IsolationClipObjectDescriptor DescribeClip(long id, IClip clip) => new()
    {
        ObjectId = id,
        State = IsolationClipSnapshotFactory.Create(clip),
        EffectsJson = System.Text.Json.JsonSerializer.Serialize(clip.Effects),
        EffectProvidersJson = System.Text.Json.JsonSerializer.Serialize(clip.EffectProviders),
        ExtraDataJson = System.Text.Json.JsonSerializer.Serialize(clip.ExtraData),
    };

    private static void ApplyClipState(IClip clip, IsolationClipSnapshot state)
    {
        clip.Duration = state.Duration;
        clip.TargetWidth = state.TargetWidth;
        clip.TargetHeight = state.TargetHeight;
        clip.TargetX = state.TargetX;
        clip.TargetY = state.TargetY;
        clip.StartingX = state.StartingX;
        clip.StartingY = state.StartingY;
        clip.ExtendToWholeDraft = state.ExtendToWholeDraft;
        clip.FilePath = string.IsNullOrWhiteSpace(state.FilePath) ? null : state.FilePath;
    }

    private static IsolationSoundTrackDescriptor DescribeSoundTrack(long id, ISoundTrack track) => new()
    {
        ObjectId = id,
        FromPlugin = track.FromPlugin,
        TrackType = (int)track.TrackType,
        TypeName = track.TypeName,
        Id = track.Id,
        Name = track.Name,
        LayerIndex = track.LayerIndex,
        StartFrame = track.StartFrame,
        RelativeStartFrame = track.RelativeStartFrame,
        Duration = track.Duration,
        FilePath = track.FilePath ?? string.Empty,
        NeedFilePath = track.NeedFilePath,
        Ratio = track.Ratio,
        Volume = track.Volume,
        SamplePerSecond = track.SamplePerSecond,
        EffectsJson = System.Text.Json.JsonSerializer.Serialize(track.Effects),
        ExtraDataJson = System.Text.Json.JsonSerializer.Serialize(track.ExtraData),
    };

    private static void ApplySoundTrackState(ISoundTrack track, IsolationSoundTrackDescriptor state)
    {
        track.FilePath = string.IsNullOrWhiteSpace(state.FilePath) ? null : state.FilePath;
        track.Ratio = state.Ratio;
        track.Volume = state.Volume;
    }

    private RenderResponseEnvelope ProjectLifecycle(RenderRequestEnvelope envelope, Func<IPluginBase, projectFrameCut.Render.RenderAPIBase.Project.ProjectJSONStructure, projectFrameCut.Render.RenderAPIBase.Project.ProjectJSONStructure?> callback)
    {
        var request = Read<IsolationPluginProjectRequest>(envelope);
        var project = System.Text.Json.JsonSerializer.Deserialize<projectFrameCut.Render.RenderAPIBase.Project.ProjectJSONStructure>(request.ProjectJson)
            ?? throw new InvalidDataException("The external plugin project payload is invalid.");
        var result = callback(RequirePlugin(), project);
        return Success(envelope, new IsolationPluginProjectResponse
        {
            HasProject = result is not null,
            ProjectJson = result is null ? string.Empty : System.Text.Json.JsonSerializer.Serialize(result),
        });
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
        List<IPicture> pictures = [];
        try
        {
            foreach (var item in request.DynamicValues) ValueProviderFrameContext.Set(item.Key, IsolationValueConverter.ToObject(item.Value));
            foreach (var item in request.DynamicPictures)
            {
                var picture = await PicturePayloadCodec.ReadAsync(item.Value, _payloads, cancellationToken).ConfigureAwait(false);
                pictures.Add(picture);
                ValueProviderFrameContext.Set(item.Key, picture);
            }
            using var output = render();
            var lease = await PicturePayloadCodec.WriteAsync(output, _payloads, _payloadKind, cancellationToken).ConfigureAwait(false);
            return Success(envelope, new IsolationPictureResponse { Picture = lease.Reference });
        }
        finally
        {
            foreach (var picture in pictures) picture.Dispose();
            ValueProviderFrameContext.EndFrame();
        }
    }

    private IComputer? ResolveComputer(IEffect effect)
    {
        if (string.IsNullOrWhiteSpace(effect.NeedComputer)) return null;
        var plugin = RequirePlugin();
        return plugin.ComputerProvider.TryGetValue(effect.NeedComputer, out var factory) ? factory() : null;
    }

    private void ApplyProviderState(IEffectProvider provider, IsolationProviderState state, IReadOnlyDictionary<string, IPicture> pictures)
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
            var value = pictures.TryGetValue(item.Id, out var picture) ? picture : IsolationValueConverter.ToObject(item.Value) ?? item.DefaultValue;
            fields[item.Id] = item.IsDynamic
                ? new DynamicEffectParamField(item.Id, type, item.BoundProviderId, value) { FieldType = type }
                : new StaticEffectArgumentField(value, type);
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
        StartPoint = effect switch { IContinuousEffect x => x.StartPoint, IAudioContinuousEffect x => x.StartPoint, IContinuousTextEffect x => x.StartPoint, _ => 0 },
        EndPoint = effect switch { IContinuousEffect x => x.EndPoint, IAudioContinuousEffect x => x.EndPoint, IContinuousTextEffect x => x.EndPoint, _ => 0 },
        IsScoped = effect switch { IContinuousEffect x => x.IsScoped, IContinuousTextEffect x => x.IsScoped, _ => false },
        ProjectFrameRate = effect is ISourceReplacementEffect replacement ? replacement.ProjectFrameRate : 0,
        Parameters = effect.Parameters.Where(x => !DynamicParam.IsDynamicValue(x.Value)).ToDictionary(x => x.Key, x => IsolationValueConverter.FromObject(x.Value)),
        DynamicProviderIds = provider?.Fields.Where(x => x.IsDynamic && !string.IsNullOrWhiteSpace(x.BoundProviderId)).Select(x => x.BoundProviderId).Distinct().ToList() ?? [],
        IsColorAdjust = effect is IColorAdjustEffect,
        ValueField = effect is IValueProviderEffect field ? DescribeField(field) : null,
    };

    private static void ApplyEffectState(IEffect effect, IsolationEffectMutableState state)
    {
        effect.Name = state.Name;
        effect.Id = state.InstanceId;
        effect.Enabled = state.Enabled;
        effect.Index = state.Index;
        effect.RelativeWidth = state.RelativeWidth;
        effect.RelativeHeight = state.RelativeHeight;
        effect.BindedEffectProvidingSystemID = string.IsNullOrEmpty(state.BoundProviderId) ? null : state.BoundProviderId;
        if (effect is IContinuousEffect continuous)
        {
            continuous.StartPoint = state.StartPoint;
            continuous.EndPoint = state.EndPoint;
            continuous.IsScoped = state.IsScoped;
        }
        if (effect is ISourceReplacementEffect replacement) replacement.ProjectFrameRate = state.ProjectFrameRate;
    }

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

    private static IsolationVectorComponentDescriptor DescribeVectorComponent(IVectorComponent component, long objectId) => new()
    {
        ObjectId = objectId,
        FromPlugin = component.FromPlugin,
        TypeName = component.TypeName,
        Name = component.Name,
        InstanceId = component.Id.ToString(),
        Index = component.Index,
        Parameters = component.Parameters.ToDictionary(x => x.Key, x => IsolationValueConverter.FromObject(x.Value)),
        AnimationFramesJson = System.Text.Json.JsonSerializer.Serialize(component.AnimationFrames),
        AnimatableFields = component.AnimatableFields.Values.Select(x => new IsolationAnimatableField
        {
            Id = x.Id,
            DisplayName = x.DisplayName,
            Description = x.Description,
            MinimumValue = x.MinimumValue,
            MaximumValue = x.MaximumValue,
        }).ToList(),
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
    private static bool IsSupportedExternalEffect(EffectType type) => type is EffectType.NormalEffect
        or EffectType.ContinuousEffect
        or EffectType.AudioNormalEffect
        or EffectType.AudioContinuousEffect
        or EffectType.SpeedVarianceProvider
        or EffectType.ClipPositionProvider
        or EffectType.ContinuousClipPositionProvider
        or EffectType.MixtureProvider
        or EffectType.TextEffect
        or EffectType.ContinuousTextEffect
        or EffectType.SourceReplacement
        or EffectType.NonIPictureOutputValueProvider;
    private static bool IsBuiltInVectorSegment(Type type) => type == typeof(VectorSegment)
        || type == typeof(StraightLineVectorSegment)
        || type == typeof(RectangleVectorSegment)
        || type == typeof(RoundedRectangleVectorSegment)
        || type == typeof(EllipseVectorSegment)
        || type == typeof(CubicBezierVectorSegment)
        || type == typeof(QuadraticBezierVectorSegment)
        || type == typeof(ArcVectorSegment)
        || type == typeof(PolygonVectorSegment)
        || type == typeof(GradientPolygonVectorSegment)
        || type == typeof(PolylineVectorSegment);
    private void CaptureLog(string message, string level)
    {
        if (_currentRequest.Value is Guid requestId && _logs.TryGetValue(requestId, out var logs))
            logs.Enqueue(new() { Level = level, Message = message });
    }
    private static T Read<T>(RenderRequestEnvelope request) => RenderRpcSerializer.Deserialize<T>(request.Payload);
    private static RenderResponseEnvelope Success<T>(RenderRequestEnvelope request, T payload) => new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(payload) };
    private static RenderResponseEnvelope Failure(RenderRequestEnvelope request, RenderErrorCode code, string message) => new() { RequestId = request.RequestId, Error = new() { Code = code, Message = message, Details = message } };
}
