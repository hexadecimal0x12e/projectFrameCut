using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Sources;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;

namespace projectFrameCut.Render.Rendering;

internal sealed class PreviewVideoProxies(string projectRoot) : IAsyncDisposable
{
    private const int SizeTolerance = 15;
    private readonly string root = Path.Combine(projectRoot, "thumbs", "sourceProxies", "v1");
    private readonly object gate = new();
    private CacheIndexDatabase? database;
    private CacheIndexDatabase Database
    {
        get
        {
            lock (gate) return database ??= new(root, """
                CREATE TABLE IF NOT EXISTS video_proxies (
                    id TEXT PRIMARY KEY, source_identity TEXT NOT NULL, path TEXT NOT NULL,
                    width INTEGER NOT NULL, height INTEGER NOT NULL, hdr INTEGER NOT NULL,
                    has_alpha INTEGER NOT NULL, has_auxiliary INTEGER NOT NULL, length INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS proxy_source ON video_proxies(source_identity);
                """);
        }
    }
    private readonly Dictionary<string, Source> sources = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<string, VideoClip.VideoDecoderPool> pools = new(StringComparer.Ordinal);
    private readonly List<Request> pending = [];
    private readonly HashSet<(string Source, int Width, int Height)> failed = [];
    private readonly CancellationTokenSource cancellation = new();
    private Task? worker;
    private Request? active;
    private bool disposed;

    public Scope Enter(IEnumerable<IClip> clips, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, uint frame)
    {
        var snapshot = new Dictionary<string, Snapshot>(sources.Comparer);
        lock (gate)
        {
            foreach (var clip in clips.OfType<VideoClip>())
            {
                if (clip.FilePath is not { Length: > 0 } path || path[0] is '#' or '$' || snapshot.ContainsKey(path) || !File.Exists(path)) continue;
                try
                {
                    var info = new FileInfo(Path.GetFullPath(path));
                    string identity = Hash($"{(OperatingSystem.IsWindows() ? info.FullName.ToUpperInvariant() : info.FullName)}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}");
                    if (!sources.TryGetValue(info.FullName, out var source) || source.Identity != identity)
                    {
                        source = new(info.FullName, identity);
                        sources[info.FullName] = source;
                        Load(source);
                    }
                    snapshot[path] = new(source, source.Entries.ToArray());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log(ex, $"Read preview proxy cache for {path}");
                }
            }
        }
        var scope = new Scope(this, snapshot);
        foreach (var clip in clips.OfType<VideoClip>())
        {
            if (!clip.ExtendToWholeDraft && (frame < clip.StartFrame || (ulong)frame >= (ulong)clip.StartFrame + clip.Duration)) continue;
            if (clip.Decoder is not { Width: > 0, Height: > 0 } decoder) continue;
            var region = clip.GetSourceRegion(decoder.Width, decoder.Height);
            scope.Resolve(clip, decoder.Width, decoder.Height, region.Width, region.Height,
                Math.Max(1, (int)Math.Round((double)(clip.TargetWidth > 0 ? clip.TargetWidth : projectWidth) * canvasWidth / Math.Max(1, projectWidth))),
                Math.Max(1, (int)Math.Round((double)(clip.TargetHeight > 0 ? clip.TargetHeight : projectHeight) * canvasHeight / Math.Max(1, projectHeight))));
        }
        return scope;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private void Load(Source source)
    {
        if (!File.Exists(Path.Combine(root, CacheIndexDatabase.FileName))) return;
        try
        {
            using var connection = Database.Open();
            using var query = CacheIndexDatabase.Command(connection, """
                SELECT id, path, width, height, hdr, has_alpha, has_auxiliary, length
                FROM video_proxies WHERE source_identity=$source;
                """, ("$source", source.Identity));
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var entry = new Entry
                {
                    Id = reader.GetString(0), SourceIdentity = source.Identity,
                    Path = CacheIndexDatabase.ResolvePath(root, reader.GetString(1)),
                    Width = reader.GetInt32(2), Height = reader.GetInt32(3), Hdr = reader.GetBoolean(4),
                    HasAlpha = reader.GetBoolean(5), HasAuxiliary = reader.GetBoolean(6), Length = reader.GetInt64(7),
                };
                if (entry.Width <= 0 || entry.Height <= 0 || !Guid.TryParseExact(entry.Id, "N", out _) || entry.Length <= 0) continue;
                var info = new FileInfo(entry.Path);
                if (!info.Exists || info.Length != entry.Length) continue;
                source.Entries.Add(entry);
            }
        }
        catch (Exception ex) { Log(ex, $"Read preview proxy index for {source.Path}"); }
    }

