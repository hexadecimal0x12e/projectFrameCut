using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using projectFrameCut.IntegratedAPIServer.Headless;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.IntegratedAPIServer.MCP;

public enum McpTransportMode
{
    Stdio,
    Http,
    RawPipe,
}

/// <summary>Hosts the headless project tools over MCP stdio, raw pipes, or Streamable HTTP.</summary>
public static class McpService
{
    public static async Task RunAsync(McpServiceOptions serviceOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceOptions);
        string userDataRoot = !string.IsNullOrWhiteSpace(serviceOptions.UserDataRoot)
            ? Path.GetFullPath(serviceOptions.UserDataRoot)
            : !string.IsNullOrWhiteSpace(serviceOptions.ProjectRoot)
                ? ResolveUserDataRootFromProject(serviceOptions.ProjectRoot)
                : throw new ArgumentException("No-project MCP mode requires a user data root.", nameof(serviceOptions));
        if (serviceOptions.Transport is not McpTransportMode.Stdio and not McpTransportMode.Http and not McpTransportMode.RawPipe)
            throw new ArgumentOutOfRangeException(nameof(serviceOptions), "Unknown MCP transport mode.");
        if (serviceOptions.Transport == McpTransportMode.RawPipe && string.IsNullOrWhiteSpace(serviceOptions.RawPipeName))
            throw new ArgumentException("Raw pipe MCP transport requires a pipe name.", nameof(serviceOptions));
        if (!string.IsNullOrWhiteSpace(serviceOptions.RawPipeParentPid))
        {
            if (serviceOptions.Transport != McpTransportMode.RawPipe)
                throw new ArgumentException("Raw pipe parent PID is only valid with the raw pipe MCP transport.", nameof(serviceOptions));
            if (!int.TryParse(serviceOptions.RawPipeParentPid, out int parentPid) || parentPid <= 0)
                throw new ArgumentException("Raw pipe parent PID must be a positive process ID.", nameof(serviceOptions));
        }
        if (serviceOptions.Transport == McpTransportMode.Http)
        {
            if (serviceOptions.HttpListenUri is null)
                throw new ArgumentException("HTTP MCP transport requires a listen URI.", nameof(serviceOptions));
            IntegratedApiServer.ValidateListenUri(serviceOptions.HttpListenUri);
        }
        if (serviceOptions.RpcListenUri is not null)
        {
            IntegratedApiServer.ValidateListenUri(serviceOptions.RpcListenUri);
            IntegratedApiServer.ValidateRpcToken(serviceOptions.RpcToken ?? string.Empty);
        }
        else if (!string.IsNullOrWhiteSpace(serviceOptions.RpcToken))
        {
            throw new ArgumentException("An RPC token requires an RPC listen URI.", nameof(serviceOptions));
        }
        if (serviceOptions.StartClient && string.IsNullOrWhiteSpace(serviceOptions.ClientExecutable))
            throw new ArgumentException("Starting the MCP client requires a client executable.", nameof(serviceOptions));

        HeadlessProjectService? headlessService = null;
        RenderBackendService? renderService = null;
        IntegratedApiServer? httpMcpServer = null;
        IntegratedApiServer? rpcServer = null;
        ProjectMcpModeController? modeController = null;
        CancellationTokenSource? pipeLifetime = null;
        Task? pipeServerTask = null;
        Process? clientProcess = null;
        CancellationTokenSource? parentLifetime = null;
        Task? parentMonitor = null;
        CancellationToken serviceCancellationToken = cancellationToken;
        if (serviceOptions.Transport == McpTransportMode.RawPipe && !string.IsNullOrWhiteSpace(serviceOptions.RawPipeParentPid))
        {
            parentLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            parentMonitor = StartParentMonitor(serviceOptions.RawPipeParentPid, parentLifetime);
            serviceCancellationToken = parentLifetime.Token;
        }
        using var renderRuntimeLock = new SemaphoreSlim(1, 1);
        bool renderRuntimeInitialized = false;

