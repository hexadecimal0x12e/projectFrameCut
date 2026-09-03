using FFmpeg.AutoGen;
using projectFrameCut.Asset;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Drawing.Base;
using projectFrameCut.DraftStuff;
using projectFrameCut.IntegratedAPIServer;
using projectFrameCut.IntegratedAPIServer.Headless;
using projectFrameCut.IntegratedAPIServer.MCP;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using static projectFrameCut.Shared.Logger;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using projectFrameCut.Render.HwAccelEngine;
using LocalizedResources;
using System.Diagnostics.CodeAnalysis;



#if WINDOWS
using projectFrameCut.Render.WindowsRender;
using ILGPU;

#elif ANDROID

#elif iDevices

#elif LINUX
using ILGPU;

#endif


namespace projectFrameCut
{
    /// <summary>
    /// Entry point for commands exposed by pjfc.
    /// Keep the option descriptions in this file in sync with Program and HomePage.
    /// </summary>
    public static class CLIProgram
    {
        #region init
        private const int SuccessExitCode = 0;
        private const int InvalidCommandExitCode = 2;
        public static string AppDataPath =>
#if WINDOWS
            WinUI.App.IsPackaged() ? Windows.Storage.ApplicationData.Current.LocalFolder.Path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hexadecimal0x12e", "hexadecimal0x12e.projectFrameCut");
#elif LINUX
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".projectFrameCut", "AppData");
#elif MACOS
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support", "projectFrameCut");
#else
            MauiProgram.BasicDataPath;
#endif

        public static int CLIMain(string[] args)
        {
            FFmpeg.AutoGen.DynamicallyLoadedBindings.EnableAutoInitialization = false; //avoid ready check exploding FFmpeg.AutoGen library before we set the root path

            args ??= Array.Empty<string>();

            if (args.Length == 0 || IsHelpOption(args[0]))
            {
                if (args.Length > 1)
                {
                    return WriteCommandHelp(args[1]);
                }

                WriteGeneralHelp();
                return SuccessExitCode;
            }

#if ANDROID
            try
            {
                MyLoggerExtensions.OnLog += [DebuggerNonUserCode()] (msg, level) =>
                {
                    switch (level.ToLower())
                    {
                        case "info":
                            Android.Util.Log.Info("projectFrameCut", msg);
                            break;
                        case "warning":
                        case "warn":
                            Android.Util.Log.Warn("projectFrameCut", msg);
                            break;
                        case "error":
                            Android.Util.Log.Error("projectFrameCut", msg);
                            break;
                        case "critical":
                            Android.Util.Log.Wtf("projectFrameCut", msg);
                            break;
                        default:
                            Android.Util.Log.Info($"projectFrameCut/{level}", msg);
                            break;
                    }
                };
            }
            catch { }
#endif

            if (args.FirstOrDefault(c => c.StartsWith("--ffmpegRoot=")) is string ffPath)
            {
                var ffmpegRoot = ffPath.Substring("--ffmpegRoot=".Length);
                if (!string.IsNullOrWhiteSpace(ffmpegRoot) && Directory.Exists(ffmpegRoot))
                {
                    FFmpeg.AutoGen.ffmpeg.RootPath = ffmpegRoot;
                    Log($"FFmpeg library root path: {ffmpeg.RootPath}");
                    FFmpeg.AutoGen.DynamicallyLoadedBindings.EnableAutoInitialization = false;
                    FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;

                    try
                    {
                        FFmpeg.AutoGen.DynamicallyLoadedBindings.Initialize(true, true);
                        FFmpegHelper.SetupFFmpegLogging();
                        Log($"internal FFmpeg library: version {ffmpeg.av_version_info()}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Failed to initialize FFmpeg from '{ffmpegRoot}': {ex.Message}");
                        return 1;
                    }
                }
            }

            InitializeCliLocalization(args);

            switch (args[0].ToLowerInvariant())
            {
                case "mcp":
                    return RunMcp(args.Skip(1).ToArray());
                case "headless":
                    return RunBackend(args.Skip(1).ToArray());
                case "rpc_server":
                    return RunRpcServer(args.Skip(1).ToArray());
                case "render":
                    return RunRender(args.Skip(1).ToArray());
                case "about":
                    WriteAbout();
                    return 0;
            }

            Console.Error.WriteLine($"Unknown command: {args[0]}");
            Console.Error.WriteLine("Run 'pjfc help' to see the available commands.");
            return InvalidCommandExitCode;
        }

        private static int WriteCommandHelp(string command)
        {
            if (command.Equals("rpc_server", StringComparison.OrdinalIgnoreCase))
            {
                WriteRpcServerHelp();
                return SuccessExitCode;
            }

            if (command.Equals("render", StringComparison.OrdinalIgnoreCase))
            {
                WriteRenderHelp();
                return SuccessExitCode;
            }

            if (command.Equals("headless", StringComparison.OrdinalIgnoreCase))
            {
                WriteBackendHelp();
                return SuccessExitCode;
            }

            if (command.Equals("mcp", StringComparison.OrdinalIgnoreCase))
            {
                WriteMcpHelp();
                return SuccessExitCode;
            }

            if (command.Equals("gui", StringComparison.OrdinalIgnoreCase))
            {
                WriteGuiHelp();
                return SuccessExitCode;
            }

            Console.Error.WriteLine($"No help topic found for '{command}'.");
            Console.Error.WriteLine("Run 'pjfc help' to see the available topics.");
            return InvalidCommandExitCode;
        }

