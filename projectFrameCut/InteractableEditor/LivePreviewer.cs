using Microsoft.Maui.Controls.PlatformConfiguration;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using projectFrameCut.Drawing.Base.Picture;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut.LivePreview
{
    public class LivePreviewer
    {
        private const string StaticFrameCacheVersion = "v2-target-layout";
        private const string ClipPreviewCacheVersion = "v2-frame-content";
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
        public string ProjectRoot => string.IsNullOrWhiteSpace(TempPath) ? string.Empty : Directory.GetParent(Path.GetFullPath(TempPath))?.FullName ?? string.Empty;
        public string ProjectName { get; set; } = "Untitled Project";

        public bool IsFrameRendered(uint frameIndex)
        {
            if (Clips == null) return false;
            if (frameIndex >= TotalDuration) return false;
            var frameHash = FrameHashLookup.TryGetValue(frameIndex, out var indexedHash) ? indexedHash : "nullframe";
            return Directory.Exists(TempPath)
                && Directory.EnumerateFiles(TempPath, $"projectFrameCut_Render_{StaticFrameCacheVersion}_{frameHash}_*.png", SearchOption.TopDirectoryOnly).Any();
        }

        public string RenderFrame(uint frameIndex, int targetWidth, int targetHeight)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(Clips, "Clips not set yet.");
                (targetWidth, targetHeight) = NormalizeTargetSize(targetWidth, targetHeight, requireEven: false);
                var frameHash = FrameHashLookup.TryGetValue(frameIndex, out var indexedHash) ? indexedHash : "nullframe";
                var cachedPath = Path.Combine(ProjectRoot, "thumbs", $"projectFrameCut_Render_{StaticFrameCacheVersion}_{frameHash}_{targetWidth}x{targetHeight}.png");
                if (File.Exists(cachedPath)) return cachedPath;
                var artifact = (RpcClient ?? RenderRpcBootstrap.Client).RenderTimelineFrameAsync(new TimelineFrameRequest
                {
                    SessionId = RenderSessionId,
                    FrameIndex = frameIndex,
                    Width = targetWidth,
                    Height = targetHeight,
                }).AsTask().GetAwaiter().GetResult();
                return ResolveArtifactPath(artifact, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log(ex, $"Render frame #{frameIndex}", this);
                var errFrame = ClipInitializationFailure.CreateFallbackFrame(targetWidth, targetHeight, 8, "Rendering", ex.Message);
                var destPath = Path.Combine(TempPath, $"projectFrameCut_RenderError_{frameIndex}.png");
                errFrame.SaveToPng(destPath);
                return destPath;
            }
        }

        public IPicture GetFrame(uint frameIndex, int targetWidth, int targetHeight)
        {
            return new Picture8bpp(RenderFrame(frameIndex, targetWidth, targetHeight));
        }

        public async Task UpdateDraft(DraftStructureJSON json)
        {
            var clips = json.Clips;
            clips ??= [];

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
                        if (RpcClient is null)
                        {
                            clipInstance.ReInit(8);
                            if (!ClipInitializationFailure.HasDeferredFailures(clipInstance.ExtraData))
                                ClipInitializationFailure.Clear(clipInstance);
                        }
                    }
                    catch (Exception ex)
                    {
                        ClipInitializationFailure.Mark(clipInstance, "Source or ResolveEffect", ex);
                        Log(ex, $"Initialize live-preview clip {clipInstance.Name} ({clipInstance.Id}); using checkerboard fallback", this);
                    }
                    finally
                    {
                        // Remote media normally cannot be opened on the UI device. The clip is still
                        // required for ContainsFrame/layout calculations; its bitmap comes from RPC.
                        lock (clipsList)
                        {
                            clipsList.Add(clipInstance);
                        }
                    }
                }));
            }

            await Task.WhenAll(reinitTasks);

            Clips = clipsList.ToArray();
            SoundTracks = DraftImportAndExportHelper.JSONToISoundTracks(json).ToArray();
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

            var request = new OpenProjectRequest
            {
                SessionId = RenderSessionId,
                ProjectRoot = RenderProjectRoot ?? ProjectRoot,
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

            Log($"[LiveRender] Updated clips, total {Clips.Length} clips.");
        }

        public string RenderClipFrame(Guid clipId, uint frameIndex, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, CancellationToken token)
        {
            if (ClipHashLookup.TryGetValue(clipId, out var clipHashes)
                && clipHashes.TryGetValue(frameIndex, out var clipHash))
            {
                var cachedPath = Path.Combine(
                    ProjectRoot,
                    "thumbs",
                    "perClip",
                    clipId.ToString(),
                    "dynamic",
                    $"dynamic_{ClipPreviewCacheVersion}_{clipHash}_{Math.Max(1, projectWidth)}x{Math.Max(1, projectHeight)}_{Math.Max(1, canvasWidth)}x{Math.Max(1, canvasHeight)}.png");
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
            }, token).AsTask().GetAwaiter().GetResult();
            return ResolveArtifactPath(artifact, token);
        }

        public bool HasAudioSources()
        {
            var hasAudioClip = Clips?.Any(c => c.ClipType == ClipMode.AudioClip || c.ClipType == ClipMode.VideoClip) ?? false;
            var hasSoundTrack = SoundTracks?.Any() ?? false;
            return hasAudioClip || hasSoundTrack;
        }

        public async Task ResetAudioPlaybackSources(int ppb = 8)
        {
            // Remote source paths belong to the render server and cannot be reopened by the UI
            // process. The remote OpenProject call already initialized these sources; playback
            // below consumes the WAV/MP4 artifacts downloaded from that server.
            if (RpcClient is not null)
            {
                return;
            }

            if (Clips is not null)
            {
                foreach (var clip in Clips.Where(c => c.ClipType == ClipMode.AudioClip || c.ClipType == ClipMode.VideoClip))
                {
                    await Task.Run(() => clip.ReInit(ppb));
                }
            }

            if (SoundTracks is not null)
            {
                foreach (var track in SoundTracks)
                {
                    await Task.Run(track.ReInit);
                }
            }
        }

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
