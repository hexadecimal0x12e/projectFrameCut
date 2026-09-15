using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.IO.Pipes;

namespace projectFrameCut.Services;

internal sealed class ExternalPluginBackendPlatform(ExternalPluginBackendLaunchOption launch) : IPluginIsolationPlatform
{
    internal static string InstanceName => $"external-{Environment.ProcessId}";
    internal static string SessionDirectory => Path.Combine(MauiProgram.CachePath, "plugin-external-backend");

    public async ValueTask<IPluginIsolationSession> StartAsync(PluginIsolationLaunchContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if(!OperatingSystem.IsWindows() || !OperatingSystem.IsLinux() || !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("External plugin backends are only supported on Windows, Linux, and macOS.");
        if (context.Transport.ControlMode is not (IsolationControlMode.Auto or IsolationControlMode.NamedPipe))
            throw new NotSupportedException($"External plugin backends do not support control mode '{context.Transport.ControlMode}'.");
        if (!string.Equals(context.InstancePackageName, InstanceName, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The external plugin backend session does not belong to this application process.");

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var pluginRoot = Path.GetFullPath(context.PluginRoot);
        var pluginsRoot = Path.GetFullPath(Path.Combine(MauiProgram.BasicDataPath, "Plugins"));
        if (!pluginRoot.StartsWith(pluginsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison) ||
            !string.Equals(Path.GetFileName(pluginRoot), context.PluginId, comparison))
            throw new UnauthorizedAccessException("The external backend package directory is invalid.");
        var sessionRoot = Path.GetFullPath(context.SessionRoot);
        if (!string.Equals(Path.GetDirectoryName(sessionRoot), Path.GetFullPath(SessionDirectory), comparison) ||
            !Guid.TryParseExact(Path.GetFileName(sessionRoot), "N", out _))
            throw new ArgumentException("The external backend session must be a unique directory below the backend cache directory.");
        if (Directory.Exists(sessionRoot)) throw new IOException("The external backend session directory already exists.");

        Directory.CreateDirectory(sessionRoot);
        var pipeName = $"projectFrameCut.ExternalPlugin.{Guid.NewGuid():N}";
        NamedPipeServerStream? pendingPipe = null;
        StreamIsolationControlChannel? channel = null;
        Process? process = null;
        var terminated = 0;
        try
        {
            pendingPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            process = StartBackend();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(context.Transport.RequestTimeout);
            await pendingPipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            await PluginIsolationHostHandshake.AuthorizeExternalBackendAsync(pendingPipe, context, timeout.Token).ConfigureAwait(false);
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
                throw new InvalidDataException("External plugin backend protocol version mismatch.");

            var payloads = new SessionPayloadExchange(sessionRoot, context.Transport.MaximumInlineBytes, context.Transport.MaximumPayloadBytes);
            var resources = new SessionResourceBroker(sessionRoot, context.Transport.MaximumPayloadBytes);
            var session = new PluginIsolationSession(context.PluginId, channel, payloads, resources, capabilities, context.Transport, TerminateAsync);
            _ = MonitorChannelAsync(session);
            if (!launch.UseShellExecute && process is not null) _ = MonitorBackendAsync(session);
            Logger.Log($"External plugin backend for '{context.PluginId}' connected.");
            return session;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            else if (pendingPipe is not null) await pendingPipe.DisposeAsync().ConfigureAwait(false);
            await TerminateAsync("External backend startup timed out.").ConfigureAwait(false);
            throw new TimeoutException($"External plugin backend '{context.PluginId}' did not connect within {context.Transport.RequestTimeout}.");
        }
        catch
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            else if (pendingPipe is not null) await pendingPipe.DisposeAsync().ConfigureAwait(false);
            await TerminateAsync("External backend startup failed.").ConfigureAwait(false);
            throw;
        }

        Process? StartBackend()
        {
            var entryPoint = launch.EntryPoint;
            if (!launch.UseShellExecute && !Path.IsPathRooted(entryPoint))
            {
                entryPoint = Path.GetFullPath(Path.Combine(pluginRoot, entryPoint));
                if (!entryPoint.StartsWith(pluginRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
                    throw new UnauthorizedAccessException("The external backend entry point escapes the signed plugin directory.");
            }
            if (!launch.UseShellExecute && (!Path.IsPathRooted(entryPoint) || !File.Exists(entryPoint)))
                throw new FileNotFoundException("The external backend executable was not found.", entryPoint);
            if (!launch.UseShellExecute && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(entryPoint, File.GetUnixFileMode(entryPoint) | UnixFileMode.UserExecute);
            var info = new ProcessStartInfo
            {
                FileName = entryPoint,
                UseShellExecute = launch.UseShellExecute,
                CreateNoWindow = launch.CreateNoWindow,
                WorkingDirectory = pluginRoot,
            };
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
            }) info.ArgumentList.Add(argument);
            if (MyLoggerExtensions.LoggingDiagnosticInfo) info.ArgumentList.Add("--logDiagnostic");
            return Process.Start(info);
        }

        ValueTask TerminateAsync(string reason)
        {
            if (Interlocked.Exchange(ref terminated, 1) != 0) return ValueTask.CompletedTask;
            try
            {
                process?.Dispose();
                if (Directory.Exists(sessionRoot)) Directory.Delete(sessionRoot, true);
            }
            catch (Exception ex) { Logger.Log(ex, "clean up the external plugin backend session", this); }
            Logger.Log($"External plugin backend for '{context.PluginId}' stopped: {reason}");
            return ValueTask.CompletedTask;
        }

        async Task MonitorChannelAsync(PluginIsolationSession session)
        {
            try
            {
                await channel!.Completion.ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref terminated) == 0) Logger.Log(ex, "monitor the external plugin backend channel", this);
            }
        }

        async Task MonitorBackendAsync(PluginIsolationSession session)
        {
            try
            {
                await process!.WaitForExitAsync().ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref terminated) == 0) Logger.Log(ex, "monitor the external plugin backend", this);
            }
        }
    }
}
