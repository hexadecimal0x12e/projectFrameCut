using Microsoft.Maui.Controls.PlatformConfiguration;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using projectFrameCut.Drawing.Base.Picture;


namespace projectFrameCut.LivePreview
{
    public enum NativePreviewOutputMode
    {
        Disabled,
        Automatic,
        Required,
    }

    public sealed record PreviewFrameSource(
        string VfdPath,
        int Width,
        int Height,
        PreviewPixelFormat TargetPixelFormat,
        bool RequireSwapChain)
    {
        internal ScRgbFrame? ScRgb { get; init; }
        internal byte[]? Png { get; init; }
        internal byte[]? AlphaMask { get; init; }
        public string PreviewContainer { get; init; } = string.Empty;
        public string PreviewEffectHash { get; init; } = string.Empty;
    }

    public partial class LivePreviewer
    {
        public IClip[]? Clips;
        public ISoundTrack[]? SoundTracks;
        private AudioSampleSource? audioSamples;
        public AudioSampleSource AudioSamples => audioSamples ??= new(Clips ?? [], SoundTracks ?? [], targetFrameRate);
        public int targetFrameRate = 60;
        public uint TotalDuration;
        public string TempPath = string.Empty;
        public string? ProxyRoot;
        public string ProjectJson { get; set; } = string.Empty;
        public IRenderClient? RpcClient { get; set; }
        public Func<RenderArtifact, CancellationToken, Task<string>>? ArtifactResolver { get; set; }
        public string? RenderProjectRoot { get; set; }
        public string? RenderProxyRoot { get; set; }
        public IReadOnlyList<AssetPathEntry>? RemoteAssets { get; set; }
        public int ProjectRelativeWidth { get; set; }
        public int ProjectRelativeHeight { get; set; }
        public event Action<double, TimeSpan>? OnProgressChanged;
        public Guid RenderSessionId { get; set; } = Guid.NewGuid();
        public FrameHashIndex HashIndex { get; private set; } = new();
        private readonly ConcurrentDictionary<uint, (FrameHashOptions Options, string Hash, string Path)> renderedFrames = new();
        private readonly SemaphoreSlim _updateDraftGate = new(1, 1);
        private readonly TaskCompletionSource _backendReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private HashSet<Guid> _cacheableClipIds = [];
        public string ProjectRoot => string.IsNullOrWhiteSpace(TempPath) ? string.Empty : Directory.GetParent(Path.GetFullPath(TempPath))?.FullName ?? string.Empty;
        public string ProjectName { get; set; } = "Untitled Project";
        public static NativePreviewOutputMode DefaultOutputMode { get; set; } = NativePreviewOutputMode.Required;
        public bool IsFrameRendered(uint frameIndex)
        {
            if (Clips is null || frameIndex >= TotalDuration || !renderedFrames.TryGetValue(frameIndex, out var saved)
                || !File.Exists(saved.Path)) return false;
            var identity = FrameHashHelper.GetFrameHash(Clips, frameIndex, saved.Options);
            return identity.CanReuse && identity.Hash == saved.Hash;
        }