        try
        {
            if (serviceOptions.RpcListenUri is not null || serviceOptions.StartClient)
            {
                await EnsureRenderRuntimeAsync(serviceCancellationToken).ConfigureAwait(false);
                renderService = new RenderBackendService(stateRoot: userDataRoot);
                headlessService = new HeadlessProjectService(renderService, serviceOptions.GlobalAssetsDatabasePath);
                if (!string.IsNullOrWhiteSpace(serviceOptions.ProjectRoot))
                    await headlessService.InitializeAsync(serviceOptions.ProjectRoot, serviceCancellationToken).ConfigureAwait(false);

                if (serviceOptions.RpcListenUri is not null)
                {
                    bool shareHttpListener = serviceOptions.Transport == McpTransportMode.Http &&
                        HaveSameListenAddress(serviceOptions.HttpListenUri!, serviceOptions.RpcListenUri);
                    if (!shareHttpListener)
                    {
                        rpcServer = new IntegratedApiServer();
                        await rpcServer.StartHeadlessAsync(CreateHttpOptions(
                            serviceOptions,
                            serviceOptions.RpcListenUri,
                            enableMcp: false), headlessService, serviceCancellationToken).ConfigureAwait(false);
                        serviceOptions.RpcServerStarted?.Invoke(serviceOptions.RpcListenUri);
                    }
                }

                if (serviceOptions.StartClient)
                {
                    serviceCancellationToken.ThrowIfCancellationRequested();
                    string pipeName = $"projectFrameCut-mcp-{Guid.NewGuid():N}";
                    string pipeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    pipeLifetime = CancellationTokenSource.CreateLinkedTokenSource(serviceCancellationToken);
                    pipeServerTask = new NamedPipeRenderServer(headlessService)
                        .RunAsync(pipeName, pipeToken, cancellationToken: pipeLifetime.Token);
                    clientProcess = StartClientProcess(
                        serviceOptions.ClientExecutable!,
                        pipeName,
                        pipeToken,
                        serviceOptions.WarningSink,
                        serviceOptions.ClientExited);
                }
            }

            modeController = new ProjectMcpModeController(
                userDataRoot,
                async (projectRoot, token) =>
                {
                    await EnsureRenderRuntimeAsync(token).ConfigureAwait(false);
                    if (headlessService is null) return new HeadlessProjectMcpBackend(projectRoot);

                    await headlessService.EnsureDefaultProjectAsync(projectRoot, token).ConfigureAwait(false);
                    return await SharedHeadlessProjectMcpBackend.CreateAsync(headlessService, cancellationToken: token).ConfigureAwait(false);
                },
                headlessService is null
                    ? null
                    : token => headlessService.ClearDefaultProjectAsync(token));
            if (!string.IsNullOrWhiteSpace(serviceOptions.ProjectRoot))
            {
                await modeController.EnterProjectAsync(serviceOptions.ProjectRoot, serviceCancellationToken).ConfigureAwait(false);
            }

            Task mcpTask;
            if (serviceOptions.Transport == McpTransportMode.Stdio)
            {
                mcpTask = RunStdioMcpServerAsync(modeController, serviceCancellationToken);
            }
            else if (serviceOptions.Transport == McpTransportMode.RawPipe)
            {
                mcpTask = RunRawPipeMcpServerAsync(modeController, serviceOptions.RawPipeName!, serviceCancellationToken);
            }
            else
            {
                httpMcpServer = new IntegratedApiServer();
                bool shareHttpListener = headlessService is not null &&
                    serviceOptions.RpcListenUri is not null &&
                    HaveSameListenAddress(serviceOptions.HttpListenUri!, serviceOptions.RpcListenUri!);
                IntegratedApiServerOptions httpOptions = CreateHttpOptions(
                    serviceOptions,
                    serviceOptions.HttpListenUri!,
                    enableMcp: true,
                    enableRpc: shareHttpListener);
                if (shareHttpListener)
                {
                    await httpMcpServer.StartAsync(
                        httpOptions,
                        modeController,
                        headlessService!,
                        serviceCancellationToken).ConfigureAwait(false);
                    serviceOptions.RpcServerStarted?.Invoke(serviceOptions.HttpListenUri!);
                }
                else
                {
                    await httpMcpServer.StartAsync(httpOptions, modeController, serviceCancellationToken).ConfigureAwait(false);
                }

                serviceOptions.HttpMcpServerStarted?.Invoke(serviceOptions.HttpListenUri!);
                mcpTask = Task.Delay(Timeout.InfiniteTimeSpan, serviceCancellationToken);
            }

            if (pipeServerTask is null)
            {
                await mcpTask.ConfigureAwait(false);
            }
            else
            {
                Task first = await Task.WhenAny(mcpTask, pipeServerTask).ConfigureAwait(false);
                if (ReferenceEquals(first, pipeServerTask) && !serviceCancellationToken.IsCancellationRequested)
                    await pipeServerTask.ConfigureAwait(false);
                await mcpTask.ConfigureAwait(false);
            }

            async ValueTask EnsureRenderRuntimeAsync(CancellationToken token)
            {
                if (renderRuntimeInitialized || serviceOptions.RenderRuntimeInitializer is null) return;
                await renderRuntimeLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (renderRuntimeInitialized) return;
                    await serviceOptions.RenderRuntimeInitializer(token).ConfigureAwait(false);
                    renderRuntimeInitialized = true;
                }
                finally
                {
                    renderRuntimeLock.Release();
                }
            }
        }
        catch (OperationCanceledException) when (parentLifetime is not null &&
            parentLifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (parentLifetime is not null)
            {
                try { parentLifetime.Cancel(); } catch { }
            }
            if (pipeLifetime is not null)
            {
                try { pipeLifetime.Cancel(); } catch { }
            }
            if (clientProcess is not null)
            {
                try
                {
                    if (!clientProcess.HasExited) clientProcess.Kill(entireProcessTree: true);
                }
                catch { }
                clientProcess.Dispose();
            }
            if (pipeServerTask is not null)
            {
                try { await pipeServerTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch when (cancellationToken.IsCancellationRequested) { }
            }
            pipeLifetime?.Dispose();
            if (parentMonitor is not null)
            {
                try { await parentMonitor.ConfigureAwait(false); } catch { }
            }
            parentLifetime?.Dispose();
            if (httpMcpServer is not null) await httpMcpServer.DisposeAsync().ConfigureAwait(false);
            if (rpcServer is not null) await rpcServer.DisposeAsync().ConfigureAwait(false);
            if (modeController is not null) await modeController.DisposeAsync().ConfigureAwait(false);
            if (headlessService is not null) await headlessService.DisposeAsync().ConfigureAwait(false);
            if (renderService is not null) await renderService.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Process StartClientProcess(
        string executable,
        string pipeName,
        string pipeToken,
        Action<string>? warningSink,
        Action? clientExited)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("gui");
        startInfo.ArgumentList.Add("--mcpMode=client");
        startInfo.ArgumentList.Add($"--mcpPipe={pipeName}");
        startInfo.ArgumentList.Add($"--mcpToken={pipeToken}");
        startInfo.ArgumentList.Add("--quiet");

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The projectFrameCut MCP client process could not be started.");
        try
        {
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    warningSink?.Invoke($"MCP client: {eventArgs.Data}");
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    warningSink?.Invoke($"MCP client error: {eventArgs.Data}");
            };
            process.Exited += (_, _) => clientExited?.Invoke();
            process.EnableRaisingEvents = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            process.Dispose();
            throw;
        }
    }

    private static IntegratedApiServerOptions CreateHttpOptions(
        McpServiceOptions serviceOptions,
        Uri listenUri,
        bool enableMcp,
        bool enableRpc = true)
        => new()
        {
            ListenUri = listenUri,
            RpcToken = enableRpc ? serviceOptions.RpcToken : null,
            ProjectRoot = serviceOptions.ProjectRoot,
            GlobalAssetsDatabasePath = serviceOptions.GlobalAssetsDatabasePath,
            EnableMcp = enableMcp,
            RequireMcpAuthorization = false,
            IncludeIntegratedClientMcpTools = false,
            WarningSink = serviceOptions.WarningSink,
        };

    private static bool HaveSameListenAddress(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
           left.Port == right.Port;

    private static string ResolveUserDataRootFromProject(string projectRoot)
    {
        string fullProjectRoot = Path.GetFullPath(projectRoot);
        DirectoryInfo? parent = Directory.GetParent(fullProjectRoot);
        return parent is not null && string.Equals(parent.Name, "My Drafts", StringComparison.OrdinalIgnoreCase)
            ? parent.Parent?.FullName ?? parent.FullName
            : parent?.FullName ?? fullProjectRoot;
    }

    private static Task RunStdioMcpServerAsync(
        IIntegratedApiBackend backend,
        CancellationToken cancellationToken)
        => RunMcpServerAsync(
            backend,
            options => new StdioServerTransport(options, loggerFactory: null),
            cancellationToken);

    private static async Task RunRawPipeMcpServerAsync(
        IIntegratedApiBackend backend,
        string pipeName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Pipe name is required.", nameof(pipeName));

        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        await RunMcpServerAsync(
            backend,
            _ => new StreamServerTransport(pipe, pipe, "projectFrameCut.RawPipe", loggerFactory: null),
            cancellationToken).ConfigureAwait(false);
    }

    private static Task StartParentMonitor(string? parentPid, CancellationTokenSource cancellation)
    {
        if (!int.TryParse(parentPid, out int pid) || pid <= 0) return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                using var parent = Process.GetProcessById(pid);
                await parent.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch { }

            if (!cancellation.IsCancellationRequested)
                cancellation.Cancel();
        });
    }

    private static async Task RunMcpServerAsync(
        IIntegratedApiBackend backend,
        Func<McpServerOptions, ITransport> transportFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(transportFactory);

        var authorization = new EndpointAuthorizationManager();
        var requestContextAccessor = new IntegratedApiRequestContextAccessor();

        await using var services = new ServiceCollection()
            .AddSingleton<IntegratedApiRequestContextAccessor>(requestContextAccessor)
            .AddSingleton<EndpointAuthorizationManager>(authorization)
            .AddSingleton<IIntegratedApiBackend>(backend)
            .BuildServiceProvider();

        using var toolCollection = new ProjectModeMcpToolCollection(
            (ProjectMcpModeController)backend,
            authorization,
            requestContextAccessor,
            requireAuthorization: false,
            includeIntegratedClientTools: false);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "projectFrameCut.IntegratedAPIServer",
                Version = "1.0.0",
            },
            ToolCollection = toolCollection.Tools,
        };

        await using var transport = transportFactory(options);
