namespace projectFrameCut.ApplicationAPIBase.Workspace;

public interface IWorkspaceModule : IAsyncDisposable
{
    string Id { get; }
    string FromPlugin { get; }
    Task InitializeAsync(WorkspaceContext context, CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IWorkspaceModuleDependencies { IReadOnlyCollection<Type> Dependencies { get; } }

public abstract class WorkspaceModuleBase : IWorkspaceModule
{
    private bool _disposed;
    protected WorkspaceContext? Context { get; private set; }
    public abstract string Id { get; }
    public virtual string FromPlugin => string.Empty;
    public virtual Task InitializeAsync(WorkspaceContext context, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Context = context ?? throw new ArgumentNullException(nameof(context));
        return Task.CompletedTask;
    }
    public virtual Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisposeAsyncCore();
        GC.SuppressFinalize(this);
    }
    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
}

public sealed class WorkspaceModuleRegistry : IWorkspaceModuleRegistry
{
    private readonly List<IWorkspaceModule> _modules = [];
    public IReadOnlyCollection<IWorkspaceModule> Modules => _modules.AsReadOnly();
    public void Register(IWorkspaceModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (_modules.Any(x => string.Equals(x.Id, module.Id, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Workspace module id '{module.Id}' is already registered.");
        _modules.Add(module);
    }
    public bool Remove(string moduleId, out IWorkspaceModule? module)
    {
        module = _modules.FirstOrDefault(x => string.Equals(x.Id, moduleId, StringComparison.Ordinal));
        return module is not null && _modules.Remove(module);
    }
    public IWorkspaceModule Get(string moduleId) => _modules.FirstOrDefault(x => string.Equals(x.Id, moduleId, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Workspace module '{moduleId}' is not registered.");
    public T Get<T>() where T : class, IWorkspaceModule => TryGet<T>(out var module) ? module : throw new KeyNotFoundException($"Workspace module '{typeof(T).FullName}' is not registered.");
    public bool TryGet<T>(out T? module) where T : class, IWorkspaceModule
    {
        module = _modules.OfType<T>().FirstOrDefault();
        return module is not null;
    }
}

public sealed class Workspace : IWorkspace
{
    private readonly WorkspaceModuleRegistry _registry = new();
    private readonly List<IWorkspaceModule> _orderedModules = [];
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private bool _disposed;

    public Workspace(string id, string kind, string fromPlugin = "", IWorkspaceStorage? storage = null, IServiceProvider? services = null)
    {
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
        Kind = string.IsNullOrWhiteSpace(kind) ? throw new ArgumentException("Workspace kind is required.", nameof(kind)) : kind;
        FromPlugin = fromPlugin ?? string.Empty;
        Context = new WorkspaceContext(this, _registry, storage ?? NullWorkspaceStorage.Instance, services ?? EmptyServiceProvider.Instance);
    }
    public string Id { get; }
    public string Kind { get; }
    public string FromPlugin { get; }
    public WorkspaceState State { get; private set; } = WorkspaceState.Created;
    public WorkspaceContext Context { get; }
    public IReadOnlyCollection<IWorkspaceModule> Modules => _registry.Modules;
    public event EventHandler<WorkspaceStateChangedEventArgs>? StateChanged;
    public Workspace RegisterModule(IWorkspaceModule module)
    {
        if (State != WorkspaceState.Created) throw new InvalidOperationException("Modules must be registered before the workspace starts.");
        _registry.Register(module);
        return this;
    }
    public T GetModule<T>() where T : class, IWorkspaceModule => _registry.Get<T>();
    public bool TryGetModule<T>(out T? module) where T : class, IWorkspaceModule => _registry.TryGet(out module);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State == WorkspaceState.Running) return;
            if (State is not WorkspaceState.Created and not WorkspaceState.Stopped) throw new InvalidOperationException($"Workspace cannot start from state {State}.");
            if (_orderedModules.Count == 0)
            {
                ChangeState(WorkspaceState.Initializing);
                _orderedModules.AddRange(TopologicalSort(_registry.Modules));
                foreach (var module in _orderedModules) await module.InitializeAsync(Context, cancellationToken);
                ChangeState(WorkspaceState.Initialized);
            }
            ChangeState(WorkspaceState.Starting);
            foreach (var module in _orderedModules) await module.StartAsync(cancellationToken);
            ChangeState(WorkspaceState.Running);
        }
        catch (Exception ex)
        {
            ChangeState(WorkspaceState.Faulted, ex);
            for (var i = _orderedModules.Count - 1; i >= 0; i--) try { await _orderedModules[i].StopAsync(); } catch { }
            throw;
        }
        finally { _lifecycleLock.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (State is WorkspaceState.Stopped or WorkspaceState.Created or WorkspaceState.Disposed) return;
            ChangeState(WorkspaceState.Stopping);
            for (var i = _orderedModules.Count - 1; i >= 0; i--) await _orderedModules[i].StopAsync(cancellationToken);
            ChangeState(WorkspaceState.Stopped);
        }
        finally { _lifecycleLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync();
        for (var i = _orderedModules.Count - 1; i >= 0; i--) await _orderedModules[i].DisposeAsync();
        _disposed = true;
        ChangeState(WorkspaceState.Disposed);
        _lifecycleLock.Dispose();
    }

    private void ChangeState(WorkspaceState state, Exception? error = null)
    {
        var previous = State; State = state;
        StateChanged?.Invoke(this, new WorkspaceStateChangedEventArgs(previous, state, error));
    }

    private static IReadOnlyList<IWorkspaceModule> TopologicalSort(IEnumerable<IWorkspaceModule> modules)
    {
        var source = modules.ToList();
        var result = new List<IWorkspaceModule>(source.Count);
        var visiting = new HashSet<IWorkspaceModule>();
        var visited = new HashSet<IWorkspaceModule>();
        void Visit(IWorkspaceModule module)
        {
            if (visited.Contains(module)) return;
            if (!visiting.Add(module)) throw new InvalidOperationException($"Circular workspace module dependency detected at '{module.Id}'.");
            if (module is IWorkspaceModuleDependencies declaration)
                foreach (var dependencyType in declaration.Dependencies)
                    Visit(source.FirstOrDefault(dependencyType.IsInstanceOfType) ?? throw new InvalidOperationException($"Module '{module.Id}' requires '{dependencyType.FullName}'."));
            visiting.Remove(module); visited.Add(module); result.Add(module);
        }
        foreach (var module in source) Visit(module);
        return result;
    }
}
