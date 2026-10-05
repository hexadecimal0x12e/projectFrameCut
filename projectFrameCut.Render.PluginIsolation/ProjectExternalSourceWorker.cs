using projectFrameCut.Render.Contracts;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;

namespace projectFrameCut.Render.PluginIsolation;

public static class ProjectExternalSourceWorker
{
    public static async Task AuthorizeAsync(Stream pipe, PluginIsolationLaunchContext context, CancellationToken cancellationToken)
    {
        var data = await IsolationFrame.ReadAsync(pipe, cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException("External source worker disconnected before authorization.");
        var envelope = RenderRpcSerializer.Deserialize<RenderRequestEnvelope>(data);
        var request = RenderRpcSerializer.Deserialize<ProjectExternalSourceAuthorization>(envelope.Payload);
        var expected = Encoding.UTF8.GetBytes(context.AuthenticationToken);
        var supplied = Encoding.UTF8.GetBytes(request.Token);
        var valid = envelope.Operation == RenderOperation.IsolationAuthorizeProjectExternalSource
            && envelope.ProtocolVersion == RenderProtocol.CurrentVersion
            && request.SourceId == context.PluginId && request.Challenge.Length == 32
            && supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected)
            && string.Equals(request.ManifestSha256, context.ExternalSourceManifestHash, StringComparison.OrdinalIgnoreCase);
        if (valid) await ProjectExternalSourceDatabase.ValidateAsync(context.PluginRoot, request.ManifestSha256, cancellationToken).ConfigureAwait(false);
        var response = new RenderResponseEnvelope { RequestId = envelope.RequestId };
        if (valid)
        {
            request.Token = string.Empty;
            response.Payload = RenderRpcSerializer.Serialize(request);
        }
        else response.Error = new() { Code = RenderErrorCode.Unauthorized, Message = "External source worker authorization failed." };
        await IsolationFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(response), cancellationToken).ConfigureAwait(false);
        if (!valid) throw new UnauthorizedAccessException("External source worker authorization failed.");
    }

    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var options = RuntimeOptions.Parse(args);
        var hash = args.Single(x => x.StartsWith("--externalSourceHash=", StringComparison.Ordinal))["--externalSourceHash=".Length..];
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        var authorization = new ProjectExternalSourceAuthorization { Token = options.AuthenticationToken, SourceId = options.PluginId, ManifestSha256 = hash, Challenge = RandomNumberGenerator.GetBytes(32) };
        var request = new RenderRequestEnvelope { Operation = RenderOperation.IsolationAuthorizeProjectExternalSource, Payload = RenderRpcSerializer.Serialize(authorization) };
        await IsolationFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(request), timeout.Token).ConfigureAwait(false);
        var response = RenderRpcSerializer.Deserialize<RenderResponseEnvelope>(await IsolationFrame.ReadAsync(pipe, timeout.Token).ConfigureAwait(false)
            ?? throw new EndOfStreamException("Host disconnected during external source authorization."));
        if (response.Error is not null) response.Error.ThrowAsException();
        var accepted = RenderRpcSerializer.Deserialize<ProjectExternalSourceAuthorization>(response.Payload);
        if (response.RequestId != request.RequestId || response.ProtocolVersion != RenderProtocol.CurrentVersion
            || accepted.SourceId != options.PluginId || accepted.ManifestSha256 != hash
            || !CryptographicOperations.FixedTimeEquals(authorization.Challenge, accepted.Challenge))
            throw new UnauthorizedAccessException("Invalid external source authorization response.");
        var validated = await ProjectExternalSourceDatabase.ValidateAsync(options.PluginRoot, hash, lifetime.Token).ConfigureAwait(false);
        var loadContext = new SourceLoadContext(ProjectExternalSourceDatabase.ResolvePath(options.PluginRoot, validated.Manifest.Assembly), options.PluginRoot);
        IExternalVideoSourceProvider? provider = null;
        Task? monitor = null;
        try
        {
            Environment.CurrentDirectory = options.PluginRoot;
            var type = loadContext.LoadFromAssemblyPath(ProjectExternalSourceDatabase.ResolvePath(options.PluginRoot, validated.Manifest.Assembly)).GetType(validated.Manifest.EntryPoint, true)!;
            if (!type.IsVisible || type.IsAbstract || !typeof(IExternalVideoSourceProvider).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidDataException($"Entry point '{type.FullName}' must publicly implement IExternalVideoSourceProvider with a public parameterless constructor.");
            provider = (IExternalVideoSourceProvider)Activator.CreateInstance(type)!;
            await using var payloads = new SessionPayloadExchange(options.SessionRoot);
            await using var service = new ProjectExternalSourceWorkerService(provider, options.AuthenticationToken, options.ParentProcessId, payloads, lifetime);
            monitor = MonitorParentAsync();
            Log($"External source worker {Environment.ProcessId} loaded '{validated.Manifest.Id}'.");
            await StreamIsolationRequestDispatcher.RunAsync(pipe, service, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            if (monitor is not null) await monitor.ConfigureAwait(false);
            if (provider is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (provider is IDisposable disposable) disposable.Dispose();
            loadContext.Unload();
            Log($"External source worker {Environment.ProcessId} stopped.");
        }

        async Task MonitorParentAsync()
        {
            try
            {
                using var parent = Process.GetProcessById(options.ParentProcessId);
                await parent.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
                lifetime.Cancel();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
            {
                Log(ex, "Access external source worker parent; using control pipe closure to detect host exit.");
            }
            catch (ArgumentException ex)
            {
                Log(ex, "Locate external source worker parent; using control pipe closure to detect host exit.");
            }
            catch (Exception ex) { Log(ex, "Monitor external source worker parent."); lifetime.Cancel(); }
        }
    }

    private sealed class SourceLoadContext(string assemblyPath, string root) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);
        protected override Assembly? Load(AssemblyName name)
        {
            var shared = Default.Assemblies.FirstOrDefault(x => x.GetName().Name == name.Name);
            if (shared is not null && (name.Name == typeof(IExternalVideoSourceProvider).Assembly.GetName().Name || name.Name is "protobuf-net" or "protobuf-net.Core")) return shared;
            var path = resolver.ResolveAssemblyToPath(name) ?? Path.Combine(root, name.Name + ".dll");
            if (!File.Exists(path)) return null;
            return LoadFromAssemblyPath(ProjectExternalSourceDatabase.ResolvePath(root, Path.GetRelativePath(root, path).Replace('\\', '/')));
        }
        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var path = resolver.ResolveUnmanagedDllToPath(name);
            if (path is null)
                path = new[] { name, name + ".dll", "lib" + name + ".so", "lib" + name + ".dylib" }
                    .Select(x => ProjectExternalSourceDatabase.ResolvePath(root, x)).FirstOrDefault(File.Exists);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(ProjectExternalSourceDatabase.ResolvePath(root, Path.GetRelativePath(root, path).Replace('\\', '/')));
        }
    }

}
