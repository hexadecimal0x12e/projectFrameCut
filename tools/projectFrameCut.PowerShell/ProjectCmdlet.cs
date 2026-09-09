using System.Collections;
using System.Management.Automation;
using System.Text.Json;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.PowerShell;

public abstract class ProjectCmdlet : CancellableCmdlet
{
    protected abstract GuiProjectOperation Operation { get; }
    protected virtual bool IsWrite => false;
    protected virtual bool ReturnResult => true;
    [Parameter][ValidateRange(1, 3600)] public int TimeoutSeconds { get; set; } = 60;

    protected override void ProcessRecord()
    {
        try
        {
            var connection = Connection.Current;
            if (IsWrite && !ShouldProcess($"Project session {connection.SessionId}", MyInvocation.MyCommand.Name)) return;
            var parameters = MyInvocation.BoundParameters
                .Where(x => x.Key is not ("PassThru" or "ChangeReason" or "TimeoutSeconds" or "WhatIf" or "Confirm" or "Verbose" or "Debug" or "ErrorAction"
                    or "WarningAction" or "InformationAction" or "ProgressAction" or "ErrorVariable" or "WarningVariable" or "InformationVariable"
                    or "OutVariable" or "OutBuffer" or "PipelineVariable"))
                .ToDictionary(x => x.Key, x => x.Key == "FilePath"
                    ? (object?)GetUnresolvedProviderPathFromPSPath((string)x.Value) : Normalize(x.Value));
            var request = new GuiProjectRequest
            {
                Operation = Operation,
                ParametersJson = JsonSerializer.Serialize(parameters),
                TimeoutSeconds = TimeoutSeconds,
                ChangeReason = this is ProjectWriteCmdlet write
                    ? string.IsNullOrWhiteSpace(write.ChangeReason) ? MyInvocation.MyCommand.Name : write.ChangeReason.Trim()
                    : string.Empty,
            };
            WriteVerbose($"GUI RPC {Operation}, request {request.RequestId}.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds + 2));
            var result = connection.Client.InvokeGuiProjectAsync(request, timeout.Token).AsTask().GetAwaiter().GetResult();
            if (result.RequestId != request.RequestId || result.SessionId != connection.SessionId) throw new InvalidOperationException("GUI RPC response does not match the session and request.");
            if (result.Error is not null) throw new RenderRpcException(result.Error);
            if (!ReturnResult) return;
            using var json = JsonDocument.Parse(result.Json);
            if (json.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var item in json.RootElement.EnumerateArray()) WriteObject(ToObject(item));
            else if (json.RootElement.ValueKind != JsonValueKind.Null) WriteObject(ToObject(json.RootElement));
        }
        catch (Exception ex) { Fail(ex); }
    }

    private static object? Normalize(object? value) => value switch
    {
        PSObject p => Normalize(p.BaseObject),
        SwitchParameter s => s.IsPresent,
        IDictionary d => d.Cast<DictionaryEntry>().ToDictionary(x => x.Key.ToString()!, x => Normalize(x.Value)),
        string or null => value,
        IEnumerable items => items.Cast<object?>().Select(Normalize).ToArray(),
        _ => value,
    };

    private static object? ToObject(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new PSObject();
            foreach (var p in value.EnumerateObject()) result.Properties.Add(new PSNoteProperty(p.Name, ToObject(p.Value)));
            return result;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Select(ToObject).ToArray(),
            JsonValueKind.String => value.TryGetGuid(out var id) ? id : value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var n) ? (object)n : value.GetDouble(),
            JsonValueKind.True => true, JsonValueKind.False => false, _ => null,
        };
    }
}

public abstract class ProjectWriteCmdlet : ProjectCmdlet
{
    [Parameter] public SwitchParameter PassThru { get; set; }
    [Parameter][ValidateLength(1, 512)] public string? ChangeReason { get; set; }
    protected override bool IsWrite => true;
    protected override bool ReturnResult => PassThru;
}
