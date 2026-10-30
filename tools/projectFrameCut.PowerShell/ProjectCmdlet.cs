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
    protected virtual string? RequiredClipType => null;
    [Parameter][ValidateRange(1, 3600)] public int TimeoutSeconds { get; set; } = 60;

    protected override void ProcessRecord()
    {
        bool newBatch = false;
        try
        {
            var connection = Connection.Current;
            if (IsWrite && !ShouldProcess($"Project session {connection.SessionId}", MyInvocation.MyCommand.Name)) return;
            if ((Operation is GuiProjectOperation.StartBatch or GuiProjectOperation.CompleteBatch or GuiProjectOperation.StopBatch) && !connection.SupportsBatch)
                throw new NotSupportedException("This GUI connection does not support project batches.");
            if (Operation == GuiProjectOperation.StartBatch && connection.BatchId != Guid.Empty)
                throw new InvalidOperationException("A project batch is already active. Complete or stop it first.");
            if (Operation == GuiProjectOperation.StartBatch)
            {
                connection.BatchId = Guid.NewGuid();
                newBatch = true;
            }
            var parameters = MyInvocation.BoundParameters
                .Where(x => x.Key is not ("PassThru" or "ChangeReason" or "TimeoutSeconds" or "WhatIf" or "Confirm" or "Verbose" or "Debug" or "ErrorAction"
                    or "WarningAction" or "InformationAction" or "ProgressAction" or "ErrorVariable" or "WarningVariable" or "InformationVariable"
                    or "OutVariable" or "OutBuffer" or "PipelineVariable"))
                .ToDictionary(x => x.Key, x => x.Key is "FilePath" or "CoverFilePath"
                    ? (object?)GetUnresolvedProviderPathFromPSPath((string)x.Value) : Normalize(x.Value));
            if (RequiredClipType is not null) parameters["RequiredClipType"] = RequiredClipType;
            var request = new GuiProjectRequest
            {
                Operation = Operation,
                BatchId = connection.BatchId != Guid.Empty ? connection.BatchId
                    : Operation == GuiProjectOperation.CompleteBatch ? connection.LastBatchId : Guid.Empty,
                ParametersJson = JsonSerializer.Serialize(parameters),
                TimeoutSeconds = TimeoutSeconds,
                ChangeReason = this is ProjectWriteCmdlet write
                    ? string.IsNullOrWhiteSpace(write.ChangeReason) ? MyInvocation.MyCommand.Name : write.ChangeReason.Trim()
                    : string.Empty,
            };
            WriteVerbose($"GUI RPC {Operation}, request {request.RequestId}.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds + 2));
            var pending = connection.Client.InvokeGuiProjectAsync(request, timeout.Token).AsTask();
            GuiProjectResult result;
            if (Operation == GuiProjectOperation.ExportFrameSequence)
            {
                try
                {
                    WriteProgress(new ProgressRecord(0, "Rendering preview frames", "Preparing renderer") { PercentComplete = 0 });
                    while (!pending.IsCompleted)
                    {
                        if (Task.WhenAny(pending, Task.Delay(500, timeout.Token)).GetAwaiter().GetResult() == pending) break;
                        timeout.Token.ThrowIfCancellationRequested();
                        var progress = connection.Client.InvokeGuiProjectAsync(new()
                        {
                            Operation = GuiProjectOperation.GetRequestProgress,
                            ParametersJson = JsonSerializer.Serialize(new { request.RequestId }),
                            TimeoutSeconds = TimeoutSeconds,
                        }, timeout.Token).AsTask().GetAwaiter().GetResult();
                        if (progress.Error is not null) throw new RenderRpcException(progress.Error);
                        if (progress.IsProgress)
                            WriteProgress(new ProgressRecord(0, "Rendering preview frames", $"{progress.PercentComplete}%")
                            {
                                PercentComplete = Math.Clamp(progress.PercentComplete, 0, 100),
                                SecondsRemaining = progress.SecondsRemaining,
                            });
                    }
                    result = pending.GetAwaiter().GetResult();
                }
                finally
                {
                    if (!pending.IsCompleted) timeout.Cancel();
                    WriteProgress(new ProgressRecord(0, "Rendering preview frames", "Finished") { RecordType = ProgressRecordType.Completed });
                }
            }
            else result = pending.GetAwaiter().GetResult();
            if (result.RequestId != request.RequestId || result.SessionId != connection.SessionId) throw new InvalidOperationException("GUI RPC response does not match the session and request.");
            if (result.Error is not null) throw new RenderRpcException(result.Error);
            if (Operation is GuiProjectOperation.CompleteBatch or GuiProjectOperation.StopBatch)
            {
                connection.LastBatchId = connection.BatchId != Guid.Empty ? connection.BatchId : connection.LastBatchId;
                connection.BatchId = Guid.Empty;
            }
            if (!ReturnResult) return;
            using var json = JsonDocument.Parse(result.Json);
            if (json.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var item in json.RootElement.EnumerateArray()) WriteObject(ToObject(item));
            else if (json.RootElement.ValueKind != JsonValueKind.Null) WriteObject(ToObject(json.RootElement));
        }
        catch (Exception ex)
        {
            if (newBatch && ex is RenderRpcException) Connection.Current.BatchId = Guid.Empty;
            Fail(ex);
        }
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
    [Parameter][ValidateLength(1, 512)] public virtual string? ChangeReason { get; set; }
    protected override bool IsWrite => true;
    protected override bool ReturnResult => PassThru;
}
