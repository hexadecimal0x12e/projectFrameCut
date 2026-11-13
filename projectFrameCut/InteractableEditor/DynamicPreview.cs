using projectFrameCut.Asset;
using projectFrameCut.LivePreview;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Text.Json;

namespace projectFrameCut.InteractableEditor;

public sealed partial class DynamicPreview : IDisposable
{
    private IClip[]? _clips;
    private LivePreviewer? _previewer;
    private long _renderVersion;
    private readonly object _clipsGate = new();
    private int _activePreviewOps;
    private readonly List<IClip[]> _pendingDisposeClipBatches = new();
    private readonly ConcurrentDictionary<string, byte> _sourceColorFallbackLogKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _missingProviderFallbackLogKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _effectFallbackLogKeys = new(StringComparer.Ordinal);

    public event Action<Guid, string>? ClipInitializationFailed;
    public event Action<Guid>? ClipInitializationRecovered;

    public DynamicPreview()
    {

    }

    public IClip[]? Clips => _clips;

    public async Task UpdateDraft(DraftStructureJSON json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var clips = json.Clips;
        if (clips is null || clips.Length == 0) return;

        var clipsList = new List<IClip>();
        var reinitTasks = new List<Task>();

        foreach (var clip in clips)
        {
            if (clip.ClipType == ClipMode.MarkingClip)
            {
                continue;
            }

            var clipJson = JsonSerializer.SerializeToElement(clip);
            var clipInstance = PluginManager.CreateClip(clipJson);
            if (clipInstance.FilePath?.StartsWith('$') == true)
            {
                clipInstance.FilePath = AssetDatabase.Assets[clipInstance.FilePath.Substring(1)].Path;
            }
            clipsList.Add(clipInstance);
            reinitTasks.Add(Task.Run(() =>
            {
                try
                {
                    ClipInitializationFailure.Clear(clipInstance);
                    clipInstance.ReInit(8);
                }
                catch (Exception ex)
                {
                    ClipInitializationFailure.Mark(clipInstance, "Source or ResolveEffect", ex);
                    Log(ex, $"Initialize preview clip {clipInstance.Name} ({clipInstance.Id}); using checkerboard fallback", this);
                }
            }));
        }

        await Task.WhenAll(reinitTasks);

        var newClips = clipsList.ToArray();

        IClip[]? batchToDispose = null;
        lock (_clipsGate)
        {
            var oldClips = _clips;
            _clips = newClips;
            if (oldClips is not null)
            {
                if (_activePreviewOps > 0)
                {
                    _pendingDisposeClipBatches.Add(oldClips);
                }
                else
                {
                    batchToDispose = oldClips;
                }
            }
        }

        if (batchToDispose is not null)
        {
            DisposeClipBatch(batchToDispose);
        }

        ResetFallbackLogs();
        CacheOverlayPreparedPreviews([]);
        foreach (var clip in newClips) NotifyInitializationState(clip);
    }

    private void NotifyInitializationState(IClip clip)
    {
        if (ClipInitializationFailure.IsMarked(clip))
            ClipInitializationFailed?.Invoke(clip.Id, ClipInitializationFailure.GetDescription(clip.ExtraData));
        else
            ClipInitializationRecovered?.Invoke(clip.Id);
    }

    public void SetClips(IClip[]? clips)
    {
        if (clips is null)
        {
            return;
        }

        CancelClipWarmups();

        IClip[]? batchToDispose = null;
        lock (_clipsGate)
        {
            if (ReferenceEquals(_clips, clips)) return;
            var oldClips = _clips;
            _clips = clips;
            if (oldClips is not null)
            {
                if (_activePreviewOps > 0)
                {
                    _pendingDisposeClipBatches.Add(oldClips);
                }
                else
                {
                    batchToDispose = oldClips;
                }
            }
        }

        if (batchToDispose is not null)
        {
            DisposeClipBatch(batchToDispose);
        }

        ResetFallbackLogs();
        CacheOverlayPreparedPreviews([]);
        foreach (var clip in clips) NotifyInitializationState(clip);
    }

    public void SetLivePreviewer(ref LivePreviewer? previewer)
    {
        _previewer = previewer;
    }

    public void Dispose()
    {
        effectRuntime.Dispose();
        nodeCache.Dispose();
        CancelClipWarmups();
        Interlocked.Increment(ref _renderVersion);
        Interlocked.Increment(ref _prepareVersion);
        DisposeClips();
        ResetFallbackLogs();
        CacheOverlayPreparedPreviews([]);
    }

    private static void LogOnce(ConcurrentDictionary<string, byte> gate, string key, string message)
    {
        if (gate.TryAdd(key, 0))
        {
            Log(message);
        }
    }

    private void ResetFallbackLogs()
    {
        _sourceColorFallbackLogKeys.Clear();
        _missingProviderFallbackLogKeys.Clear();
        _effectFallbackLogKeys.Clear();
    }

    private void DisposeClips()
    {
        List<IClip[]>? batchesToDispose = null;
        lock (_clipsGate)
        {
            if (_clips is not null)
            {
                if (_activePreviewOps > 0)
                {
                    _pendingDisposeClipBatches.Add(_clips);
                }
                else
                {
                    batchesToDispose ??= new List<IClip[]>();
                    batchesToDispose.Add(_clips);
                }
            }

            _clips = null;

            if (_activePreviewOps == 0 && _pendingDisposeClipBatches.Count > 0)
            {
                batchesToDispose ??= new List<IClip[]>();
                batchesToDispose.AddRange(_pendingDisposeClipBatches);
                _pendingDisposeClipBatches.Clear();
            }
        }

        if (batchesToDispose is null)
        {
            return;
        }

        foreach (var batch in batchesToDispose)
        {
            DisposeClipBatch(batch);
        }
    }

    private IClip[] AcquireClipsSnapshot()
    {
        lock (_clipsGate)
        {
            _activePreviewOps++;
            return _clips ?? Array.Empty<IClip>();
        }
    }

    private void ReleaseClipsSnapshot()
    {
        List<IClip[]>? batchesToDispose = null;
        lock (_clipsGate)
        {
            if (_activePreviewOps > 0)
            {
                _activePreviewOps--;
            }

            if (_activePreviewOps == 0 && _pendingDisposeClipBatches.Count > 0)
            {
                batchesToDispose = new List<IClip[]>(_pendingDisposeClipBatches);
                _pendingDisposeClipBatches.Clear();
            }
        }

        if (batchesToDispose is null)
        {
            return;
        }

        foreach (var batch in batchesToDispose)
        {
            DisposeClipBatch(batch);
        }
    }

    private static void DisposeClipBatch(IReadOnlyList<IClip> clips)
    {
        foreach (var clip in clips)
        {
            try
            {
                EffectHelper.ReleaseClipEffects(clip);
                clip.Dispose();
            }
            catch
            {
            }
        }
    }
}
