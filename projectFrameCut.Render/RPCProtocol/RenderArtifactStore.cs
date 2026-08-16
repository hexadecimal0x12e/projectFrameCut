using projectFrameCut.Render.Contracts;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace projectFrameCut.Render.RPCProtocol;

public interface IRenderArtifactStore
{
    string ResolveProjectPath(string projectRoot, string projectRelativePath);
    string CreateTemporaryPath(string finalPath);
    void CommitTemporaryFile(string temporaryPath, string finalPath);
    RenderArtifact Register(Guid sessionId, string projectRoot, string projectRelativePath, string mediaType, bool cacheHit, bool isPreview, int width = 0, int height = 0, double frameRate = 0);
    bool Release(Guid sessionId, Guid artifactId);
}

public sealed class RenderArtifactStore : IRenderArtifactStore
{
    private readonly ConcurrentDictionary<Guid, ArtifactRegistration> _artifacts = new();
    private readonly ConcurrentDictionary<string, object> _commitGates = new(StringComparer.OrdinalIgnoreCase);

    public string ResolveProjectPath(string projectRoot, string projectRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRelativePath);

        var canonicalRoot = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedRelative = projectRelativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelative))
        {
            throw new ArgumentException("Artifact paths must be relative to the project root.", nameof(projectRelativePath));
        }

        var fullPath = Path.GetFullPath(Path.Combine(canonicalRoot, normalizedRelative));
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(canonicalRoot, pathComparison))
        {
            throw new UnauthorizedAccessException("Artifact path escapes the project root.");
        }

        return fullPath;
    }

    public string CreateTemporaryPath(string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new ArgumentException("The artifact path does not have a directory.", nameof(finalPath));
        Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(finalPath);
        var fileName = Path.GetFileNameWithoutExtension(finalPath);
        return Path.Combine(directory, $".{fileName}.tmp-{Guid.NewGuid():N}{extension}");
    }

    public void CommitTemporaryFile(string temporaryPath, string finalPath)
    {
        if (!File.Exists(temporaryPath))
        {
            throw new FileNotFoundException("The temporary render artifact was not produced.", temporaryPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var gate = _commitGates.GetOrAdd(finalPath, static _ => new object());
        lock (gate)
        {
            // A second session may have produced the same content while this writer
            // was rendering. Keep the first complete cache file and discard the loser.
            if (File.Exists(finalPath))
            {
                try { File.Delete(temporaryPath); } catch { }
                return;
            }

            File.Move(temporaryPath, finalPath);
        }
    }

    public RenderArtifact Register(Guid sessionId, string projectRoot, string projectRelativePath, string mediaType, bool cacheHit, bool isPreview, int width = 0, int height = 0, double frameRate = 0)
    {
        var fullPath = ResolveProjectPath(projectRoot, projectRelativePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Render artifact was not found.", fullPath);
        }

        var artifact = new RenderArtifact
        {
            ArtifactId = Guid.NewGuid(),
            SessionId = sessionId,
            ProjectRelativePath = projectRelativePath.Replace(Path.DirectorySeparatorChar, '/'),
            MediaType = mediaType,
            Size = new FileInfo(fullPath).Length,
            ContentHash = ComputeHash(fullPath),
            Width = width,
            Height = height,
            FrameRate = frameRate,
            CacheHit = cacheHit,
            IsPreview = isPreview,
        };
        _artifacts[artifact.ArtifactId] = new ArtifactRegistration(sessionId, fullPath, isPreview);
        return artifact;
    }

    public bool Release(Guid sessionId, Guid artifactId)
    {
        if (!_artifacts.TryGetValue(artifactId, out var registration) || registration.SessionId != sessionId)
        {
            return false;
        }
        if (!_artifacts.TryRemove(artifactId, out registration)) return false;

        // Preview files are reusable project cache entries and must survive ReleaseArtifact.
        if (!registration.IsPreview)
        {
            try { File.Delete(registration.FullPath); } catch { }
        }
        return true;
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record ArtifactRegistration(Guid SessionId, string FullPath, bool IsPreview);
}
