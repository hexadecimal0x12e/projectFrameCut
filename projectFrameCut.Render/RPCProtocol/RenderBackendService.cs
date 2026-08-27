using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RPCProtocol;

public sealed class RenderBackendService(IRenderArtifactStore? artifactStore = null, string? stateRoot = null, Action<RenderJob>? completionSink = null, Action<RenderJob>? progressSink = null) : IRenderService, IAsyncDisposable
{
    private const string TimelineFrameCacheVersion = "v3-preview-pixel-format";
    private const string ClipPreviewCacheVersion = "v3-preview-pixel-format";
    private const string TimelineSegmentCacheVersion = "v2-audio-source";
    private const string AudioSegmentCacheVersion = "v2-track-source";
    private const string FrameHashIndexVersion = "v1-sparse-frame-clip";
    private readonly IRenderArtifactStore _artifacts = artifactStore ?? new RenderArtifactStore();
    private readonly string? _stateRoot = string.IsNullOrWhiteSpace(stateRoot) ? null : Path.GetFullPath(stateRoot);
    private readonly Action<RenderJob>? _completionSink = completionSink;
    private readonly Action<RenderJob>? _progressSink = progressSink;
    private readonly ConcurrentDictionary<Guid, BackendSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, JobEntry> _jobs = new();
    private readonly object _persistGate = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public async ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        if (request.ProtocolVersion < RenderProtocol.MinimumSupportedVersion || request.ProtocolVersion > RenderProtocol.CurrentVersion)
        {
            return Failure(request, RenderErrorCode.ProtocolMismatch, $"Unsupported render protocol version {request.ProtocolVersion}.");
        }