#pragma warning disable MCPEXP002
        await using var server = McpServer.Create(transport, options, loggerFactory: null, services);
#pragma warning restore MCPEXP002
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class McpServiceOptions
{
    public string? ProjectRoot { get; init; }

    public string? UserDataRoot { get; init; }

    public McpTransportMode Transport { get; init; } = McpTransportMode.Stdio;

    public string? RawPipeName { get; init; }

    public string? RawPipeParentPid { get; init; }

    public Uri? HttpListenUri { get; init; }

    public Uri? RpcListenUri { get; init; }

    public string? RpcToken { get; init; }

    public string? GlobalAssetsDatabasePath { get; init; }

    public string? ClientExecutable { get; init; }

    public bool StartClient { get; init; }

    public Func<CancellationToken, ValueTask>? RenderRuntimeInitializer { get; init; }

    public Action<string>? WarningSink { get; init; }

    public Action? ClientExited { get; init; }

    public Action<Uri>? HttpMcpServerStarted { get; init; }

    public Action<Uri>? RpcServerStarted { get; init; }
}

/// <summary>Compatibility wrapper for the original stdio-only hosting API.</summary>
public static class StdioMcpService
{
    public static Task RunAsync(string projectRoot, CancellationToken cancellationToken = default)
        => McpService.RunAsync(new McpServiceOptions
        {
            ProjectRoot = projectRoot,
            UserDataRoot = ResolveCompatibilityUserDataRoot(projectRoot),
        }, cancellationToken);

