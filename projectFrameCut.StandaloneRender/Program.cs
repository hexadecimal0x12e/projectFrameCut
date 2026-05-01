using FFmpeg.AutoGen;
using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Render.Benchmark;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.WindowsRender;
using projectFrameCut.Shared;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static ILGPU.IR.BasicBlock;
using static projectFrameCut.Shared.Logger;

namespace projectFrameCut.StandaloneRender
{
    internal class Program
    {
        const int PluginAPIVersion = 1;

        static readonly JsonSerializerOptions savingOpts = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

        static async Task<int> Main(string[] args)
        {
            if (!args.Contains("--nolog"))
            {
                MyLoggerExtensions.OnLog += (m, l) =>
                {
                    if (l.Equals("info", StringComparison.InvariantCultureIgnoreCase))
                    {
                        Console.WriteLine(m);
                        return;
                    }

                    var oldColor = Console.ForegroundColor;
                    try
                    {
                        Console.ForegroundColor = l.Equals("error", StringComparison.InvariantCultureIgnoreCase) ? ConsoleColor.Red :
                                              l.Equals("stat", StringComparison.InvariantCultureIgnoreCase) ? ConsoleColor.Green :
                                              (l.Equals("warning", StringComparison.InvariantCultureIgnoreCase) || l.Equals("warn", StringComparison.InvariantCultureIgnoreCase)) ? ConsoleColor.Yellow :
                                              (l.Equals("debug", StringComparison.InvariantCultureIgnoreCase) || l.Equals("diag", StringComparison.InvariantCultureIgnoreCase)) ? ConsoleColor.Cyan :
                                              l.StartsWith("FFmpeg", StringComparison.InvariantCultureIgnoreCase) ? ConsoleColor.Magenta :
                                              ConsoleColor.Gray;

                        Console.Write($"[{l}]");
                    }
                    finally
                    {
                        Console.ForegroundColor = oldColor;
                    }

                    Console.WriteLine($" {m}");
                };
                Console.WriteLine($"projectFrameCut.StandaloneRender v{Assembly.GetExecutingAssembly().GetName().Version}");
                Console.Write(GetInfo());
                Console.WriteLine($"Copyright hexadecimal0x12e 2025-2026. https://github.com/hexadecimal0x12e/projectFrameCut/");

            }
            if (args.Contains("--logDiagnostic"))
            {
                MyLoggerExtensions.LoggingDiagnosticInfo = true;
            }


            if (args.Length == 0 || args.Any(x => x.Equals("-h") || x.Equals("--help")))
            {
                Console.WriteLine(
                    """
                    Help:
                    Usage for render: 
                        projectFrameCut.StandaloneRender 
                            [--nolog | --logDiagnostic] 
                            [--resolveArgsFromEnvironmentVars] 
                            [--externalAssemblyPath=<path1>[;<path2>;<path3>...]] 
                            <mode> <args>

                    Available modes:
                        - render: Render video/audio/all from the given project file.
                        - bench: Benchmark hardware accelerators for rendering.

                    Arguments:
                    Mode 'render':
                        -project=<project dir>
                        -output=<output file/folder>
                        -output_options=<width>,<height>,<fps>,<pixel format>,<encoder>
                        [-target=<video|audio|all>]
                        [-assetDbFile=<path to database.json file>]
                        [-pluginRoot=<path to plugin root>]
                        [-Use16bpp=<true|false>]
                        [-maxParallelThreads=<number> or -oneByOneRender=<true|false>]
                        [-multiAccelerator=<true|false>]
                        [-acceleratorType=<auto|cuda|opencl|cpu> or -acceleratorDeviceId=<device id> or -acceleratorDeviceIds=<device ids|all>]
                        [-GCOptions=0,1,2]
                        [-outputIntermediatePath=<intermediate output path>]
                        [-FFmpegLibraryPath=<path to FFmpeg libraries>]
                        [-diagReportPath=<path diag report output directory>]


                    Mode 'bench':
                        [-multiAccelerator=<true|false>]
                        [-acceleratorType=<auto|cuda|opencl|cpu> or -acceleratorDeviceId=<device id> or -acceleratorDeviceIds=<device ids|all>]    

                    ---

                    Other usage:
                        projectFrameCut.StandaloneRender list_accels - List all available accelerator devices. Result will printed in JSON format to stderr.
                        projectFrameCut.StandaloneRender about - Print about info.
                        projectFrameCut.StandaloneRender -h | --help - Show this help message.
                        
                    See more at the document.
                    Press Enter to exit.
                    """);
                Console.ReadLine();
                return 0;
            }

            var runningMode = args[0];

            if (runningMode == "about")
            {
                Console.WriteLine();
                Console.WriteLine(GetInfo(true));
                return 0;
            }


            ConcurrentDictionary<string, string> switches = new(args
               .Skip(1)
               .Select(x => x.Split('=', 2))
               .Where(x => x.Length == 2)
               .Select(x => new KeyValuePair<string, string>(x[0].TrimStart('-', '/'), x[1])));

            if (args.Contains("--resolveArgsFromEnvironmentVars"))
            {
                var envVars = new Dictionary<string, string>();
                foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
                {
                    if (de.Key is string key && de.Value is string value) envVars[key] = value;
                }

                foreach (var item in envVars.Where(kv => kv.Key.StartsWith("projectFrameCut_")))
                {
                    switches[item.Key.Split("projectFrameCut_", 2)[1]] = item.Value;
                }

            }

            Log($"Running mode: {runningMode}");
            Log($"Switches: {Environment.NewLine}{string.Join(Environment.NewLine, switches.Select(kv => $"- {kv.Key}: {kv.Value}"))}");
            Log($"Flags: {Environment.NewLine}{string.Join(Environment.NewLine, args.Where(c => c.StartsWith("--")).Select(c => $"- {c.Substring(2)}"))}");

            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
            {
                var requestedAssembly = new AssemblyName(eventArgs.Name);
                var probingPaths = new List<string>
                {
                    AppContext.BaseDirectory,
                };
                if (switches.TryGetValue("externalAssemblyPath", out string? value))
                {
                    probingPaths.AddRange(value.Split([';'], StringSplitOptions.RemoveEmptyEntries));
                }
                return TryResolveAssembly(requestedAssembly.Name!, probingPaths.ToArray(), keepInMemory: true);
            };


            switch (runningMode)
            {
                case "render":
                    var accelResult = InitAccel(switches);
                    if (accelResult != 0)
                    {
                        return accelResult;
                    }
                    try
                    {
                        return await GoRender(switches);
                    }
                    catch (TaskCanceledException)
                    {
                        Log("Render task was canceled.");
                        return 255;
                    }
                    catch { throw; }
                case "list_accels":
                    Context context = Context.CreateDefault();
                    var devices = context.Devices.ToList();
                    List<AcceleratorInfo> listAccels = new();
                    for (uint i = 0; i < devices.Count; i++)
                    {
                        var item = devices[(int)i];
                        listAccels.Add(new AcceleratorInfo(i, item.Name, item.AcceleratorType.ToString()));
                    }
                    Console.Error.WriteLine(JsonSerializer.Serialize(listAccels, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                default:
                    Log($"ERROR: Mode {runningMode} doesn't defined.");
                    return 16;

            }

        }

        private static int InitAccel(ConcurrentDictionary<string, string> switches)
        {
            try
            {

                Context context = Context.CreateDefault();
                var devices = context.Devices.ToList();
                List<Device> picked = new();
                for (int i = 0; i < devices.Count; i++)
                {
                    Log($"Accelerator device #{i}: {devices[i].Name} ({devices[i].AcceleratorType})");
                }


                var multiAccel = bool.TryParse(switches.GetOrAdd("multiAccelerator", "false"), out var ma) ? ma : false;
                if (!multiAccel)
                {
                    var acceleratorId = int.TryParse(switches.GetOrAdd("acceleratorDeviceId", "-1"), out var result1) ? result1 : -1;
                    var accelType = switches.GetOrAdd("acceleratorType", "auto");
                    var acc = ILGPUComputerHelper.PickOneAccel(accelType, acceleratorId, devices);
                    if (acc is null)
                    {
                        Log($"ERROR: Cannot pick accelerator device. Check the configuration.");
                        return 2;
                    }
                    picked.Add(acc);
                    Log($"Selecting accelerator device #{devices.IndexOf(acc)}: {acc.Name} ({acc.AcceleratorType})");

                }
                else
                {
                    var accelsIdsStr = switches.GetOrAdd("acceleratorDeviceIds", "");
                    if (string.IsNullOrWhiteSpace(accelsIdsStr))
                    {
                        Log("ERROR: multiAccelerator is set to true, but no acceleratorDeviceIds provided.");
                        return 2;
                    }

                    if (accelsIdsStr == "all")
                    {
                        picked = devices.Where(a => a.AcceleratorType != AcceleratorType.CPU).ToList();
                    }
                    else
                    {

                        var accelsIds = accelsIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(s => int.TryParse(s, out var id) ? id : -1)
                            .Where(id => id >= 0)
                            .ToList();
                        picked = accelsIds.Select(id =>
                        {
                            var acc = ILGPUComputerHelper.PickOneAccel("auto", id, devices);
                            if (acc is null)
                            {
                                Log($"ERROR: Cannot pick accelerator device with id {id}.");
                            }
                            return acc;
                        }).ToList()!;
                    }

                }

                if (picked == null || picked.Count == 0)
                {
                    Log($"ERROR: No accelerator device picked. Check the configuration.");
                    return 2;
                }

                foreach (var item in picked)
                {
                    Log($"Picked accelerator {item.Name} : {item.AcceleratorType}");
                }

                Accelerator[] accelerators = picked.Select(d => d.CreateAccelerator(context)).ToArray();

                ILGPUPlugin.accelerators = accelerators;
                return 0;

            }
            catch (Exception ex)
            {
                Log(ex, "Get accels");
                throw;
            }
        }

        private static async Task<int> GoRender(ConcurrentDictionary<string, string> switches)
        {
            #region init encoder
            bool trace = Environment.GetCommandLineArgs().Contains("--trace");
            Log("Initiliazing FFmpeg...");
            ffmpeg.RootPath = switches.GetOrAdd("FFmpegLibraryPath", AppContext.BaseDirectory);
            FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            if (FFmpeg.AutoGen.DynamicallyLoadedBindings.TryInitialize())
            {
                FFmpegHelper.SetupFFmpegLogging(trace ? ffmpeg.AV_LOG_DEBUG : ffmpeg.AV_LOG_INFO);
                Log($"internal FFmpeg library: version {ffmpeg.av_version_info()}, {ffmpeg.avcodec_license()}\r\nconfiguration:{ffmpeg.avcodec_configuration()}");
            }
            else
            {
                Log($"FFmpeg library failed to load. ({ffmpeg.BindingVerificationResult?.Failures?.Aggregate("", (a, b) => $"{a}{Environment.NewLine}{b.FunctionName} failed to load in {b.LibraryName}: {b.Message}")})", "error");
                return 1;
            }
            FFmpegHelper.SetupFFmpegLogging();
            Log($"internal FFmpeg library: version {ffmpeg.av_version_info()}");

            var outputOptions = switches["output_options"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int width = 0, height = 0, fps = 0;
            FFmpeg.AutoGen.AVPixelFormat outputFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            string outputEncoder = "";
            if (outputOptions.Length != 5)
            {
                Log("ERROR: output_options must contain 5 values: width,height,fps,pixel format,encoder");
                return 1;
            }
            width = int.Parse(outputOptions[0]);
            height = int.Parse(outputOptions[1]);
            fps = int.Parse(outputOptions[2]);

            if (!Enum.TryParse(outputOptions[3], out outputFormat) || outputFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            {
                Log($"ERROR: Pixel format {outputOptions[3]} not found in AVPixelFormat.");
                return 1;
            }

            outputEncoder = outputOptions[4];

            var use16Bit = bool.TryParse(switches.GetOrAdd("Use16bpp", "true"), out var b1) ? b1 : true;
            var bpp = use16Bit ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture;

            if (!switches.ContainsKey("output"))
            {
                Log("ERROR: No output path specified. Use -output=<output file> to specify the output path.");
                return 1;
            }
            var outputPath = switches["output"];

            Log($"Output options: {width}x{height} @ {fps} fps, pixel format: {outputFormat}, encoder: {outputEncoder}, bitPerPixel:{(use16Bit ? "16" : "8")}");

            #endregion

            #region plugin loading

            var plugins = new List<projectFrameCut.Render.RenderAPIBase.Plugins.IPluginBase>
                {
                    new InternalPluginBase(),
                    new ILGPUPlugin(),
                };

            if (switches.TryGetValue("pluginRoot", out var pluginRoot) && !string.IsNullOrWhiteSpace(pluginRoot))
            {
                if (Directory.Exists(pluginRoot))
                {
                    Log($"Loading external plugins from: {pluginRoot}");
                    foreach (var dllPath in Directory.GetFiles(pluginRoot, "*.dll"))
                    {
                        try
                        {
                            var assembly = Assembly.LoadFrom(dllPath);
                            var types = assembly.GetTypes();
                            var ldr = types?.First(a => a.Name == "PluginLoader");
                            if (ldr is null)
                            {
                                Log("No PluginLoader class found in assembly.", "warning");
                                continue;
                            }
                            var ldrMethod = ldr.GetMethod("CreateInstance");
                            var pluginObj = ldrMethod?.Invoke(null, ["stand-Alone", Path.GetDirectoryName(dllPath)]);
                            if (pluginObj is IPluginBase plugin)
                            {
                                if (plugin.PluginAPIVersion != PluginAPIVersion)
                                {
                                    Log($"Plugin {plugin.Name} has a mismatch PluginAPIVersion. Excepted {PluginAPIVersion}, got {plugin.PluginAPIVersion}.", "error");
                                    continue;
                                }
                                plugins.Add(plugin);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Failed to load assembly {Path.GetFileName(dllPath)}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Log($"WARNING: Plugin root directory '{pluginRoot}' does not exist.");
                }
            }

            PluginManager.Init(plugins);
            #endregion

            #region read args
            if (!switches.ContainsKey("project"))
            {
                Log("ERROR: No project file specified. Use -project=<project root dir> to specify the project file.");
                return 1;
            }
            var workingPath = switches["project"];
            if (string.IsNullOrWhiteSpace(workingPath) || !Directory.Exists(workingPath))
            {
                Log("ERROR: Invalid project path.");
                return 1;
            }

            string? target = "all";
            if (!switches.TryGetValue("target", out target))
            {
                target = "all";
                Log("No target specified, default to 'all'.");
            }

            var GCOption = 0;
            if (switches.TryGetValue("GCOptions", out var gcopt))
            {
                if (!int.TryParse(gcopt, out GCOption) || (GCOption < 0 || GCOption > 2))
                {
                    Log($"Invalid GCOptions option '{gcopt}', must be 0, 1 or 2. Default to 0.");
                    GCOption = 0;
                }
            }

            if (GCOption == 2)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            }

            Log($"GC Option:{GCOption}");

            ConcurrentDictionary<string, AssetItem> assets = new();

            if (switches.TryGetValue("assetDbFile", out var assetDbPath))
            {
                assets = JsonSerializer.Deserialize<ConcurrentDictionary<string, AssetItem>>(File.ReadAllText(assetDbPath))
                    ?? new ConcurrentDictionary<string, AssetItem>();
                Log($"Read {assets.Count} assets from asset database.");
            }

            int maxParallelThreads = int.TryParse(switches.GetOrAdd("maxParallelThreads", "8"), out var result) ? result : 8;
            bool blockWrite = false, renderByLayer = false;
            if (bool.TryParse(switches.GetOrAdd("oneByOneRender", "false"), out var oneByOneRender) && oneByOneRender)
            {
                maxParallelThreads = 1;
                blockWrite = true;
            }
            else
            {
                blockWrite = false;
            }
            if (!bool.TryParse(switches.GetOrAdd("renderByLayer", "false"), out renderByLayer)) renderByLayer = false;

            bool hwAccelDecode = bool.TryParse(switches.GetOrAdd("preferHwAccelDecoder", "false"), out var hwAccelDecodeValue) && hwAccelDecodeValue;
            InternalPluginBase.HWAccelOptionGetter = new(() => hwAccelDecode);

            PictureLifecycleTracker.Enabled = trace && !Renderer.IsProfilerAttached;
            PictureLifecycleTracker.TrackCollection = trace && !Renderer.IsProfilerAttached;

            #endregion

            #region read draft
            ProjectJSONStructure project = new();
            DraftStructureJSON timeline = new();
            if (File.Exists(Path.Combine(workingPath, "project.pjfc")))
            {
                project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(workingPath, "project.pjfc")), savingOpts) ?? new();
            }
            else if (File.Exists(Path.Combine(workingPath, "project.json")))
            {
                project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(workingPath, "project.json")), savingOpts) ?? new();
            }
            else
            {
                Log("ERROR: project.pjfc or project.json not found in project directory.");
                return 1;
            }

            if (File.Exists(Path.Combine(workingPath, "timeline.json")))
            {
                timeline = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(workingPath, "timeline.json")), savingOpts) ?? new();
            }
            else
            {
                Log("ERROR: timeline.json not found in project directory.");
                return 1;
            }

            if (File.Exists(Path.Combine(workingPath, "assets.json")))
            {
                var projAssets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(workingPath, "assets.json")), savingOpts) ?? new();
                ConcurrentDictionary<string, AssetItem> assetDict = new ConcurrentDictionary<string, AssetItem>(projAssets.ToDictionary((AssetItem a) => a.AssetId ?? $"unknown+{Random.Shared.Next()}", (AssetItem a) => a));
                assets = new ConcurrentDictionary<string, AssetItem>(assets.Concat(assetDict));
            }
            else
            {
                Log("ERROR: assets.json not found in project directory.");
                return 1;
            }
            Environment.CurrentDirectory = workingPath;
            #endregion