    private void Enqueue(Source source, int width, int height, bool hdr)
    {
        lock (gate)
        {
            if (disposed || failed.Contains((source.Identity, width, height))
                || source.Entries.Any(e => Matches(e.Width, e.Height, width, height))
                || active is { } a && a.Source.Identity == source.Identity && Matches(a.Width, a.Height, width, height)
                || pending.Any(r => r.Source.Identity == source.Identity && Matches(r.Width, r.Height, width, height))) return;
            pending.Add(new(source, width, height, hdr));
            if (worker is null || worker.IsCompleted)
            {
                using (ExecutionContext.SuppressFlow()) worker = Task.Run(Generate);
            }
        }
    }

    private void Generate()
    {
        while (true)
        {
            Request request;
            lock (gate)
            {
                if (disposed || pending.Count == 0) { worker = null; return; }
                request = pending[0];
                pending.RemoveAt(0);
                active = request;
            }
            var token = cancellation.Token;
            string directory = Path.Combine(root, request.Source.Identity);
            string id = Guid.NewGuid().ToString("N");
            string output = Path.Combine(directory, id + ".mkv");
            string temporary = output + ".tmp";
            bool published = false;
            try
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(directory);
                Log($"[PreviewProxy] Generating {request.Width}x{request.Height}: {request.Source.Path}");
                var result = VideoResizer.ReencodePreviewProxy(request.Source.Path, temporary, request.Width, request.Height, token);
                var entry = new Entry()
                {
                    Id = id, SourceIdentity = request.Source.Identity,
                    Width = result.Width, Height = result.Height, Hdr = request.Hdr,
                    HasAlpha = result.HasAlpha, HasAuxiliary = result.HasAuxiliary,
                    Path = temporary, Length = new FileInfo(temporary).Length,
                };
                using (var decoder = CreateDecoder(entry, entry.Hdr)) Validate(decoder, entry.Width, entry.Height);
                var sourceInfo = new FileInfo(request.Source.Path);
                string sourceIdentity = Hash($"{(OperatingSystem.IsWindows() ? sourceInfo.FullName.ToUpperInvariant() : sourceInfo.FullName)}\n{sourceInfo.Length}\n{sourceInfo.LastWriteTimeUtc.Ticks}");
                if (sourceIdentity != request.Source.Identity) throw new IOException("The source changed while its preview proxy was being generated.");
                token.ThrowIfCancellationRequested();
                File.Move(temporary, output);
                entry.Path = output;
                using (var connection = Database.Open())
                using (var command = CacheIndexDatabase.Command(connection, """
                    INSERT INTO video_proxies(id, source_identity, path, width, height, hdr, has_alpha, has_auxiliary, length)
                    VALUES ($id, $source, $path, $width, $height, $hdr, $alpha, $auxiliary, $length);
                    """, ("$id", entry.Id), ("$source", entry.SourceIdentity), ("$path", Path.GetRelativePath(root, output)),
                    ("$width", entry.Width), ("$height", entry.Height), ("$hdr", entry.Hdr),
                    ("$alpha", entry.HasAlpha), ("$auxiliary", entry.HasAuxiliary), ("$length", entry.Length)))
                    command.ExecuteNonQuery();
                published = true;
                lock (gate) request.Source.Entries.Add(entry);
                Log($"[PreviewProxy] Saved {entry.Width}x{entry.Height}: {output}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (gate) failed.Add((request.Source.Identity, request.Width, request.Height));
                Log(ex, $"Generate preview proxy for {request.Source.Path} at {request.Width}x{request.Height}; using original source");
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                    if (!published) File.Delete(output);
                }
                catch (Exception ex) { Log(ex, $"Clean incomplete preview proxy {output}"); }
                lock (gate) active = null;
            }
        }
    }

    private static void Validate(IVideoSource decoder, int width, int height)
    {
        if (decoder.Width != width || decoder.Height != height) throw new InvalidDataException("Invalid preview proxy dimensions.");
        using var first = decoder.GetFrame(0, 0, 0, width, height, 1, 1);
        if (decoder.TotalFrames > 1)
        {
            using var last = decoder.GetFrame(checked((uint)(decoder.TotalFrames - 1)), 0, 0, width, height, 1, 1);
        }
    }

    private static bool Matches(int width, int height, int requestedWidth, int requestedHeight)
        => Math.Abs(width - requestedWidth) <= SizeTolerance && Math.Abs(height - requestedHeight) <= SizeTolerance;

    private static IVideoSource CreateDecoder(Entry entry, bool hdr)
        => entry.HasAuxiliary ? new AlphaBrightnessDecoderContext(entry.Path)
            : hdr || entry.HasAlpha ? new HDRDecoderContext(entry.Path, entry.HasAlpha)
            : new DecoderContext8Bit(entry.Path);

    private IPicture Read(Entry entry, bool hdr, uint frame, int x, int y, int width, int height,
        int targetWidth, int targetHeight, double brightness, IPicture.PicturePixelMode pixelMode)
    {
        VideoClip.VideoDecoderPool pool;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string key = entry.Path + (hdr ? "::hdr" : "::sdr");
            if (!pools.TryGetValue(key, out pool!))
                pools[key] = pool = VideoClip.AcquireDecoderPool(key, () => CreateDecoder(entry, hdr));
        }
        using var lease = pool.Rent(frame);
        var decoder = lease.Decoder;
        if (decoder.Width != entry.Width || decoder.Height != entry.Height)
            throw new InvalidDataException("Invalid preview proxy dimensions.");
        if (decoder.TotalFrames > 0) frame = (uint)Math.Min(frame, decoder.TotalFrames - 1);
        var picture = decoder is IHDRVideoSource h
            ? h.GetHDRFrame(frame, x, y, width, height, targetWidth, targetHeight, hasAlpha: true)
                .SetBrightnessOffset(brightness).ToBitPerPixel(pixelMode)
            : decoder.GetFrame(frame, x, y, width, height, targetWidth, targetHeight).ToBitPerPixel(pixelMode);
        lease.MarkPosition(frame);
        return picture;
    }

    private void Invalidate(Source source, Entry entry, Exception error)
    {
        lock (gate)
        {
            if (!source.Entries.Remove(entry)) return;
            failed.Add((source.Identity, entry.Width, entry.Height));
        }
        Log(error, $"Read preview proxy {entry.Path}; using original source");
        try
        {
            using var connection = Database.Open();
            using var command = CacheIndexDatabase.Command(connection, "DELETE FROM video_proxies WHERE id=$id;", ("$id", entry.Id));
            command.ExecuteNonQuery();
        }
        catch (Exception ex) { Log(ex, $"Remove invalid preview proxy index entry for {entry.Path}"); }
    }

    public async ValueTask DisposeAsync()
    {
        Task? task;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            pending.Clear();
            task = worker;
        }
        cancellation.Cancel();
        if (task is not null) await task.ConfigureAwait(false);
        foreach (var (key, pool) in pools) VideoClip.ReleaseDecoderPool(key, pool);
        pools.Clear();
        cancellation.Dispose();
    }

    internal sealed class Source(string path, string identity)
    {
        public string Path { get; } = path;
        public string Identity { get; } = identity;
        public List<Entry> Entries { get; } = [];
    }

    internal sealed class Entry
    {
        public string Id { get; set; } = string.Empty;
        public string SourceIdentity { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Hdr { get; set; }
        public bool HasAlpha { get; set; }
        public bool HasAuxiliary { get; set; }
        public long Length { get; set; }
        public string Path { get; set; } = string.Empty;
    }

    private sealed record Request(Source Source, int Width, int Height, bool Hdr);
    internal sealed record Snapshot(Source Source, Entry[] Entries);

    internal sealed class Scope : IDisposable
    {
        private static readonly AsyncLocal<Scope?> current = new();
        private readonly Scope? previous = current.Value;
        private readonly PreviewVideoProxies owner;
        private readonly Dictionary<string, Snapshot> sources;
        private readonly ConcurrentDictionary<string, byte> invalid = new();
        public static Scope? Current => current.Value;
        public string? Identity { get; }
        public bool Failed { get; private set; }

        internal Scope(PreviewVideoProxies owner, Dictionary<string, Snapshot> sources)
        {
            this.owner = owner;
            this.sources = sources;
            Identity = sources.Count == 0 ? null : "proxy-v1-" + Hash(string.Join(";", sources.Values
                .OrderBy(s => s.Source.Identity, StringComparer.Ordinal)
                .Select(s => s.Source.Identity + ":" + string.Join(",", s.Entries.Select(e => e.Id).Order(StringComparer.Ordinal)))));
            current.Value = this;
        }

        internal Entry? Resolve(VideoClip clip, int sourceWidth, int sourceHeight, int cropWidth, int cropHeight, int width, int height)
        {
            if (clip.FilePath is null || !sources.TryGetValue(clip.FilePath, out var source)) return null;
            double scale = Math.Min(1d, Math.Max((double)width / cropWidth, (double)height / cropHeight));
            int w = Math.Max(1, (int)Math.Ceiling(sourceWidth * scale));
            int h = Math.Max(1, (int)Math.Ceiling(sourceHeight * scale));
            var entry = source.Entries.Where(e => !invalid.ContainsKey(e.Id) && Matches(e.Width, e.Height, w, h))
                .OrderBy(e => Math.Abs(e.Width - w) + Math.Abs(e.Height - h)).ThenBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();
            if (entry is null) owner.Enqueue(source.Source, w, h, clip.Decoder is IHDRVideoSource);
            return entry;
        }

        public IPicture? TryRead(VideoClip clip, uint frame, int sourceWidth, int sourceHeight,
            int x, int y, int width, int height, int targetWidth, int targetHeight, IPicture.PicturePixelMode pixelMode)
        {
            var entry = Resolve(clip, sourceWidth, sourceHeight, width, height, targetWidth, targetHeight);
            if (entry is null) return null;
            try
            {
                int left = Math.Clamp((int)Math.Round((double)x * entry.Width / sourceWidth), 0, entry.Width - 1);
                int top = Math.Clamp((int)Math.Round((double)y * entry.Height / sourceHeight), 0, entry.Height - 1);
                int right = Math.Clamp((int)Math.Round((double)(x + width) * entry.Width / sourceWidth), left + 1, entry.Width);
                int bottom = Math.Clamp((int)Math.Round((double)(y + height) * entry.Height / sourceHeight), top + 1, entry.Height);
                return owner.Read(entry, clip.Decoder is IHDRVideoSource, frame, left, top, right - left, bottom - top,
                    targetWidth, targetHeight, clip.HDRBrightnessOffset, pixelMode);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                invalid.TryAdd(entry.Id, 0);
                Failed = true;
                owner.Invalidate(sources[clip.FilePath!].Source, entry, ex);
                return null;
            }
        }

        public void Dispose() => current.Value = previous;
    }
}
