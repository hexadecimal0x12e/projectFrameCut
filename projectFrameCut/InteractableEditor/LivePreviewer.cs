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
        bool RequireSwapChain);

    public class LivePreviewer
    {
        private const string StaticFrameCacheVersion = "v4-vfd";
        private const string ClipPreviewCacheVersion = "v4-vfd";
        public IClip[]? Clips;
        public ISoundTrack[]? SoundTracks;
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
        private IReadOnlyDictionary<uint, string> FrameHashLookup { get; set; } = new Dictionary<uint, string>();
        private IReadOnlyDictionary<Guid, IReadOnlyDictionary<uint, string>> ClipHashLookup { get; set; }
            = new Dictionary<Guid, IReadOnlyDictionary<uint, string>>();
        private readonly SemaphoreSlim _updateDraftGate = new(1, 1);
        private HashSet<Guid> _cacheableClipIds = [];
        public string ProjectRoot => string.IsNullOrWhiteSpace(TempPath) ? string.Empty : Directory.GetParent(Path.GetFullPath(TempPath))?.FullName ?? string.Empty;
        public string ProjectName { get; set; } = "Untitled Project";
        public static NativePreviewOutputMode DefaultOutputMode { get; set; } = NativePreviewOutputMode.Required;
        private bool HasExternalSources => Clips?.Any(x => RemoteRpcVideoSource.IsExternalPath(x.FilePath)) == true;

        public bool IsFrameRendered(uint frameIndex)
        {
            if (Clips == null || HasExternalSources || !CanCacheFrame(frameIndex)) return false;
            if (frameIndex >= TotalDuration) return false;
            var frameHash = FrameHashLookup.TryGetValue(frameIndex, out var indexedHash)
                ? indexedHash
                : Timeline.GetFrameHash(Clips, frameIndex);
            return Directory.Exists(TempPath)
                && Directory.EnumerateFiles(TempPath, $"projectFrameCut_Render_{StaticFrameCacheVersion}_{frameHash}_*.vfd", SearchOption.TopDirectoryOnly).Any();
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
            var frameHash = FrameHashLookup.TryGetValue(frameIndex, out var indexedHash)
                ? indexedHash
                : Timeline.GetFrameHash(Clips, frameIndex);
            var cachedPath = Path.Combine(ProjectRoot, "thumbs", $"projectFrameCut_Render_{StaticFrameCacheVersion}_{frameHash}_{targetWidth}x{targetHeight}_vfd8.vfd");
            if (!HasExternalSources && CanCacheFrame(frameIndex) && File.Exists(cachedPath)) return cachedPath;
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
                        SessionId = RenderSessionId, ClipId = id, FrameIndex = frameIndex,
                        CanvasWidth = width, CanvasHeight = height,
                        ProjectWidth = ProjectRelativeWidth, ProjectHeight = ProjectRelativeHeight,
                        PreferredPixelFormat = PreviewPixelFormat.PngImage, BeforeLayout = true,
                    }, cancellationToken).ConfigureAwait(false)
                    : await client.RenderTimelineFrameAsync(new TimelineFrameRequest
                    {
                        SessionId = RenderSessionId, FrameIndex = frameIndex, Width = width, Height = height,
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
                                var proxyPath = Path.Combine(MauiProgram.DataPath, "My Assets", ".proxy", $"{asset.AssetId}.mp4");
                                if (Path.Exists(proxyPath))
                                {
                                    clipInstance.FilePath = proxyPath;
                                    Log($"The proxy for {clipInstance.Name} is used.");
                                }
                                else
                                {
                                    Log($"The proxy for {clipInstance.Name} does not exist.");
                                }
                            }
                            else
                            {
                                Log($"Live preview asset '{assetId}' was not found; the clip will use its fallback frame.", "warn");
                            }
                        }
                        else if (ProxyRoot is not null && clipInstance.FilePath is not null)
                        {
                            var proxiedPath = Path.Combine(ProxyRoot, $"{Path.GetFileNameWithoutExtension(clipInstance.FilePath)}.proxy.mp4");

                            if (Path.Exists(proxiedPath))
                            {
                                clipInstance.FilePath = proxiedPath;
                                Log($"The proxy for {clipInstance.Name} is used.");
                            }
                            else
                            {
                                Log($"The proxy for {clipInstance.Name} does not exist.");
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
                ProxyRoot = RenderProxyRoot ?? ProxyRoot ?? string.Empty,
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
            _cacheableClipIds = session.CacheableClipIds.ToHashSet();
            HashIndex = session.HashIndex ?? new();
            FrameHashLookup = HashIndex.FrameHashes
                .GroupBy(entry => entry.FrameIndex)
                .ToDictionary(group => group.Key, group => group.Last().Hash);
            ClipHashLookup = HashIndex.ClipHashes.ToDictionary(
                entry => entry.ClipId,
                entry => (IReadOnlyDictionary<uint, string>)entry.FrameHashes
                    .GroupBy(frame => frame.FrameIndex)
                    .ToDictionary(group => group.Key, group => group.Last().Hash));
            TotalDuration = session.Duration;

            Log($"[LiveRender] Updated {Clips.Length} clips and {SoundTracks.Length} soundtracks in {sw.ElapsedMilliseconds}ms.");
        }

        public string RenderClipFrame(Guid clipId, uint frameIndex, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, CancellationToken token)
            => MaterializePng(RenderClipFrameVfd(clipId, frameIndex, canvasWidth, canvasHeight, projectWidth, projectHeight, token));

        private string RenderClipFrameVfd(Guid clipId, uint frameIndex, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, CancellationToken token, string? vectorClipJson = null)
        {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
            using var frameMark = new UserMarkRange("Preview.RenderClipVfd", $"clip={clipId}, frame={frameIndex}, size={canvasWidth}x{canvasHeight}");
#endif
            string? clipHash = null;
            if (ClipHashLookup.TryGetValue(clipId, out var clipHashes))
                clipHashes.TryGetValue(frameIndex, out clipHash);
            if (clipHash is null && Clips is { } clips && clips.FirstOrDefault(clip => clip.Id == clipId) is { } clip)
                clipHash = Timeline.GetClipFrameHash(clips, clip, frameIndex);
            if (!HasExternalSources && clipHash is not null && vectorClipJson is null
                && Clips is { } cacheClips && cacheClips.FirstOrDefault(c => c.Id == clipId) is { } cacheClip
                && CanCacheClipFrame(cacheClip.Id))
            {
                var cachedPath = Path.Combine(
                    ProjectRoot,
                    "thumbs",
                    "perClip",
                    clipId.ToString(),
                    "dynamic",
                    $"dynamic_{ClipPreviewCacheVersion}_{clipHash}_{Math.Max(1, projectWidth)}x{Math.Max(1, projectHeight)}_{Math.Max(1, canvasWidth)}x{Math.Max(1, canvasHeight)}_vfd8.vfd");
                if (File.Exists(cachedPath)) return cachedPath;
            }
            var artifact = (RpcClient ?? RenderRpcBootstrap.Client).RenderClipPreviewAsync(new ClipPreviewRequest
            {
                SessionId = RenderSessionId,
                ClipId = clipId,
                FrameIndex = frameIndex,
                CanvasWidth = canvasWidth,
                CanvasHeight = canvasHeight,
                ProjectWidth = projectWidth,
                ProjectHeight = projectHeight,
                PreferredPixelFormat = PreviewPixelFormat.EncodedImage,
                VectorClipJson = vectorClipJson,
            }, token).AsTask().GetAwaiter().GetResult();
            if (artifact.PixelFormat != PreviewPixelFormat.VfdPicture)
                throw new InvalidDataException($"The render backend returned unsupported preview format {artifact.PixelFormat}.");
            return ResolveArtifactPath(artifact, token);
        }

        public PreviewFrameSource RenderClipFrameForDisplay(Guid clipId, uint frameIndex, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, CancellationToken token, string? vectorClipJson = null)
        {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
            using var displayMark = new UserMarkRange("Preview.RenderClipDisplay", $"clip={clipId}, frame={frameIndex}, size={canvasWidth}x{canvasHeight}");
#endif
            if (DefaultOutputMode == NativePreviewOutputMode.Disabled || !OperatingSystem.IsWindows())
            {
                var path = RenderClipFrameVfd(clipId, frameIndex, canvasWidth, canvasHeight, projectWidth, projectHeight, token, vectorClipJson);
                return new PreviewFrameSource(path, canvasWidth, canvasHeight, PreviewPixelFormat.EncodedImage, false);
            }

            try
            {
                var artifact = (RpcClient ?? RenderRpcBootstrap.Client).RenderClipPreviewAsync(new ClipPreviewRequest
                {
                    SessionId = RenderSessionId,
                    ClipId = clipId,
                    FrameIndex = frameIndex,
                    CanvasWidth = canvasWidth,
                    CanvasHeight = canvasHeight,
                    ProjectWidth = projectWidth,
                    ProjectHeight = projectHeight,
                    PreferredPixelFormat = PreviewPixelFormat.Rgba16FloatScRgb,
                    VectorClipJson = vectorClipJson,
                }, token).AsTask().GetAwaiter().GetResult();
                if (artifact.PixelFormat != PreviewPixelFormat.VfdPicture)
                    throw new NotSupportedException("The render backend did not return a VFD clip-preview artifact.");

                var path = ResolveArtifactPath(artifact, token);
                return new PreviewFrameSource(path, artifact.Width, artifact.Height, PreviewPixelFormat.Rgba16FloatScRgb, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !token.IsCancellationRequested)
            {
                Log(ex, $"Render native preview for clip {clipId} at frame #{frameIndex}; using standard preview", this);
                var path = RenderClipFrameVfd(clipId, frameIndex, canvasWidth, canvasHeight, projectWidth, projectHeight, token, vectorClipJson);
                return new PreviewFrameSource(path, canvasWidth, canvasHeight, PreviewPixelFormat.EncodedImage, false);
            }
        }

        public bool HasAudioSources()
        {
            return SoundTracks?.Any(track => SoundTrackMetadata.ReadBool(track.ExtraData, SoundTrackMetadata.EnabledKey, true)) ?? false;
        }

        public bool CanCacheClipFrame(Guid clipId)
            => _cacheableClipIds.Contains(clipId);

        private bool CanCacheFrame(uint frameIndex)
            => Clips is { } clips && clips.Where(clip => clip.ContainsFrame(frameIndex)
                || clip.ExtendToWholeDraft && clip.LayerIndex > Renderer.SubTrackOffset).All(clip => CanCacheClipFrame(clip.Id));

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

        private static string BuildFrameCacheKey(string frameHash, int width, int height)
            => $"{StaticFrameCacheVersion}_{frameHash}_{width}x{height}";
    }
}
