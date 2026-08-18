using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.IntegratedAPIServer;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Shared;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace projectFrameCut.WinUI
{
    /// <summary>
    /// Entry point for commands exposed by pjfc-cli.
    /// Keep the option descriptions in this file in sync with Program and HomePage.
    /// </summary>
    public static class CLIProgram
    {
        private const int SuccessExitCode = 0;
        private const int InvalidCommandExitCode = 2;
        private static string AppDataPath => App.IsPackaged() ? Windows.Storage.ApplicationData.Current.LocalFolder.Path : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "projectFrameCut");

        public static int Main(string[] args)
        {
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

            switch (args[0].ToLowerInvariant())
            {
                case "headless":
                    return RunBackend(args.Skip(1).ToArray());
                case "rpc_server":
                    return RunRpcServer(args.Skip(1).ToArray());
                case "about":
                    WriteAbout();
                    return 0;
            }

            Console.Error.WriteLine($"Unknown command: {args[0]}");
            Console.Error.WriteLine("Run 'pjfc-cli help' to see the available commands.");
            return InvalidCommandExitCode;
        }

        private static int WriteCommandHelp(string command)
        {
            if (command.Equals("rpc_server", StringComparison.OrdinalIgnoreCase))
            {
                WriteRpcServerHelp();
                return SuccessExitCode;
            }

            if (command.Equals("headless", StringComparison.OrdinalIgnoreCase))
            {
                WriteBackendHelp();
                return SuccessExitCode;
            }

            if (command.Equals("gui", StringComparison.OrdinalIgnoreCase))
            {
                WriteGuiHelp();
                return SuccessExitCode;
            }

            Console.Error.WriteLine($"No help topic found for '{command}'.");
            Console.Error.WriteLine("Run 'pjfc-cli help' to see the available topics.");
            return InvalidCommandExitCode;
        }

        private static bool IsHelpOption(string value) =>
            value.Equals("help", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/?", StringComparison.OrdinalIgnoreCase);

        private static void WriteGeneralHelp()
        {
            Console.WriteLine(
@"projectFrameCut command-line interface

Usage:
  pjfc-cli <command> [arguments] [options]
  pjfc-cli help [command]

Commands:
  gui        Launch the projectFrameCut graphical interface.
  headless   Start the headless backend for remote access and automation.
  help        Show general help or detailed help for a command.
  reset      Reset the application to its default state by clearing settings.
  about      Show version and build information.

Global options:
  --quiet            Suppress the pjfc-cli version banner and copyright notice.

  --consoleLog       Write application logs to the console.

  --logDiagnostic    Include diagnostic-level log messages.

Help options:
  -h, --help, /?    Show this help text.
");
        }

        private static void WriteBackendHelp()
        {
            Console.WriteLine(
@"Start the projectFrameCut headless backend

Usage:
  pjfc-cli backend --listen=<http[s]://host:port> --token=<token> --projectRoot=<path> [--dataRoot=<path>]

Options:
  --listen      HTTP or HTTPS listen address.
  --token       Bearer token used by RPC clients. It must contain at least 32
                non-whitespace characters.
  --projectRoot Project directory to load before the RPC server starts.
  --dataRoot    projectFrameCut's User Data directory. If not specified, default to the path defined in
                <App Data>\OverrideUserDataPath.txt's path or %USERPROFILE%\Documents\projectFrameCut by default.

The backend loads the project before accepting RPC requests and keeps running until Ctrl+C or process termination.
Use ASP.NET's Environment variables to configure the HTTP server option (except application URL), such as ASPNETCORE_Kestrel__Certificates__Default__Path, and ASPNETCORE_Kestrel__Certificates__Default__Password.
");
        }

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
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cancellation.Cancel();
                };
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
            await server.StartHeadlessAsync(new IntegratedApiServerOptions
            {
                ListenUri = listenUri,
                RpcToken = token,
                ProjectRoot = projectRoot,
                GlobalAssetsDatabasePath = Path.Combine(dataRoot, "My Assets", ".database", "database.json"),
                EnableMcp = false,
                WarningSink = warning => Console.Error.WriteLine($"Warning: {warning}"),
            }, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"projectFrameCut backend listening at {listenUri.AbsoluteUri.TrimEnd('/')}/rpc");
            Console.WriteLine("Press Ctrl+C to stop.");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return SuccessExitCode;
        }

        private static void WriteRpcServerHelp()
        {
            Console.WriteLine(
@"Start the projectFrameCut Render RPC server
This is internal command used by the GUI to start the Render RPC server.
It is not intended to be run directly by users.

Usage:
  pjfc-cli rpc_server --pipe=<pipe-name> --token=<token> --dataRoot=<path> [--parentPid=<pid>] [--quiet]

The command is normally started by the graphical application.
");
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
                if (string.IsNullOrWhiteSpace(pipe) || string.IsNullOrWhiteSpace(token))
                {
                    Console.Error.WriteLine("rpc_server requires --pipe=<pipe-name> and --token=<token> and --dataRoot=<path>.");
                    WriteRpcServerHelp();
                    return InvalidCommandExitCode;
                }

                using var cancellation = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cancellation.Cancel();
                };
                return RunRpcServerAsync(pipe, token, parentPid, dataRoot, cancellation.Token).GetAwaiter().GetResult();
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

        private static async Task<int> RunRpcServerAsync(string pipe, string token, string? parentPid, string dataRoot, CancellationToken cancellationToken)
        {
            InitializeRenderRuntime(dataRoot);
            await using var service = new RenderBackendService();
            await new NamedPipeRenderServer(service).RunAsync(pipe, token, parentPid, cancellationToken).ConfigureAwait(false);
            return SuccessExitCode;
        }

        private static void InitializeRenderRuntime(string dataRoot)
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

            FFmpeg.AutoGen.DynamicallyLoadedBindings.EnableAutoInitialization = false;
            FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            FFmpeg.AutoGen.ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "FFmpeg", "8.x_internal");
            if (!FFmpeg.AutoGen.DynamicallyLoadedBindings.TryInitialize())
                throw new InvalidOperationException($"FFmpeg initialization failed at '{FFmpeg.AutoGen.ffmpeg.RootPath}'.");
            FFmpegHelper.SetupFFmpegLogging(FFmpeg.AutoGen.ffmpeg.AV_LOG_WARNING);
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

        private static void WriteGuiHelp()
        {
            Console.WriteLine(
@"

Launch the projectFrameCut Application UI

Usage:
  pjfc-cli gui [<target>] [options]
  pjfc:[<target>][?<option>[&<option>...]] 

Arguments:
  <target>                         Item to open after the GUI starts. Supported
                                   targets are a .pjfc project/package, a project
                                   directory, or a .pjfcPlugin package. Quote paths
                                   containing spaces.

Launch options:
  --continue                       Open the last project when <target> is omitted.
                                   An explicit target takes precedence.

  --noSplash                       Do not display the startup splash screen.

  --overrideCulture=<culture>      Override the application culture for this run.
                                   <culture> is a .NET culture name such as zh-CN,
                                   en-US, or ja-JP.

  --userData=<path>                Override the user-data directory for one run
                                   (include your projects, assets, templates, 
                                   skills, and render cache).

                                   To change the user-data directory permanently, 
                                   use the Settings in the GUI or edit the config file.

  --basicUserData=<path>           Override the application-data directory used
                                   for settings and other internal state in one run.

  --noSettings                     Do not persist setting changes automatically
                                   made during this run. The setting changes will 
                                   be committed to the file only when this app closes 
                                   normally.

  --disablePlugins                 Disable plugin-engine startup for this run.

  --scripting=disable|enableWithHostingPipe        
                                   Control the scripting engine for this run. 
                                   if this argument is omitted, the scripting engine 
                                   is controlled by the user preferences.

                                   'disable' disables scripting whatever the user preferences are.

                                   'enableWithHostingPipe' is very dangerous and 
                                   should only be used in a secure environment, because 
                                   it allows arbitrary code execution from a remote process.

                                   When scripting is disabled in the user preferences,  
                                   the scripting engine is always disabled and this argument has no effect.

Logging and diagnostics:
  --log                            Open the dedicated log window.

Integration options:
  --mcp=<http[s]://host:port>     Start the integrated MCP HTTP server for the
                                   project being opened. The MCP endpoint is /mcp.

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
  pjfc-cli gui
  pjfc-cli gui ""D:\Video Projects\demo.pjfc""
  pjfc-cli gui --continue --noSplash
  pjfc-cli gui ""D:\Video Projects\demo.pjfc"" --consoleLog --logDiagnostic
  pjfc-cli gui --overrideCulture=en-US --userData=""D:\pjfc-data""
  projectFrameCut.exe ""D:\Video Projects\demo.pjfc""
  projectFrameCut.exe ""pjfc:file:///D:/Video%20Projects/demo.pjfc?--noSplash""

Notes:
  * Most GUI options are case-sensitive; use the spelling shown above.
    --continue and --mcp are accepted case-insensitively.
  * Options that take a value require the --name=value form.
  * When several non-option arguments are supplied, the longest one is selected
    as the launch target. Supplying one target is recommended.
  * `pjfc-cli gui` launches the GUI immediately. Use `pjfc-cli help gui` to view
    this document without starting it.");
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
{ProgramConfig}@{ProgramCommit} build on {programDate}
https://github.com/hexadecimal0x12e/projectFrameCut

.NET CoreCLR version: {Environment.Version}
.NET MAUI version:    {typeof(View).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "10.0.?"}
IPluginBase API:      v{IPluginBase.CurrentPluginAPIVersion} 
IApplicationPluginBase API: v{IApplicationPluginBase.CurrentAppLevelPluginAPIVersion}
{renderType.GetName().Name}: v{renderType.GetName().Version} {renderHash}
{drawingType.GetName().Name}: v{drawingType.GetName().Version} ({drawingCommit})
Package: {WinUI.App.GetPackageFullName() ?? "N/A (Portable)"} 

This project is licensed under the Apache License, Version 2.0 for personal/educational and non-commercial use ONLY.
See the LICENSE and license.md file in the project root for more information.
""");
        }
    }
}
