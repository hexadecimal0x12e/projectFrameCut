using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Shared;
using projectFrameCut.Render.ClipsAndTracks;

namespace projectFrameCut.Render.Compose;

/// <summary>Session-local, bounded cache of final mixed PCM, indexed by absolute timeline samples.</summary>
public sealed class AudioSampleSource(IClip[] clips, ISoundTrack[] tracks, int frameRate, int sampleRate = 48000, int channels = 2)
{
    internal static readonly object Sync = new();
    private const int ChunkSamples = 40960;
    private const int MaxChunks = 8;
    private readonly object gate = new();
    private readonly Dictionary<int, FloatAudioSamples> chunks = new();
    private readonly LinkedList<int> recent = new();
    private readonly Dictionary<ISoundTrack, (string? Path, long Length, DateTime Modified)> files = new();
    private Func<int, int, CancellationToken, FloatAudioSamples>? read;
    private int configuration;
    public int FrameRate => frameRate;
    public int SamplePerSecond => sampleRate;
    public int ChannelCount => channels;
    public Func<string>? Revision { get; set; }

    public IAudioSamples<float> ReadFrame(uint frame, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        int start = checked((int)Math.Round((double)frame / frameRate * sampleRate));
        int end = checked((int)Math.Round(((double)frame + 1) / frameRate * sampleRate));
        return ReadSamples(start, end - start, token);
    }

    public FloatAudioSamples ReadSamples(int start, int count, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        int end = checked(start + count);
        var result = new FloatAudioSamples
        {
            Channels = Enumerable.Range(0, channels).Select(_ => new float[count]).ToArray(),
            SampleCount = count,
            SamplePerSecond = sampleRate,
        };
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var changed = new HashSet<ISoundTrack>();
            int current = GetConfiguration(changed);
            if (read is not null && current != configuration)
            {
                read = null;
                chunks.Clear();
                recent.Clear();
            }
            if (read is null)
            {
                lock (Sync)
                    foreach (var track in tracks)
                        if ((track.SamplePerSecond <= 0 || changed.Contains(track))
                            && SoundTrackMetadata.ReadBool(track.ExtraData, SoundTrackMetadata.EnabledKey, true)
                            && ClipInitializationFailure.GetStage(track.ExtraData) != "ExternalSourceUnavailable")
                        {
                            token.ThrowIfCancellationRequested();
                            SoundTrackMetadata.ReInit(track);
                        }
                read = new AudioComposer<float> { Clips = clips, SoundTracks = tracks }
                    .CreateSampleReader(frameRate, sampleRate, channels, out _);
                configuration = GetConfiguration();
            }
            for (int position = start; position < end;)
            {
                token.ThrowIfCancellationRequested();
                int chunkStart = position / ChunkSamples * ChunkSamples;
                if (!chunks.TryGetValue(chunkStart, out var chunk))
                {
                    chunk = read(chunkStart, Math.Min(ChunkSamples, int.MaxValue - chunkStart), token);
                    if (chunks.Count >= MaxChunks)
                    {
                        chunks.Remove(recent.First!.Value);
                        recent.RemoveFirst();
                    }
                    chunks.Add(chunkStart, chunk);
                }
                recent.Remove(chunkStart);
                recent.AddLast(chunkStart);
                int length = Math.Min(end - position, chunk.SampleCount - (position - chunkStart));
                for (int c = 0; c < channels; c++)
                    chunk.Channels[c].AsSpan(position - chunkStart, length).CopyTo(result.Channels[c].AsSpan(position - start));
                position += length;
            }
        }
        return result;
    }

    private int GetConfiguration(HashSet<ISoundTrack>? changed = null)
    {
        var hash = new HashCode();
        hash.Add(Revision?.Invoke());
        foreach (var track in tracks)
        {
            hash.Add(track.FilePath);
            hash.Add(track.StartFrame);
            hash.Add(track.RelativeStartFrame);
            hash.Add(track.Duration);
            hash.Add(track.Volume);
            hash.Add(track.Ratio);
            bool enabled = SoundTrackMetadata.ReadBool(track.ExtraData, SoundTrackMetadata.EnabledKey, true);
            hash.Add(enabled);
            hash.Add(track.EffectsInstances);
            if (!enabled || string.IsNullOrWhiteSpace(track.FilePath)) continue;
            var file = new FileInfo(track.FilePath);
            var stamp = (track.FilePath, file.Exists ? file.Length : -1, file.LastWriteTimeUtc);
            if (files.TryGetValue(track, out var previous) && previous != stamp) changed?.Add(track);
            files[track] = stamp;
            hash.Add(stamp);
        }
        return hash.ToHashCode();
    }
}