        public string RenderFrame(uint frameIndex, int targetWidth, int targetHeight, CancellationToken token = default)
        {
            (targetWidth, targetHeight) = NormalizeTargetSize(targetWidth, targetHeight, requireEven: false);
            try
            {
                return MaterializePng(RenderFrameVfdCore(frameIndex, targetWidth, targetHeight, token));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log(ex, $"Render frame #{frameIndex}", this);
                using var errFrame = ClipInitializationFailure.CreateFallbackFrame(targetWidth, targetHeight, 8, "Rendering", ex.Message);
                var destPath = Path.Combine(GetMaterializedPreviewRoot(), $"projectFrameCut_RenderError_{frameIndex}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                errFrame.SaveToPng(destPath);
                return destPath;
            }
        }

        public string? TryRenderFrame(uint frameIndex, int targetWidth, int targetHeight, CancellationToken token = default)
        {
            (targetWidth, targetHeight) = NormalizeTargetSize(targetWidth, targetHeight, requireEven: false);
            try
            {
                return MaterializePng(RenderFrameVfdCore(frameIndex, targetWidth, targetHeight, token));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log(ex, $"Render frame #{frameIndex}", this);
                return null;
            }
        }

        private string RenderFrameVfdCore(uint frameIndex, int targetWidth, int targetHeight, CancellationToken token)
        {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
            using var renderMark = new UserMarkRange("Preview.RenderTimelineFrame", $"frame={frameIndex}, size={targetWidth}x{targetHeight}");
#endif
            ArgumentNullException.ThrowIfNull(Clips, "Clips not set yet.");
            var hashOptions = new FrameHashOptions
            {
                Width = targetWidth,
                Height = targetHeight,
                ProjectWidth = ProjectRelativeWidth,
                ProjectHeight = ProjectRelativeHeight,
                FrameRate = targetFrameRate,
                Stage = "timeline",
                ProcessFromCanvas = Timeline.ProcessEffectFromCanvas,
                AutoCenter = true,
            };
            var artifact = (RpcClient ?? RenderRpcBootstrap.Client).RenderTimelineFrameAsync(new TimelineFrameRequest
            {
                SessionId = RenderSessionId,
                FrameIndex = frameIndex,
                Width = targetWidth,
                Height = targetHeight,
                PreferredPixelFormat = PreviewPixelFormat.EncodedImage,
            }, token).AsTask().GetAwaiter().GetResult();
            var path = ResolveArtifactPath(artifact, token);
            if (artifact.PixelFormat != PreviewPixelFormat.VfdPicture)
                throw new InvalidDataException($"The render backend returned unsupported preview format {artifact.PixelFormat}.");
            if (!File.Exists(path))
                throw new FileNotFoundException("The render backend returned an artifact that does not exist.", path);
            var identity = FrameHashHelper.GetFrameHash(Clips, frameIndex, hashOptions);
            if (identity.CanReuse) renderedFrames[frameIndex] = (hashOptions, identity.Hash, path);
            return path;
        }

        public async Task<(RenderArtifact Artifact, string Path)> RenderPngPreviewAsync(uint frameIndex, int width, int height,
            Guid? clipId = null, CancellationToken cancellationToken = default)
        {
            await _updateDraftGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ArgumentNullException.ThrowIfNull(Clips, "Clips not set yet.");
                var client = RpcClient ?? RenderRpcBootstrap.Client;
                var artifact = clipId is { } id
                    ? await client.RenderClipPreviewAsync(new ClipPreviewRequest
                    {
                        SessionId = RenderSessionId,
                        ClipId = id,
                        FrameIndex = frameIndex,
                        CanvasWidth = width,
                        CanvasHeight = height,
                        ProjectWidth = ProjectRelativeWidth,
                        ProjectHeight = ProjectRelativeHeight,
                        PreferredPixelFormat = PreviewPixelFormat.PngImage,
                        BeforeLayout = true,
                    }, cancellationToken).ConfigureAwait(false)
                    : await client.RenderTimelineFrameAsync(new TimelineFrameRequest
                    {
                        SessionId = RenderSessionId,
                        FrameIndex = frameIndex,
                        Width = width,
                        Height = height,
                        PreferredPixelFormat = PreviewPixelFormat.PngImage,
                    }, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (artifact.MediaType != "image/png")
                        throw new InvalidDataException("The render backend did not return a PNG preview.");
                    var path = await ResolveArtifactPathAsync(artifact, cancellationToken).ConfigureAwait(false);
                    if (!File.Exists(path)) throw new FileNotFoundException("The preview PNG does not exist.", path);
                    return (artifact, path);
                }
                finally
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await client.ReleaseArtifactAsync(new() { SessionId = artifact.SessionId, ArtifactId = artifact.ArtifactId }, timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) { Log(ex, "Release PNG preview artifact", this); }
                }
            }
            finally
            {
                _updateDraftGate.Release();
            }
        }

        public IPicture GetFrame(uint frameIndex, int targetWidth, int targetHeight)
        {
            (targetWidth, targetHeight) = NormalizeTargetSize(targetWidth, targetHeight, requireEven: false);
            return PreviewFrameMaterializer.LoadVfd(RenderFrameVfdCore(frameIndex, targetWidth, targetHeight, default));
        }