        private static bool IsHelpOption(string value) =>
            value.Equals("help", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/?", StringComparison.OrdinalIgnoreCase);

        private static void InitializeCliLocalization(string[] args)
        {
            Localized = SimpleLocalizer.Init(args.FirstOrDefault(c => c.StartsWith("--locale="))?.Substring("--locale=".Length));
            SettingsManager.SettingLocalizedResources = ISimpleLocalizerBase_Settings.GetMapping().TryGetValue(Localized._LocaleId_, out var loc) ? loc : ISimpleLocalizerBase_Settings.GetMapping().First().Value;
            SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources = ISimpleLocalizerBase_PropertyPanel.GetMapping().TryGetValue(Localized._LocaleId_, out var pploc) ? pploc : ISimpleLocalizerBase_PropertyPanel.GetMapping().First().Value;
            projectFrameCut.ApplicationAPIBase.Localize.APIBaseLocalizedResources.Localized = ApplicationAPIBaseLocalizerBase.GetMapping().TryGetValue(Localized._LocaleId_, out var apiloc) ? apiloc : ApplicationAPIBaseLocalizerBase.GetMapping().First().Value;
#if WINDOWS
            SimpleLocalizerBaseGeneratedHelper.Localized = ISimpleLocalizerBase_Helper.GetMapping().TryGetValue(Localized._LocaleId_, out var hloc) ? hloc : ISimpleLocalizerBase_Helper.GetMapping().First().Value;
#endif
            PluginManager.CurrentLocale = Localized._LocaleId_;
            PluginManager.ExtenedLocalizationGetter = new(k =>
                Localized.IsItemExist(k) ? Localized.DynamicLookup(k, k) : null);
        }
        #endregion

        #region mcp

        private static int RunMcp(string[] args)
        {
            if (args.Any(IsHelpOption))
            {
                WriteMcpHelp();
                return SuccessExitCode;
            }

            string? projectRoot = GetOption(args, "projectRoot", required: false)
                ?? GetOption(args, "project", required: false)
                ?? args.FirstOrDefault(static argument => !argument.StartsWith("--", StringComparison.Ordinal));

            if (!string.IsNullOrWhiteSpace(projectRoot) && !Directory.Exists(projectRoot))
            {
                Console.Error.WriteLine($"MCP project root does not exist: {projectRoot}");
                return InvalidCommandExitCode;
            }

            string dataRoot;
            try
            {
                dataRoot = Path.GetFullPath(ResolveDataRoot(GetOption(args, "dataRoot", required: false)));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to resolve the MCP user data root: {ex.Message}");
                return InvalidCommandExitCode;
            }

            string transportValue = GetOption(args, "transport") ?? "stdio";
            McpTransportMode transport;
            if (transportValue.Equals("stdio", StringComparison.OrdinalIgnoreCase))
            {
                if (!args.Contains("--quiet"))
                {
                    Console.Error.WriteLine("Error: MCP stdio transport requires --quiet flag.");
                    Console.Error.WriteLine("To avoid unexpected behavior, MCP stdio server cannot run without the --quiet flag.");
                    return InvalidCommandExitCode;
                }

                transport = McpTransportMode.Stdio;
            }
            else if (transportValue.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                transport = McpTransportMode.Http;
            }
            else if (transportValue.Equals("raw_pipe", StringComparison.OrdinalIgnoreCase))
            {
#if WINDOWS || LINUX
                var origColor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine("WARNNING: 'raw_pipe' transport is used internally ONLY. The behave of this transport is not guaranteed to be stable and may change with time.");
                Console.Error.WriteLine("You can ignore this message safely when this client was start by Agent.");
                Console.ForegroundColor = origColor;
#else
                Log("'raw_pipe' transport is used internally ONLY. The behave of this transport is not guaranteed to be stable and may change with time.", "warn");
#endif
                transport = McpTransportMode.RawPipe;
            }
            else
            {
                Console.Error.WriteLine("mcp --transport must be either 'stdio' or 'http'.");
                return InvalidCommandExitCode;
            }

            Uri? httpListenUri = null;
            string? httpListen = GetOption(args, "listen", required: false);
            if (transport == McpTransportMode.Http)
            {
                if (string.IsNullOrWhiteSpace(httpListen) ||
                    !Uri.TryCreate(httpListen, UriKind.Absolute, out httpListenUri))
                {
                    Console.Error.WriteLine("HTTP MCP transport requires --listen=<http[s]://host:port>.");
                    return InvalidCommandExitCode;
                }

                try
                {
                    IntegratedApiServer.ValidateListenUri(httpListenUri);
                }
                catch (ArgumentException ex)
                {
                    Console.Error.WriteLine($"Invalid MCP HTTP listen address: {ex.Message}");
                    return InvalidCommandExitCode;
                }
            }
            else if (!string.IsNullOrWhiteSpace(httpListen))
            {
                Console.Error.WriteLine("mcp --listen is only valid with --transport=http.");
                return InvalidCommandExitCode;
            }

            string? rawPipeName = GetOption(args, "pipe", required: false);
            string? rawPipeParentPid = GetOption(args, "parentPid", required: false);
            if (transport == McpTransportMode.RawPipe)
            {
                if (string.IsNullOrWhiteSpace(rawPipeName))
                {
                    Console.Error.WriteLine("Raw pipe MCP transport requires --pipe=<pipeName>.");
                    return InvalidCommandExitCode;
                }
                if (!string.IsNullOrWhiteSpace(rawPipeParentPid) &&
                    (!int.TryParse(rawPipeParentPid, out int parentPid) || parentPid <= 0))
                {
                    Console.Error.WriteLine("Raw pipe parent PID must be a positive process ID.");
                    return InvalidCommandExitCode;
                }
            }
            else if (!string.IsNullOrWhiteSpace(rawPipeName))
            {
                Console.Error.WriteLine("mcp --pipe is only valid with --transport=raw_pipe.");
                return InvalidCommandExitCode;
            }
            else if (!string.IsNullOrWhiteSpace(rawPipeParentPid))
            {
                Console.Error.WriteLine("mcp --parentPid is only valid with --transport=raw_pipe.");
                return InvalidCommandExitCode;
            }

            Uri? rpcListenUri = null;
            string? rpcToken = GetOption(args, "rpcToken", required: false)
                ?? GetOption(args, "projectServerToken", required: false);
            string globalAssetsDatabasePath = Path.Combine(dataRoot, "My Assets", ".database", "database.json");
            bool startClient = !args.Any(static argument =>
                argument.Equals("--headless", StringComparison.OrdinalIgnoreCase));
            string? rpcListen = GetOption(args, "rpcListen", required: false)
                ?? GetOption(args, "projectServer", required: false);
            if (!string.IsNullOrWhiteSpace(rpcListen))
            {
                if (!Uri.TryCreate(rpcListen, UriKind.Absolute, out rpcListenUri))
                {
                    Console.Error.WriteLine("mcp --rpcListen requires an absolute <http[s]://host:port> address.");
                    return InvalidCommandExitCode;
                }

                try
                {
                    IntegratedApiServer.ValidateListenUri(rpcListenUri);
                    IntegratedApiServer.ValidateRpcToken(rpcToken ?? string.Empty);
                }
                catch (ArgumentException ex)
                {
                    Console.Error.WriteLine($"Invalid MCP RPC server configuration: {ex.Message}");
                    return InvalidCommandExitCode;
                }

            }
            else if (!string.IsNullOrWhiteSpace(rpcToken))
            {
                Console.Error.WriteLine("mcp --rpcToken requires --rpcListen=<http[s]://host:port>.");
                return InvalidCommandExitCode;
            }

            using var cancellation = new CancellationTokenSource();
#if WINDOWS || LINUX
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
#endif

            try
            {
                McpService.RunAsync(new McpServiceOptions
                {
                    ProjectRoot = projectRoot,
                    UserDataRoot = dataRoot,
                    Transport = transport,
                    RawPipeName = rawPipeName,
                    RawPipeParentPid = rawPipeParentPid,
                    HttpListenUri = httpListenUri,
                    RpcListenUri = rpcListenUri,
                    RpcToken = rpcToken,
                    GlobalAssetsDatabasePath = globalAssetsDatabasePath,
                    StartClient = startClient,
                    RenderRuntimeInitializer = _ =>
                    {
                        InitializeRenderRuntime(dataRoot);
                        return ValueTask.CompletedTask;
                    },
                    ClientExited = () =>
                    {
                        try { cancellation.Cancel(); }
                        catch (ObjectDisposedException) { }
                    },
#if WINDOWS
                    ClientExecutable = $"projectFrameCutCompatible_{MauiProgram.AppIdentifier}_{Assembly.GetExecutingAssembly().GetName().Version}.exe",
#elif LINUX
                    ClientExecutable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName,
#endif
                    WarningSink = warning => Console.Error.WriteLine($"Warning: {warning}"),
                    HttpMcpServerStarted = uri =>
                    {
                        Console.Error.WriteLine($"projectFrameCut MCP HTTP server listening at {uri.AbsoluteUri.TrimEnd('/')}/mcp");
                    },
                    RpcServerStarted = uri =>
                    {
                        string serverAddress = uri.AbsoluteUri.TrimEnd('/');
                        Console.Error.WriteLine($"projectFrameCut RPC server listening at {serverAddress}/rpc");
                    },
                }, cancellation.Token).GetAwaiter().GetResult();
                return SuccessExitCode;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return SuccessExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"MCP {transportValue.ToLowerInvariant()} server failed: {ex}");
                return 1;
            }
        }

        #endregion

        #region backend

