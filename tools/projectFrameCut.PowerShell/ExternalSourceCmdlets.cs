using System.Management.Automation;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

[Cmdlet("Get", "ProjectExternalSource")]
public sealed class GetProjectExternalSourceCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetExternalSource;
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? ImportId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? ClientId { get; set; }
}

[Cmdlet("Add", "ProjectExternalSource", SupportsShouldProcess = true)]
public sealed class AddProjectExternalSourceCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddExternalSource;
    [Parameter(Mandatory = true)][ValidateNotNullOrEmpty] public string FilePath { get; set; } = "";
    [Parameter] public Guid? ReplaceImportId { get; set; }
    [Parameter] public bool? Loaded { get; set; }
}

[Cmdlet("Set", "ProjectExternalSource", SupportsShouldProcess = true, DefaultParameterSetName = "Project")]
public sealed class SetProjectExternalSourceCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetExternalSource;
    [Parameter(Mandatory = true, ParameterSetName = "Project", ValueFromPipelineByPropertyName = true)] public Guid ImportId { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Rpc", ValueFromPipelineByPropertyName = true)] public Guid ClientId { get; set; }
    [Parameter(Mandatory = true)] public bool Loaded { get; set; }
}

[Cmdlet("Remove", "ProjectExternalSource", SupportsShouldProcess = true)]
public sealed class RemoveProjectExternalSourceCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.RemoveExternalSource;
    [Parameter(Mandatory = true, ParameterSetName = "Project", ValueFromPipelineByPropertyName = true)] public Guid ImportId { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Rpc", ValueFromPipelineByPropertyName = true)] public Guid ClientId { get; set; }
}