        public byte[] RenderFramePngBytes(uint frameIndex, int targetWidth, int targetHeight, CancellationToken token = default)
        {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
            using var pngMark = new UserMarkRange("Preview.MaterializePng", $"frame={frameIndex}, size={targetWidth}x{targetHeight}");
#endif
            (targetWidth, targetHeight) = NormalizeTargetSize(targetWidth, targetHeight, requireEven: false);
            try
            {
                return PreviewFrameMaterializer.ToPngBytes(RenderFrameVfdCore(frameIndex, targetWidth, targetHeight, token));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log(ex, $"Render frame #{frameIndex}", this);
                using var frame = ClipInitializationFailure.CreateFallbackFrame(targetWidth, targetHeight, 8, "Rendering", ex.Message);
                return PreviewFrameMaterializer.ToPngBytes(frame);
            }
        }

        public PreviewFrameSource RenderFrameForDisplay(uint frameIndex, int targetWidth, int targetHeight, CancellationToken token)
        {
            (targetWidth, targetHeight) = NormalizeTargetSize(targetWidth, targetHeight, requireEven: false);
            if (DefaultOutputMode == NativePreviewOutputMode.Disabled || !OperatingSystem.IsWindows())
            {
                var path = RenderFrameVfdCore(frameIndex, targetWidth, targetHeight, token);
                return new PreviewFrameSource(path, targetWidth, targetHeight, PreviewPixelFormat.EncodedImage, false);
            }

            try
            {
                var artifact = (RpcClient ?? RenderRpcBootstrap.Client).RenderTimelineFrameAsync(new TimelineFrameRequest
                {
                    SessionId = RenderSessionId,
                    FrameIndex = frameIndex,
                    Width = targetWidth,
                    Height = targetHeight,
                    PreferredPixelFormat = PreviewPixelFormat.Rgba16FloatScRgb,
                }, token).AsTask().GetAwaiter().GetResult();
                if (artifact.PixelFormat != PreviewPixelFormat.VfdPicture)
                    throw new NotSupportedException("The render backend did not return a VFD preview artifact.");

                var path = ResolveArtifactPath(artifact, token);
                return new PreviewFrameSource(path, artifact.Width, artifact.Height, PreviewPixelFormat.Rgba16FloatScRgb, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !token.IsCancellationRequested)
            {
                Log(ex, $"Render native preview frame #{frameIndex}; using standard preview", this);
                var path = RenderFrameVfdCore(frameIndex, targetWidth, targetHeight, token);
                return new PreviewFrameSource(path, targetWidth, targetHeight, PreviewPixelFormat.EncodedImage, false);
            }
        }

        public async Task UpdateDraft(DraftStructureJSON json)
        {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
            using var updateMark = new UserMarkRange("Preview.Reconfigure");
            using (new UserMarkRange("Preview.WaitUpdateLock"))
#endif
            await _updateDraftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() => UpdateDraftCoreAsync(json)).ConfigureAwait(false);
            }
            finally
            {
                _updateDraftGate.Release();
            }
        }

        private async Task UpdateDraftCoreAsync(DraftStructureJSON json)
        {
            await StopDynamicPreviewAsync().ConfigureAwait(false);
#if DIAGHUB_ENABLE_TRACE_SYSTEM
            using var configureMark = new UserMarkRange("Preview.Reconfigure.Work", $"clips={json.Clips?.Length ?? 0}, soundtracks={json.SoundTracks?.Length ?? 0}");
#endif
            var sw = Stopwatch.StartNew();
            var clips = json.Clips ?? [];
            var clipsList = new List<IClip>();
            var reinitTasks = new List<Task>();

            foreach (var clip in clips)
            {
                if (clip is null)
                {
                    Log("Live preview skipped a null clip entry.", "warn");
                    continue;
                }
                if (clip.ClipType == ClipMode.MarkingClip)
                {
                    continue;
                }

                reinitTasks.Add(Task.Run(() =>
                {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
                    using var clipMark = new UserMarkRange("Preview.InitializeClip", $"clip={clip.Id}, type={clip.ClipType}");
#endif
                    IClip clipInstance = null!;
                    try
                    {
                        var clipJson = JsonSerializer.SerializeToElement(clip);
                        clipInstance = PluginManager.CreateClip(clipJson);
                    }
                    catch (Exception ex)
                    {
                        if (clipInstance is not null)
                            ClipInitializationFailure.Mark(clipInstance, "Initialization", ex);
                        Log(ex, $"Create clip instance for {clip.Name}", this);
                        return;
                    }
                    if (clipInstance is null)
                    {
                        Log($"Live preview skipped clip {clip.Id}/{clip.Name}: the clip provider returned no instance.", "warn");
                        return;
                    }
                    if (clipInstance.FilePath is not null)
                    {
                        if (clipInstance.FilePath.StartsWith('$'))
                        {
                            var assetId = clipInstance.FilePath.Substring(1);
                            var remoteAsset = RemoteAssets?.LastOrDefault(item =>
                                string.Equals(item.AssetId, assetId, StringComparison.Ordinal));
                            if (remoteAsset is not null && !string.IsNullOrWhiteSpace(remoteAsset.Path))
                            {
                                // A remote path is only useful to the render server. Keep it on the
                                // metadata clip so geometry/timing can still drive dynamic overlays;
                                // RenderClipFrame performs the actual source rendering remotely.
                                clipInstance.FilePath = remoteAsset.Path;
                            }
                            else if (AssetDatabase.Assets.TryGetValue(assetId, out var asset) && asset is not null && !string.IsNullOrWhiteSpace(asset.Path))
                            {
                                clipInstance.FilePath = asset.Path;
                            }
                            else
                            {
                                Log($"Live preview asset '{assetId}' was not found; the clip will use its fallback frame.", "warn");
                            }
                        }
                    }
                    try
                    {
                        EffectHelper.ResolveClipEffects(clipInstance);
                    }
                    catch (Exception ex)
                    {
                        ClipInitializationFailure.Mark(clipInstance, "ResolveEffect", ex);
                        Log(ex, $"Resolve preview metadata effects for {clipInstance.Name}", this);
                    }
                    lock (clipsList)
                    {
                        clipsList.Add(clipInstance);
                    }
                }));
            }

            await Task.WhenAll(reinitTasks).ConfigureAwait(false);

            Clips = clipsList.ToArray();
            {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
                using var audioMark = new UserMarkRange("Preview.InitializeSoundTracks", $"soundtracks={json.SoundTracks?.Length ?? 0}");
#endif
                var soundTracks = DraftImportAndExportHelper.JSONToISoundTracks(json, InitAtLoad: false).ToList();
                SoundTrackMetadata.AddMissingLegacyTracks(Clips, soundTracks, message => Log($"[LiveRender] {message}", "warn"), initialize: false);
                foreach (var track in SoundTracks ?? [])
                {
                    try { track.Dispose(); }
                    catch (Exception ex) { Log(ex, $"Dispose preview soundtrack metadata {track.Id}", this); }
                }
                SoundTracks = soundTracks.ToArray();
                audioSamples = null;
            }
            ulong max = 0;
            foreach (var clip in Clips)
            {
                var end = (ulong)clip.StartFrame + clip.Duration;
                if (end > uint.MaxValue)
                {
                    Log($"[LiveRender] Ignoring overflowing timeline end for clip {clip.Id}/{clip.Name}: start={clip.StartFrame}, duration={clip.Duration}.", "warn");
                    max = Math.Max(max, (ulong)clip.StartFrame + 1);
                    continue;
                }
                max = Math.Max(end, max);

            }

            TotalDuration = (uint)max;

#if DIAGHUB_ENABLE_TRACE_SYSTEM
            using var backendMark = new UserMarkRange("Preview.SyncBackendProject");
#endif
            var request = new OpenProjectRequest
            {
                SessionId = RenderSessionId,
                ProjectRoot = RenderProjectRoot ?? ProjectRoot,
                AllowedExternalSources = ProjectExternalSourceService.GetApprovals(RenderProjectRoot ?? ProjectRoot),
                ProjectJson = ProjectJson,
                TimelineJson = JsonSerializer.Serialize(json),
                ProxyRoot = string.Empty,
                ProjectWidth = Math.Max(1, ProjectRelativeWidth),
                ProjectHeight = Math.Max(1, ProjectRelativeHeight),
                FrameRate = Math.Max(1, targetFrameRate),
                Assets = RemoteAssets?.ToList() ?? AssetDatabase.Assets.Select(static item => new AssetPathEntry
                {
                    AssetId = item.Key,
                    Path = item.Value.Path ?? string.Empty,
                }).Where(static item => !string.IsNullOrWhiteSpace(item.Path)).ToList(),
            };
            // Start the Render backend only after a concrete project has been loaded.
            // This keeps application startup and the home page independent from the
            // Windows CLI RPC process. The project root is passed so each open
            // project gets a dedicated backend bound to it.
            if (RpcClient is null) RenderRpcBootstrap.Initialize(request.ProjectRoot, projectName: ProjectName);
            var session = await (RpcClient ?? RenderRpcBootstrap.Client).OpenProjectAsync(request).ConfigureAwait(false);
            if (_dynamicPreviewSnapshotHash != session.SnapshotHash)
            {
                _dynamicPreviewFrameStores.Clear();
                _dynamicPreviewSnapshotHash = session.SnapshotHash;
            }
            _backendReady.TrySetResult();
            _cacheableClipIds = session.CacheableClipIds.ToHashSet();
            HashIndex = session.HashIndex ?? new();
            renderedFrames.Clear();
            TotalDuration = session.Duration;

            Log($"[LiveRender] Updated {Clips.Length} clips and {SoundTracks.Length} soundtracks in {sw.ElapsedMilliseconds}ms.");
        }

        public async Task GenerateTimelineThumbnailsAsync(Guid clipId, string sourcePath, int totalFrames, int intervalFrames, int maxCount, int width, CancellationToken cancellationToken)
        {
            await _backendReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _updateDraftGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var client = RpcClient ?? RenderRpcBootstrap.Client;
                var response = await client.GenerateTimelineThumbnailsAsync(new()
                {
                    SessionId = RenderSessionId, ClipId = clipId, SourcePath = sourcePath,
                    TotalFrames = totalFrames, IntervalFrames = intervalFrames, MaxCount = maxCount, Width = width,
                }, cancellationToken).ConfigureAwait(false);
                try
                {
                    foreach (var artifact in response.Artifacts)
                    {
                        var path = await ResolveArtifactPathAsync(artifact, cancellationToken).ConfigureAwait(false);
                        var localPath = Path.Combine(ProjectRoot, "thumbs", "perClip", clipId.ToString(), "timeline", Path.GetFileName(artifact.ProjectRelativePath));
                        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(localPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                            File.Copy(path, localPath, true);
                        }
                    }
                }
                finally
                {
                    foreach (var artifact in response.Artifacts)
                    {
                        try
                        {
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            await client.ReleaseArtifactAsync(new() { SessionId = artifact.SessionId, ArtifactId = artifact.ArtifactId }, timeout.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) { Log(ex, "Release timeline thumbnail artifact", this); }
                    }
                }
                if (response.Errors.Count > 0)
                    Log($"Timeline thumbnail generation for clip {clipId} failed for {response.Errors.Count} frame(s).", "warn");
            }
            finally { _updateDraftGate.Release(); }
        }

        public string RenderClipFrame(Guid clipId, uint frameIndex, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, CancellationToken token)
            => RenderClipFramePngAsync(clipId, frameIndex, canvasWidth, canvasHeight, projectWidth, projectHeight, token).GetAwaiter().GetResult();

        private async Task<string> RenderClipFramePngAsync(Guid clipId, uint frameIndex, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, CancellationToken token, string? vectorClipJson = null)
        {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
            using var frameMark = new UserMarkRange("Preview.RenderClipPng", $"clip={clipId}, frame={frameIndex}, size={canvasWidth}x{canvasHeight}");
#endif
            var artifact = await (RpcClient ?? RenderRpcBootstrap.Client).RenderClipPreviewAsync(new ClipPreviewRequest
            {
                SessionId = RenderSessionId,
                ClipId = clipId,
                FrameIndex = frameIndex,
                CanvasWidth = canvasWidth,
                CanvasHeight = canvasHeight,
                ProjectWidth = projectWidth,
                ProjectHeight = projectHeight,
                PreferredPixelFormat = PreviewPixelFormat.PngImage,
                UsePreviewMetadata = true,
                VectorClipJson = vectorClipJson,
            }, token).ConfigureAwait(false);
            if (artifact.PixelFormat != PreviewPixelFormat.EncodedImage)
                throw new InvalidDataException($"The render backend returned unsupported preview format {artifact.PixelFormat}.");
            return await ResolveArtifactPathAsync(artifact, token).ConfigureAwait(false);
        }

        public PreviewFrameSource RenderClipFrameForDisplay(Guid clipId, uint frameIndex, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, CancellationToken token, string? vectorClipJson = null)
            => RenderClipFrameForDisplayAsync(clipId, frameIndex, canvasWidth, canvasHeight, projectWidth, projectHeight, token, vectorClipJson).GetAwaiter().GetResult();

        public async Task<PreviewFrameSource> RenderClipFrameForDisplayAsync(Guid clipId, uint frameIndex, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, CancellationToken token, string? vectorClipJson = null)
        {
            var format = DefaultOutputMode != NativePreviewOutputMode.Disabled && OperatingSystem.IsWindows()
                ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage;
            try { return await PrepareDisplayAsync(format).ConfigureAwait(false); }
            catch (Exception ex) when (format == PreviewPixelFormat.Rgba16FloatScRgb && ex is not OperationCanceledException && !token.IsCancellationRequested)
            {
                Log(ex, $"Render native preview for clip {clipId} at frame #{frameIndex}; using standard preview", this);
                return await PrepareDisplayAsync(PreviewPixelFormat.EncodedImage).ConfigureAwait(false);
            }

            async Task<PreviewFrameSource> PrepareDisplayAsync(PreviewPixelFormat target)
            {
                for (int attempt = 0; ; attempt++)
                {
                    var artifact = await (RpcClient ?? RenderRpcBootstrap.Client).RenderClipPreviewAsync(new ClipPreviewRequest
                    {
                        SessionId = RenderSessionId, ClipId = clipId,
                        FrameIndex = frameIndex, CanvasWidth = canvasWidth, CanvasHeight = canvasHeight,
                        ProjectWidth = projectWidth, ProjectHeight = projectHeight,
                        PreferredPixelFormat = target, VectorClipJson = vectorClipJson, UsePreviewMetadata = true,
                    }, token).ConfigureAwait(false);
                    if (artifact.PixelFormat != PreviewPixelFormat.VfdPicture)
                        throw new InvalidDataException($"The render backend returned unsupported preview format {artifact.PixelFormat}.");
                    try
                    {
                        var path = await ResolveArtifactPathAsync(artifact, token).ConfigureAwait(false);
                        return await Task.Run(() => PreviewFrameMaterializer.Prepare(new(path, artifact.Width, artifact.Height, target, false)
                        {
                            PreviewContainer = artifact.PreviewContainer, PreviewEffectHash = artifact.PreviewEffectHash,
                        }), token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (attempt == 0 && !token.IsCancellationRequested && (ex is IOException or InvalidDataException)) { }
                }
            }
        }

        public bool HasAudioSources()
        {
            return SoundTracks?.Any(track => SoundTrackMetadata.ReadBool(track.ExtraData, SoundTrackMetadata.EnabledKey, true)) ?? false;
        }

        public bool CanCacheClipFrame(Guid clipId)
            => Clips?.FirstOrDefault(c => c.Id == clipId) is { } clip
                && !ClipInitializationFailure.IsMarked(clip)
                && Timeline.CanCacheClipFrame(Clips, clip);

        public Task ResetAudioPlaybackSources() => Task.CompletedTask;

        public async Task<string?> RenderSomeAudio(int startIndex, int length, int targetFramerate, CancellationToken token, int sampleRate = 96000, int channels = 2)
        {
            if (!HasAudioSources())
            {
                return null;
            }
            var artifact = await (RpcClient ?? RenderRpcBootstrap.Client).RenderAudioSegmentAsync(new AudioSegmentRequest
            {
                SessionId = RenderSessionId,
                StartFrame = checked((uint)startIndex),
                Length = checked((uint)length),
                FrameRate = targetFramerate,
                SampleRate = sampleRate,
                Channels = channels,
            }, token).ConfigureAwait(false);
            return await ResolveArtifactPathAsync(artifact, token).ConfigureAwait(false);
        }

        public async Task<PreviewAudioClock> StartPreviewAudioAsync(uint startFrame, int frameRate, CancellationToken token)
        {
            if (!HasAudioSources())
                return new PreviewAudioClock { StartFrame = startFrame, SampleRate = 48000, Channels = 2 };
            return await (RpcClient ?? RenderRpcBootstrap.Client).ControlPreviewAudioAsync(new PreviewAudioCommandRequest
            {
                SessionId = RenderSessionId,
                Command = PreviewAudioCommand.Start,
                StartFrame = startFrame,
                FrameRate = Math.Max(1, frameRate),
            }, token).ConfigureAwait(false);
        }

        public async Task<PreviewAudioClock> GetPreviewAudioClockAsync(long generation, CancellationToken token)
            => await (RpcClient ?? RenderRpcBootstrap.Client).GetPreviewAudioClockAsync(new PreviewAudioClockRequest
            {
                SessionId = RenderSessionId,
                Generation = generation,
            }, token).ConfigureAwait(false);

        public async Task StopPreviewAudioAsync(CancellationToken token = default)
        {
            try
            {
                await (RpcClient ?? RenderRpcBootstrap.Client).ControlPreviewAudioAsync(new PreviewAudioCommandRequest
                {
                    SessionId = RenderSessionId,
                    Command = PreviewAudioCommand.Stop,
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }

        public async Task<string> RenderSomeFrames(int startIndex, int length, int targetWidth, int targetFramerate, int targetHeight, CancellationToken token, bool includeAudio = true)
        {
            (targetWidth, targetHeight) = NormalizeTargetSize(targetWidth, targetHeight, requireEven: false);
            var artifact = await (RpcClient ?? RenderRpcBootstrap.Client).RenderTimelineSegmentAsync(new TimelineSegmentRequest
            {
                SessionId = RenderSessionId,
                StartFrame = checked((uint)startIndex),
                Length = checked((uint)length),
                Width = targetWidth,
                Height = targetHeight,
                FrameRate = targetFramerate,
                IncludeAudio = includeAudio,
            }, token).ConfigureAwait(false);
            OnProgressChanged?.Invoke(1, TimeSpan.Zero);
            return await ResolveArtifactPathAsync(artifact, token).ConfigureAwait(false);
        }

        private string ResolveArtifactPath(RenderArtifact artifact, CancellationToken cancellationToken)
            => ResolveArtifactPathAsync(artifact, cancellationToken).GetAwaiter().GetResult();

        private Task<string> ResolveArtifactPathAsync(RenderArtifact artifact, CancellationToken cancellationToken)
            => ArtifactResolver is not null
                ? ArtifactResolver(artifact, cancellationToken)
                : Task.FromResult(RenderRpcBootstrap.ResolveArtifactPath(ProjectRoot, artifact));

        private string MaterializePng(string vfdPath)
        {
            var root = GetMaterializedPreviewRoot();
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, $"{Path.GetFileNameWithoutExtension(vfdPath)}.png");
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= File.GetLastWriteTimeUtc(vfdPath)) return path;

            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, PreviewFrameMaterializer.ToPngBytes(vfdPath));
                File.Move(temporaryPath, path, true);
                return path;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }

        private string GetMaterializedPreviewRoot()
            => Path.Combine(
                string.IsNullOrWhiteSpace(ProjectRoot) ? MauiProgram.CachePath : Path.Combine(ProjectRoot, "thumbs"),
                "unCacheablePerClip",
                RenderSessionId.ToString("N"));

        public void CleanupMaterializedPreviews()
        {
            _dynamicPreviewFrameStores.Clear();
            var root = GetMaterializedPreviewRoot();
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                var parent = Directory.GetParent(root)?.FullName;
                if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent);
            }
            catch (Exception ex)
            {
                Log(ex, $"Delete materialized preview files from {root}", this);
            }
        }

        private static (int width, int height) NormalizeTargetSize(int width, int height, bool requireEven)
        {
            var normalizedWidth = Math.Max(1, width);
            var normalizedHeight = Math.Max(1, height);

            if (!requireEven)
            {
                return (normalizedWidth, normalizedHeight);
            }

            if ((normalizedWidth & 1) == 1)
            {
                normalizedWidth++;
            }

            if ((normalizedHeight & 1) == 1)
            {
                normalizedHeight++;
            }

            normalizedWidth = Math.Max(2, normalizedWidth);
            normalizedHeight = Math.Max(2, normalizedHeight);
            return (normalizedWidth, normalizedHeight);
        }

    }
}
