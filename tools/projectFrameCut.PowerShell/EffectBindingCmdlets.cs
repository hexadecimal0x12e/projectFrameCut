using System.Collections;
using System.Management.Automation;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

[Cmdlet("Get", "ProjectClipEffectGraph")]
public sealed class GetProjectClipEffectGraphCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipEffectGraph;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter] public SwitchParameter AsMermaid { get; set; }
}

[Cmdlet("Get", "ProjectClipEffectBinding")]
public sealed class GetProjectClipEffectBindingCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipEffectBinding;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? ProviderId { get; set; }
    [Parameter] public string? FieldId { get; set; }
}

[Cmdlet("Get", "ProjectClipEffectBindingSource")]
public sealed class GetProjectClipEffectBindingSourceCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipEffectBindingSource;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? ProviderId { get; set; }
    [Parameter] public string? FieldId { get; set; }
    [Parameter][ValidateSet("Picture", "NativeContent")] public string? Pipeline { get; set; }
}

[Cmdlet("Set", "ProjectClipEffectBinding", SupportsShouldProcess = true, DefaultParameterSetName = "Source")]
public sealed class SetProjectClipEffectBindingCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetClipEffectBinding;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? ProviderId { get; set; }
    [Parameter(Mandatory = true)][ValidateNotNullOrEmpty] public string FieldId { get; set; } = "";
    [Parameter(Mandatory = true, ParameterSetName = "Source")][ValidateNotNullOrEmpty] public string SourceId { get; set; } = "";
    [Parameter(Mandatory = true, ParameterSetName = "Provider")] public Guid SourceProviderId { get; set; }
    [Parameter(ParameterSetName = "Provider")][ValidateNotNullOrEmpty] public string? SourceOutputFieldId { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Input")] public SwitchParameter ClipInput { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "BuiltIn")][ValidateSet("Frame", "Progress")] public string? BuiltIn { get; set; }
}

[Cmdlet("Remove", "ProjectClipEffectBinding", SupportsShouldProcess = true)]
public sealed class RemoveProjectClipEffectBindingCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.RemoveClipEffectBinding;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? ProviderId { get; set; }
    [Parameter(Mandatory = true)][ValidateNotNullOrEmpty] public string FieldId { get; set; } = "";
}

[Cmdlet("Clear", "ProjectClipEffectBinding", SupportsShouldProcess = true)]
public sealed class ClearProjectClipEffectBindingCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.ClearClipEffectBinding;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? ProviderId { get; set; }
    [Parameter] public string? OutputFieldId { get; set; }
    [Parameter][ValidateSet("Picture", "NativeContent")] public string? Pipeline { get; set; }
}

[Cmdlet("Set", "ProjectClipEffectOutput", SupportsShouldProcess = true)]
public sealed class SetProjectClipEffectOutputCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetClipEffectOutput;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? ProviderId { get; set; }
    [Parameter] public string? OutputFieldId { get; set; }
    [Parameter][ValidateSet("Picture", "NativeContent")] public string? Pipeline { get; set; }
}

[Cmdlet("Get", "ProjectClipArgument")]
public sealed class GetProjectClipArgumentCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipArgument;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter] public string? FieldId { get; set; }
}

[Cmdlet("Set", "ProjectClipArgument", SupportsShouldProcess = true)]
public sealed class SetProjectClipArgumentCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetClipArgument;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter] public Hashtable? Fields { get; set; }
    [Parameter] public SwitchParameter ResetToDefaults { get; set; }
}

[Cmdlet("Get", "ProjectClipValueEndpoint")]
public sealed class GetProjectClipValueEndpointCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipValueEndpoint;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? EndpointId { get; set; }
    [Parameter] public SwitchParameter Available { get; set; }
    [Parameter][ValidateSet("Picture", "NativeContent")] public string? Pipeline { get; set; }
}

[Cmdlet("Add", "ProjectClipValueEndpoint", SupportsShouldProcess = true, DefaultParameterSetName = "Output")]
public sealed class AddProjectClipValueEndpointCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddClipValueEndpoint;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter] public string? Name { get; set; }
    [Parameter(ParameterSetName = "Output")][ValidateSet("Numeric", "Integer", "UnsignedInteger", "Long", "UnsignedLong", "Boolean", "String", "Color", "Size", "Position", "SizeAndPosition", "IPicture", "VectorPicture")] public string? ValueType { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Input")] public Guid SourceClipId { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Input")] public Guid SourceEndpointId { get; set; }
    [Parameter][ValidateSet("Picture", "NativeContent")] public string? Pipeline { get; set; }
}

