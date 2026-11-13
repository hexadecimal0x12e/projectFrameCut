using projectFrameCut.Render.Contracts;
using System.Diagnostics;
using System.IO.Pipes;

namespace projectFrameCut.Render.PluginIsolation;

public sealed class ProjectExternalSourceProcessPlatform(IReadOnlyList<string> executables) : IPluginIsolationPlatform
{
    public async ValueTask<IPluginIsolationSession> StartAsync(PluginIsolationLaunchContext context, CancellationToken cancellationToken = default)
    {
        if (context.ExternalSourceManifestHash is null) throw new ArgumentException("An external source manifest hash is required.");
        await ProjectExternalSourceDatabase.ValidateAsync(context.PluginRoot, context.ExternalSourceManifestHash, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(context.SessionRoot);
        var pipeName = $"projectFrameCut.ExternalSource.{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        StreamIsolationControlChannel? channel = null;
        Process? process = null;
        var terminated = 0;
        try
        {
            List<Exception> failures = [];
            foreach (var executable in executables)
            {
                try
                {
                    var info = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                    if (Path.GetExtension(executable).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        info.FileName = "dotnet";
                        info.ArgumentList.Add(executable);
                    }
                    foreach (var argument in new[] { "external_source_worker", $"--pipe={pipeName}", $"--token={context.AuthenticationToken}",
                        $"--pluginId={context.PluginId}", $"--pluginRoot={context.PluginRoot}", $"--sessionRoot={context.SessionRoot}",
                        $"--parentPid={Environment.ProcessId}", $"--instancePackageName={context.InstancePackageName}", $"--externalSourceHash={context.ExternalSourceManifestHash}" })
                        info.ArgumentList.Add(argument);
                    process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start external source worker.");
                    process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log($"[ExternalSource:{context.PluginId}] {e.Data}"); };
                    process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log($"[ExternalSource:{context.PluginId}] {e.Data}", "error"); };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    break;
                }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (process is null) throw new InvalidOperationException("Unable to start external source worker.", new AggregateException(failures));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(context.Transport.RequestTimeout);
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            await ProjectExternalSourceWorker.AuthorizeAsync(pipe, context, timeout.Token).ConfigureAwait(false);
            channel = new StreamIsolationControlChannel(pipe, IsolationControlMode.NamedPipe);
            var response = await channel.SendAsync(new() { Operation = RenderOperation.IsolationNegotiate, Payload = RenderRpcSerializer.Serialize(new IsolationNegotiateRequest
            { AuthenticationToken = context.AuthenticationToken, HostProcessId = Environment.ProcessId, PreferredPayloadKind = context.Transport.PayloadMode ?? IsolationPayloadKind.SharedMemory }) }, timeout.Token).ConfigureAwait(false);
            response?.Error?.ThrowAsException();
            var capabilities = RenderRpcSerializer.Deserialize<IsolationChannelCapabilities>(response.Payload);
            if (capabilities.ProtocolVersion != PluginIsolationProtocol.CurrentVersion) throw new InvalidDataException("External source isolation protocol mismatch.");
            var session = new PluginIsolationSession(context.PluginId, channel, new SessionPayloadExchange(context.SessionRoot), new SessionResourceBroker(context.SessionRoot), capabilities, context.Transport, TerminateAsync);
            _ = MonitorAsync();
            Log($"External source '{context.PluginId}' worker {process.Id} started.");
            return session;
        }
        catch
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            else await pipe.DisposeAsync().ConfigureAwait(false);
            await TerminateAsync("External source startup failed.").ConfigureAwait(false);
            throw;
        }

        async Task MonitorAsync()
        {
            try { await process!.WaitForExitAsync().ConfigureAwait(false); await TerminateAsync("External source worker exited.").ConfigureAwait(false); }
            catch (Exception ex) { Log(ex, "Monitor external source worker."); }
        }

        async ValueTask TerminateAsync(string reason)
        {
            if (Interlocked.Exchange(ref terminated, 1) != 0) return;
            Log($"External source '{context.PluginId}': {reason}");
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { Log(ex, "Stop external source worker."); }
            finally
            {
                process?.Dispose();
                try { Directory.Delete(context.SessionRoot, true); }
                catch (Exception ex) { Log(ex, "Clean external source session."); }
            }
        }
    }
}