            CancellationTokenSource cts = new();
            VideoBuilder builder = null!;
            var noSigInt = !Environment.GetCommandLineArgs().Contains("--noSigInt");
            async Task composeVideo(string resultPath)
            {
                var clips = JSONToIClips(timeline, assets, bpp);

                switches.TryGetValue("diagReportPath", out var diagReportPath);
                if (PictureLifecycleTracker.Enabled)
                {
                    PictureLifecycleTracker.Clear();
                }

                builder = new VideoBuilder(resultPath, width, height, fps, outputEncoder, outputFormat.ToString())
                {
                    EnablePreview = true,
                    DoGCAfterEachWrite = GCOption > 0,
                    DisposeFrameAfterEachWrite = true,
                    Duration = timeline.Duration,
                    BlockWrite = blockWrite,
                };

                Renderer renderer = new Renderer
                {
                    builder = builder,
                    Clips = clips,
                    Duration = timeline.Duration,
                    MaxThreads = blockWrite ? 1 : maxParallelThreads,
                    LogProcessStack = !string.IsNullOrWhiteSpace(diagReportPath),
                    LogRenderState = (bool.TryParse(switches.TryGetValue("LogState", out var ls2) ? ls2 : "false", out var lsbool) && lsbool),
                    LogStaticsData = true,
                    GCOption = GCOption,
                    Use16Bit = use16Bit,
                    EnableRenderWatchdogForceStart = false,
                    MaxRenderScheduleTimeout = 0,
                    MinSchedulePreparedFrames = 1
                };

                if (!Environment.GetCommandLineArgs().Contains("--nolog"))
                {
                    renderer.OnProgressChanged += (s, e) =>
                    {
                        if (renderer.CurrentSecondPerFrame <= 1.5)
                        {
                            Console.Write($"Rendering finished {s:p0}, ETA:{e:hh\\:mm\\:ss}, FPS:{renderer.CurrentFps:n2} {(noSigInt ? "- Press Ctrl-C to interrupt render process.          " : "            ")} \r");
                        }
                        else
                        {
                            Console.Write($"Rendering finished {s:p0}, ETA:{e:hh\\:mm\\:ss}, {(1 / renderer.CurrentFps):n2} second per frame {(noSigInt ? "- Press Ctrl-C to interrupt render process.          " : "            ")} \r");
                        }
                    };
                }

                builder?.Build()?.Start();
                renderer.PrepareRender(cts.Token);
                Stopwatch sw1 = new();
                Log("Start render...");
                sw1.Restart();
                try
                {
                    if (blockWrite)
                    {
                        await renderer.GoRenderSync(cts.Token);
                    }
                    else if (renderByLayer)
                    {
                        await renderer.GoRenderByLayer(cts.Token);
                    }
                    else
                    {
                        await renderer.GoRender(cts.Token);
                    }
                    Log($"Render done,total elapsed {sw1}, avg elapsed {renderer.EachElapsedForPreparing.Average(t => t.TotalSeconds)} spf to prepare and {renderer.EachElapsed.Average(t => t.TotalSeconds)} spf to render");
                }
                catch (TaskCanceledException)
                {
                    Log("Render was canceled.");
                }
                catch (Exception ex)
                {
                    Log(ex, "Render error");
                    throw;
                }



                if (!string.IsNullOrWhiteSpace(diagReportPath))
                {
                    try
                    {
                        Log("Export diag data...");
                        DiagReportExporter.ExportCsv(diagReportPath!, renderer);
                        await PictureLifecycleTracker.ExportPictureLifecycleTrackerSnapshots(Path.Combine(diagReportPath!, $"PictureLifeCycle-{Guid.NewGuid()}.csv"));
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "Export diagReportPath CSV");
                    }
                }

