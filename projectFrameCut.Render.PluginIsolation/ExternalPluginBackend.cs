using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;

namespace projectFrameCut.Render.PluginIsolation;

public sealed class ExternalPluginBackendContext
{
    public required string PluginId { get; init; }
    public required string PluginRoot { get; init; }
    public required string Locale { get; init; }
    public required IReadOnlyDictionary<string, string> Configuration { get; init; }
}

public static class ExternalPluginBackend
{
    public static async Task RunAsync(
        string[] args,
        Func<ExternalPluginBackendContext, IPluginBase> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(factory);
        var options = RuntimeOptions.Parse(args);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var parentMonitor = MonitorParentAsync(options.ParentProcessId, lifetime);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            Logger.Log($"External plugin backend connecting to '{options.PipeName}' for '{options.PluginId}'.");
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

            var challenge = RandomNumberGenerator.GetBytes(32);
            var authorizationRequest = new RenderRequestEnvelope
            {
                ClientId = $"external-plugin-backend-{Environment.ProcessId}",
                Operation = RenderOperation.IsolationAuthorizeExternalBackend,
                Payload = RenderRpcSerializer.Serialize(new IsolationExternalBackendAuthorizationRequest
                {
                    AuthenticationToken = options.AuthenticationToken,
                    PluginId = options.PluginId,
                    InstancePackageName = options.InstancePackageName,
                    Challenge = challenge,
                }),
            };
            await IsolationFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(authorizationRequest), connectTimeout.Token).ConfigureAwait(false);
            var responseData = await IsolationFrame.ReadAsync(pipe, connectTimeout.Token).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The host closed the pipe during external backend authorization.");
            var response = RenderRpcSerializer.Deserialize<RenderResponseEnvelope>(responseData);
            if (response.Error is not null) response.Error.ThrowAsException();
            if (response.RequestId != authorizationRequest.RequestId || response.ProtocolVersion != RenderProtocol.CurrentVersion)
                throw new InvalidDataException("The host returned an invalid external backend authorization envelope.");
            var authorization = RenderRpcSerializer.Deserialize<IsolationExternalBackendAuthorizationResponse>(response.Payload);
            if (!string.Equals(authorization.PluginId, options.PluginId, StringComparison.Ordinal) ||
                authorization.Challenge.Length != challenge.Length ||
                !CryptographicOperations.FixedTimeEquals(authorization.Challenge, challenge))
                throw new UnauthorizedAccessException("The host returned an invalid external backend authorization response.");

            await using var payloads = new SessionPayloadExchange(options.SessionRoot);
            await using var resources = new SessionResourceBroker(options.SessionRoot);
            await using var communication = new NamedPipePluginCommunicationService(options.PluginId, lifetime.Token);
            using var communicationScope = GlobalPluginHelper.BeginPluginCommunicationScope(communication);
            using var service = new PluginIsolationWorkerService(
                options.AuthenticationToken,
                options.ParentProcessId,
                options.SessionRoot,
                options.PluginId,
                options.PluginRoot,
                string.Empty,
                payloads,
                resources,
                communication,
                lifetime,
                request => factory(new ExternalPluginBackendContext
                {
                    PluginId = request.PluginId,
                    PluginRoot = options.PluginRoot,
                    Locale = request.Locale,
                    Configuration = request.Configuration,
                }));
            await StreamIsolationRequestDispatcher.RunAsync(pipe, service, lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            try { await parentMonitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private static async Task MonitorParentAsync(int parentProcessId, CancellationTokenSource lifetime)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            await parent.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            if (!lifetime.IsCancellationRequested) lifetime.Cancel();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Logger.Log(ex, "monitor the external plugin backend host process", typeof(ExternalPluginBackend));
        }
    }
}
