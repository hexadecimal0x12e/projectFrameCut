using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.Rendering;
using System.Collections.Concurrent;

namespace projectFrameCut.Render.RPCProtocol;

public sealed partial class RenderBackendService
{
    private void StartPreviewHashWarmup(BackendSession session)
    {
        foreach (var mode in new[] { FrameHashMode.Static, FrameHashMode.Dynamic })
        foreach (var pixelMode in new[] { 8, 16 })
            PreparePreviewHashes(session, new()
            {
                Mode = mode, Width = session.Width, Height = session.Height,
                ProjectWidth = session.Width, ProjectHeight = session.Height,
                FrameRate = session.FrameRate, PixelMode = pixelMode,
                Stage = "clip", ProcessFromCanvas = Timeline.ProcessEffectFromCanvas,
            });
    }

    private bool TryGetPreparedPreviewHash(BackendSession session, IClip clip, uint frame,
        FrameHashOptions options, out FrameHashResult hash)
    {
        var prepared = PreparePreviewHashes(session, options);
        hash = default;
        if (!prepared.Clips.TryGetValue(clip.Id, out var saved)
            || saved.Dependencies.Any(ClipInitializationFailure.IsMarked)
            || saved.Guard != GetPreviewHashGuard(session, saved.Dependencies)) return false;
        if (!saved.Hashes.TryGetValue(clip is IImmutableContentClip ? 0 : frame, out var value)) return false;
        hash = new(value, true);
        return true;
    }

    private PreparedPreviewHashes PreparePreviewHashes(BackendSession session, FrameHashOptions options)
    {
        lock (session.PreviewHashes)
        {
            if (session.PreviewHashes.TryGetValue(options, out var saved)) return saved;
            var prepared = new PreparedPreviewHashes();
            // Keep viewport resizing from retaining an unlimited number of full-project indexes.
            if (session.PreviewHashes.Count >= 8)
            {
                var oldest = session.PreviewHashes.Keys.First();
                session.PreviewHashes[oldest].Cancel();
                session.PreviewHashes.Remove(oldest);
            }
            session.PreviewHashes.Add(options, prepared);
            var operation = session.BeginOperation();
            _ = Task.Run(async () =>
            {
                using (operation)
                using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, prepared.Cancellation.Token))
                {
                    var token = cancellation.Token;
                    try
                    {
                        using var sources = ProjectExternalSourceRuntime.Use(session.ExternalSources);
                        var clips = GetVisualClips(session.Clips);
                        foreach (var clip in clips)
                        {
                            var start = clip.ExtendToWholeDraft ? 0u : clip.StartFrame;
                            var end = clip.ExtendToWholeDraft ? session.Duration
                                : Math.Min((ulong)session.Duration, (ulong)clip.StartFrame + clip.Duration);
                            PreparedClipHashes? entry = null;
                            for (ulong frame = start; frame < end;)
                            {
                                token.ThrowIfCancellationRequested();
                                await session.RenderGate.WaitAsync(token).ConfigureAwait(false);
                                try
                                {
                                    entry ??= new PreparedClipHashes(
                                        [clip, .. Timeline.CollectHashDependencies(clips, clip, [clip.Id])]);
                                    entry.Guard ??= GetPreviewHashGuard(session, entry.Dependencies);
                                    // A null render callback only accepts identities computable in static mode,
                                    // while retaining the requested mode in the existing cache key.
                                    var batchEnd = Math.Min(end, frame + 16);
                                    for (; frame < batchEnd; frame++)
                                    {
                                        token.ThrowIfCancellationRequested();
                                        var identity = FrameHashHelper.GetClipHash(clips, clip, (uint)frame, options,
                                            token: token);
                                        if (!identity.CanReuse)
                                        {
                                            frame = end;
                                            break;
                                        }
                                        entry.Hashes[clip is IImmutableContentClip ? 0 : (uint)frame] = identity.Hash;
                                        prepared.Clips.TryAdd(clip.Id, entry);
                                        if (clip is IImmutableContentClip)
                                        {
                                            frame = end;
                                            break;
                                        }
                                    }
                                }
                                finally { session.RenderGate.Release(); }
                                await Task.Delay(1, token).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception ex) { Log(ex, $"Prepare preview frame hashes for session {session.Id}"); }
                    finally { prepared.Cancellation.Dispose(); }
                }
            });
            return prepared;
        }
    }

    private static string GetPreviewHashGuard(BackendSession session, IClip[] clips)
        => FrameHashHelper.HashValue(new
        {
            session.CacheNamespace,
            Sources = clips.Select(c => FrameHashHelper.SourceFingerprint(c.FilePath)).ToArray(),
            Fonts = clips.OfType<TextClip>().Select(c => FrameHashHelper.SourceFingerprint(c.FontPath)).ToArray(),
            EffectHelper.ForcePreferToType, EffectHelper.DefaultImplementsType,
            PreferredPlugins = RenderAPIBase.Plugins.IPluginBase.EffectImplementations.PreferredPlugins
                .OrderBy(p => p.Key.TypeName, StringComparer.Ordinal).ThenBy(p => p.Key.ImplementType)
                .Select(p => new { p.Key, p.Value }).ToArray(),
            Compose.ClassicOverlayMixture.EnableApproximatePath,
        });

    private sealed class PreparedPreviewHashes
    {
        public ConcurrentDictionary<Guid, PreparedClipHashes> Clips { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();

        public void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private sealed class PreparedClipHashes(IClip[] dependencies)
    {
        public IClip[] Dependencies { get; } = dependencies;
        public string? Guard { get; set; }
        public ConcurrentDictionary<uint, string> Hashes { get; } = new();
    }

    private sealed partial class BackendSession
    {
        public Dictionary<FrameHashOptions, PreparedPreviewHashes> PreviewHashes { get; } = new();
    }
}
