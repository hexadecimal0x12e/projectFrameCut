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
    [Parameter(Mandatory = true, ParameterSetName = "Persistent")] public Guid ClientId { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Persistent")] public string PrivateKey { get; set; } = "";
    [Parameter(ParameterSetName = "Persistent")] public string Service { get; set; } = "rpc";
    [Parameter(ParameterSetName = "Persistent")] public string? PersistentRequestDirectory { get; set; }
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
            if (ParameterSetName is "Authorize" or "Persistent") info = RequestAsync().GetAwaiter().GetResult();
            else info = new()
            {
                PipeName = ParameterSetName == "Id" ? RenderProtocol.AdditionalPipePrefix + PipeId : PipeName,
                Token = ParameterSetName == "Id" ? PipeId : Token
            };
            if (info.Token.Length != 64 || !info.Token.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(info.PipeName))
                throw new ArgumentException("Expected a pipe name and a 64-character hexadecimal token.");
            var clientId = info.ClientId == Guid.Empty ? $"powershell-{Guid.NewGuid():N}" : info.ClientId.ToString("D");
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
            $"--timeout={TimeoutSeconds}"
        };
        if (ParameterSetName == "Persistent")
        {
            cmdline =
            [
                ..cmdline,
                $"--clientId={ClientId:D}",
                $"--service={Service}",
                $"--privateKey={GetUnresolvedProviderPathFromPSPath(PrivateKey)}"
            ];
        }
        else
        {
            cmdline =
            [
                ..cmdline,
                $"--name={Name}",
                $"--author={Author}",
                $"--purpose={Purpose}"
            ];
        }
        foreach (var arg in cmdline)
        {
            start.ArgumentList.Add(arg);
        }
        var requestDirectory = ParameterSetName == "Persistent" ? PersistentRequestDirectory : RequestDirectory;
        if (requestDirectory is not null) start.ArgumentList.Add($"--requestDir={GetUnresolvedProviderPathFromPSPath(requestDirectory)}");
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
        var standardOutput = await output.ConfigureAwait(false);
        var standardError = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException($"RPC authorization failed ({process.ExitCode}): {standardError}");
        return JsonSerializer.Deserialize<RPCAuthResponse>(standardError, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.connection
            ?? throw new InvalidOperationException($"Empty RPC connection response. Output: {standardOutput}");
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
