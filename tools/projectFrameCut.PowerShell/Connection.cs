using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.CompilerServices;
using System.Text.Json;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.PowerShell;

internal sealed class Connection : IDisposable
{
    private static readonly ConditionalWeakTable<Runspace, Connection> Connections = new();
    internal RenderClient Client { get; }
    internal Guid SessionId { get; }
    private readonly Runspace _runspace;

    internal Connection(RenderClient client, Guid sessionId, Runspace runspace)
    { Client = client; SessionId = sessionId; _runspace = runspace; }

    internal static Connection Current => Connections.TryGetValue(Runspace.DefaultRunspace, out var c)
        ? c : throw new InvalidOperationException("Run Connect-ProjectFrameCut first.");

    internal static void Replace(Connection connection)
    {
        Disconnect();
        Connections.Add(connection._runspace, connection);
        connection._runspace.StateChanged += connection.StateChanged;
    }

    private void StateChanged(object? sender, RunspaceStateEventArgs e)
    {
        if (e.RunspaceStateInfo.State is RunspaceState.Closed or RunspaceState.Broken) Dispose();
    }

    internal static void Disconnect()
    {
        if (Connections.TryGetValue(Runspace.DefaultRunspace, out var c)) c.Dispose();
    }

    public void Dispose()
    {
        _runspace.StateChanged -= StateChanged;
        Connections.Remove(_runspace);
        Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

public abstract class CancellableCmdlet : PSCmdlet
{
    protected readonly CancellationTokenSource Cancellation = new();
    protected override void StopProcessing() => Cancellation.Cancel();
    protected override void EndProcessing() => Cancellation.Dispose();
    protected void Fail(Exception ex) => ThrowTerminatingError(new ErrorRecord(ex,
        ex is RenderRpcException rpc ? rpc.Error.Code.ToString() : ex.GetType().Name,
        ex is OperationCanceledException ? ErrorCategory.OperationStopped : ErrorCategory.InvalidOperation, null));
}

[Cmdlet(VerbsCommunications.Connect, "ProjectFrameCut", DefaultParameterSetName = "Authorize")]
public sealed class ConnectProjectFrameCutCommand : CancellableCmdlet
{
    [Parameter(Mandatory = true, ParameterSetName = "Authorize")] public string Name { get; set; } = "";
    [Parameter(Mandatory = true, ParameterSetName = "Authorize")] public string Author { get; set; } = "";
    [Parameter(Mandatory = true, ParameterSetName = "Authorize")] public string Purpose { get; set; } = "";
    [Parameter(ParameterSetName = "Authorize")] public string ExecutablePath { get; set; } = "pjfc";
    [Parameter(ParameterSetName = "Authorize")] public string? RequestDirectory { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Id")] public string PipeId { get; set; } = "";
    [Parameter(Mandatory = true, ParameterSetName = "Pipe")] public string PipeName { get; set; } = "";
    [Parameter(Mandatory = true, ParameterSetName = "Pipe")] public string Token { get; set; } = "";
    [Parameter][ValidateRange(5, 3600)] public int TimeoutSeconds { get; set; } = 300;

    protected override void ProcessRecord()
    {
        RenderClient? client = null;
        try
        {
            Cancellation.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            ExternalRpcConnection info;
            if (ParameterSetName == "Authorize") info = RequestAsync().GetAwaiter().GetResult();
            else info = new()
            {
                PipeName = ParameterSetName == "Id" ? RenderProtocol.AdditionalPipePrefix + PipeId : PipeName,
                Token = ParameterSetName == "Id" ? PipeId : Token
            };
            if (info.Token.Length != 64 || !info.Token.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(info.PipeName))
                throw new ArgumentException("Expected a pipe name and a 64-character hexadecimal token.");
            var clientId = $"powershell-{Guid.NewGuid():N}";
            client = new(new NamedPipeRenderClientTransport(info.PipeName, info.Token, clientId), clientId);
            var capabilities = client.GetCapabilitiesAsync(Cancellation.Token).AsTask().GetAwaiter().GetResult();
            if (!capabilities.Operations.Contains(nameof(RenderOperation.InvokeGuiProject)))
                throw new NotSupportedException("This pipe does not support GUI project operations. Create a new pipe from the target project window.");
            var session = client.GetGuiProjectSessionAsync(new(), Cancellation.Token).AsTask().GetAwaiter().GetResult();
            if (session.SessionId == Guid.Empty) throw new InvalidOperationException("No GUI project is bound to this pipe.");
            Connection.Replace(new(client, session.SessionId, Runspace.DefaultRunspace));
            client = null;
            WriteVerbose($"Connected to GUI project session {session.SessionId}.");
            WriteObject(new PSObject(new { session.SessionId, Connected = true }));
        }
        catch (Exception ex) { Fail(ex); }
        finally { if (client is not null) client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private async Task<ExternalRpcConnection> RequestAsync()
    {
        var start = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var cmdline = new[]
        {
            "rpc_request",
            "--wait",
            $"--name={Name}",
            $"--author={Author}", 
            $"--purpose={Purpose}", 
            $"--timeout={TimeoutSeconds}" 
        };
        foreach (var arg in cmdline)
        {
            start.ArgumentList.Add(arg);
        }
        if (RequestDirectory is not null) start.ArgumentList.Add($"--requestDir={GetUnresolvedProviderPathFromPSPath(RequestDirectory)}");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start pjfc.");
        using var registration = Cancellation.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(Cancellation.Token);
        var error = process.StandardError.ReadToEndAsync(Cancellation.Token);
        await process.WaitForExitAsync(Cancellation.Token).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException($"RPC authorization failed ({process.ExitCode}): {await error.ConfigureAwait(false)}");
        await error.ConfigureAwait(false);
        return JsonSerializer.Deserialize<RPCAuthResponse>(await error.ConfigureAwait(false), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.connection ?? throw new InvalidOperationException("Empty RPC connection response.");
    }

    private class RPCAuthResponse
    {
        public string status { get; set; } = "";
        public ExternalRpcConnection connection { get; set; }
    }
}

[Cmdlet(VerbsCommunications.Disconnect, "ProjectFrameCut")]
public sealed class DisconnectProjectFrameCutCommand : PSCmdlet
{
    protected override void ProcessRecord() => Connection.Disconnect();
}

public sealed class ModuleLifecycle : IModuleAssemblyCleanup
{
    public void OnRemove(PSModuleInfo module) => Connection.Disconnect();
}