        try
        {
            return request.Operation switch
            {
                RenderOperation.GetCapabilities => Success(request, GetCapabilities()),
                RenderOperation.OpenProject => Success(request, await OpenProjectAsync(Read<OpenProjectRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.CloseProject => Success(request, await CloseProjectAsync(Read<SessionRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.GetProjectSnapshot => Success(request, GetProjectSnapshot(Read<SessionRequest>(request))),
                RenderOperation.GetTimeline => Success(request, GetTimeline(Read<SessionRequest>(request))),
                RenderOperation.GetAssetMetadata => Success(request, await GetAssetMetadataAsync(Read<AssetMetadataRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.GetAvailableEffects => Success(request, GetAvailableEffects(Read<EffectCatalogRequest>(request))),
                RenderOperation.RenderTimelineFrame => Success(request, await RenderTimelineFrameAsync(Read<TimelineFrameRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.RenderTimelineSegment => Success(request, await RenderTimelineSegmentAsync(Read<TimelineSegmentRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.RenderAudioSegment => Success(request, await RenderAudioSegmentAsync(Read<AudioSegmentRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.RenderClipPreview => Success(request, await RenderClipPreviewAsync(Read<ClipPreviewRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.RenderClipPreviewBatch => Success(request, await RenderClipPreviewBatchAsync(Read<ClipPreviewBatchRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.RenderProject => Success(request, StartRenderProject(Read<RenderProjectRequest>(request))),
                RenderOperation.GetJobStatus => Success(request, GetJob(Read<JobRequest>(request).JobId)),
                RenderOperation.CancelJob => Success(request, CancelJob(Read<JobRequest>(request).JobId)),
                RenderOperation.ListRenderJobs => Success(request, ListJobs(Read<ListRenderJobsRequest>(request))),
                RenderOperation.ReleaseArtifact => Success(request, ReleaseArtifact(Read<ArtifactRequest>(request))),
                _ => Failure(request, RenderErrorCode.Unsupported, $"Render operation '{request.Operation}' is not supported."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(request, RenderErrorCode.Canceled, "Render request was canceled.");
        }
        catch (KeyNotFoundException ex)
        {
            return Failure(request, RenderErrorCode.SessionNotFound, ex);
        }
        catch (ArgumentException ex)
        {
            return Failure(request, RenderErrorCode.InvalidRequest, ex);
        }
        catch (Exception ex)
        {
            Log(ex, $"Render RPC {request.Operation}", this);
            return Failure(request, RenderErrorCode.BackendFailure, ex, retryable: false);
        }
    }

    private RenderCapabilities GetCapabilities() => new()
    {
        ProtocolVersion = RenderProtocol.CurrentVersion,
        MinimumProtocolVersion = RenderProtocol.MinimumSupportedVersion,
        BackendVersion = typeof(Renderer).Assembly.GetName().Version?.ToString() ?? "unknown",
        Operations = Enum.GetValues<RenderOperation>()
            .Where(static operation => operation != RenderOperation.Unknown && (int)operation < 100)
            .Select(static operation => operation.ToString())
            .ToList(),
        Encoders = ["libx264"],
        Features = ["direct-transport", "named-pipe", "unix-socket", "timeline-preview", "clip-preview-without-layout", "thumbs-cache", "render-jobs", "persistent-render-jobs", "artifact-files"],
    };

    private async ValueTask<RenderSession> OpenProjectAsync(OpenProjectRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TimelineJson);
        var root = Path.GetFullPath(request.ProjectRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Project root '{root}' does not exist.");

        var sessionId = request.SessionId == Guid.Empty ? Guid.NewGuid() : request.SessionId;
        var draft = JsonSerializer.Deserialize<DraftStructureJSON>(request.TimelineJson, _jsonOptions)
            ?? throw new ArgumentException("Timeline JSON is invalid.");
        var assets = request.Assets
            .Where(static item => !string.IsNullOrWhiteSpace(item.AssetId))
            .GroupBy(static item => item.AssetId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => ResolveProjectSourcePath(root, group.Last().Path) ?? string.Empty,
                StringComparer.Ordinal);

        var clips = await Task.Run(() => CreateClips(draft, assets, request.ProxyRoot, root, cancellationToken), cancellationToken).ConfigureAwait(false);
        var soundTracks = await Task.Run(() => CreateSoundTracks(draft, assets, root, cancellationToken), cancellationToken).ConfigureAwait(false);
        // Project duration is a timeline property. Do not derive it from runtime
        // GetEffectiveDuration(): speed providers and open-ended sources may deliberately
        // report UInt32.MaxValue even though the clip has a finite UI/timeline duration.
        var duration = ResolveDuration(draft);
        var project = string.IsNullOrWhiteSpace(request.ProjectJson)
            ? null
            : JsonSerializer.Deserialize<ProjectJSONStructure>(request.ProjectJson, _jsonOptions);

        var snapshotHash = ComputeTextHash($"{request.ProjectJson}\n{request.TimelineJson}");
        FrameHashIndex hashIndex;
        try
        {
            hashIndex = await BuildFrameHashIndexAsync(GetVisualClips(clips), duration, snapshotHash, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (var clip in clips) { try { clip.Dispose(); } catch { } }
            foreach (var track in soundTracks) { try { track.Dispose(); } catch { } }
            throw;
        }

        var cacheNamespace = string.IsNullOrWhiteSpace(request.CacheNamespace)
            ? string.Empty
            : ComputeTextHash(request.CacheNamespace);
        var backendSession = new BackendSession(
            sessionId,
            root,
            request.ProjectJson,
            request.TimelineJson,
            project?.ProjectName ?? Path.GetFileName(root),
            Math.Max(1, request.ProjectWidth > 0 ? request.ProjectWidth : project?.RelativeWidth ?? 1),
            Math.Max(1, request.ProjectHeight > 0 ? request.ProjectHeight : project?.RelativeHeight ?? 1),
            Math.Max(1, request.FrameRate > 0 ? request.FrameRate : (int)(project?.TargetFrameRate ?? 30)),
            duration,
            clips,
            soundTracks,
            assets,
            snapshotHash,
            hashIndex,
            cacheNamespace);

        if (_sessions.TryGetValue(sessionId, out var previous))
        {
            CancelJobsForSession(sessionId);
            await previous.DisposeAsync().ConfigureAwait(false);
        }
        _sessions[sessionId] = backendSession;
        return backendSession.ToContract();
    }

    private static Task<FrameHashIndex> BuildFrameHashIndexAsync(IClip[] clips, uint duration, string snapshotHash, CancellationToken cancellationToken)
    {
        if (duration > int.MaxValue)
            throw new InvalidOperationException($"Cannot precompute a frame hash index for duration {duration}; it exceeds the supported index size.");

        return Task.Run(() =>
        {
            var frameCount = (int)duration;
            var frameHashes = new ConcurrentBag<FrameHashEntry>();
            var clipHashes = clips.ToDictionary(clip => clip.Id, _ => new ConcurrentBag<FrameHashEntry>());
            var options = new ParallelOptions { CancellationToken = cancellationToken };

            Parallel.For(0, frameCount, options, frameNumber =>
            {
                var frameIndex = (uint)frameNumber;
                var frameHash = Timeline.GetFrameHash(clips, frameIndex);
                if (!string.Equals(frameHash, "nullframe", StringComparison.Ordinal))
                {
                    frameHashes.Add(new FrameHashEntry { FrameIndex = frameIndex, Hash = frameHash });
                }

                foreach (var clip in clips)
                {
                    if (!clip.ContainsFrame(frameIndex)
                        && !(clip.ExtendToWholeDraft && clip.LayerIndex > Renderer.SubTrackOffset))
                        continue;

                    clipHashes[clip.Id].Add(new FrameHashEntry
                    {
                        FrameIndex = frameIndex,
                        Hash = Timeline.GetClipFrameHash(clips, clip, frameIndex),
                    });
                }
            });

            return new FrameHashIndex
            {
                Version = FrameHashIndexVersion,
                SnapshotHash = snapshotHash,
                FrameHashes = frameHashes.OrderBy(entry => entry.FrameIndex).ToList(),
                ClipHashes = clipHashes.Select(pair => new ClipFrameHashIndex
                {
                    ClipId = pair.Key,
                    FrameHashes = pair.Value.OrderBy(entry => entry.FrameIndex).ToList(),
                }).ToList(),
            };
        }, cancellationToken);
    }

    private async ValueTask<EmptyResponse> CloseProjectAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelJobsForSession(request.SessionId);
        if (_sessions.TryRemove(request.SessionId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        return new();
    }

    private ProjectSnapshot GetProjectSnapshot(SessionRequest request)
    {
        var session = GetSession(request.SessionId);
        return new ProjectSnapshot { Session = session.ToContract(), ProjectJson = session.ProjectJson, TimelineJson = session.TimelineJson };
    }

    private TimelineSnapshot GetTimeline(SessionRequest request)
    {
        var session = GetSession(request.SessionId);
        return new TimelineSnapshot
        {
            SessionId = session.Id,
            Duration = session.Duration,
            ClipCount = session.Clips.Length,
            Clips = session.Clips.Select(static clip => new TimelineClip
            {
                ClipId = clip.Id,
                Name = clip.Name,
                TypeName = clip.TypeName,
                LayerIndex = clip.LayerIndex,
                SubLayerIndex = clip.SubLayerIndex,
                StartFrame = clip.StartFrame,
                Duration = clip.Duration,
            }).ToList(),
        };
    }

    private async ValueTask<AssetMetadata> GetAssetMetadataAsync(AssetMetadataRequest request, CancellationToken cancellationToken)
    {
        var session = GetSession(request.SessionId);
        var path = ResolveAssetPath(session, request);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            var result = new AssetMetadata
            {
                AssetId = request.AssetId,
                MediaType = ResolveMediaType(path),
                Size = info.Length,
                ContentHash = ComputeFileHash(path),
            };
            try
            {
                using var source = PluginManager.CreateVideoSource(path);
                source.Initialize();
                result.Width = source.Width;
                result.Height = source.Height;
                result.FrameRate = source.Fps;
                result.FrameCount = source.TotalFrames;
            }
            catch
            {
                // Non-video assets still return stable file metadata.
            }
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    private EffectCatalog GetAvailableEffects(EffectCatalogRequest request)
    {
        _ = GetSession(request.SessionId);
        var catalog = new EffectCatalog();
        foreach (var (typeName, creator) in EffectHelper.EffectsProviderEnum)
        {
            try
            {
                var effect = creator().RestoreInstanceWithDefaultType();
                catalog.Effects.Add(new EffectDescriptor
                {
                    TypeName = typeName,
                    Name = effect.Name,
                    PluginId = effect.FromPlugin,
                    EffectType = effect.TypeOfEffect.ToString(),
                    Description = effect.GetInfo().Description ?? string.Empty,
                });
            }
            catch (Exception ex)
            {
                Log(ex, $"Describe effect {typeName}", this);
            }
        }
        foreach (var plugin in PluginManager.LoadedPlugins.Values)
        {
            catalog.Plugins.Add(new PluginDescriptor
            {
                PluginId = plugin.PluginID,
                Name = plugin.Name,
                Version = plugin.Version?.ToString() ?? string.Empty,
            });
        }
        return catalog;
    }

    private async ValueTask<RenderArtifact> RenderTimelineFrameAsync(TimelineFrameRequest request, CancellationToken cancellationToken)
    {
        var session = GetSession(request.SessionId);
        var width = Math.Max(1, request.Width);
        var height = Math.Max(1, request.Height);
        await session.RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var frameHash = session.GetFrameHash(request.FrameIndex);
            var namespacePrefix = string.IsNullOrEmpty(session.CacheNamespace) ? string.Empty : $"{session.CacheNamespace}_";
            var wantsScRgb = request.PreferredPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb;
            var formatSuffix = wantsScRgb ? "rgba16f-scrgb" : "png";
            var cacheKey = $"{TimelineFrameCacheVersion}_{namespacePrefix}{frameHash}_{width}x{height}_{formatSuffix}";
            var relativePath = $"thumbs/projectFrameCut_Render_{cacheKey}.{(wantsScRgb ? "rgba16f" : "png")}";
            var finalPath = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
            var cacheHit = File.Exists(finalPath);
            if (!cacheHit)
            {
                var temporaryPath = _artifacts.CreateTemporaryPath(finalPath);
                IPicture? picture = null;
                try
                {
                    var visualClips = GetVisualClips(session.Clips);
                    foreach (var clip in visualClips)
                    {
                        try { clip.ReInit(wantsScRgb ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture); }
                        catch (Exception ex) { ClipInitializationFailure.Mark(clip, "Source or ResolveEffect", ex); }
                    }
                    var layers = Timeline.GetFramesInOneFrame(visualClips, request.FrameIndex, width, height, projectRelativeWidth: session.Width, projectRelativeHeight: session.Height);
                    picture = Timeline.MixtureLayers(layers, request.FrameIndex, width, height, autoCenterImplicitClip: true, projectRelativeWidth: session.Width, projectRelativeHeight: session.Height);
                    if (wantsScRgb)
                        WriteScRgbFrame(picture, temporaryPath);
                    else
                        picture.ToBitPerPixel(8).SaveToPng(temporaryPath);
                    _artifacts.CommitTemporaryFile(temporaryPath, finalPath);
                }
                catch
                {
                    try { File.Delete(temporaryPath); } catch { }
                    throw;
                }
                finally
                {
                    try { picture?.Dispose(); } catch { }
                }
            }
            return _artifacts.Register(
                session.Id, session.ProjectRoot, relativePath,
                wantsScRgb ? "application/x-projectframecut-rgba16f" : "image/png",
                cacheHit, isPreview: true, width, height, session.FrameRate,
                wantsScRgb ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage,
                wantsScRgb ? checked(width * 8) : 0,
                wantsScRgb ? "scRGB-linear-P709" : string.Empty);
        }
        finally
        {
            session.RenderGate.Release();
        }
    }

    private async ValueTask<RenderArtifact> RenderClipPreviewAsync(ClipPreviewRequest request, CancellationToken cancellationToken)
    {
        var session = GetSession(request.SessionId);
        var clip = session.Clips.FirstOrDefault(candidate => candidate.Id == request.ClipId)
            ?? throw new KeyNotFoundException($"Clip '{request.ClipId}' was not found in render session '{request.SessionId}'.");
        if (clip.ClipType == ClipMode.AudioClip)
            throw new NotSupportedException($"Clip '{request.ClipId}' is an audio clip and cannot produce a picture preview.");
        var canvasWidth = Math.Max(1, request.CanvasWidth);
        var canvasHeight = Math.Max(1, request.CanvasHeight);
        var projectWidth = Math.Max(1, request.ProjectWidth > 0 ? request.ProjectWidth : session.Width);
        var projectHeight = Math.Max(1, request.ProjectHeight > 0 ? request.ProjectHeight : session.Height);
        var previewWidth = ResolveClipPreviewDimension(clip.TargetWidth, projectWidth, canvasWidth);
        var previewHeight = ResolveClipPreviewDimension(clip.TargetHeight, projectHeight, canvasHeight);

        await session.RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Use the same full-timeline content hash as static previews. This is
            // important for transform clips whose output depends on bound clips
            // outside the requested clip itself.
            var clipHash = session.GetClipFrameHash(clip.Id, request.FrameIndex);
            var namespacePrefix = string.IsNullOrEmpty(session.CacheNamespace) ? string.Empty : $"{session.CacheNamespace}_";
            var wantsScRgb = request.PreferredPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb;
            var formatSuffix = wantsScRgb ? "rgba16f-scrgb" : "png";
            var relativePath = $"thumbs/perClip/{clip.Id}/dynamic/dynamic_{ClipPreviewCacheVersion}_{namespacePrefix}{clipHash}_{projectWidth}x{projectHeight}_{canvasWidth}x{canvasHeight}_{formatSuffix}.{(wantsScRgb ? "rgba16f" : "png")}";
            var finalPath = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
            var cacheHit = File.Exists(finalPath);
            if (!cacheHit)
            {
                var temporaryPath = _artifacts.CreateTemporaryPath(finalPath);
                IPicture? picture = null;
                try
                {
                    if (wantsScRgb)
                    {
                        try { clip.ReInit(IPicture.PicturePixelMode.UShortPicture); }
                        catch (Exception ex) { ClipInitializationFailure.Mark(clip, "Source or ResolveEffect", ex); }
                    }
                    picture = ClipPreviewRenderer.Render(
                        clip,
                        GetVisualClips(session.Clips),
                        canvasWidth,
                        canvasHeight,
                        projectWidth,
                        projectHeight,
                        request.FrameIndex,
                        cancellationToken,
                        wantsScRgb ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture)
                        ?? throw new InvalidOperationException($"Clip '{clip.Id}' did not produce a preview frame.");
                    if (wantsScRgb)
                        WriteScRgbFrame(picture, temporaryPath);
                    else
                        picture.ToBitPerPixel(8).SaveToPng(temporaryPath);
                    _artifacts.CommitTemporaryFile(temporaryPath, finalPath);
                }
                catch
                {
                    try { File.Delete(temporaryPath); } catch { }
                    throw;
                }
                finally
                {
                    try { picture?.Dispose(); } catch { }
                }
            }
            return _artifacts.Register(
                session.Id, session.ProjectRoot, relativePath,
                wantsScRgb ? "application/x-projectframecut-rgba16f" : "image/png",
                cacheHit, isPreview: true, previewWidth, previewHeight, session.FrameRate,
                wantsScRgb ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage,
                wantsScRgb ? checked(previewWidth * 8) : 0,
                wantsScRgb ? "scRGB-linear-P709" : string.Empty);
        }
        finally
        {
            session.RenderGate.Release();
        }
    }

    private async ValueTask<ClipPreviewBatchResponse> RenderClipPreviewBatchAsync(ClipPreviewBatchRequest request, CancellationToken cancellationToken)
    {
        var response = new ClipPreviewBatchResponse();
        foreach (var item in request.Requests)
        {
            try
            {
                response.Artifacts.Add(await RenderClipPreviewAsync(item, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                response.Errors.Add(new RemoteError(ex));
            }
        }
        return response;
    }

    private static void WriteScRgbFrame(IPicture picture, string path)
    {
        ArgumentNullException.ThrowIfNull(picture);
        var width = Math.Max(1, picture.Width);
        var height = Math.Max(1, picture.Height);
        var row = new byte[checked(width * 8)];
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, row.Length, FileOptions.SequentialScan);

        if (picture is IPicture<ushort> picture16)
        {
            var hdr = picture16 as IHDRPicture<ushort>;
            var isPqHdr = hdr is not null && float.IsFinite(hdr.MaximumBrightness) && hdr.MaximumBrightness > 300f;
            for (var y = 0; y < height; y++)
            {
                var baseIndex = y * width;
                for (var x = 0; x < width; x++)
                {
                    var index = baseIndex + x;
                    var alpha = ResolveAlpha(picture16.a, picture16.HasAlphaChannel, index);
                    var red = picture16.r[index] / 65535f;
                    var green = picture16.g[index] / 65535f;
                    var blue = picture16.b[index] / 65535f;
                    var color = isPqHdr
                        ? PqBt2020ToScRgb(red, green, blue)
                        : (SrgbToLinear(red), SrgbToLinear(green), SrgbToLinear(blue));
                    WriteHalfPixel(row, x * 8, color.Item1 * alpha, color.Item2 * alpha, color.Item3 * alpha, alpha);
                }
                stream.Write(row);
            }
            return;
        }

        if (picture is not IPicture<byte> picture8)
            throw new NotSupportedException($"Cannot export {picture.GetType().Name} as an FP16 preview frame.");

        for (var y = 0; y < height; y++)
        {
            var baseIndex = y * width;
            for (var x = 0; x < width; x++)
            {
                var index = baseIndex + x;
                var alpha = ResolveAlpha(picture8.a, picture8.HasAlphaChannel, index);
                var red = SrgbToLinear(picture8.r[index] / 255f) * alpha;
                var green = SrgbToLinear(picture8.g[index] / 255f) * alpha;
                var blue = SrgbToLinear(picture8.b[index] / 255f) * alpha;
                WriteHalfPixel(row, x * 8, red, green, blue, alpha);
            }
            stream.Write(row);
        }
    }

    private static float ResolveAlpha(float[]? alpha, bool hasAlpha, int index)
        => hasAlpha && alpha is not null && index < alpha.Length && float.IsFinite(alpha[index])
            ? Math.Clamp(alpha[index], 0f, 1f)
            : 1f;

    private static void WriteHalfPixel(byte[] destination, int offset, float red, float green, float blue, float alpha)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset, 2), BitConverter.HalfToUInt16Bits((Half)Math.Clamp(red, -0.5f, 125f)));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset + 2, 2), BitConverter.HalfToUInt16Bits((Half)Math.Clamp(green, -0.5f, 125f)));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset + 4, 2), BitConverter.HalfToUInt16Bits((Half)Math.Clamp(blue, -0.5f, 125f)));
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset + 6, 2), BitConverter.HalfToUInt16Bits((Half)Math.Clamp(alpha, 0f, 1f)));
    }

    private static (float Red, float Green, float Blue) PqBt2020ToScRgb(float red, float green, float blue)
    {
        const float nitsPerScRgbUnit = 80f;
        var r2020 = DecodePq(red) * 10000f / nitsPerScRgbUnit;
        var g2020 = DecodePq(green) * 10000f / nitsPerScRgbUnit;
        var b2020 = DecodePq(blue) * 10000f / nitsPerScRgbUnit;

        return (
            1.660491f * r2020 - 0.587641f * g2020 - 0.072850f * b2020,
            -0.124550f * r2020 + 1.132900f * g2020 - 0.008349f * b2020,
            -0.018151f * r2020 - 0.100579f * g2020 + 1.118730f * b2020);
    }

    private static float DecodePq(float signal)
    {
        const float m1 = 2610f / 16384f;
        const float m2 = 2523f / 32f;
        const float c1 = 3424f / 4096f;
        const float c2 = 2413f / 128f;
        const float c3 = 2392f / 128f;
        var p = MathF.Pow(Math.Clamp(signal, 0f, 1f), 1f / m2);
        var denominator = c2 - c3 * p;
        return denominator <= 0f ? 0f : MathF.Pow(MathF.Max(p - c1, 0f) / denominator, 1f / m1);
    }

    private static float SrgbToLinear(float signal)
    {
        signal = Math.Clamp(signal, 0f, 1f);
        return signal <= 0.04045f
            ? signal / 12.92f
            : MathF.Pow((signal + 0.055f) / 1.055f, 2.4f);
    }

    private static int ResolveClipPreviewDimension(int clipDimension, int projectDimension, int canvasDimension)
        => clipDimension <= 0 || projectDimension <= 0
            ? Math.Max(1, canvasDimension)
            : Math.Max(1, (int)Math.Round((double)clipDimension * canvasDimension / projectDimension, MidpointRounding.AwayFromZero));

    private async ValueTask<RenderArtifact> RenderTimelineSegmentAsync(TimelineSegmentRequest request, CancellationToken cancellationToken)
    {
        var session = GetSession(request.SessionId);
        var keySource = $"{TimelineSegmentCacheVersion}|{session.CacheNamespace}|{session.SnapshotHash}|{request.StartFrame}|{request.Length}|{request.Width}|{request.Height}|{request.FrameRate}|{request.IncludeAudio}";
        var relativePath = $"thumbs/projectFrameCut_Render_segment_{ComputeTextHash(keySource)}.mp4";
        return await RenderSegmentInternalAsync(session, request, relativePath, isPreview: true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RenderArtifact> RenderAudioSegmentAsync(AudioSegmentRequest request, CancellationToken cancellationToken)
    {
        var session = GetSession(request.SessionId);
        if (!HasAudio(session)) throw new InvalidOperationException("The project does not contain audio sources.");
        var sampleRate = Math.Max(8000, request.SampleRate);
        var channels = Math.Clamp(request.Channels, 1, 8);
        var frameRate = Math.Max(1, request.FrameRate);
        var length = request.Length > 0 ? request.Length : session.Duration;
        var key = ComputeTextHash($"{AudioSegmentCacheVersion}|{session.CacheNamespace}|{session.SnapshotHash}|audio|{request.StartFrame}|{length}|{frameRate}|{sampleRate}|{channels}");
        var relativePath = $"thumbs/projectFrameCut_Render_audio_{key}.wav";
        var finalPath = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
        var cacheHit = File.Exists(finalPath);
        if (!cacheHit)
        {
            await session.RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(finalPath))
                {
                    var temporaryPath = _artifacts.CreateTemporaryPath(finalPath);
                    try
                    {
                        using (var writer = new AudioWriter(temporaryPath, sampleRate, channels, "pcm_s16le"))
                        {
                            var composer = new AudioComposer<float>
                            {
                                Clips = session.Clips,
                                SoundTracks = session.SoundTracks,
                                Writer = writer,
                                StartFrame = request.StartFrame,
                                Duration = length,
                            };
                            await Task.Run(() => composer.Compose(frameRate, sampleRate, channels, 40960, cancellationToken), cancellationToken).ConfigureAwait(false);
                            writer.Finish();
                        }
                        _artifacts.CommitTemporaryFile(temporaryPath, finalPath);
                    }
                    finally
                    {
                        try { File.Delete(temporaryPath); } catch { }
                    }
                }
            }
            finally
            {
                session.RenderGate.Release();
            }
        }
        return _artifacts.Register(session.Id, session.ProjectRoot, relativePath, "audio/wav", cacheHit, isPreview: true, frameRate: frameRate);
    }

    private RenderJob StartRenderProject(RenderProjectRequest request)
    {
        var session = GetSession(request.SessionId);
        var jobId = Guid.NewGuid();
        var entry = new JobEntry(new RenderJob
        {
            JobId = jobId,
            SessionId = session.Id,
            State = RenderJobState.Queued,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            ProjectRoot = session.ProjectRoot,
            ProjectName = string.IsNullOrWhiteSpace(request.ProjectName) ? session.ProjectName : request.ProjectName,
            OutputPath = request.OutputPath,
            Background = request.Background,
        }, PersistJobs);
        _jobs[jobId] = entry;
        PersistJobs();
        _ = Task.Run(() => RunProjectJobAsync(entry, request));
        return entry.Snapshot();
    }

    private async Task RunProjectJobAsync(JobEntry entry, RenderProjectRequest request)
    {
        try
        {
            var session = GetSession(request.SessionId);
            entry.Update(job => job.State = RenderJobState.Running);
            ReportProgress(entry);
            var safeName = SanitizeFileName(request.OutputFileName, "render.mp4");
            var relativePath = $"cache/render/{entry.JobId:N}/{safeName}";
            var segment = new TimelineSegmentRequest
            {
                SessionId = request.SessionId,
                StartFrame = 0,
                Length = session.Duration,
                Width = request.Width,
                Height = request.Height,
                FrameRate = request.FrameRate,
                IncludeAudio = request.IncludeAudio,
            };
            var artifact = await RenderSegmentInternalAsync(session, segment, relativePath, isPreview: false, entry.Cancellation.Token,
                (progress, eta) =>
                {
                    entry.Update(job =>
                    {
                        job.Progress = progress;
                        job.EstimatedRemainingTicks = eta.Ticks;
                    });
                    ReportProgress(entry);
                }, request.Encoder, request.PixelFormat).ConfigureAwait(false);
            entry.Update(job =>
            {
                job.State = RenderJobState.Completed;
                job.Progress = 1;
                job.Artifact = artifact;
            });
        }
        catch (OperationCanceledException)
        {
            entry.Update(job => job.State = RenderJobState.Canceled);
        }
        catch (Exception ex)
        {
            Log(ex, $"Render job {entry.JobId}", this);
            entry.Update(job =>
            {
                job.State = RenderJobState.Failed;
                job.Error = new RemoteError(ex);
            });
        }
        finally
        {
            PersistJobs();
            var snapshot = entry.Snapshot();
            if (snapshot.State is RenderJobState.Completed or RenderJobState.Failed or RenderJobState.Canceled)
            {
                try { _completionSink?.Invoke(snapshot); } catch (Exception ex) { Log(ex, "Render completion notification", this); }
            }
        }
    }

    private async ValueTask<RenderArtifact> RenderSegmentInternalAsync(BackendSession session, TimelineSegmentRequest request, string relativePath, bool isPreview, CancellationToken cancellationToken, Action<double, TimeSpan>? progress = null, string encoder = "libx264", string pixelFormat = "AV_PIX_FMT_YUV420P")
    {
        var width = Math.Max(2, request.Width + (request.Width & 1));
        var height = Math.Max(2, request.Height + (request.Height & 1));
        var frameRate = Math.Max(1, request.FrameRate);
        var length = request.Length > 0 ? request.Length : session.Duration;
        var finalPath = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
        var cacheHit = File.Exists(finalPath);
        if (cacheHit) return _artifacts.Register(session.Id, session.ProjectRoot, relativePath, "video/mp4", true, isPreview, width, height, frameRate);

        await session.RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(finalPath)) return _artifacts.Register(session.Id, session.ProjectRoot, relativePath, "video/mp4", true, isPreview, width, height, frameRate);

            var outputTemporaryPath = _artifacts.CreateTemporaryPath(finalPath);
            var videoTemporaryPath = Path.Combine(Path.GetDirectoryName(outputTemporaryPath)!, $".video-{Guid.NewGuid():N}.mp4");
            var audioTemporaryPath = Path.Combine(Path.GetDirectoryName(outputTemporaryPath)!, $".audio-{Guid.NewGuid():N}.wav");
            VideoBuilder? builder = null;
            try
            {
                builder = new VideoBuilder(videoTemporaryPath, width, height, frameRate, encoder, pixelFormat)
                {
                    Duration = uint.MaxValue,
                    BlockWrite = true,
                };
                var renderer = new Renderer
                {
                    StartFrame = request.StartFrame,
                    Duration = length,
                    builder = builder,
                    Clips = GetVisualClips(session.Clips),
                    Use16Bit = false,
                    AutoCenterImplicitClip = true,
                    MaxThreads = 1,
                    ProjectRelativeWidth = session.Width,
                    ProjectRelativeHeight = session.Height,
                };
                if (progress is not null) renderer.OnProgressChanged += progress;
                renderer.PrepareRender(cancellationToken);
                await renderer.GoRender(cancellationToken).ConfigureAwait(false);
                if (progress is not null) renderer.OnProgressChanged -= progress;
                builder.Writer.Finish();
                builder.Dispose();
                builder = null;

                var hasAudio = request.IncludeAudio && HasAudio(session);
                if (hasAudio)
                {
                    using var writer = new AudioWriter(audioTemporaryPath, Math.Max(8000, request.AudioSampleRate), Math.Clamp(request.AudioChannels, 1, 8), "pcm_s16le");
                    var composer = new AudioComposer<float>
                    {
                        Clips = session.Clips,
                        SoundTracks = session.SoundTracks,
                        Writer = writer,
                        StartFrame = request.StartFrame,
                        Duration = length,
                    };
                    await Task.Run(() => composer.Compose(frameRate, request.AudioSampleRate, request.AudioChannels, 40960, cancellationToken), cancellationToken).ConfigureAwait(false);
                    writer.Finish();
                    await Task.Run(() => VideoAudioMuxer.MuxFromFiles(videoTemporaryPath, audioTemporaryPath, outputTemporaryPath, true), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    File.Move(videoTemporaryPath, outputTemporaryPath, overwrite: true);
                }

                _artifacts.CommitTemporaryFile(outputTemporaryPath, finalPath);
            }
            finally
            {
                try { builder?.Dispose(); } catch { }
                try { File.Delete(outputTemporaryPath); } catch { }
                try { File.Delete(videoTemporaryPath); } catch { }
                try { File.Delete(audioTemporaryPath); } catch { }
            }
            return _artifacts.Register(session.Id, session.ProjectRoot, relativePath, "video/mp4", false, isPreview, width, height, frameRate);
        }
        finally
        {
            session.RenderGate.Release();
        }
    }

    private void ReportProgress(JobEntry entry)
    {
        try { _progressSink?.Invoke(entry.Snapshot()); }
        catch (Exception ex) { Log(ex, "Render progress notification", this); }
    }

    private RenderJob GetJob(Guid jobId)
        => _jobs.TryGetValue(jobId, out var entry) ? entry.Snapshot() : throw new KeyNotFoundException($"Render job '{jobId}' was not found.");

    private RenderJob CancelJob(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry)) throw new KeyNotFoundException($"Render job '{jobId}' was not found.");
        entry.Cancellation.Cancel();
        PersistJobs();
        return entry.Snapshot();
    }

    private List<RenderJob> ListJobs(ListRenderJobsRequest request)
    {
        LoadPersistedJobs();
        var jobs = _jobs.Values.Select(static entry => entry.Snapshot());
        if (!string.IsNullOrWhiteSpace(request.ProjectRoot))
        {
            var root = Path.GetFullPath(request.ProjectRoot);
            jobs = jobs.Where(job => string.Equals(Path.GetFullPath(job.ProjectRoot), root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        }
        if (!request.IncludeCompleted)
            jobs = jobs.Where(static job => job.State is RenderJobState.Queued or RenderJobState.Running);
        return jobs.OrderByDescending(static job => job.UpdatedAtUtc).ToList();
    }

    private int _loadedPersistedJobs;
    private void LoadPersistedJobs()
    {
        if (_loadedPersistedJobs != 0) return;
        _loadedPersistedJobs = 1;
        var path = JobsStatePath;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var jobs = JsonSerializer.Deserialize<List<RenderJob>>(File.ReadAllText(path), _jsonOptions) ?? [];
            foreach (var job in jobs)
            {
                if (job.State is RenderJobState.Queued or RenderJobState.Running)
                {
                    job.State = RenderJobState.Failed;
                    job.Error = new RemoteError { Code = RenderErrorCode.BackendFailure, Message = "The render worker was restarted before this job completed." };
                }
                _jobs.TryAdd(job.JobId, new JobEntry(job, PersistJobs));
            }
        }
        catch (Exception ex) { Log(ex, "Load persisted render jobs", this); }
    }

    private string? JobsStatePath => _stateRoot is null ? null : Path.Combine(_stateRoot, "RenderJobs", "jobs.json");

    private void PersistJobs()
    {
        var path = JobsStatePath;
        if (path is null) return;
        try
        {
            lock (_persistGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_jobs.Values.Select(static entry => entry.Snapshot()).ToList(), _jsonOptions));
                File.Move(temp, path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "Persist render jobs", this);
        }
    }

    private EmptyResponse ReleaseArtifact(ArtifactRequest request)
    {
        _ = GetSession(request.SessionId);
        if (!_artifacts.Release(request.SessionId, request.ArtifactId)) throw new FileNotFoundException($"Artifact '{request.ArtifactId}' was not found for this session.");
        return new();
    }

    public (byte[] Content, string Path) ReadArtifact(ArtifactRequest request)
    {
        _ = GetSession(request.SessionId);
        if (!_artifacts.TryGetPath(request.SessionId, request.ArtifactId, out var path))
            throw new FileNotFoundException($"Artifact '{request.ArtifactId}' was not found for this session.");
        return (File.ReadAllBytes(path), path);
    }

    private BackendSession GetSession(Guid sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session : throw new KeyNotFoundException($"Render session '{sessionId}' was not found.");

    private IClip[] CreateClips(DraftStructureJSON draft, IReadOnlyDictionary<string, string> assets, string proxyRoot, string projectRoot, CancellationToken cancellationToken)
    {
        var clips = new List<IClip>();
        foreach (var dto in draft.Clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dto.ClipType == ClipMode.MarkingClip) continue;
            var clip = PluginManager.CreateClip(JsonSerializer.SerializeToElement(dto, _jsonOptions));
            ResolveSourcePath(clip, dto.FilePath, assets, proxyRoot, projectRoot);
            try
            {
                clip.ReInit(IPicture.PicturePixelMode.BytePicture);
                clip.EffectsInstances = EffectHelper.GetClipEffectsInstances(clip);
                ClipInitializationFailure.Clear(clip);
            }
            catch (Exception ex)
            {
                ClipInitializationFailure.Mark(clip, "Source or ResolveEffect", ex);
            }
            clips.Add(clip);
        }
        return clips.ToArray();
    }

    private ISoundTrack[] CreateSoundTracks(DraftStructureJSON draft, IReadOnlyDictionary<string, string> assets, string projectRoot, CancellationToken cancellationToken)
    {
        var tracks = new List<ISoundTrack>();
        foreach (var dto in draft.SoundTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = PluginManager.CreateSoundTrack(JsonSerializer.SerializeToElement(dto, _jsonOptions));
            track.ExtraData = dto.MetaData ?? new();
            if (track.ExtraData.TryGetValue("Volume", out object? volumeValue))
            {
                track.Volume = volumeValue switch
                {
                    double value => (float)value,
                    float value => value,
                    JsonElement value when value.TryGetDouble(out double parsedDouble) => (float)parsedDouble,
                    _ when float.TryParse(
                        volumeValue?.ToString(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float parsedFloat) => parsedFloat,
                    _ => 1f,
                };
            }
            ResolveSourcePath(track, dto.FilePath, assets, projectRoot);
            track.ReInit();
            tracks.Add(track);
        }
        return tracks.ToArray();
    }

    private static void ResolveSourcePath(IClip clip, string? dtoPath, IReadOnlyDictionary<string, string> assets, string proxyRoot, string projectRoot)
    {
        var path = clip.FilePath ?? dtoPath;
        if (path?.StartsWith('$') == true && assets.TryGetValue(path[1..], out var assetPath)) path = assetPath;
        path = ResolveProjectSourcePath(projectRoot, path);
        if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(proxyRoot))
        {
            var proxy = Path.Combine(proxyRoot, $"{Path.GetFileNameWithoutExtension(path)}.proxy.mp4");
            if (File.Exists(proxy)) path = proxy;
        }
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { clip.FilePath = path; } catch (InvalidOperationException) { }
        }
    }

    private static void ResolveSourcePath(ISoundTrack track, string? dtoPath, IReadOnlyDictionary<string, string> assets, string projectRoot)
    {
        var path = track.FilePath ?? dtoPath;
        if (path?.StartsWith('$') == true && assets.TryGetValue(path[1..], out var assetPath)) path = assetPath;
        if (path?.StartsWith('$') == true)
        {
            throw new FileNotFoundException(
                $"Soundtrack '{track.Name}' references asset '{path[1..]}', but that asset was not supplied to the render backend.",
                path);
        }
        path = ResolveProjectSourcePath(projectRoot, path);
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { track.FilePath = path; } catch (InvalidOperationException) { }
        }
    }

    private static string? ResolveProjectSourcePath(string projectRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('$')) return path;
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(projectRoot, path));
    }

    private static uint ResolveDuration(DraftStructureJSON draft)
    {
        ulong duration = Math.Max(draft.Duration, draft.AudioDuration);
        foreach (var clip in draft.Clips)
        {
            // ClipDraftDTO.Duration is the finite timeline width exported by DraftPage,
            // including for assets whose source itself is infinite/open-ended.
            var end = (ulong)clip.StartFrame + clip.Duration;
            if (end > uint.MaxValue)
            {
                Log($"[RenderRPC] Ignoring overflowing timeline end for clip {clip.Id}/{clip.Name}: start={clip.StartFrame}, duration={clip.Duration}, draftDuration={draft.Duration}.", "warn");
                duration = Math.Max(duration, (ulong)clip.StartFrame + 1);
                continue;
            }
            duration = Math.Max(duration, end);
        }
        foreach (var track in draft.SoundTracks)
        {
            var end = (ulong)track.StartFrame + track.Duration;
            if (end > uint.MaxValue)
            {
                Log($"[RenderRPC] Ignoring overflowing timeline end for soundtrack {track.Id}/{track.Name}: start={track.StartFrame}, duration={track.Duration}, draftDuration={draft.Duration}.", "warn");
                duration = Math.Max(duration, (ulong)track.StartFrame + 1);
                continue;
            }
            duration = Math.Max(duration, end);
        }
        // Every value contributing to duration is now range-checked. Keep this clamp as a
        // last-resort compatibility guard for malformed legacy project data.
        duration = Math.Min(duration, uint.MaxValue);
        return (uint)duration;
    }

    private static bool HasAudio(BackendSession session)
        => session.SoundTracks.Length > 0 || session.Clips.Any(static clip => clip.ClipType is ClipMode.AudioClip or ClipMode.VideoClip);

    private static IClip[] GetVisualClips(IEnumerable<IClip> clips)
        => clips.Where(static clip => clip.ClipType != ClipMode.AudioClip).ToArray();

    private string ResolveAssetPath(BackendSession session, AssetMetadataRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AssetId) && session.Assets.TryGetValue(request.AssetId, out var assetPath)) return Path.GetFullPath(assetPath);
        if (!string.IsNullOrWhiteSpace(request.ProjectRelativePath)) return _artifacts.ResolveProjectPath(session.ProjectRoot, request.ProjectRelativePath);
        throw new ArgumentException("Either AssetId or ProjectRelativePath is required.");
    }

    private void CancelJobsForSession(Guid sessionId)
    {
        foreach (var entry in _jobs.Values.Where(entry => entry.SessionId == sessionId)) entry.Cancellation.Cancel();
    }

    private static string ResolveMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".wav" => "audio/wav", ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".webm" => "video/webm", _ => "application/octet-stream",
    };

