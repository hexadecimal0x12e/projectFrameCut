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

    internal static Guid Open(ExternalRpcConnection info, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(info.PipeName))
        {
            throw new ArgumentException("Expected a pipe name.");
        }
        var clientId = info.ClientId == Guid.Empty ? $"powershell-{Guid.NewGuid():N}" : info.ClientId.ToString("D");
        RenderClient? client = new(new NamedPipeRenderClientTransport(info.PipeName, clientId), clientId);
        try
        {
            var capabilities = client.GetCapabilitiesAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
            if (!capabilities.Operations.Contains(nameof(RenderOperation.InvokeGuiProject)))
            {
                throw new NotSupportedException("This pipe does not support GUI project operations. Create a new pipe from the target project window.");
            }
            var session = client.GetGuiProjectSessionAsync(new(), cancellationToken).AsTask().GetAwaiter().GetResult();
            if (session.SessionId == Guid.Empty)
            {
                throw new InvalidOperationException("No GUI project is bound to this pipe.");
            }
            Replace(new(client, session.SessionId, Runspace.DefaultRunspace));
            client = null;
            return session.SessionId;
        }
        finally
        {
            if (client is not null)
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
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
    [Parameter(ParameterSetName = "Authorize")]
    [Parameter(ParameterSetName = "Persistent")]
    public string ExecutablePath { get; set; } = "pjfc";
    [Parameter(ParameterSetName = "Authorize")] public string? RequestDirectory { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Persistent")] public Guid ClientId { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Persistent")] public string PrivateKey { get; set; } = "";
    [Parameter(ParameterSetName = "Persistent")][ValidatePattern("^[A-Za-z0-9._-]{1,24}$")] public string Service { get; set; } = "rpc";
    [Parameter(ParameterSetName = "Persistent")] public string? PersistentRequestDirectory { get; set; }
    [Parameter(Mandatory = true, ParameterSetName = "Id")] public string PipeId { get; set; } = "";
    [Parameter(Mandatory = true, ParameterSetName = "Pipe")] public string PipeName { get; set; } = "";
    [Parameter][ValidateRange(5, 3600)] public int TimeoutSeconds { get; set; } = 300;

    protected override void ProcessRecord()
    {
        try
        {
            Cancellation.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            ExternalRpcConnection info;
            if (ParameterSetName is "Authorize" or "Persistent") info = RequestAsync().GetAwaiter().GetResult();
            else info = new()
            {
                PipeName = ParameterSetName == "Id" ? RenderProtocol.AdditionalPipePrefix + PipeId : PipeName,
            };
            var sessionId = Connection.Open(info, Cancellation.Token);
            WriteVerbose($"Connected to GUI project session {sessionId}.");
            WriteObject(new PSObject(new { SessionId = sessionId, Connected = true }));
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task<ExternalRpcConnection> RequestAsync()
    {
        if (ParameterSetName == "Persistent")
            return await RequestPersistentAsync(ExecutablePath, ClientId, GetUnresolvedProviderPathFromPSPath(PrivateKey), Service,
                PersistentRequestDirectory is null ? null : GetUnresolvedProviderPathFromPSPath(PersistentRequestDirectory), TimeoutSeconds, Cancellation.Token).ConfigureAwait(false);
        return await RequestCoreAsync(ExecutablePath,
        [
            "rpc_request",
            "--wait",
            $"--timeout={TimeoutSeconds}",
            $"--name={Name}",
            $"--author={Author}",
            $"--purpose={Purpose}",
        ], RequestDirectory is null ? null : GetUnresolvedProviderPathFromPSPath(RequestDirectory), Cancellation.Token).ConfigureAwait(false);
    }

    internal static Task<ExternalRpcConnection> RequestPersistentAsync(string executablePath, Guid clientId, string privateKey,
        string service, string? requestDirectory, int timeoutSeconds, CancellationToken cancellationToken) => RequestCoreAsync(executablePath,
        [
            "rpc_request",
            "--wait",
            $"--timeout={timeoutSeconds}",
            $"--clientId={clientId:D}",
            $"--service={service}",
            $"--privateKey={privateKey}"
        ], requestDirectory, cancellationToken);

    private static async Task<ExternalRpcConnection> RequestCoreAsync(string executablePath, string[] cmdline,
        string? requestDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in cmdline)
        {
            start.ArgumentList.Add(arg);
        }
        if (requestDirectory is not null) start.ArgumentList.Add($"--requestDir={requestDirectory}");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start pjfc.");
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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

[Cmdlet(VerbsCommunications.Connect, "ProjectFrameCutPersistent")]
public sealed class ConnectProjectFrameCutPersistentCommand : CancellableCmdlet
{
    [Parameter(Mandatory = true)] public Guid ClientId { get; set; }
    [Parameter(Mandatory = true)] public string PrivateKey { get; set; } = "";
    [Parameter][ValidatePattern("^[A-Za-z0-9._-]{1,24}$")] public string Service { get; set; } = "rpc";
    [Parameter] public string ExecutablePath { get; set; } = "pjfc";
    [Parameter] public string? RequestDirectory { get; set; }
    [Parameter][ValidateRange(5, 3600)] public int TimeoutSeconds { get; set; } = 300;

    protected override void ProcessRecord()
    {
        try
        {
            Cancellation.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            var info = ConnectProjectFrameCutCommand.RequestPersistentAsync(ExecutablePath, ClientId,
                GetUnresolvedProviderPathFromPSPath(PrivateKey), Service,
                RequestDirectory is null ? null : GetUnresolvedProviderPathFromPSPath(RequestDirectory),
                TimeoutSeconds, Cancellation.Token).GetAwaiter().GetResult();
            var sessionId = Connection.Open(info, Cancellation.Token);
            WriteVerbose($"Connected persistent client {ClientId:D} to GUI project session {sessionId}.");
            WriteObject(new PSObject(new { SessionId = sessionId, ClientId, Service, Connected = true }));
        }
        catch (Exception ex) { Fail(ex); }
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
