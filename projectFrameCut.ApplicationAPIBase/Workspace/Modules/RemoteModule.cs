using projectFrameCut.Render.RenderAPIBase.Project;

namespace projectFrameCut.ApplicationAPIBase.Workspace.Modules;

public interface IRemoteWorkspaceSession
{
    Task SaveAsync(ProjectJSONStructure project, DraftStructureJSON draft, IReadOnlyCollection<AssetItem> assets, string reason, CancellationToken cancellationToken = default);
}

/// <summary>Owns remote persistence without introducing UI alerts into ProjectModule.</summary>
public sealed class RemoteModule(IRemoteWorkspaceSession session) : WorkspaceModuleBase, IWorkspaceModuleDependencies
{
    public const string ModuleId = "remote.persistence";
    public override string Id => ModuleId;
    public IReadOnlyCollection<Type> Dependencies { get; } = [typeof(ProjectModule), typeof(TimelineModule), typeof(AssetModule)];
    public Task SaveAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (Context is null) throw new InvalidOperationException("The module is not initialized.");
        return session.SaveAsync(
            Context.Modules.Get<ProjectModule>().Project,
            Context.Modules.Get<TimelineModule>().Export(),
            Context.Modules.Get<AssetModule>().Assets,
            reason,
            cancellationToken);
    }
}
