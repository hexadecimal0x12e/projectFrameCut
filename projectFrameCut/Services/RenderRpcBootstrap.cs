using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Shared;

namespace projectFrameCut.Services;

/// <summary>
/// Owns the render RPC backend bound to the currently open project. The backend
/// is started (and, when the project root changes, replaced) on demand, and is
/// disposed when the project closes so a long-running render process cannot leak
/// memory across projects.
/// </summary>
internal static class RenderRpcBootstrap
{
    private static readonly object Gate = new();
    private static RenderServerProcessManager? _manager;

    /// <summary>
    /// The render RPC client of the backend currently bound to an open project,
    /// or of a fallback backend started on demand. Call <see cref="Initialize(string?)"/>
    /// first so the backend is bound to the project root you operate on.
    /// </summary>
    public static IRenderClient Client
    {
        get
        {
            lock (Gate)
            {
                if (_manager is null)
                {
                    // 兜底：没有任何绑定项目的后端时，启动一个默认后端。
                    _manager = new RenderServerProcessManager("projectFrameCut-integrated-editor");
                    _manager.Start();
                }
            }
            return _manager!.Client;
        }
    }

    /// <summary>
    /// Ensures the render RPC backend for <paramref name="projectRoot"/> is running.
    /// Opening a different project tears down the previous backend first, so each
    /// project owns a dedicated render process that is stopped when the project
    /// closes (see <see cref="DisposeAsync"/>).
    /// </summary>
    public static void Initialize(string? projectRoot = null)
    {
        lock (Gate)
        {
            if (_manager is not null)
            {
                if (SameProjectRoot(_manager.ProjectRoot, projectRoot)) return;
                // 项目根变化：结束旧后端，避免旧项目资源长期驻留在渲染进程中。
                var old = _manager;
                _manager = null;
                try
                {
                    old.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log(ex, "Dispose the previous render RPC backend");
                }
            }
            _manager = new RenderServerProcessManager("projectFrameCut-integrated-editor");
            _manager.Start(projectRoot);
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

    private static bool SameProjectRoot(string? current, string? requested)
    {
        if (string.IsNullOrWhiteSpace(current) && string.IsNullOrWhiteSpace(requested)) return true;
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(requested)) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string currentRoot = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string requestedRoot = Path.GetFullPath(requested).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(currentRoot, requestedRoot, comparison);
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
