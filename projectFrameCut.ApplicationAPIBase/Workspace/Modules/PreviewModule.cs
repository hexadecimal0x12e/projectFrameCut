namespace projectFrameCut.ApplicationAPIBase.Workspace.Modules;

public interface IPreviewBackend : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<object?> GetFrameAsync(uint frame, CancellationToken cancellationToken = default);
}

public sealed class PreviewModule(IPreviewBackend? backend = null) : WorkspaceModuleBase, IWorkspaceModuleDependencies
{
    public const string ModuleId = "preview.core";
    public override string Id => ModuleId;
    public IReadOnlyCollection<Type> Dependencies { get; } = [typeof(ProjectModule), typeof(TimelineModule)];
    public IPreviewBackend? Backend { get; private set; } = backend;
    public void SetBackend(IPreviewBackend value) => Backend = value ?? throw new ArgumentNullException(nameof(value));
    public override Task StartAsync(CancellationToken cancellationToken = default) => Backend?.StartAsync(cancellationToken) ?? Task.CompletedTask;
    public override Task StopAsync(CancellationToken cancellationToken = default) => Backend?.StopAsync(cancellationToken) ?? Task.CompletedTask;
    protected override async ValueTask DisposeAsyncCore() { if (Backend is not null) await Backend.DisposeAsync(); }
}