    private static string SanitizeFileName(string value, string fallback)
    {
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(value) ? fallback : value);
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private static string ComputeTextHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string ComputeFileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static T Read<T>(RenderRequestEnvelope request) => RenderRpcSerializer.Deserialize<T>(request.Payload);
    private static RenderResponseEnvelope Success<T>(RenderRequestEnvelope request, T payload) => new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(payload) };
    private static RenderResponseEnvelope Failure(RenderRequestEnvelope request, RenderErrorCode code, string message, string details = "", bool retryable = false) => new()
    {
        RequestId = request.RequestId,
        Error = new RemoteError { Code = code, Message = message, Details = details, Retryable = retryable },
    };

    private static RenderResponseEnvelope Failure(RenderRequestEnvelope request, RenderErrorCode code, Exception exception, bool retryable = false, string? customMessage = null, string? customDetails = null) => new()
    {
        RequestId = request.RequestId,
        Error = new RemoteError(exception, code, retryable, customMessage, customDetails),
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var job in _jobs.Values) job.Cancellation.Cancel();
        foreach (var session in _sessions.Values) await session.DisposeAsync().ConfigureAwait(false);
        _sessions.Clear();
        PersistJobs();
    }

    private sealed class BackendSession(
        Guid id, string projectRoot, string projectJson, string timelineJson, string projectName,
        int width, int height, int frameRate, uint duration, IClip[] clips, ISoundTrack[] soundTracks,
        IReadOnlyDictionary<string, string> assets, string snapshotHash, FrameHashIndex hashIndex,
        string cacheNamespace) : IAsyncDisposable
    {
        public Guid Id { get; } = id;
        public string ProjectRoot { get; } = projectRoot;
        public string ProjectJson { get; } = projectJson;
        public string TimelineJson { get; } = timelineJson;
        public string ProjectName { get; } = projectName;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int FrameRate { get; } = frameRate;
        public uint Duration { get; } = duration;
        public IClip[] Clips { get; } = clips;
        public ISoundTrack[] SoundTracks { get; } = soundTracks;
        public IReadOnlyDictionary<string, string> Assets { get; } = assets;
        public string SnapshotHash { get; } = snapshotHash;
        public FrameHashIndex HashIndex { get; } = hashIndex;
        public string CacheNamespace { get; } = cacheNamespace;
        public SemaphoreSlim RenderGate { get; } = new(1, 1);
        private readonly IReadOnlyDictionary<uint, string> _frameHashLookup = hashIndex.FrameHashes
            .GroupBy(entry => entry.FrameIndex)
            .ToDictionary(group => group.Key, group => group.Last().Hash);
        private readonly IReadOnlyDictionary<Guid, IReadOnlyDictionary<uint, string>> _clipHashLookup = hashIndex.ClipHashes
            .ToDictionary(
                entry => entry.ClipId,
                entry => (IReadOnlyDictionary<uint, string>)entry.FrameHashes
                    .GroupBy(frame => frame.FrameIndex)
                    .ToDictionary(group => group.Key, group => group.Last().Hash));

        public string GetFrameHash(uint frameIndex)
            => _frameHashLookup.TryGetValue(frameIndex, out var hash) ? hash : "nullframe";

        public string GetClipFrameHash(Guid clipId, uint frameIndex)
            => _clipHashLookup.TryGetValue(clipId, out var clipHashes)
                && clipHashes.TryGetValue(frameIndex, out var hash)
                ? hash
                : "__error__";

        public RenderSession ToContract() => new()
        {
            SessionId = Id, ProjectName = ProjectName, ProjectWidth = Width, ProjectHeight = Height,
            FrameRate = FrameRate, Duration = Duration, ClipCount = Clips.Length, SnapshotHash = SnapshotHash,
            HashIndex = HashIndex,
        };

        public ValueTask DisposeAsync()
        {
            foreach (var clip in Clips) { try { clip.Dispose(); } catch { } }
            foreach (var track in SoundTracks) { try { track.Dispose(); } catch { } }
            RenderGate.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class JobEntry
    {
        private readonly object _gate = new();
        private readonly RenderJob _job;
        private readonly Action _changed;
        public JobEntry(RenderJob job, Action? changed = null) { _job = job; _changed = changed ?? new Action(() => { }); Cancellation = new CancellationTokenSource(); }
        public Guid JobId => _job.JobId;
        public Guid SessionId => _job.SessionId;
        public CancellationTokenSource Cancellation { get; }
        public void Update(Action<RenderJob> update) { lock (_gate) { update(_job); _job.UpdatedAtUtc = DateTime.UtcNow; } _changed(); }
        public RenderJob Snapshot() { lock (_gate) return RenderRpcSerializer.Clone(_job); }
    }
}

public sealed class RenderServiceHost : IAsyncDisposable
{
    private readonly RenderBackendService _service;
    public RenderServiceHost(string? clientId = null, IRenderArtifactStore? artifactStore = null)
    {
        _service = new RenderBackendService(artifactStore);
        Transport = new DirectRenderTransport(_service);
        Client = new RenderClient(Transport, clientId);
    }

    public IRenderTransport Transport { get; }
    public IRenderClient Client { get; }
    public IRenderService Service => _service;

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        await _service.DisposeAsync().ConfigureAwait(false);
    }
}
