using FFmpeg.AutoGen;
using projectFrameCut.Asset;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Drawing.Base;
using projectFrameCut.DraftStuff;
using projectFrameCut.IntegratedAPIServer;
using projectFrameCut.IntegratedAPIServer.Headless;
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
    /// Entry point for commands exposed by pjfc-cli.
    /// Keep the option descriptions in this file in sync with Program and HomePage.
    /// </summary>
    public static class CLIProgram
    {
        #region init
        private const int SuccessExitCode = 0;
        private const int InvalidCommandExitCode = 2;
        private static string AppDataPath =>
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

            switch (args[0].ToLowerInvariant())
            {
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
            Console.Error.WriteLine("Run 'pjfc-cli help' to see the available commands.");
            return InvalidCommandExitCode;
        }

        private static int WriteCommandHelp(string command)
        {
#if !ANDROID && !iDevices
            if (command.Equals("rpc_server", StringComparison.OrdinalIgnoreCase))
            {
                WriteRpcServerHelp();
                return SuccessExitCode;
            }
#endif

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
                if (string.IsNullOrWhiteSpace(pipe) || string.IsNullOrWhiteSpace(token))
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
#if !iDevices
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
                completionSink: RenderCompletionNotifier.Notify);
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
            Action<double, TimeSpan>? progress = null)
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
            using var consoleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; consoleCancellation.Cancel(); };

            async Task RenderVideo(string path)
            {
                var clips = DraftImportAndExportHelper.JSONToIClips(timeline, false, bpp).Where(c => c.ClipType != ClipMode.AudioClip).ToArray();
                if (clips.Length == 0) throw new InvalidOperationException("No video clips in the project.");
                foreach (var clip in clips) clip.ReInit(bpp);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
                var builder = new VideoBuilder(path, width, height, fps, output[4], pixelFormat.ToString())
                {
                    EnablePreview = true,
                    Duration = duration,
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
                    Duration = duration,
                    Use16Bit = bpp == IPicture.PicturePixelMode.UShortPicture,
                    GCOption = gcOption,
                    MaxThreads = maxThreads,
                    OneByOneRender = serial,
                    RenderByLayers = renderByLayers,
                    PrepareInWorkerThreads = prepareInWorkers,
                    EnableThreadAffinity = affinity,
                    MinSchedulePreparedFrames = 1
                };
                renderer.OnProgressChanged += (value, eta) =>
                {
                    progress?.Invoke(target == "all" ? value * 0.9 : value, eta);
                    //Console.Write($"Rendering {value:P1}, ETA {eta:hh\\:mm\\:ss}, FPS {renderer.CurrentFps:N2}       \r");
                };
                builder.Build()?.Start();
                renderer.PrepareRender(consoleCancellation.Token);
                await renderer.GoRender(consoleCancellation.Token).ConfigureAwait(false);
                if (serial) builder.Writer?.Finish();
                else builder.Finish(i => Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, i, width, height), i, width, height), duration, (_, _) => { });
                foreach (var clip in clips) clip.Dispose();
                renderer.builder = null;
                Console.WriteLine();
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
                case "video": await RenderVideo(outputPath); break;
                case "audio": RenderAudio(outputPath); break;
                case "all":
                    var video = Path.Combine(tempPath, $"{project.ProjectName}_Intermediate_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(outputPath)}");
                    var audio = Path.Combine(tempPath, $"{project.ProjectName}_Intermediate_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                    await RenderVideo(video);
                    progress?.Invoke(0.92, TimeSpan.Zero);
                    RenderAudio(audio);
                    progress?.Invoke(0.97, TimeSpan.Zero);
                    if (File.Exists(audio))
                        VideoAudioMuxer.MuxFromFiles(video, audio, outputPath, true);
                    else
                        File.Move(video, outputPath, overwrite: true);
                    File.Delete(video);
                    File.Delete(audio);
                    break;
                default: throw new ArgumentException($"Unknown target '{target}'.");
            }
            Environment.SetEnvironmentVariable("projectFrameCut_LastOutput", outputPath, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("projectFrameCut_RenderFinished", "1", EnvironmentVariableTarget.Process);
            return 0;
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

            var exitCode = await service.RunAsync(async (progress, cancellationToken) =>
            {
                InitializeCliRenderRuntime(dataRoot, switches.GetValueOrDefault("FFmpegLibraryPath", string.Empty));
                return await RunRenderPipelineAsync(switches, cancellationToken, progress).ConfigureAwait(false);
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
            private readonly object _gate = new();
            private readonly string _statePath;
            private readonly CancellationTokenSource _renderCancellation;
            private readonly CancellationTokenSource _serverCancellation;
            private RenderJob _job;

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
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                };
                Persist();
            }

            public async Task<int> RunAsync(Func<Action<double, TimeSpan>, CancellationToken, Task<int>> render)
            {
                Update(job => job.State = RenderJobState.Running);
                try
                {
                    var result = await render((progress, eta) => Update(job =>
                    {
                        job.Progress = Math.Clamp(progress, 0, 1);
                        job.EstimatedRemainingTicks = Math.Max(0, eta.Ticks);
                    }), _renderCancellation.Token).ConfigureAwait(false);
                    if (result != 0) throw new InvalidOperationException($"CLI renderer exited with code {result}.");
                    Update(job =>
                    {
                        job.State = RenderJobState.Completed;
                        job.Progress = 1;
                        job.EstimatedRemainingTicks = 0;
                    });
                    NotifyCompletion();
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    Update(job => job.State = RenderJobState.Canceled);
                    NotifyCompletion();
                    return 255;
                }
                catch (Exception ex)
                {
                    Log(ex, $"CLI render job {_job.JobId}");
                    Update(job =>
                    {
                        job.State = RenderJobState.Failed;
                        job.Error = new RenderError
                        {
                            Code = RenderErrorCode.BackendFailure,
                            Message = ex.Message,
                            Details = ex.ToString(),
                        };
                    });
                    NotifyCompletion();
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
                    return ValueTask.FromResult(Failure(request, RenderErrorCode.BackendFailure, ex.Message, ex.ToString()));
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
                    PersistCore();
                }
            }

            private RenderJob Snapshot()
            {
                lock (_gate) return RenderRpcSerializer.Clone(_job);
            }

            private void Persist()
            {
                lock (_gate) PersistCore();
            }

            private void PersistCore()
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                    var temp = _statePath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(_job));
                    File.Move(temp, _statePath, overwrite: true);
                }
                catch (Exception ex) { Log(ex, "Persist CLI render job"); }
            }

            private void NotifyCompletion()
            {
                try { RenderCompletionNotifier.Notify(Snapshot()); }
                catch (Exception ex) { Log(ex, "CLI render completion notification"); }
            }

            private static RenderResponseEnvelope Success<T>(RenderRequestEnvelope request, T payload) => new()
            {
                RequestId = request.RequestId,
                Payload = RenderRpcSerializer.Serialize(payload),
            };

            private static RenderResponseEnvelope Failure(RenderRequestEnvelope request, RenderErrorCode code, string message, string details = "") => new()
            {
                RequestId = request.RequestId,
                Error = new RenderError { Code = code, Message = message, Details = details },
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

        private static void WriteGeneralHelp()
        {
            Console.WriteLine(
@"projectFrameCut command-line interface

Usage:
  pjfc-cli <command> [arguments] [options]
  pjfc-cli help [command]

Commands:
  gui        Launch the projectFrameCut graphical interface.
  render     Run the built-in renderer.
  headless   Start the headless backend for remote access and automation.
  help       Show general help or detailed help for a command.
  reset      Reset the application to its default state by clearing settings.
  about      Show version and build information.

Global options:
  --quiet            Suppress the pjfc-cli version banner and copyright notice.

  --consoleLog       Write application logs to the console.

  --logDiagnostic    Include diagnostic-level log messages.

  --loadPlugins      Load all enabled User-level plugin(s) which has been enabled.

  --ffmpegRoot       Sets the root path for FFmpeg binaries. 
                     If not specified, defaults to the internal FFmpeg path,
                     or the user configured path/plugin in the GUI settings.

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

        private static void WriteRenderHelp()
        {
            Console.WriteLine(
@"Render a project

Usage:
  pjfc-cli render -project=<project directory> -output=<file>
                  -output_options=<width>,<height>,<fps>,<pixel format>,<encoder>
                  [-target=video|audio|all] [-assetDbFile=<database.json>]
                  [-maxParallelThreads=<number>] [-oneByOneRender=true|false]
                  [-renderByLayer=true|false] [-prepareInWorker=true|false]
                  [-enableThreadAffinity=true|false] [-GCOptions=0|1|2]

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
It is not intended to be run directly by users.

Usage:
  pjfc-cli rpc_server --pipe=<pipe-name> --token=<token> --dataRoot=<path> [--parentPid=<pid>] [--quiet]

Optional integrated HTTP RPC server:
  --http=<http[s]://host:port>
                       Additionally start the headless HTTP protobuf RPC server
                       provided by projectFrameCut.IntegratedAPIServer alongside
                       the named-pipe server, for remote control and
                       cross-module communication. Both channels share the same
                       render backend, so render sessions are visible across
                       them. The HTTP endpoint serves /rpc and /artifact.
                       If the HTTP server cannot be started, the command fails.

  --projectRoot=<path> Project directory the server is started for. It is
                       normally supplied by the GUI when it starts a backend for
                       a project. With --http, the HTTP RPC server preloads this
                       project so clients do not hit a server-has-no-project
                       error; without it the HTTP server starts without a
                       preloaded project and clients open projects on demand.

  --httpToken=<token>  Bearer token for the HTTP RPC endpoint. If omitted, the
                       pipe --token value is reused, which must then contain at
                       least 32 non-whitespace characters.

The command is normally started by the graphical application.
");
        }

        #endregion
    }
}
