using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.IO.Pipes;

namespace projectFrameCut.Services;

internal sealed class DesktopPluginIsolationPlatform : IPluginIsolationPlatform
{
    internal static string InstanceName => $"desktop-{Environment.ProcessId}";
    internal static string SessionDirectory => Path.Combine(MauiProgram.CachePath, "plugin-process-isolation");
    internal static bool IsSupported => CliProcessLauncher.GetExecutableCandidates().Count > 0;

    public async ValueTask<IPluginIsolationSession> StartAsync(PluginIsolationLaunchContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsSupported) throw new PlatformNotSupportedException("Process plugin isolation is only available on desktop platforms.");
        if (context.Transport.ControlMode is not (IsolationControlMode.Auto or IsolationControlMode.NamedPipe))
            throw new NotSupportedException($"Process plugin isolation does not support control mode '{context.Transport.ControlMode}'.");
        if (!string.Equals(context.InstancePackageName, InstanceName, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The plugin isolation session does not belong to this application process.");

        var pluginRoot = Path.GetFullPath(context.PluginRoot);
        var pluginsRoot = Path.GetFullPath(Path.Combine(MauiProgram.BasicDataPath, "Plugins"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!pluginRoot.StartsWith(pluginsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)
            || !string.Equals(Path.GetFileName(pluginRoot), context.PluginId, comparison))
            throw new UnauthorizedAccessException("The plugin directory must match an installed plugin below the application data directory.");

        var sessionRoot = Path.GetFullPath(context.SessionRoot);
        if (!string.Equals(Path.GetDirectoryName(sessionRoot), Path.GetFullPath(SessionDirectory), comparison)
            || !Guid.TryParseExact(Path.GetFileName(sessionRoot), "N", out _))
            throw new ArgumentException("The process isolation session must be a unique directory below the isolation cache directory.");
        if (Directory.Exists(sessionRoot)) throw new IOException("The process isolation session directory already exists.");

        Directory.CreateDirectory(sessionRoot);
        var pipeName = $"projectFrameCut.PluginProcessIsolation.{Guid.NewGuid():N}";
        NamedPipeServerStream? pendingPipe = null;
        StreamIsolationControlChannel? channel = null;
        Process? process = null;
        var terminated = 0;
        try
        {
            pendingPipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            process = StartWorker();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(context.Transport.RequestTimeout);
            await pendingPipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            await PluginIsolationHostHandshake.AuthorizeRuntimeAsync(pendingPipe, context, timeout.Token).ConfigureAwait(false);
            channel = new StreamIsolationControlChannel(pendingPipe, IsolationControlMode.NamedPipe);
            var response = await channel.SendAsync(new RenderRequestEnvelope
            {
                ClientId = $"plugin-host-{Environment.ProcessId}",
                Operation = RenderOperation.IsolationNegotiate,
                Payload = RenderRpcSerializer.Serialize(new IsolationNegotiateRequest
                {
                    AuthenticationToken = context.AuthenticationToken,
                    HostProcessId = Environment.ProcessId,
                    PreferredPayloadKind = context.Transport.PayloadMode ?? IsolationPayloadKind.SharedMemory,
                }),
            }, timeout.Token).ConfigureAwait(false);
            if (response.Error is not null) response.Error.ThrowAsException();
            var capabilities = RenderRpcSerializer.Deserialize<IsolationChannelCapabilities>(response.Payload);
            if (capabilities.ProtocolVersion != PluginIsolationProtocol.CurrentVersion)
                throw new InvalidDataException("Plugin isolation protocol version mismatch.");

            var payloads = new SessionPayloadExchange(sessionRoot, context.Transport.MaximumInlineBytes, context.Transport.MaximumPayloadBytes);
            var resources = new SessionResourceBroker(sessionRoot, context.Transport.MaximumPayloadBytes);
            var session = new PluginIsolationSession(context.PluginId, channel, payloads, resources, capabilities, context.Transport, TerminateAsync);
            _ = MonitorWorkerAsync();
            Logger.Log($"Plugin '{context.PluginId}' process isolation worker {process.Id} started.");
            return session;
        }
        catch
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            else if (pendingPipe is not null) await pendingPipe.DisposeAsync().ConfigureAwait(false);
            await TerminateAsync("Worker startup failed.").ConfigureAwait(false);
            throw;
        }

        Process StartWorker()
        {
            List<Exception> failures = [];
            foreach (var executable in CliProcessLauncher.GetExecutableCandidates())
            {
                try
                {
                    var noConsole = !SettingsManager.IsBoolSettingTrue("plugin_IsolationShowConsole");
                    var info = CliProcessLauncher.CreateStartInfo(executable, noConsole);
                    foreach (var argument in new[]
                    {
                        "plugin_worker",
                        $"--pipe={pipeName}",
                        $"--token={context.AuthenticationToken}",
                        $"--sessionRoot={sessionRoot}",
                        $"--parentPid={Environment.ProcessId}",
                        $"--pluginId={context.PluginId}",
                        $"--pluginRoot={pluginRoot}",
                        $"--instancePackageName={InstanceName}",
                        $"--ffmpegRoot={FFmpeg.AutoGen.ffmpeg.RootPath}",
                        "--consoleLog",
                    }) info.ArgumentList.Add(argument);
                    if (MyLoggerExtensions.LoggingDiagnosticInfo) info.ArgumentList.Add("--logDiagnostic");
                    var worker = Process.Start(info) ?? throw new InvalidOperationException("Unable to start the plugin isolation worker.");
                    if (noConsole)
                    {
                        worker.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Logger.Log(e.Data, "PluginWorker Stderr"); };
                        worker.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Logger.Log(e.Data, "PluginWorker Stdout"); };
                        worker.BeginErrorReadLine();
                        worker.BeginOutputReadLine();
                    }
                    return worker;
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
            throw new InvalidOperationException("Unable to start the process plugin isolation worker.", new AggregateException(failures));
        }

        async ValueTask TerminateAsync(string reason)
        {
            if (Interlocked.Exchange(ref terminated, 1) != 0) return;
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                if (Directory.Exists(sessionRoot)) Directory.Delete(sessionRoot, true);
            }
            catch (Exception ex) { Logger.Log(ex, "clean up the process plugin isolation session", this); }
            finally { process?.Dispose(); }
            Logger.Log($"Plugin process isolation worker for '{context.PluginId}' stopped: {reason}");
        }

        async Task MonitorWorkerAsync()
        {
            try
            {
                await process!.WaitForExitAsync().ConfigureAwait(false);
                await TerminateAsync("Worker process exited.").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref terminated) == 0) Logger.Log(ex, "monitor the process plugin isolation worker", this);
            }
        }
    }
}
