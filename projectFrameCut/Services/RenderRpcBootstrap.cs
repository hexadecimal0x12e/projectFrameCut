using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Services;

internal static class RenderRpcBootstrap
{
    private static readonly object Gate = new();
    private static RenderServerProcessManager? _manager;

    public static IRenderClient Client
    {
        get
        {
            Initialize();
            return _manager!.Client;
        }
    }

    public static void Initialize()
    {
        if (_manager is not null) return;
        lock (Gate)
        {
            if (_manager is not null) return;
            _manager = new RenderServerProcessManager("projectFrameCut-integrated-editor");
            _manager.Start();
        }
    }

    public static async ValueTask DisposeAsync()
    {
        RenderServerProcessManager? manager;
        lock (Gate)
        {
            manager = _manager;
            _manager = null;
        }
        if (manager is not null) await manager.DisposeAsync().ConfigureAwait(false);
    }

    public static string ResolveArtifactPath(string projectRoot, RenderArtifact artifact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var canonicalRoot = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var relative = artifact.ProjectRelativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Render service returned an absolute artifact path.");
        var fullPath = Path.GetFullPath(Path.Combine(canonicalRoot, relative));
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(canonicalRoot, pathComparison))
            throw new InvalidDataException("Render service returned an artifact outside the project root.");
        return fullPath;
    }
}
