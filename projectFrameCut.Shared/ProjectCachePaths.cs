using System.Collections.Concurrent;
using System.Text.Json;

namespace projectFrameCut.Shared;

public static class ProjectCachePaths
{
    public const string SettingKey = "General_ProjectCacheRoot";
    public const string EnvironmentKey = "PJFC_PROJECT_CACHE_ROOT";
    public const string MarkerFileName = ".projectFrameCut-cache";
    public static readonly string[] DirectoryNames = ["thumbs", "proxy", "incrementalRendering", "cache"];
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly ConcurrentDictionary<string, CacheLocation> Locations = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static bool configured;
    public static string RedirectRoot { get; private set; } = string.Empty;

    private sealed record CacheLocation(string Root, Exception? Error = null);

    public static void Configure(string? root)
    {
        lock (Gate)
        {
            if (configured) return;
            RedirectRoot = root?.Trim() ?? string.Empty;
            configured = true;
        }
    }

    public static string GetConfiguredRoot(string projectRoot, string? projectName = null)
    {
        projectRoot = Normalize(projectRoot);
        if (string.IsNullOrWhiteSpace(RedirectRoot)) return projectRoot;
        string name = string.IsNullOrWhiteSpace(projectName) ? ReadProjectName(projectRoot) : projectName;
        name = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") name = Path.GetFileName(projectRoot);
        return Normalize(Path.Combine(RedirectRoot, name));
    }

    public static string Initialize(string projectRoot, string? projectName = null)
    {
        projectRoot = Normalize(projectRoot);
        lock (Gate)
        {
            if (Locations.TryGetValue(projectRoot, out var current)) return current.Root;
            string name = string.IsNullOrWhiteSpace(projectName) ? ReadProjectName(projectRoot) : projectName;
            string root;
            Exception? error = null;
            try
            {
                root = GetConfiguredRoot(projectRoot, name);
                if (!string.Equals(root, projectRoot, Comparison))
                {
                    VerifyWritable(root);
                    foreach (string directory in DirectoryNames) VerifyWritable(Path.Combine(root, directory));
                    File.WriteAllText(Path.Combine(root, MarkerFileName), string.Empty);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                root = projectRoot;
                error = ex;
                Logger.Log($"Project cache redirection failed for '{projectRoot}'; using the project directory: {ex.Message}", "warn");
            }
            Locations[projectRoot] = new(root, error);
            if (!string.Equals(root, projectRoot, Comparison)) Logger.Log($"Project cache for '{projectRoot}': '{root}'.");
            return root;
        }
    }

    public static string GetRoot(string projectRoot)
        => Locations.TryGetValue(Normalize(projectRoot), out var location) ? location.Root : Initialize(projectRoot);

    public static string GetTemporaryRoot(string projectRoot, string defaultPath)
        => string.IsNullOrWhiteSpace(RedirectRoot) ? defaultPath : ResolvePath(projectRoot, "cache/render");

    public static IReadOnlyList<string> GetStorageRoots(string projectRoot)
    {
        projectRoot = Normalize(projectRoot);
        var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { projectRoot };
        if (Locations.TryGetValue(projectRoot, out var location)) roots.Add(location.Root);
        try { roots.Add(GetConfiguredRoot(projectRoot)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        return roots.ToArray();
    }

    public static void Bind(string projectRoot, string cacheRoot, string? projectName = null)
    {
        projectRoot = Normalize(projectRoot);
        cacheRoot = Normalize(cacheRoot);
        lock (Gate)
        {
            if (Locations.TryGetValue(projectRoot, out var current))
            {
                if (!string.Equals(cacheRoot, current.Root, Comparison))
                    throw new InvalidOperationException("The cache directory cannot change while the project is open.");
                return;
            }
            string name = string.IsNullOrWhiteSpace(projectName) ? ReadProjectName(projectRoot) : projectName;
            if (!string.Equals(cacheRoot, projectRoot, Comparison) && !string.Equals(cacheRoot, GetConfiguredRoot(projectRoot, name), Comparison))
                throw new UnauthorizedAccessException("The cache directory does not match the configured project cache location.");
            Locations[projectRoot] = new(cacheRoot);
        }
    }

    public static void Forget(string projectRoot) => Locations.TryRemove(Normalize(projectRoot), out _);

    public static Exception? TakeRedirectError(string projectRoot)
    {
        projectRoot = Normalize(projectRoot);
        lock (Gate)
        {
            if (!Locations.TryGetValue(projectRoot, out var location) || location.Error is null) return null;
            Locations[projectRoot] = location with { Error = null };
            return location.Error;
        }
    }

    public static string ResolvePath(string projectRoot, string relativePath)
    {
        projectRoot = Normalize(projectRoot);
        string logicalPath = ResolveUnder(projectRoot, relativePath);
        string relative = Path.GetRelativePath(projectRoot, logicalPath);
        string first = relative.Split(Path.DirectorySeparatorChar)[0];
        if (DirectoryNames.Contains(first, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) && !IsProjectCover(logicalPath))
            return ResolveUnder(GetRoot(projectRoot), relative);
        return logicalPath;
    }

    public static string GetRelativePath(string projectRoot, string fullPath)
    {
        projectRoot = Normalize(projectRoot);
        fullPath = Path.GetFullPath(fullPath);
        string cacheRoot = GetRoot(projectRoot);
        if (IsUnder(fullPath, cacheRoot))
        {
            string relative = Path.GetRelativePath(cacheRoot, fullPath);
            if (string.Equals(ResolvePath(projectRoot, relative), fullPath, Comparison))
                return relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        if (!IsUnder(fullPath, projectRoot)) throw new UnauthorizedAccessException("The path escapes the project and its cache directory.");
        return Path.GetRelativePath(projectRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    public static bool IsProjectCover(string path)
        => string.Equals(Path.GetFileName(path), "_project.png", Comparison) &&
           string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "thumbs", Comparison);

    public static bool IsUnder(string path, string root)
    {
        path = Normalize(path);
        root = Normalize(root);
        return string.Equals(path, root, Comparison) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Comparison);
    }

    public static void VerifyWritable(string root)
    {
        Directory.CreateDirectory(root);
        using var file = new FileStream(Path.Combine(root, $".pjfc-write-{Guid.NewGuid():N}"), FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        file.WriteByte(0);
    }

    private static string ResolveUnder(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        relativePath = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':')) throw new ArgumentException("The path must be relative.", nameof(relativePath));
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsUnder(path, root)) throw new UnauthorizedAccessException("The path escapes its root directory.");
        return path;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string ReadProjectName(string root)
    {
        string path = Path.Combine(root, File.Exists(Path.Combine(root, "project.pjfc")) ? "project.pjfc" : "project.json");
        try
        {
            if (File.Exists(path))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true });
                if (json.RootElement.TryGetProperty("ProjectName", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
                    return name.GetString()!;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return Path.GetFileName(root);
    }
}
