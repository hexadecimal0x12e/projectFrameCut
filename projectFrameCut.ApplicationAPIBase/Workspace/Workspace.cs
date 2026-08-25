namespace projectFrameCut.ApplicationAPIBase.Workspace;

public enum WorkspaceState { Created, Initializing, Initialized, Starting, Running, Stopping, Stopped, Faulted, Disposed }

public sealed class WorkspaceStateChangedEventArgs(WorkspaceState previous, WorkspaceState current, Exception? error = null) : EventArgs
{
    public WorkspaceState Previous { get; } = previous;
    public WorkspaceState Current { get; } = current;
    public Exception? Error { get; } = error;
}

public interface IWorkspace : IAsyncDisposable
{
    string Id { get; }
    string Kind { get; }
    string FromPlugin { get; }
    WorkspaceState State { get; }
    WorkspaceContext Context { get; }
    IReadOnlyCollection<IWorkspaceModule> Modules { get; }
    T GetModule<T>() where T : class, IWorkspaceModule;
    bool TryGetModule<T>(out T? module) where T : class, IWorkspaceModule;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    event EventHandler<WorkspaceStateChangedEventArgs>? StateChanged;
}

public interface IWorkspaceModuleRegistry
{
    IReadOnlyCollection<IWorkspaceModule> Modules { get; }
    void Register(IWorkspaceModule module);
    bool Remove(string moduleId, out IWorkspaceModule? module);
    IWorkspaceModule Get(string moduleId);
    T Get<T>() where T : class, IWorkspaceModule;
    bool TryGet<T>(out T? module) where T : class, IWorkspaceModule;
}

public interface IWorkspaceStorage
{
    string RootPath { get; }
    Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default);
    Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default);
    Task WriteTextAsync(string relativePath, string content, CancellationToken cancellationToken = default);
}

public sealed class WorkspaceContext
{
    internal WorkspaceContext(IWorkspace workspace, IWorkspaceModuleRegistry modules, IWorkspaceStorage storage, IServiceProvider services)
        => (Workspace, Modules, Storage, Services) = (workspace, modules, storage, services);
    public IWorkspace Workspace { get; }
    public IWorkspaceModuleRegistry Modules { get; }
    public IWorkspaceStorage Storage { get; }
    public IServiceProvider Services { get; }
}

public sealed class FileWorkspaceStorage(string rootPath) : IWorkspaceStorage
{
    public string RootPath { get; } = Path.GetFullPath(rootPath);
    public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default) => Task.FromResult(File.Exists(Resolve(relativePath)));
    public async Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var path = Resolve(relativePath);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
    }
    public async Task WriteTextAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }
    private string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var path = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        var root = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(path, RootPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The storage path must remain inside the workspace root.", nameof(relativePath));
        return path;
    }
}

public sealed class NullWorkspaceStorage : IWorkspaceStorage
{
    public static NullWorkspaceStorage Instance { get; } = new();
    public string RootPath => string.Empty;
    public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<string?> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    public Task WriteTextAsync(string relativePath, string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class EmptyServiceProvider : IServiceProvider
{
    public static EmptyServiceProvider Instance { get; } = new();
    public object? GetService(Type serviceType) => null;
}