                if (cts.IsCancellationRequested) return;

                Log("Finish writing video...");
                builder?.Finish((i) => Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, i, width, height), i, width, height), timeline.Duration);

                Log($"Releasing resources...");

                foreach (var item in clips)
                {
                    item?.Dispose();
                }
                renderer.builder = null;
            }

            void composeAudio(string resultPath)
            {
                var clips = JSONToIClips(timeline, assets, bpp).Where(c => c.ClipType == ClipMode.AudioClip || c.ClipType == ClipMode.VideoClip).ToArray();
                var tracks = JSONToISoundTracks(timeline, assets).ToArray();

                if (!clips.ArrayAny() && !tracks.ArrayAny())
                {
                    Log("No sound clips in the whole draft. returning...");
                    return;
                }

                Log($"Found {clips.Length} audio clips.");

                Log("Initializing all clips...");
                foreach (IClip clip in clips)
                {
                    clip.ReInit(8);
                }

                var writer = new AudioWriter(outputPath, 96000, 2, "pcm_s16le");

                var composer = new AudioComposer<float>
                {
                    Clips = clips,
                    SoundTracks = tracks,
                    Writer = writer
                };
                if (!Environment.GetCommandLineArgs().Contains("--nolog"))
                {
                    composer.OnProgressChanged += (s, e) =>
                    {
                        Console.Write($"Composing finished {s:p0}, ETA:{e:mm\\:ss} \r");
                    };
                }


                composer.Compose(fps, 96000, 2, 4096, cts.Token);
                Console.WriteLine();
                Console.WriteLine();
                writer.Finish();
                writer.Dispose();

                foreach (var item in clips)
                {
                    item?.Dispose();
                }
                return;
            }

            if (switches.TryGetValue("stopAfter", out var stopAfterStr) && int.TryParse(stopAfterStr, out var stopAfter)) //for Instrument testing, to avoid long time cause a lot huge log.
            {
                Log($"stopAfter is set, render will stop after {stopAfter}s.");

                Timer t = new((c) =>
                {
                    Log($"Time's up! stopAfter's {stopAfter}s timeout reached, exiting...");
                    cts.Cancel();
                    builder?.Interrupt();
                    Environment.Exit(255);
                });

                t.Change(stopAfter * 1000, Timeout.Infinite);

            }
            if (!noSigInt)
            {
                var cancelled = false;
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    if (cancelled) Process.GetCurrentProcess().Kill();
                    cancelled = true;
                    Console.WriteLine("You hit Ctrl-C! try cancelling render...");
                    Console.WriteLine("Hit Ctrl-C again to stop immediately.");

                    new Thread(async () =>
                    {
                        await cts.CancelAsync();
                        builder?.Interrupt();
                        Environment.Exit(255);
                    }).Start();
                };
                Log("Render job starts. Press Ctrl-C to interrupt render process.");
            }
            else
            {
                Log("Render job starts. Press Ctrl-C to stop.");
            }

            switch (target)
            {
                case "video":
                    await composeVideo(outputPath);
                    break;
                case "audio":
                    composeAudio(outputPath);
                    break;
                case "all":
                    var outputDir = switches.TryGetValue("outputIntermediatePath", out var iPath) ? iPath : Path.GetDirectoryName(outputPath);
                    outputDir ??= Environment.CurrentDirectory;
                    var ext = Path.GetExtension(outputPath);
                    string vidOutputPath = Path.Combine(outputDir, $"{project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                    string audOutputPath = Path.Combine(outputDir, $"{project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                    await composeVideo(vidOutputPath);
                    composeAudio(audOutputPath);
                    VideoAudioMuxer.MuxFromFiles(vidOutputPath, audOutputPath, outputPath, true);
                    try
                    {
                        File.Delete(vidOutputPath);
                        File.Delete(audOutputPath);
                    }
                    catch { }
                    break;
                default:
                    Log($"ERROR: Unknown target '{target}'.");
                    return 1;
            }

            Log($"All done, result is in '{outputPath}'.\r\nExiting...");
            return 0;
        }

        public static IClip[] JSONToIClips(DraftStructureJSON json, IDictionary<string, AssetItem> assets, IPicture.PicturePixelMode bpp)
        {
            var elements = (JsonSerializer.SerializeToElement(json).Deserialize<DraftStructureJSON>()?.Clips) ?? throw new NullReferenceException("Failed to cast ClipDraftDTOs to IClips."); //I don't want to write a lot of code to clone attributes from dto to IClip, it's too hard and may cause a lot of mystery bugs.

            var clipsList = new List<IClip>();

            foreach (var clip in elements.Cast<JsonElement>())
            {
                if (clip.TryGetProperty("ClipType", out var clipTypeProp)
                    && clipTypeProp.ValueKind == JsonValueKind.Number
                    && clipTypeProp.TryGetInt32(out var clipTypeValue)
                    && (ClipMode)clipTypeValue == ClipMode.MarkingClip)
                {
                    continue;
                }

                var clipInstance = PluginManager.CreateClip(clip);
                if (clipInstance.FilePath?.StartsWith('$') ?? false)
                {
                    try
                    {
                        var id = clipInstance.FilePath.Substring(1);
                        if (!assets.TryGetValue(id, out var value))
                        {
                            throw new FileNotFoundException($"Asset for clip {clipInstance.Name} ({clipInstance.TypeName}) '{id}' is not exist.");
                        }
                        clipInstance.FilePath = value.Path;
                    }
                    catch (InvalidOperationException)
                    {
                        //safe to ignore
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
                else if (string.IsNullOrEmpty(clipInstance.FilePath) && clip.TryGetProperty("FilePath", out var fp) && clipInstance.NeedFilePath)
                {
                    try
                    {
                        clipInstance.FilePath = fp.GetString();
                    }
                    catch (InvalidOperationException)
                    {
                        //safe to ignore
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
                clipInstance.ReInit(bpp);
                clipsList.Add(clipInstance);

            }
            return clipsList.ToArray();
        }

        public static ISoundTrack[] JSONToISoundTracks(DraftStructureJSON json, IDictionary<string, AssetItem> assets)
        {
            var elements = (JsonSerializer.SerializeToElement(json).Deserialize<DraftStructureJSON>()?.SoundTracks) ?? throw new NullReferenceException("Failed to cast SoundtrackDTOs to ISoundTracks.");

            var tracksList = new List<ISoundTrack>();

            foreach (var track in elements.Cast<JsonElement>())
            {
                var trackInstance = PluginManager.CreateSoundTrack(track);
                trackInstance.ExtraData = track.Deserialize<SoundtrackDTO>()?.MetaData ?? new();

                if (trackInstance.ExtraData.TryGetValue("Volume", out var trackVolObj))
                {
                    trackInstance.Volume = trackVolObj switch
                    {
                        double d => (float)d,
                        float f => f,
                        System.Text.Json.JsonElement je when je.TryGetDouble(out var jd) => (float)jd,
                        _ when float.TryParse(trackVolObj?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pv) => pv,
                        _ => 1f
                    };
                }

                if (trackInstance.FilePath?.StartsWith('$') ?? false)
                {
                    try
                    {
                        var id = trackInstance.FilePath.Substring(1);
                        if (!assets.TryGetValue(id, out var value))
                        {
                            throw new FileNotFoundException($"Asset for clip {trackInstance.Name} ({trackInstance.TypeName}) '{id}' is not exist.");
                        }
                        trackInstance.FilePath = value.Path;
                    }
                    catch (InvalidOperationException)
                    {
                        //safe to ignore
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
                else if (string.IsNullOrEmpty(trackInstance.FilePath) && track.TryGetProperty("FilePath", out var fp) && trackInstance.NeedFilePath)
                {
                    try
                    {
                        trackInstance.FilePath = fp.GetString();
                    }
                    catch (InvalidOperationException)
                    {
                        //safe to ignore
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }

                trackInstance.ReInit();
                tracksList.Add(trackInstance);
            }

            return tracksList.ToArray();
        }

        public static string GetInfo(bool all = false)
        {
            StringBuilder builder = new StringBuilder();
            var renderType = typeof(Renderer).Assembly;
            var baseType = typeof(IPluginBase).Assembly;
            string renderHash = "";
            try
            {
                renderHash = !renderType.IsDynamic && Path.Exists(renderType.Location) ? HashServices.ComputeFileHash(renderType.Location) : "unknown";
            }
            catch { renderHash = "unknown"; }
            string baseHash = "";
            try
            {
                baseHash = !baseType.IsDynamic && Path.Exists(baseType.Location) ? HashServices.ComputeFileHash(baseType.Location) : "unknown";
            }
            catch { baseHash = "unknown"; }
            builder.AppendLine($"BaseAPI Definition: V{IPluginBase.CurrentPluginAPIVersion}({baseType.GetName().Version}), hash:{baseHash}");
            builder.AppendLine($"Core render library: v{renderType.GetName().Version}, hash:{renderHash}");
            if (all)
            {
                List<string> printedAsb = new();
                foreach (var asb in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (printedAsb.Contains(asb.FullName))
                        {
                            continue;
                        }
                        printedAsb.Add(asb.FullName);
                        var guid = asb.GetCustomAttribute<GuidAttribute>()?.Value ?? "unknown";
                        string asbHash = "";
                        try
                        {
                            asbHash = !asb.IsDynamic && Path.Exists(asb.Location) ? HashServices.ComputeFileHash(asb.Location) : "unknown";
                        }
                        catch { asbHash = "unknown"; }

                        builder.AppendLine($"Assembly {asb.FullName}, {asb.GetName().Version} GUID:{guid} hash:{asbHash}");
                    }
                    catch
                    {
                        builder.AppendLine($"{asb.FullName}, cannot get assembly info.");
                    }
                    finally
                    {
                        builder.AppendLine();
                    }
                }

                builder.AppendLine(
                    """
                    The standalone render is the part of projectFrameCut. To use this standalone render, you MUST have a license/permission to use the projectFrameCut itself.
                    Most of the project, and this standalone render is licensed under Apache License.
                    The use of other open-source project can be found in readme of the repository:
                    https://github.com/hexadecimal0x12e/projectFrameCut/

                    """
                    );
            }
            return builder.ToString();
        }

        private static Assembly? TryResolveAssembly(string name, string[] paths, bool keepInMemory)
        {
            Log($"Try loading assembly {name}...");
            foreach (var item in paths)
            {
                var p = Path.Combine(item, name + ".dll");
                if (!File.Exists(p)) continue;
                Log($"Found assembly {name} in {p}.");
                if (keepInMemory)
                {
                    var fs = File.OpenRead(p);
                    var buf = new byte[fs.Length];
                    fs.ReadExactly(buf);
                    fs.Dispose();
                    return Assembly.Load(buf);
                }
                else
                {
                    return Assembly.LoadFile(p);
                }

            }
            Log($"Assembly {name} not found.");
            return null;
        }
    }
}
