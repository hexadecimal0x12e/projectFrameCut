using projectFrameCut.Render.RenderAPIBase.Project;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.ApplicationAPIBase.Workspace.Modules;

public sealed class ProjectDirtyChangedEventArgs(bool isDirty, string? reason) : EventArgs
{
    public bool IsDirty { get; } = isDirty;
    public string? Reason { get; } = reason;
}

public sealed class ProjectModule(ProjectJSONStructure project, string projectPath = "", bool isReadOnly = false) : WorkspaceModuleBase
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public const string ModuleId = "project.core";
    public override string Id => ModuleId;
    public ProjectJSONStructure Project { get; } = project ?? throw new ArgumentNullException(nameof(project));
    public string ProjectPath { get; } = projectPath ?? string.Empty;
    public bool IsReadOnly { get; } = isReadOnly;
    public bool IsDirty { get; private set; }
    public double FramesPerSecond => Project.TargetFrameRate;
    public event EventHandler<ProjectDirtyChangedEventArgs>? DirtyChanged;

    public void MarkDirty(string? reason = null)
    {
        if (IsReadOnly) return;
        IsDirty = true;
        Project.LastChanged = DateTime.UtcNow;
        DirtyChanged?.Invoke(this, new ProjectDirtyChangedEventArgs(true, reason));
    }

    public void MarkClean()
    {
        IsDirty = false;
        DirtyChanged?.Invoke(this, new ProjectDirtyChangedEventArgs(false, null));
    }

    public async Task SaveProjectAsync(CancellationToken cancellationToken = default)
    {
        if (IsReadOnly) throw new InvalidOperationException("The project is read-only.");
        if (Context is null) throw new InvalidOperationException("The module is not initialized.");
        await Context.Storage.WriteTextAsync("project.pjfc", JsonSerializer.Serialize(Project, Options), cancellationToken);
        if (!string.IsNullOrWhiteSpace(Context.Storage.RootPath)) Project.SaveSnapshotMapping(Context.Storage.RootPath, Options);
        MarkClean();
    }
}