    public static Task RunAsync(StdioMcpServiceOptions serviceOptions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceOptions);
        return McpService.RunAsync(new McpServiceOptions
        {
            ProjectRoot = serviceOptions.ProjectRoot,
            UserDataRoot = serviceOptions.UserDataRoot ??
                ResolveCompatibilityUserDataRoot(serviceOptions.ProjectRoot),
            RpcListenUri = serviceOptions.ProjectServerListenUri,
            RpcToken = serviceOptions.ProjectServerToken,
            GlobalAssetsDatabasePath = serviceOptions.GlobalAssetsDatabasePath,
            ClientExecutable = serviceOptions.ClientExecutable,
            StartClient = serviceOptions.StartClient,
            RenderRuntimeInitializer = serviceOptions.RenderRuntimeInitializer,
            WarningSink = serviceOptions.WarningSink,
            ClientExited = serviceOptions.ClientExited,
            RpcServerStarted = serviceOptions.ProjectServerStarted,
        }, cancellationToken);
    }

    private static string ResolveCompatibilityUserDataRoot(string projectRoot)
    {
        string fullProjectRoot = Path.GetFullPath(projectRoot);
        DirectoryInfo? parent = Directory.GetParent(fullProjectRoot);
        return parent is not null && string.Equals(parent.Name, "My Drafts", StringComparison.OrdinalIgnoreCase)
            ? parent.Parent?.FullName ?? parent.FullName
            : parent?.FullName ?? fullProjectRoot;
    }
}

public sealed class StdioMcpServiceOptions
{
    public required string ProjectRoot { get; init; }

    public string? UserDataRoot { get; init; }

    public Uri? ProjectServerListenUri { get; init; }

    public string? ProjectServerToken { get; init; }

    public string? GlobalAssetsDatabasePath { get; init; }

    public string? ClientExecutable { get; init; }

    public bool StartClient { get; init; }

    public Func<CancellationToken, ValueTask>? RenderRuntimeInitializer { get; init; }

    public Action<string>? WarningSink { get; init; }

    public Action? ClientExited { get; init; }

    public Action<Uri>? ProjectServerStarted { get; init; }
}