        private static int RunBackend(string[] args)
        {
            try
            {
                if (args.Any(IsHelpOption))
                {
                    WriteBackendHelp();
                    return SuccessExitCode;
                }

                string listen = GetOption(args, "listen") ?? string.Empty;
                string token = GetOption(args, "token") ?? string.Empty;
                string projectRoot = GetOption(args, "projectRoot") ?? string.Empty;
                string? dataRoot = GetOption(args, "dataRoot", required: false);
                if (string.IsNullOrWhiteSpace(dataRoot))
                {
                    var overridePathFile = Path.Combine(AppDataPath, "OverrideUserDataPath.txt");
                    if (File.Exists(overridePathFile))
                    {
                        dataRoot = File.ReadAllText(overridePathFile).Trim();
                    }
                    else
                    {
                        dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "projectFrameCut");
                    }
                }
                Console.WriteLine($"Using user data dir for assets and cache: {dataRoot}");
                if (!Uri.TryCreate(listen, UriKind.Absolute, out var listenUri))
                    throw new ArgumentException("backend requires an absolute --listen=<http[s]://host:port> address.");

                using var cancellation = new CancellationTokenSource();
#if WINDOWS || LINUX
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cancellation.Cancel();
                };
#endif
                return RunBackendAsync(listenUri, token, projectRoot, dataRoot, cancellation.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return SuccessExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Headless backend failed: {ex}");
                return 1;
            }
        }

        private static async Task<int> RunBackendAsync(
            Uri listenUri,
            string token,
            string projectRoot,
            string dataRoot,
            CancellationToken cancellationToken)
        {
            InitializeRenderRuntime(dataRoot);
            await using var server = new IntegratedApiServer();
            Console.WriteLine($"projectFrameCut backend listening at {listenUri.AbsoluteUri.TrimEnd('/')}/rpc");
            Console.WriteLine("Press Ctrl+C to stop.");
            try
            {
                await server.StartHeadlessAsync(new IntegratedApiServerOptions
                {
                    ListenUri = listenUri,
                    RpcToken = token,
                    ProjectRoot = projectRoot,
                    GlobalAssetsDatabasePath = Path.Combine(dataRoot, "My Assets", ".database", "database.json"),
                    EnableMcp = false,
                    WarningSink = warning => Console.Error.WriteLine($"Warning: {warning}"),
                }, cancellationToken).ConfigureAwait(false);

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return SuccessExitCode;
            }
            finally
            {
                await server.StopAsync().ConfigureAwait(false);
            }
        }

        private static int RunRpcServer(string[] args)
        {
            try
            {
                if (args.Any(IsHelpOption))
                {
                    WriteRpcServerHelp();
                    return SuccessExitCode;
                }
                var pipe = GetOption(args, "pipe") ?? string.Empty;
                var token = GetOption(args, "token") ?? string.Empty;
                var parentPid = GetOption(args, "parentPid", required: false);
                var dataRoot = GetOption(args, "dataRoot");
                if (string.IsNullOrWhiteSpace(pipe) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(dataRoot))
                {
                    Console.Error.WriteLine("rpc_server requires --pipe=<pipe-name> and --token=<token> and --dataRoot=<path>.");
                    WriteRpcServerHelp();
                    return InvalidCommandExitCode;
                }

                Uri? httpListenUri = null;
                string? httpToken = null;
                // --projectRoot is accepted regardless of --http. Only the optional
                // HTTP RPC server consumes it: it preloads the project so clients do
                // not hit a "server has no project" error. Without it the HTTP server
                // starts with no preloaded project and clients open projects on demand.
                string? httpProjectRoot = GetOption(args, "projectRoot", required: false);
                var httpListen = GetOption(args, "http", required: false);
                if (!string.IsNullOrWhiteSpace(httpListen))
                {
                    if (!Uri.TryCreate(httpListen, UriKind.Absolute, out httpListenUri))
                    {
                        Console.Error.WriteLine("rpc_server --http requires an absolute <http[s]://host:port> listen address.");
                        WriteRpcServerHelp();
                        return InvalidCommandExitCode;
                    }

                    httpToken = GetOption(args, "httpToken", required: false);
                    if (string.IsNullOrWhiteSpace(httpToken)) httpToken = token;
                    try
                    {
                        IntegratedApiServer.ValidateRpcToken(httpToken);
                    }
                    catch (ArgumentException)
                    {
                        Console.Error.WriteLine("rpc_server --http requires an RPC token with at least 32 non-whitespace characters; supply a longer --token or a separate --httpToken=<token>.");
                        return InvalidCommandExitCode;
                    }
                }

                using var cancellation = new CancellationTokenSource();
#if WINDOWS || LINUX
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cancellation.Cancel();
                };
#endif
                return RunRpcServerAsync(pipe, token, parentPid, dataRoot, httpListenUri, httpToken, httpProjectRoot, cancellation.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return SuccessExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Render RPC server failed: {ex}");
                return 1;
            }
        }

        private static async Task<int> RunRpcServerAsync(
            string pipe,
            string token,
            string? parentPid,
            string dataRoot,
            Uri? httpListenUri,
            string? httpToken,
            string? httpProjectRoot,
            CancellationToken cancellationToken)
        {
            InitializeRenderRuntime(dataRoot);
            // The named-pipe server and the optional HTTP RPC endpoint share this
            // render backend, so sessions opened through either channel are
            // visible to clients of the other one.
            await using var service = new RenderBackendService(
                stateRoot: dataRoot,
                completionSink: RenderCompletionNotifier.Notify,
                progressSink: RenderCompletionNotifier.NotifyProgress);
            await using var httpHost = httpListenUri is null
                ? null
                : await StartHttpRpcServerAsync(service, httpListenUri, httpToken!, httpProjectRoot, dataRoot, cancellationToken).ConfigureAwait(false);
            await new NamedPipeRenderServer(service).RunAsync(pipe, token, parentPid, cancellationToken).ConfigureAwait(false);
            return SuccessExitCode;
        }

        private static async Task<IAsyncDisposable> StartHttpRpcServerAsync(
            RenderBackendService renderService,
            Uri listenUri,
            string token,
            string? projectRoot,
            string dataRoot,
            CancellationToken cancellationToken)
        {
            var headlessService = new HeadlessProjectService(
                renderService,
                Path.Combine(dataRoot, "My Assets", ".database", "database.json"));
            var server = new IntegratedApiServer();
            try
            {
                await server.StartHeadlessAsync(new IntegratedApiServerOptions
                {
                    ListenUri = listenUri,
                    RpcToken = token,
                    ProjectRoot = projectRoot,
                    EnableMcp = false,
                    WarningSink = warning => Console.Error.WriteLine($"Warning: {warning}"),
                }, headlessService, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                await headlessService.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            Console.WriteLine($"projectFrameCut HTTP RPC server listening at {listenUri.AbsoluteUri.TrimEnd('/')}/rpc");
            return new HttpRpcServerHost(server, headlessService);
        }

        /// <summary>
        /// Owns the HTTP RPC server and its headless project service. The shared
        /// render backend is deliberately left alive; the caller disposes it.
        /// </summary>
        private sealed class HttpRpcServerHost(IntegratedApiServer server, HeadlessProjectService headlessService) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await server.DisposeAsync().ConfigureAwait(false);
                await headlessService.DisposeAsync().ConfigureAwait(false);
            }
        }

        #endregion

        #region render

        private static int RunRender(string[] args)
        {
            try
            {
                if (args.Length == 0 || args.Any(IsHelpOption))
                {
                    WriteRenderHelp();
                    return SuccessExitCode;
                }

                var switches = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var arg in args)
                {
                    var pair = arg.Split('=', 2);
                    if (pair.Length == 2) switches[pair[0].TrimStart('-', '/')] = pair[1];
                }

                var dataRoot = switches.TryGetValue("assetDbFile", out var db)
                    ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(db) ?? Environment.CurrentDirectory, "..", ".."))
                    : AppDataPath;
                if (TryGetRenderRpcOptions(switches, out var rpcOptions))
                    return RunRpcRenderAsync(switches, dataRoot, rpcOptions).GetAwaiter().GetResult();

                ConfigureHardwareAcceleration(switches);
                InitializeCliRenderRuntime(dataRoot, switches.GetValueOrDefault("FFmpegLibraryPath", string.Empty));
                return RunRenderPipelineAsync(switches, cancellationToken: default).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { return 255; }
            catch (Exception ex)
            {
                Log(ex, "CLI render failed");
                return 1;
            }
        }

        private static void InitializeCliRenderRuntime(string dataRoot, string ffmpegRoot = "")
        {
            InitializeRenderRuntime(dataRoot, ffmpegRoot);
#if WINDOWS || LINUX
            AcceleratorsManager.IsRendering = true;
            if (!AcceleratorsManager.Accelerators.Any())
                throw new InvalidOperationException("No valid rendering accelerator is available.");
#endif
        }

        private static async Task<int> RunRenderPipelineAsync(
            ConcurrentDictionary<string, string> switches,
            CancellationToken cancellationToken,
            Action<double, TimeSpan, double>? progress = null,
            Action<string>? stageChanged = null)
        {
            if (!switches.TryGetValue("project", out var projectRoot) || !Directory.Exists(projectRoot))
                throw new DirectoryNotFoundException("-project must point to a project directory.");
            if (!switches.TryGetValue("output", out var outputPath) || string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("-output is required.");
            if (!switches.TryGetValue("output_options", out var outputSpec))
                throw new ArgumentException("-output_options is required.");
            if (!switches.TryGetValue("temp_path", out var tempPath))
                tempPath = Path.GetTempPath();

            var output = outputSpec.Split(',', StringSplitOptions.TrimEntries);
            if (output.Length != 5 || !int.TryParse(output[0], out var width) || !int.TryParse(output[1], out var height) || !int.TryParse(output[2], out var fps))
                throw new ArgumentException("-output_options must be width,height,fps,pixel format,encoder.");
            if (!Enum.TryParse(output[3], true, out AVPixelFormat pixelFormat) || pixelFormat == AVPixelFormat.AV_PIX_FMT_NONE)
                throw new ArgumentException($"Unknown pixel format '{output[3]}'.");

            outputPath = outputPath.Replace("{CurrentTime}", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var bpp = FFmpegHelper.GetAVPixelFormatBitsPerPixel(pixelFormat) > 8
                ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture;
            var jsonOptions = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
            var projectFile = File.Exists(Path.Combine(projectRoot, "project.pjfc")) ? "project.pjfc" : "project.json";
            var project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(projectRoot, projectFile)), jsonOptions) ?? new();
            var timeline = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(projectRoot, "timeline.json")), jsonOptions) ?? new();

            var assets = new ConcurrentDictionary<string, AssetItem>();
            if (switches.TryGetValue("assetDbFile", out var assetDb) && File.Exists(assetDb))
                assets = JsonSerializer.Deserialize<ConcurrentDictionary<string, AssetItem>>(File.ReadAllText(assetDb), jsonOptions) ?? assets;
            if (File.Exists(Path.Combine(projectRoot, "assets.json")))
            {
                var localAssets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(projectRoot, "assets.json")), jsonOptions) ?? [];
                foreach (var asset in localAssets.Where(a => !string.IsNullOrWhiteSpace(a.AssetId))) assets[asset.AssetId!] = asset;
            }
            AssetDatabase.Assets = assets;
            DecoderContextPJFCProject.GlobalAssetGetter = new(() => assets);
            Environment.CurrentDirectory = projectRoot;

            var target = switches.GetValueOrDefault("target", "all").ToLowerInvariant();
            var gcOption = int.TryParse(switches.GetValueOrDefault("GCOptions", "0"), out var gc) ? Math.Clamp(gc, 0, 2) : 0;
            if (gcOption == 2) GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            var serial = bool.TryParse(switches.GetValueOrDefault("oneByOneRender", "false"), out var one) && one;
            var renderByLayers = bool.TryParse(switches.GetValueOrDefault("renderByLayer", "false"), out var layers) && layers;
            var prepareInWorkers = bool.TryParse(switches.GetValueOrDefault("prepareInWorker", "false"), out var prepare) && prepare;
            var affinity = bool.TryParse(switches.GetValueOrDefault("enableThreadAffinity", "true"), out var threadAffinity) && threadAffinity;
            var maxThreads = int.TryParse(switches.GetValueOrDefault("maxParallelThreads", Environment.ProcessorCount.ToString()), out var mt) ? Math.Max(1, mt) : Environment.ProcessorCount;
            var duration = Math.Max(timeline.Duration, timeline.AudioDuration);
            var chunkOptions = ParseChunkRenderOptions(switches, fps);
            long? requestedBitRate = switches.TryGetValue("bitRate", out var bitRateText)
                && long.TryParse(bitRateText, out var parsedBitRate) && parsedBitRate > 0
                    ? parsedBitRate
                    : null;
            using var consoleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
