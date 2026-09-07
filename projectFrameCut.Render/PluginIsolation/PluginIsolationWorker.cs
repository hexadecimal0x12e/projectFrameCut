using projectFrameCut.Render.Contracts;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace projectFrameCut.Render.PluginIsolation;

public static class PluginIsolationWorker
{
    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? parentMonitor = null;
        try
        {
            var options = RuntimeOptions.Parse(args);
            Logger.Log($"Plugin isolation runtime starting for '{options.PluginId}'.");
            parentMonitor = MonitorParentAsync(options.ParentProcessId, lifetime);
            await using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            Logger.Log($"Connecting to host pipe '{options.PipeName}'.");
            await pipe.ConnectAsync(connectTimeout.Token);
            Logger.Log("Host pipe connected.");
            var encryptedAssemblyPath = Path.Combine(options.PluginRoot, options.PluginId + ".dll.enc");
            var encryptedAssembly = await File.ReadAllBytesAsync(encryptedAssemblyPath, lifetime.Token);
            var challenge = RandomNumberGenerator.GetBytes(32);
            var authorizationRequest = new RenderRequestEnvelope
            {
                ClientId = $"plugin-isolation-runtime-{Environment.ProcessId}",
                Operation = RenderOperation.IsolationAuthorizePlugin,
                Payload = RenderRpcSerializer.Serialize(new IsolationPluginAuthorizationRequest
                {
                    AuthenticationToken = options.AuthenticationToken,
                    PluginId = options.PluginId,
                    InstancePackageName = options.InstancePackageName,
                    EncryptedAssemblyHash = Convert.ToHexString(SHA256.HashData(encryptedAssembly)).ToLowerInvariant(),
                    Challenge = challenge,
                }),
            };
            Logger.Log("Requesting host authorization for the encrypted plugin assembly.");
            await IsolationFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(authorizationRequest), connectTimeout.Token);
            var authorizationData = await IsolationFrame.ReadAsync(pipe, connectTimeout.Token)
                ?? throw new EndOfStreamException("The host closed the pipe during plugin authorization.");
            var authorizationResponse = RenderRpcSerializer.Deserialize<RenderResponseEnvelope>(authorizationData);
            if (authorizationResponse.Error is not null) authorizationResponse.Error.ThrowAsException();
            if (authorizationResponse.RequestId != authorizationRequest.RequestId || authorizationResponse.ProtocolVersion != RenderProtocol.CurrentVersion)
                throw new InvalidDataException("The host returned an invalid plugin authorization envelope.");
            var authorization = RenderRpcSerializer.Deserialize<IsolationPluginAuthorizationResponse>(authorizationResponse.Payload);
            if (!string.Equals(authorization.PluginId, options.PluginId, StringComparison.Ordinal)
                || authorization.Challenge.Length != challenge.Length
                || !CryptographicOperations.FixedTimeEquals(authorization.Challenge, challenge)
                || string.IsNullOrWhiteSpace(authorization.DecryptionKey))
                throw new UnauthorizedAccessException("The host returned an invalid plugin authorization response.");
            Logger.Log("Host authorization succeeded.");

            await using var payloads = new SessionPayloadExchange(options.SessionRoot);
            await using var resources = new SessionResourceBroker(options.SessionRoot);
            using var service = new PluginIsolationWorkerService(options.AuthenticationToken, options.ParentProcessId, options.SessionRoot, options.PluginId, options.PluginRoot, authorization.DecryptionKey, payloads, resources, lifetime);
            await StreamIsolationRequestDispatcher.RunAsync(pipe, service, lifetime.Token);
            Logger.Log("Plugin isolation runtime stopped.");
        }
#if !DEBUG
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            Log("Operation cancelled.");
        }
#endif
        catch (Exception ex)
        {
            Log(ex, "run the plugin isolation runtime", typeof(PluginIsolationWorker));
            Debug.WriteLine(ex.ToString());
            Environment.FailFast("run the plugin isolation runtime", ex);
        }
        finally
        {
            lifetime.Cancel();
            if (parentMonitor is not null) await parentMonitor.ConfigureAwait(false);
        }

        static async Task MonitorParentAsync(int parentProcessId, CancellationTokenSource lifetime)
        {
            try
            {
                using var parent = Process.GetProcessById(parentProcessId);
                await parent.WaitForExitAsync(lifetime.Token);
                if (!lifetime.IsCancellationRequested)
                {
                    Logger.Log($"Parent process {parentProcessId} exited; stopping runtime.");
                    lifetime.Cancel();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                Logger.Log("AppContainer cannot open the host process; using control pipe closure to detect host exit.", "warning");
            }
            catch (ArgumentException ex)
            {
                // The host can be transitioning between packaged activations when the
                // worker starts. Do not cancel the pipe handshake just because the PID
                // lookup raced with that transition; the control pipe is authoritative.
                Logger.Log(ex, "locate the parent process; using control pipe closure to detect host exit", typeof(PluginIsolationWorker));
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "monitor the parent process", typeof(PluginIsolationWorker));
                lifetime.Cancel();
            }
        }
    }
}

internal sealed partial class RuntimeOptions
{
    public required string PipeName { get; init; }
    public required string AuthenticationToken { get; init; }
    public required string SessionRoot { get; init; }
    public required int ParentProcessId { get; init; }
    public required string PluginId { get; init; }
    public required string PluginRoot { get; init; }
    public required string InstancePackageName { get; init; }

    public static RuntimeOptions Parse(string[] args)
    {
        var values = args.Where(c => CliArgumentRegex().IsMatch(c)).Select(c => c.Substring(2)).ToDictionary(c => c.Split('=', 2)[0], c => c.Split('=', 2)[1]);
        foreach (var key in new[] { "pipe", "token", "sessionRoot", "parentPid", "pluginId", "pluginRoot", "instancePackageName" })
            if (!values.ContainsKey(key)) throw new ArgumentException($"--{key} is required.");
#if DEBUG
        foreach (var item in values)
        {
            Log($"Param {item.Key}: {item.Value}");
        }
#endif
        if (!values.TryGetValue("pipe", out var pipe) || string.IsNullOrWhiteSpace(pipe)) throw new ArgumentException("--pipe is required.");
        if (!values.TryGetValue("token", out var token) || token.Length < 32) throw new ArgumentException("--token must contain at least 32 characters.");
        if (!values.TryGetValue("sessionRoot", out var root) || string.IsNullOrWhiteSpace(root)) throw new ArgumentException("--sessionRoot is required.");
        if (!values.TryGetValue("parentPid", out var pidText) || !int.TryParse(pidText, out var pid) || pid <= 0) throw new ArgumentException("--parentPid is invalid.");
        if (!values.TryGetValue("pluginId", out var pluginId) || string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("--pluginId is required.");
        if (!values.TryGetValue("pluginRoot", out var pluginRoot) || string.IsNullOrWhiteSpace(pluginRoot)) throw new ArgumentException("--pluginRoot is required.");
        if (!values.TryGetValue("instancePackageName", out var instancePackageName) || string.IsNullOrWhiteSpace(instancePackageName)) throw new ArgumentException("--instancePackageName is required.");
        return new() { PipeName = pipe, AuthenticationToken = token, SessionRoot = Path.GetFullPath(root), ParentProcessId = pid, PluginId = pluginId, PluginRoot = Path.GetFullPath(pluginRoot), InstancePackageName = instancePackageName };
    }

    [GeneratedRegex(@"^--(?<key>[^=\s]+)=(?<value>.*)$")]
    private static partial Regex CliArgumentRegex();

}