[Cmdlet("Set", "ProjectClipValueEndpoint", SupportsShouldProcess = true)]
public sealed class SetProjectClipValueEndpointCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetClipValueEndpoint;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid EndpointId { get; set; }
    [Parameter] public string? Name { get; set; }
    [Parameter] public bool? Enabled { get; set; }
    [Parameter][ValidateSet("Numeric", "Integer", "UnsignedInteger", "Long", "UnsignedLong", "Boolean", "String", "Color", "Size", "Position", "SizeAndPosition", "IPicture", "VectorPicture")] public string? ValueType { get; set; }
    [Parameter] public Guid? SourceClipId { get; set; }
    [Parameter] public Guid? SourceEndpointId { get; set; }
    [Parameter][ValidateSet("Picture", "NativeContent")] public string? Pipeline { get; set; }
}

[Cmdlet("Remove", "ProjectClipValueEndpoint", SupportsShouldProcess = true)]
public sealed class RemoveProjectClipValueEndpointCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.RemoveClipValueEndpoint;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid EndpointId { get; set; }
}

[Cmdlet("Get", "ProjectClipEffectGroup")]
public sealed class GetProjectClipEffectGroupCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipEffectGroup;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(ValueFromPipelineByPropertyName = true)] public Guid? GroupId { get; set; }
}

[Cmdlet("Add", "ProjectClipEffectGroup", SupportsShouldProcess = true)]
public sealed class AddProjectClipEffectGroupCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.AddClipEffectGroup;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true)][ValidateNotNullOrEmpty] public string Name { get; set; } = "";
    [Parameter] public Guid[]? ProviderIds { get; set; }
    [Parameter] public Guid[]? ChildGroupIds { get; set; }
    [Parameter] public Hashtable[]? Ports { get; set; }
    [Parameter] public bool? Collapsed { get; set; }
    [Parameter] public double? X { get; set; }
    [Parameter] public double? Y { get; set; }
}

[Cmdlet("Set", "ProjectClipEffectGroup", SupportsShouldProcess = true)]
public sealed class SetProjectClipEffectGroupCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SetClipEffectGroup;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid GroupId { get; set; }
    [Parameter][ValidateNotNullOrEmpty] public string? Name { get; set; }
    [Parameter] public Hashtable[]? Ports { get; set; }
    [Parameter] public bool? Collapsed { get; set; }
    [Parameter] public double? X { get; set; }
    [Parameter] public double? Y { get; set; }
}

[Cmdlet("Remove", "ProjectClipEffectGroup", SupportsShouldProcess = true)]
public sealed class RemoveProjectClipEffectGroupCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.RemoveClipEffectGroup;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid GroupId { get; set; }
    [Parameter] public SwitchParameter RemoveProviders { get; set; }
}

[Cmdlet("Export", "ProjectClipEffectGroup")]
public sealed class ExportProjectClipEffectGroupCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetClipEffectGroupTemplate;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid GroupId { get; set; }
}

[Cmdlet("Import", "ProjectClipEffectGroup", SupportsShouldProcess = true, DefaultParameterSetName = "Json")]
public sealed class ImportProjectClipEffectGroupCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.ImportClipEffectGroup;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Json")][ValidateNotNullOrEmpty] public string TemplateJson { get; set; } = "";
    [Parameter(Mandatory = true, ParameterSetName = "Library")][ValidatePattern("^[a-fA-F0-9]{64}$")] public string Hash { get; set; } = "";
    [Parameter] public double? X { get; set; }
    [Parameter] public double? Y { get; set; }
}

[Cmdlet("Get", "ProjectEffectGroupTemplate")]
public sealed class GetProjectEffectGroupTemplateCommand : ProjectCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.GetEffectGroupTemplate;
    [Parameter][ValidatePattern("^[a-fA-F0-9]{64}$")] public string? Hash { get; set; }
}

[Cmdlet("Save", "ProjectClipEffectGroup", SupportsShouldProcess = true)]
public sealed class SaveProjectClipEffectGroupCommand : ProjectWriteCmdlet
{
    protected override GuiProjectOperation Operation => GuiProjectOperation.SaveClipEffectGroup;
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid ClipId { get; set; }
    [Parameter(Mandatory = true, ValueFromPipelineByPropertyName = true)] public Guid GroupId { get; set; }
    [Parameter][ValidatePattern("^[a-fA-F0-9]{64}$")] public string? ReplaceHash { get; set; }
}