#if WINDOWS || LINUX
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; consoleCancellation.Cancel(); };
#endif
            IVideoWriter CreateConfiguredWriter(string path, bool intermediateChunk)
            {
                IVideoWriter writer = PluginManager.CreateVideoWriter(output[4]);
                writer.Width = width;
                writer.Height = height;
                writer.FramePerSecond = fps;
                writer.PixelFormat = pixelFormat.ToString();
                writer.OutputPath = path;
                if (string.IsNullOrWhiteSpace(writer.CodecName)) writer.CodecName = output[4];
                if (requestedBitRate.HasValue) writer.BitRate = requestedBitRate.Value;
                if (intermediateChunk)
                {
                    writer.BitRate = Math.Max(writer.BitRate, CalculateIntermediateBitRate(width, height, fps, bpp));
                    writer.PreferToSpeed = true;
                }
                return writer;
            }

            IVideoSource CreateChunkVideoSource(string path)
            {
                if (AlphaBrightnessDecoderContext.IsAlphaBrightnessVideo(path)) return new AlphaBrightnessDecoderContext(path);
                if (HDRDecoderContext.IsHdrVideo(path)) return new HDRDecoderContext(path);
                return bpp == IPicture.PicturePixelMode.UShortPicture
                    ? new DecoderContext16Bit(path)
                    : new DecoderContext8Bit(path);
            }

            async Task RenderVideoRange(
                string path,
                uint startFrame,
                uint frameCount,
                int renderThreads,
                Action<double, TimeSpan, double>? rangeProgress = null,
                bool writeToVoid = false,
                int chunkIndex = 0,
                int chunkCount = 1,
                bool intermediateChunk = false)
            {
                var clips = DraftImportAndExportHelper.JSONToIClips(timeline, false, bpp).Where(c => c.ClipType != ClipMode.AudioClip).ToArray();
                if (clips.Length == 0) throw new InvalidOperationException("No video clips in the project.");
                foreach (var clip in clips) clip.ReInit(bpp);
                if (!writeToVoid)
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
                var builder = writeToVoid
                    ? null
                    : new VideoBuilder(CreateConfiguredWriter(path, intermediateChunk))
                    {
                        EnablePreview = !chunkOptions.Enabled || chunkOptions.Parallelism == 1,
                        minFrameCountToGeneratePreview = 60,
                        PreviewPath = switches.GetValueOrDefault("preview_path"),
                        StartFrame = startFrame,
                        Duration = frameCount,
                        BlockWrite = serial,
                        DoGCAfterEachWrite = gcOption > 0,
                        DisposeFrameAfterEachWrite = true
                    };
                var renderer = new Renderer
                {
                    builder = builder,
                    Clips = clips,
                    TargetWidth = width,
                    TargetHeight = height,
                    StartFrame = startFrame,
                    Duration = frameCount,
                    ChunkIndex = chunkIndex,
                    ChunkCount = Math.Max(1, chunkCount),
                    CompletedFramesBeforeChunk = startFrame,
                    TotalProjectFrames = duration,
                    Use16Bit = bpp == IPicture.PicturePixelMode.UShortPicture,
                    GCOption = gcOption,
                    MaxThreads = Math.Max(1, renderThreads),
                    OneByOneRender = serial,
                    RenderByLayers = renderByLayers,
                    PrepareInWorkerThreads = prepareInWorkers,
                    EnableThreadAffinity = affinity,
                    MinSchedulePreparedFrames = 1
                };
                renderer.OnProgressChanged += (value, eta) =>
                {
                    rangeProgress?.Invoke(value, eta, renderer.CurrentFps);
                };
                try
                {
                    builder?.Build()?.Start();
                    renderer.PrepareRender(consoleCancellation.Token);
                    await renderer.GoRender(consoleCancellation.Token).ConfigureAwait(false);
                    consoleCancellation.Token.ThrowIfCancellationRequested();
                    if (builder is not null)
                    {
                        if (serial)
                        {
                            builder.Writer?.Finish();
                            builder.Dispose();
                        }
                        else builder.Finish(
                            i => Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, i, width, height), i, width, height),
                            checked(startFrame + frameCount),
                            (_, _) => { });
                    }
                }
                catch
                {
                    if (builder is { Disposed: false }) builder.Interrupt();
                    throw;
                }
                finally
                {
                    foreach (var clip in clips) clip.Dispose();
                    renderer.builder = null;
                    Console.WriteLine();
                }
            }

            async Task<ChunkRenderCoordinator?> RenderVideo(string path, bool publishChunkedResult = true, bool writeToVoid = false)
            {
                if (!chunkOptions.Enabled || writeToVoid)
                {
                    await RenderVideoRange(path, 0, duration, maxThreads,
                        (value, eta, currentFps) => progress?.Invoke(target == "all" ? value * 0.9 : value, eta, currentFps),
                        writeToVoid).ConfigureAwait(false);
                    return null;
                }
                int lastChunkCount = 0;
                var coordinator = new ChunkRenderCoordinator(
                    projectRoot,
                    duration,
                    fps,
                    Path.GetExtension(path),
                    $"{width}x{height}|{fps}|{pixelFormat}|{output[4]}|bpp={bpp}|bitrate={requestedBitRate}|serial={serial}|layers={renderByLayers}|prepare={prepareInWorkers}|assetDb={GetFileFingerprintPart(switches.GetValueOrDefault("assetDbFile"))}",
                    maxThreads,
                    chunkOptions);
                await coordinator.InitializeAsync(consoleCancellation.Token).ConfigureAwait(false);
                await coordinator.RenderPendingChunksAsync(
                    (segment, chunkPath, chunkThreads, report, token) =>
                        RenderVideoRange(chunkPath, segment.StartFrame, segment.Duration, chunkThreads, report, chunkIndex: segment.Index, chunkCount: coordinator.ChunkCount, intermediateChunk: true),
                    state =>
                    {
                        double value = state.GlobalProgress * (target == "all" ? 0.8 : 0.85);
                        progress?.Invoke(value, state.EstimatedRemaining, state.FramesPerSecond);
                        if (lastChunkCount != state.ChunkCount)
                        {
                            lastChunkCount = state.ChunkCount;
                            Console.WriteLine($"Rendering finished {state.ChunkCount} chunk(s).");
                        }
                    },
                    consoleCancellation.Token).ConfigureAwait(false);
                stageChanged?.Invoke("MergeChunks");
                string merged = await coordinator.MergeAsync(
                    path => CreateConfiguredWriter(path, intermediateChunk: false),
                    CreateChunkVideoSource,
                    (mergeProgress, mergeEta) => progress?.Invoke(
                        (target == "all" ? 0.8 : 0.85) + mergeProgress * (target == "all" ? 0.1 : 0.15),
                        mergeEta,
                        0),
                    consoleCancellation.Token).ConfigureAwait(false);
                if (publishChunkedResult)
                    await ChunkRenderCoordinator.PublishAsync(merged, path, consoleCancellation.Token).ConfigureAwait(false);
                return coordinator;
            }

            void RenderAudio(string path)
            {
                var clips = DraftImportAndExportHelper.JSONToIClips(timeline, false, IPicture.PicturePixelMode.BytePicture).Where(c => c.ClipType is ClipMode.AudioClip or ClipMode.VideoClip).ToArray();
                var tracks = DraftImportAndExportHelper.JSONToISoundTracks(timeline).ToArray();
                if (clips.Length == 0 && tracks.Length == 0) return;
                foreach (var clip in clips) clip.ReInit(IPicture.PicturePixelMode.BytePicture);
                foreach (var track in tracks) track.ReInit();
                using var writer = new AudioWriter(path, 96000, 2, "pcm_s16le");
                new AudioComposer<float> { Clips = clips, SoundTracks = tracks, Writer = writer }.Compose(fps, 96000, 2, 4096, consoleCancellation.Token);
                writer.Finish();
                foreach (var clip in clips) clip.Dispose();
                foreach (var track in tracks) track.Dispose();
            }

            switch (target)
            {
                case "video":
                    {
                        var chunkJob = await RenderVideo(outputPath);
                        if (chunkJob is not null && !chunkOptions.KeepChunkFiles) chunkJob.Cleanup();
                        break;
                    }
                case "void": await RenderVideo(outputPath, writeToVoid: true); break;
                case "audio": RenderAudio(outputPath); break;
                case "all":
                    var video = Path.Combine(tempPath, $"{project.ProjectName}_Intermediate_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(outputPath)}");
                    var audio = Path.Combine(tempPath, $"{project.ProjectName}_Intermediate_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                    var allChunkJob = await RenderVideo(video, publishChunkedResult: !chunkOptions.Enabled);
                    if (allChunkJob is not null) video = allChunkJob.MergedPath;
                    progress?.Invoke(0.92, TimeSpan.Zero, 0);
                    stageChanged?.Invoke("ComposeAudio");
                    RenderAudio(audio);
                    progress?.Invoke(0.97, TimeSpan.Zero, 0);
                    stageChanged?.Invoke("FinalEncoding");
                    if (File.Exists(audio))
                    {
                        if (allChunkJob is null)
                        {
                            VideoAudioMuxer.MuxFromFiles(video, audio, outputPath, true);
                        }
                        else
                        {
                            string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? Environment.CurrentDirectory;
                            string atomicOutput = Path.Combine(outputDirectory, $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");
                            try
                            {
                                VideoAudioMuxer.MuxFromFiles(video, audio, atomicOutput, true);
                                File.Move(atomicOutput, outputPath, overwrite: true);
                            }
                            finally
                            {
                                if (File.Exists(atomicOutput)) File.Delete(atomicOutput);
                            }
                        }
                    }
                    else
                        await ChunkRenderCoordinator.PublishAsync(video, outputPath, consoleCancellation.Token).ConfigureAwait(false);
                    if (allChunkJob is null) File.Delete(video);
                    File.Delete(audio);
                    if (allChunkJob is not null && !chunkOptions.KeepChunkFiles) allChunkJob.Cleanup();
                    break;
                default: throw new ArgumentException($"Unknown target '{target}'.");
            }
            Environment.SetEnvironmentVariable("projectFrameCut_LastOutput", outputPath, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("projectFrameCut_RenderFinished", "1", EnvironmentVariableTarget.Process);
            return 0;
        }

        private static ChunkRenderOptions ParseChunkRenderOptions(ConcurrentDictionary<string, string> switches, int frameRate)
        {
            bool enabled = bool.TryParse(switches.GetValueOrDefault("chunkRender", "false"), out var chunkRender) && chunkRender;
            bool resume = !bool.TryParse(switches.GetValueOrDefault("chunkResume", "true"), out var chunkResume) || chunkResume;
            bool keepFiles = bool.TryParse(switches.GetValueOrDefault("chunkKeepFiles", "false"), out var chunkKeepFiles) && chunkKeepFiles;
            int parallelism = int.TryParse(switches.GetValueOrDefault("chunkParallelism", "1"), out var parsedParallelism)
                ? Math.Max(1, parsedParallelism)
                : 1;
            uint? frames = null;
            double? seconds = null;
            if (switches.TryGetValue("chunkFrames", out var chunkFramesText))
            {
                if (!uint.TryParse(chunkFramesText, out var parsedFrames) || parsedFrames == 0)
                    throw new ArgumentException("-chunkFrames must be a positive integer.");
                frames = parsedFrames;
            }
            if (switches.TryGetValue("chunkSeconds", out var chunkSecondsText))
            {
                if (!double.TryParse(chunkSecondsText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedSeconds)
                    || parsedSeconds <= 0 || double.IsNaN(parsedSeconds) || double.IsInfinity(parsedSeconds))
                    throw new ArgumentException("-chunkSeconds must be a positive finite number.");
                seconds = parsedSeconds;
            }
            if (frames.HasValue && seconds.HasValue)
                throw new ArgumentException("Specify only one of -chunkFrames and -chunkSeconds.");
            if (enabled && !frames.HasValue && !seconds.HasValue)
                frames = checked((uint)Math.Max(1, frameRate) * 60u);
            return new ChunkRenderOptions
            {
                Enabled = enabled,
                ChunkFrames = frames,
                ChunkSeconds = seconds,
                Parallelism = parallelism,
                Resume = resume,
                KeepChunkFiles = keepFiles
            };
        }

        private static long CalculateIntermediateBitRate(
            int width,
            int height,
            int frameRate,
            IPicture.PicturePixelMode pixelMode)
        {
            double bitsPerPixel = pixelMode == IPicture.PicturePixelMode.UShortPicture ? 0.5 : 0.3;
            double estimate = (double)width * height * frameRate * bitsPerPixel;
            return (long)Math.Clamp(estimate, 12_000_000d, 300_000_000d);
        }

        private static string GetFileFingerprintPart(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
            var info = new FileInfo(path);
            return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }

        private static void ConfigureHardwareAcceleration(ConcurrentDictionary<string, string> switches)
        {
            bool hwAccelDecode = bool.TryParse(
                switches.GetValueOrDefault("preferHwAccelDecoder", "false"),
                out var hwAccelDecodeValue) && hwAccelDecodeValue;
            bool hwAccelEncode = bool.TryParse(
                switches.GetValueOrDefault("preferHwAccelEncoder", "false"),
                out var hwAccelEncodeValue)
                && hwAccelEncodeValue
                && !OperatingSystem.IsAndroid()
                && !OperatingSystem.IsIOS();

            InternalPluginBase.HWAccelDecodeOptionGetter = new(() => hwAccelDecode);
            InternalPluginBase.HWAccelEncodeOptionGetter = new(() => hwAccelEncode);
        }

        private static bool TryGetRenderRpcOptions(
            ConcurrentDictionary<string, string> switches,
            out CliRenderRpcOptions options)
        {
            options = default;
            var transport = "named-pipe";
            if (!switches.TryGetValue("rpcPipe", out var endpoint) || string.IsNullOrWhiteSpace(endpoint))
            {
                if (!switches.TryGetValue("rpcSocket", out endpoint) || string.IsNullOrWhiteSpace(endpoint)) return false;
                transport = "unix-socket";
            }
            if (!switches.TryGetValue("rpcToken", out var token) || string.IsNullOrWhiteSpace(token)) return false;
            if (!switches.TryGetValue("jobId", out var jobText) || !Guid.TryParse(jobText, out var jobId)) return false;
            options = new CliRenderRpcOptions(endpoint, token, jobId, transport);
            return true;
        }

        private static async Task<int> RunRpcRenderAsync(
            ConcurrentDictionary<string, string> switches,
            string dataRoot,
            CliRenderRpcOptions options)
        {
            ConfigureHardwareAcceleration(switches);
            var projectRoot = switches.GetValueOrDefault("project", string.Empty);
            var outputPath = switches.GetValueOrDefault("output", string.Empty);
            var projectName = switches.GetValueOrDefault("projectName", Path.GetFileName(projectRoot));
            var background = bool.TryParse(switches.GetValueOrDefault("background", "false"), out var parsedBackground) && parsedBackground;
            using var renderCancellation = new CancellationTokenSource();
            using var serverCancellation = new CancellationTokenSource();
            var service = new CliRenderJobRpcService(
                options.JobId, projectRoot, projectName, outputPath, background,
                dataRoot, renderCancellation, serverCancellation);
            var serverTask = options.Transport == "unix-socket"
                ? new UnixSocketRenderServer(service).RunAsync(options.Endpoint, options.Token, serverCancellation.Token)
                : new NamedPipeRenderServer(service).RunAsync(options.Endpoint, options.Token, cancellationToken: serverCancellation.Token);

            var exitCode = await service.RunAsync(async (progress, stageChanged, cancellationToken) =>
            {
                InitializeCliRenderRuntime(dataRoot, switches.GetValueOrDefault("FFmpegLibraryPath", string.Empty));
                return await RunRenderPipelineAsync(switches, cancellationToken, progress, stageChanged).ConfigureAwait(false);
            }).ConfigureAwait(false);

            // Give the GUI enough time to observe the terminal state. A foreground
            // client asks the service to stop immediately after receiving it; a
            // detached background render exits on its own after the grace period.
            try { await Task.Delay(TimeSpan.FromSeconds(30), serverCancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            serverCancellation.Cancel();
            try { await serverTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            return exitCode;
        }

        private readonly record struct CliRenderRpcOptions(string Endpoint, string Token, Guid JobId, string Transport);

        private sealed class CliRenderJobRpcService : IRenderService
        {
            private static readonly long ProgressPublishIntervalTicks = Math.Max(1, Stopwatch.Frequency / 4);
            private readonly object _gate = new();
            private readonly object _persistGate = new();
            private readonly string _statePath;
            private readonly CancellationTokenSource _renderCancellation;
            private readonly CancellationTokenSource _serverCancellation;
            private RenderJob _job;
            private long _nextProgressPublishTimestamp;
            private long _jobRevision;
            private long _lastPersistedRevision;
            private int _progressPublishInProgress;

            public CliRenderJobRpcService(
                Guid jobId, string projectRoot, string projectName, string outputPath,
                bool background, string dataRoot, CancellationTokenSource renderCancellation,
                CancellationTokenSource serverCancellation)
            {
                _renderCancellation = renderCancellation;
                _serverCancellation = serverCancellation;
                _statePath = Path.Combine(dataRoot, "RenderJobs", $"cli-{jobId:N}.json");
                _job = new RenderJob
                {
                    JobId = jobId,
                    State = RenderJobState.Queued,
                    ProjectRoot = projectRoot,
                    ProjectName = projectName,
                    OutputPath = outputPath,
                    Background = background,
                    Stage = "Render",
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                };
                _jobRevision = 1;
                Persist(CaptureForPersistence());
            }

            public async Task<int> RunAsync(Func<Action<double, TimeSpan, double>, Action<string>, CancellationToken, Task<int>> render)
            {
                Update(job => job.State = RenderJobState.Running);
                PublishProgress(force: true);
                try
                {
                    var result = await render((progress, eta, fps) =>
                    {
                        if (!TryReserveProgressPublish()) return;
                        if (!UpdateProgress(progress, eta, fps)) return;
                        QueueProgressPublish();
                    }, UpdateStage, _renderCancellation.Token).ConfigureAwait(false);
                    if (result != 0) throw new InvalidOperationException($"CLI renderer exited with code {result}.");
                    Update(job =>
                    {
                        job.State = RenderJobState.Completed;
                        job.Progress = 1;
                        job.EstimatedRemainingTicks = 0;
                    });
                    PublishCompletion();
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    Update(job => job.State = RenderJobState.Canceled);
                    PublishCompletion();
                    return 255;
                }
                catch (Exception ex)
                {
                    Log(ex, $"CLI render job {_job.JobId}");
                    Update(job =>
                    {
                        job.State = RenderJobState.Failed;
                        job.Error = new RemoteError(ex);
                    });
                    PublishCompletion();
                    return 1;
                }
            }

            public ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
            {
                if (request.ProtocolVersion < RenderProtocol.MinimumSupportedVersion || request.ProtocolVersion > RenderProtocol.CurrentVersion)
                    return ValueTask.FromResult(Failure(request, RenderErrorCode.ProtocolMismatch, $"Unsupported render protocol version {request.ProtocolVersion}."));

                try
                {
                    var response = request.Operation switch
                    {
                        RenderOperation.GetCapabilities => Success(request, new RenderCapabilities
                        {
                            ProtocolVersion = RenderProtocol.CurrentVersion,
                            MinimumProtocolVersion = RenderProtocol.MinimumSupportedVersion,
                            BackendVersion = typeof(CLIProgram).Assembly.GetName().Version?.ToString() ?? "unknown",
                            Operations = [nameof(RenderOperation.GetCapabilities), nameof(RenderOperation.GetJobStatus), nameof(RenderOperation.CancelJob), nameof(RenderOperation.ListRenderJobs), nameof(RenderOperation.CloseProject)],
                            Features = ["cli-render", "named-pipe", "render-jobs"],
                        }),
                        RenderOperation.GetJobStatus => GetJobResponse(request),
                        RenderOperation.CancelJob => CancelJobResponse(request),
                        RenderOperation.ListRenderJobs => Success(request, new List<RenderJob> { Snapshot() }),
                        RenderOperation.CloseProject => CloseResponse(request),
                        _ => Failure(request, RenderErrorCode.Unsupported, $"Operation '{request.Operation}' is not supported by a CLI render worker."),
                    };
                    return ValueTask.FromResult(response);
                }
                catch (Exception ex)
                {
                    return ValueTask.FromResult(Failure(request, RenderErrorCode.BackendFailure, ex));
                }
            }

            private RenderResponseEnvelope GetJobResponse(RenderRequestEnvelope request)
            {
                var requested = RenderRpcSerializer.Deserialize<JobRequest>(request.Payload);
                return requested.JobId == _job.JobId
                    ? Success(request, Snapshot())
                    : Failure(request, RenderErrorCode.SessionNotFound, $"Render job '{requested.JobId}' was not found.");
            }

            private RenderResponseEnvelope CancelJobResponse(RenderRequestEnvelope request)
            {
                var requested = RenderRpcSerializer.Deserialize<JobRequest>(request.Payload);
                if (requested.JobId != _job.JobId)
                    return Failure(request, RenderErrorCode.SessionNotFound, $"Render job '{requested.JobId}' was not found.");
                _renderCancellation.Cancel();
                return Success(request, Snapshot());
            }

            private RenderResponseEnvelope CloseResponse(RenderRequestEnvelope request)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    _serverCancellation.Cancel();
                });
                return Success(request, new EmptyResponse());
            }

            private void Update(Action<RenderJob> update)
            {
                lock (_gate)
                {
                    update(_job);
                    _job.UpdatedAtUtc = DateTime.UtcNow;
                    _jobRevision++;
                }
            }

            private bool UpdateProgress(double progress, TimeSpan eta, double fps)
            {
                lock (_gate)
                {
                    if (_job.State != RenderJobState.Running) return false;
                    _job.Progress = Math.Clamp(progress, 0, 1);
                    _job.EstimatedRemainingTicks = Math.Max(0, eta.Ticks);
                    _job.CurrentFps = Math.Max(0, fps);
                    _job.UpdatedAtUtc = DateTime.UtcNow;
                    _jobRevision++;
                    return true;
                }
            }

            private void UpdateStage(string stage)
            {
                Update(job => job.Stage = stage);
                PublishProgress(force: true);
            }

            private RenderJob Snapshot()
            {
                lock (_gate) return RenderRpcSerializer.Clone(_job);
            }

            private PersistedJobSnapshot CaptureForPersistence()
            {
                lock (_gate) return new(RenderRpcSerializer.Clone(_job), _jobRevision);
            }

            private bool TryReserveProgressPublish()
            {
                var now = Stopwatch.GetTimestamp();
                var next = Volatile.Read(ref _nextProgressPublishTimestamp);
                if (now < next) return false;
                return Interlocked.CompareExchange(
                    ref _nextProgressPublishTimestamp,
                    now + ProgressPublishIntervalTicks,
                    next) == next;
            }

            private void Persist(PersistedJobSnapshot snapshot)
            {
                lock (_persistGate)
                {
                    // A slower, older write must never overwrite a newer terminal
                    // state when progress callbacks overlap on render workers.
                    if (snapshot.Revision < _lastPersistedRevision) return;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                        var temp = _statePath + ".tmp";
                        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot.Job));
                        File.Move(temp, _statePath, overwrite: true);
                        _lastPersistedRevision = snapshot.Revision;
                    }
                    catch (Exception ex) { Log(ex, "Persist CLI render job"); }
                }
            }

            private void PublishProgress(bool force)
            {
                var snapshot = CaptureForPersistence();
                Persist(snapshot);
                try { RenderCompletionNotifier.NotifyProgress(snapshot.Job, force); }
                catch (Exception ex) { Log(ex, "CLI render progress notification"); }
            }

            private void QueueProgressPublish()
            {
                if (Interlocked.CompareExchange(ref _progressPublishInProgress, 1, 0) != 0) return;
                _ = Task.Run(() =>
                {
                    try { PublishProgress(force: false); }
                    catch (Exception ex) { Log(ex, "Publish CLI render progress"); }
                    finally { Volatile.Write(ref _progressPublishInProgress, 0); }
                });
            }

            private void PublishCompletion()
            {
                var snapshot = CaptureForPersistence();
                Persist(snapshot);
                try { RenderCompletionNotifier.Notify(snapshot.Job); }
                catch (Exception ex) { Log(ex, "CLI render completion notification"); }
            }

            private readonly record struct PersistedJobSnapshot(RenderJob Job, long Revision);

            private static RenderResponseEnvelope Success<T>(RenderRequestEnvelope request, T payload) => new()
            {
                RequestId = request.RequestId,
                Payload = RenderRpcSerializer.Serialize(payload),
            };

            private static RenderResponseEnvelope Failure(RenderRequestEnvelope request, RenderErrorCode code, string message, string details = "") => new()
            {
                RequestId = request.RequestId,
                Error = new RemoteError { Code = code, Message = message, Details = details },
            };

            private static RenderResponseEnvelope Failure(RenderRequestEnvelope request, RenderErrorCode code, Exception exception) => new()
            {
                RequestId = request.RequestId,
                Error = new RemoteError(exception, code),
            };
        }

        #endregion

        #region misc

        internal static void InitializeRenderRuntime(string dataRoot, string ffmpegRoot = "")
        {
            if (!PluginManager.Inited)
            {
                try { GlobalPluginHelper.PluginsDataRootPath = dataRoot; PluginManager.InitGlobalGetter(); } catch (InvalidOperationException) { }
                PluginManager.Init(
                [
                    new InternalPluginBase(),
                    new projectFrameCut.Render.HwAccelEngine.HwAccelEnginePlugin(),
                ]);
            }
            if (!ffmpeg.Ready)
            {
                try
                {
                    FFmpeg.AutoGen.DynamicallyLoadedBindings.EnableAutoInitialization = false;

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(ffmpegRoot) && Directory.Exists(ffmpegRoot))
                        {
                            ffmpeg.RootPath = ffmpegRoot;
                            Log($"Using FFmpeg libraries from command line argument, path:{ffmpegRoot}");
                        }
                        if (SettingsManager.Settings?.Any() ?? false && SettingsManager.IsBoolSettingTrue("PluginProvidedFFmpeg_Enable"))
                        {

#if WINDOWS
                            string? nativeLibDirOverride = null;
                            var pluginId = SettingsManager.GetSetting("PluginProvidedFFmpeg_PluginID", "");
                            if (pluginId == "external")
                            {
                                var ffmpegPath = SettingsManager.GetSetting("PluginProvidedFFmpeg_LibPath", "");
                                if (!string.IsNullOrWhiteSpace(ffmpegPath) && Directory.Exists(ffmpegPath))
                                {
                                    Log($"Using external FFmpeg libraries, path:{ffmpegPath}");
                                    nativeLibDirOverride = ffmpegPath;
                                }
                                else
                                {
                                    Log($"PluginProvidedFFmpeg_Enable is true, but invalid path provided:{ffmpegPath}");
                                }
                            }
                            else if (!PluginManager.LoadedPlugins.TryGetValue(pluginId, out var value))
                            {
                                Log($"PluginProvidedFFmpeg_Enable is true, but plugin {pluginId} is not loaded.");
                            }
                            else
                            {
                                var ffmpegPath = Path.Combine(AppDataPath, "Plugins", value.PluginID, "FFmpeg", "windows");
                                if (!string.IsNullOrWhiteSpace(ffmpegPath) && Directory.Exists(ffmpegPath))
                                {
                                    Log($"Using FFmpeg libraries provided by plugin {pluginId}, path:{ffmpegPath}");
                                    nativeLibDirOverride = ffmpegPath;
                                }
                                else
                                {
                                    Log($"PluginProvidedFFmpeg_Enable is true, but plugin {pluginId} provided invalid path:{ffmpegPath}");
                                }
                            }
                            if (!string.IsNullOrWhiteSpace(nativeLibDirOverride) && Directory.Exists(nativeLibDirOverride))
                            {
                                ffmpeg.RootPath = nativeLibDirOverride;
                            }
                            else
                            {
                                ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "FFmpeg", "8.x_internal");
                            }
#elif ANDROID
                            ffmpeg.RootPath = Path.Combine(FileSystem.AppDataDirectory, "ffmpeg_plugin_libs");
#endif
                        }
                        else
                        {
                            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                            {
                                ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "FFmpeg", "8.x_internal");
                            }
                            //in Android, iOS and macOS, ffmpeg bundle path will be configured automatically by the loader
                        }
                    }
                    catch { }
                    Log($"FFmpeg library root path: {ffmpeg.RootPath}");
                    FFmpeg.AutoGen.DynamicallyLoadedBindings.EnableAutoInitialization = false;
                    FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;

                    try
                    {
                        FFmpeg.AutoGen.DynamicallyLoadedBindings.Initialize(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), true);
                        FFmpegHelper.SetupFFmpegLogging();
                        Log($"internal FFmpeg library: version {ffmpeg.av_version_info()}");
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "Load internal FFmpeg library");
                    }

                }
                catch (Exception ex)
                {
                    Log(ex, "init ffmpeg");
                }

            }
        }

        private static string ResolveDataRoot(string? dataRoot)
        {
            if (!string.IsNullOrWhiteSpace(dataRoot)) return dataRoot;

            string overridePathFile = Path.Combine(AppDataPath, "OverrideUserDataPath.txt");
            return File.Exists(overridePathFile)
                ? File.ReadAllText(overridePathFile).Trim()
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "projectFrameCut");
        }

        private static string? GetOption(string[] args, string name, bool required = true)
        {
            var prefix = $"--{name}=";
            var value = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.Substring(prefix.Length);
            if (required && string.IsNullOrWhiteSpace(value))
            {
                Console.Error.WriteLine($"ERROR: Missing --{name}=... option.");
                Environment.Exit(InvalidCommandExitCode);
            }
            return value;
        }

        #endregion

        #region help
        private static void WriteGeneralHelp()
        {
            Console.WriteLine(
@"projectFrameCut command-line interface

Usage:
  pjfc <command> [arguments] [options]
  pjfc help [command]

Commands:
  gui        Launch the projectFrameCut graphical interface.
  render     Run the built-in renderer.
  headless   Start the headless backend for remote access and automation.
  mcp        Serve the user library or one project over MCP.
  help       Show general help or detailed help for a command.
  reset      Reset the application to its default state by clearing settings.
  about      Show version and build information.

Commands provided by instance manager （installed separately）:   

  stdio_mcp  Starts the stdio background MCP server.
             No any parameters are required. 

  instance   Launch the instance manager CLI. 

Global options:
  --quiet            Suppress all console outputs, include logs, version/copyright banner, and diagnostic messages.

  --consoleLog       Write application logs to the console. Mutually exclusive with with --quiet flag. 

  --logDiagnostic    Include diagnostic-level log messages.

  --loadPlugins      Load all enabled User-level plugin(s) which has been enabled.
                     Not applicable to gui mode because GUI will handle plugin by itself.

  --ffmpegRoot       Sets the root path for FFmpeg binaries. 
                     If not specified, defaults to the internal FFmpeg path, or the user configured 
                     path/plugin in the GUI settings.

  --dataRoot         Optional user-data directory used for assets and rendering.
                     If not specified, defaults to the path defined in <App Data>\OverrideUserDataPath.txt 
                     or %USERPROFILE%\Documents\projectFrameCut by default.


Help options:
  -h, --help, /?    Show this help text.
");
        }

        private static void WriteMcpHelp()
        {
            Console.WriteLine(
@"Start a projectFrameCut MCP server

Usage:
  pjfc mcp --transport=stdio --quiet [--projectRoot=<path>] [options]
  pjfc mcp --transport=http --listen=<http[s]://host:port>
           [--projectRoot=<path>] [options]

Options:
  --transport    MCP transport mode: stdio or http.
  --listen       MCP HTTP listen address. Required for HTTP transport; the MCP
                 endpoint is /mcp.
  --projectRoot  Optional project directory to load. Without it, the server
                 starts in no-project mode. --project=<path> and a positional
                 path are also accepted.
  --dataRoot     Optional user data root containing My Drafts, My Templates,
                 and My Assets.
  --headless     Do not start the graphical client in MCP mode. 
                 When not set, the client connects through an authenticated 
                 local named pipe and waits for enter_project when no project
                 is loaded. Exiting this client also stops the MCP server.

Optional RPC server:
  --rpcListen    HTTP listen address for the protobuf RPC server.
  --rpcToken     Bearer token for RPC clients. Required with --rpcListen and
                 must contain at least 32 non-whitespace characters.

The MCP server can switch between no-project library tools and project
editing tools without reconnecting. Entering or exiting a project emits a tool
list-changed notification. 

No graphical interface is started when --headless is supplied. 
Diagnostics and failures are written to stderr.

For HTTP MCP, --rpcListen may equal --listen; /mcp and /rpc then share one
listener. 

For stdio MCP, the --quiet flag is required to suppress all log outputs.
To avoid unexpected log messages in the stdio stream, if the --quiet flag is not specified, 
the stdio MCP server will exit with an error.
");
        }

        private static void WriteBackendHelp()
        {
            Console.WriteLine(
@"Start the projectFrameCut headless backend

Usage:
  pjfc backend --listen=<http[s]://host:port> --token=<token> --projectRoot=<path> [--dataRoot=<path>]

Options:
  --listen      HTTP or HTTPS listen address.
  --token       Bearer token used by RPC clients. It must contain at least 32
                non-whitespace characters.
  --projectRoot Project directory to load before the RPC server starts.
  --dataRoot    projectFrameCut's User Data directory. If not specified, default to the path defined in
                <App Data>\OverrideUserDataPath.txt's path or %USERPROFILE%\Documents\projectFrameCut by default.

The backend loads the project before accepting RPC requests and keeps running until Ctrl+C or process termination.
");
        }

        private static void WriteGuiHelp()
        {
            Console.WriteLine(
@"

Launch the projectFrameCut Application

Usage:
  pjfc gui [<target>] [options]
  pjfc gui --continue [options]
  pjfc gui --render {<target>|--continue} [options]

  pjfc:[<target>][?<option>[&<option>...]] 

Arguments:
  <target>                         Item to open after the GUI starts. Supported
                                   targets are a .pjfc project/package, a project
                                   directory, or a .pjfcPlugin package. Quote paths
                                   containing spaces.

  --continue                       Open the last project when <target> is omitted.
                                   An explicit target takes precedence.

  --render                         Load project and directly open the render page. 
                                   This option requires <target> is specified 
                                   or --continue is used. When --continue is used, the last
                                   project will be opened and directly open the render page.

                                   To implement automatic rendering, please use 'render' mode. 

Application options:

  --noSplash                       Do not display the startup splash screen.


  --overrideCulture=<culture>      Override the application culture for this run.
                                   <culture> is a BCP-47 tag such as zh-CN, en-US,
                                   or ja-JP.

  --userData=<path>                Override the user-data directory for one run
                                   (include your projects, assets, templates, 
                                   skills, and render cache).

                                   To change the user-data directory permanently, 
                                   use the Settings in the GUI or edit the config file.

  --basicUserData=<path>           Override the application-data directory used
                                   for settings and other internal state in one run.

  --scripting=disable|enable|enableWithHostingPipe        
                                   Control the scripting engine for this run. 
                                   if this argument is omitted, the scripting engine 
                                   is controlled by the user preferences.

                                   'disable' disables scripting whatever the user preferences are.

                                   'enable' enables scripting when the user preferences 
                                   are not disabled scripting engine (default behavior).

                                   'enableWithHostingPipe' is very dangerous and 
                                   should only be used in a secure environment, because 
                                   it allows arbitrary code execution from a remote process.

                                   When scripting is disabled in the user preferences,  
                                   the scripting engine is always disabled and this argument has no effect.



Platform-specific options:
  gtkArg:<argument for GTK>        Linux only: pass an argument to the GTK runtime. For example, 
                                   gtkArg:--enable-animations=false disables GTK animations.

  --log                            Windows only: Open the dedicated log window. 

Advandced option:
  --noSettings                     Do not persist setting changes automatically
                                   made during this run. The setting changes will 
                                   be committed to the file only when this app closes 
                                   normally.

  --disablePlugins                 Disable plugin-engine startup for this run.

  --allowCtrlCExit                 Allow use Ctrl+C (or SIGINT in Linux) to exit the GUI application.
                                   Note that the exit for Ctrl+C may cause data loss 
                                   if the project was not saved properly.

Remote access:
  --remote=<address>?token=<RPC_TOKEN>
  --remote=<address> --remoteToken=<RPC_TOKEN>
                                  Connect to the specified RPC Server.
                                  You can either provide the token in the remote url,
                                  or provide it separately.

                                  In this case, --continue and target will be ignored.

Protocol URI:
  pjfc:file:///C:/path/to/project.pjfc[?option[&option...]]

  A file URI can be passed through the registered pjfc: protocol. Each decoded
  query segment becomes one normal command-line argument. For example,
  ?--noSplash supplies --noSplash, while ?--overrideCulture=en-US supplies
  --overrideCulture=en-US.

Examples:
  pjfc gui
  pjfc gui ""D:\Video Projects\demo.pjfc""
  pjfc gui --render ""D:\Video Projects\demo.pjfc""
  pjfc gui --continue --noSplash
  pjfc gui ""D:\Video Projects\demo.pjfc"" --consoleLog --logDiagnostic
  pjfc gui --overrideCulture=en-US --userData=""D:\pjfc-data""
");
        }

        public static void WriteAbout()
        {
            var ProgramConfig = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown config";
            var ProgramCommit = (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Split('+').Last();
            var AssemblyName = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "projectFrameCut";
            var renderType = typeof(Renderer).Assembly;
            var drawingType = typeof(Drawing.Base.IPicture).Assembly;
            string renderHash = "", drawingHash = "", drawingCommit = "unknown", programDate = "?";
            try
            {
#pragma warning disable IL3000 // we have already detected that the assembly is not dynamic, so it's safe to get the location
                renderHash = !renderType.IsDynamic && Path.Exists(renderType.Location) ? HashServices.ComputeFileHash(renderType.Location) : "unknown";
                drawingHash = !drawingType.IsDynamic && Path.Exists(drawingType.Location) ? HashServices.ComputeFileHash(drawingType.Location) : "unknown";
                try
                {
                    var appType = Assembly.GetExecutingAssembly();
                    programDate = !appType.IsDynamic && Path.Exists(appType.Location) ? $"on {File.GetLastWriteTime(appType.Location):yyyy-MM-dd HH:mm:ss}" : "";
                }
                catch
                {
                    programDate = "?";
                }
#pragma warning restore IL3000
                drawingCommit = (drawingType.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Split('+').Last();

            }
            catch { renderHash = "unknown"; }
            Console.WriteLine(
$"""
{ProgramConfig}@{ProgramCommit} build {programDate}
https://github.com/hexadecimal0x12e/projectFrameCut

.NET CoreCLR version: {Environment.Version}
.NET MAUI version:    {typeof(View).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "10.0.?"}
IPluginBase API:      v{IPluginBase.CurrentPluginAPIVersion} 
IApplicationPluginBase API:   v{IApplicationPluginBase.CurrentAppLevelPluginAPIVersion}
{renderType.GetName().Name}:  v{renderType.GetName().Version} {renderHash}
{drawingType.GetName().Name}: v{drawingType.GetName().Version} ({drawingCommit})

This project is licensed under the Apache License, Version 2.0 for personal/educational and non-commercial use ONLY.
See the LICENSE and license.md file in the project root for more information.
""");
        }

        private static void WriteRenderHelp()
        {
            Console.WriteLine(
@"Render a project

Usage:
  pjfc render -project=<project directory> -output=<file>
              -output_options=<width>,<height>,<fps>,<pixel format>,<encoder>
                [-target=video|audio|all|void] [-assetDbFile=<database.json>]
                [-maxParallelThreads=<number>] [-oneByOneRender=true|false]
                [-renderByLayer=true|false] [-prepareInWorker=true|false]
                [-enableThreadAffinity=true|false] [-GCOptions=0|1|2]
                [-chunkRender=true|false] [-chunkFrames=<number>|-chunkSeconds=<number>]
                [-chunkParallelism=<number>] [-chunkResume=true|false]
                [-chunkKeepFiles=true|false]

This command is usually used internally, not intended for direct use by end users. 
It is provided for detached rendering of a project for the UI, and for integration with other tools.

This command provide a simple way to render a project in the command line.
For full functionality of out-of-process rendering, use StandaloneRender.

The render command is executed in-process and uses the same Renderer,
VideoBuilder, audio composer, plugin manager, and accelerator manager as the GUI.
For the usage of params, refer to the StandaloneRender's documentation.
");
        }

        private static void WriteRpcServerHelp()
        {
            Console.WriteLine(
@"Start the projectFrameCut Render RPC server
This is internal command used by the GUI to start the Render RPC server.
It is not intended to be run directly by users. The command is normally started by the graphical application.
");
        }

        #endregion
    }
}
