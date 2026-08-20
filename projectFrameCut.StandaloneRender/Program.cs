using FFmpeg.AutoGen;
using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Benchmark;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.WindowsRender;
using projectFrameCut.Render.HwAccelEngine;
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
using static projectFrameCut.Shared.Logger;
using projectFrameCut.Render.Effect;

using projectFrameCut.Drawing.Base.Picture;



#if DIAGHUB_ENABLE_TRACE_SYSTEM
using Microsoft.DiagnosticsHub;
#endif

namespace projectFrameCut.StandaloneRender
{
    public class Program
    {
        const int PluginAPIVersion = 1;

        static readonly JsonSerializerOptions savingOpts = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

        public static async Task<int> Main(string[] args)
        {
            if (!args.Contains("--nolog"))
            {
                Console.ForegroundColor = ConsoleColor.White;
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
                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    if (assembly is not null)
                    {
                        var ProgramConfig = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown config";
                        var ProgramCommit = new string((assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Split('+').Last().ToArray());
                        var AssemblyName = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "projectFrameCut StandaloneRender";
                        Console.WriteLine($"{AssemblyName} v{assembly.GetName().Version} {ProgramConfig}@{ProgramCommit}");
                        Console.WriteLine("Copyright (c) hexadecimal0x12e 2025-2026. https://github.com/hexadecimal0x12e/projectFrameCut/");
                        Console.WriteLine();
                    }

                }
                catch { }

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
                        - reencode: Decode an existing video and re-encode it. Useful for codec testing.

                    Arguments:
                    Mode 'render':
                        -project=<project dir>
                        -output=<output file>
                        -output_options=<width>,<height>,<fps>,<pixel format>,<encoder>
                        [-target=<video|audio|all>]
                        [-assetDbFile=<path to database.json file>]
                        [-pluginRoot=<path to plugin root>]
                        [-maxParallelThreads=<number>]
                        [-maxPendingWriteFrames=<number>]
                        [-oneByOneRender=<true|false> -renderByLayer=<true|false> -prepareInWorker=<true|false> -enableThreadAffinity=<true|false>]
                        [-renderWorkerAffinity=<cpu0,cpu1,cpu2... | cpuStart-cpuEnd>]
                        [-multiAccelerator=<true|false>]
                        [-acceleratorType=<auto|cuda|opencl|cpu> or -acceleratorDeviceId=<device id> or -acceleratorDeviceIds=<device ids|all>]
                        [-GCOptions=0,1,2]
                        [-outputIntermediatePath=<intermediate output path>]
                        [-FFmpegLibraryPath=<path to FFmpeg libraries>]
                        [-diagReportPath=<path diag report output directory>]
                        [-preferHwAccelDecoder=<true|false>]
                        [-PictureResizer=<cpu|hwaccel>]
                        [-VideoFrameDiskCacheRoot=<path to video frame disk cache root>]
                        [-enableDiskCacheRouting=<true|false> or -forceUseDiskCache=<true|false>]
                        [-diskCacheThreshold=<0.1-0.95>]
                        [-diskCacheMaxFrameCount=<number>]
                        [-videoBuilderDiskCacheRoot=<path>]
                        [-ApproximateMixture=<true|false>]



                    Mode 'bench' [sub-modes]:
                        bench render    - Render pipeline benchmark (default)
                        bench encode    - VideoBuilder cache & encode throughput test
                        bench decode    - IVideoSource decode performance test

                    Common arguments (all sub-modes):
                        [-writeToNull=<true|false>]

                    Sub-mode 'render' arguments:
                        [-multiAccelerator=<true|false>]
                        [-acceleratorType=<auto|cuda|opencl|cpu> or -acceleratorDeviceId=<device id> or -acceleratorDeviceIds=<device ids|all>]
                        [-maxParallelThreads=<number>]
                        [-maxPendingWriteFrames=<number>]
                        [-oneByOneRender=<true|false> -renderByLayer=<true|false> -prepareInWorker=<true|false> -enableThreadAffinity=<true|false>]
                        [-renderWorkerAffinity=<cpu0,cpu1,cpu2... | cpuStart-cpuEnd>]
                        [-GCOptions=0,1,2]
                        [-preferHwAccelDecoder=<true|false>]
                        [-PictureResizer=<cpu|hwaccel>]
                        [-ApproximateMixture=<true|false>]

                    Sub-mode 'encode' arguments:
                        [-output=<output file>]                         (if omitted → BlackHoleWriter, no real file)
                        [-encoder=<codec name>]                         (default: libx264)
                        [-pixelFormat=<AVPixelFormat name>]             (default: yuv420p)
                        [-totalFrames=<number>]                         (default: 600)
                        [-fps=<frame rate>]                             (default: 60)
                        [-enableDiskCacheRouting=<true|false>]          (test disk cache spillover)
                        [-diskCacheThreshold=<0.1-0.95>]                (default: 0.85)
                        [-maxPendingWriteFrames=<number>]

                    Sub-mode 'decode' arguments:
                        -source=<video file path>                   (required)
                        [-maxFrames=<number>]                       (limit decoded frames)
                        [-VideoFrameDiskCache=<true|false>]
                        [-preferHwAccelDecoder=<true|false>]

                    Mode 'reencode':
                        -source=<input video>                       (required)
                        -output=<output file>                       (required)
                        [-encoder=<codec name>]                     (default: libx264)
                        [-pixelFormat=<AVPixelFormat name>]         (default: AV_PIX_FMT_YUV420P)
                        [-maxFrames=<number>]                       (limit frames to reencode)
                        [-preferHwAccelDecoder=<true|false>]
                        [-preferHwAccelEncoder=<true|false>]
                        [-bitRate=<bitrate in bps>]                 (optional, encoder bitrate)

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
            Log($"{PluginManager.LoadedPlugins.Count} plugins loaded.");
            #endregion

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
                case "bench":
                    // ── 解析子模式（render / encode / decode）──────────
                    var benchSubMode = "render";
                    if (args.Length > 1 && !args[1].StartsWith('-') && !args[1].Contains('='))
                    {
                        benchSubMode = args[1].ToLowerInvariant();
                    }
                    else if (switches.TryGetValue("benchMode", out var switchBenchMode))
                    {
                        benchSubMode = switchBenchMode.ToLowerInvariant();
                    }

                    if (benchSubMode != "render" && benchSubMode != "encode" && benchSubMode != "decode")
                    {
                        Log($"ERROR: Unknown bench sub-mode '{benchSubMode}'. Available: render, encode, decode.", "error");
                        return 1;
                    }

                    Log($"Bench sub-mode: {benchSubMode}");

                    // 只有 render（渲染管线测试）需要 GPU 加速器
                    if (benchSubMode == "render" || switches.ContainsKey("preferHwAccelDecoder"))
                    {
                        var accelResult1 = InitAccel(switches);
                        if (accelResult1 != 0)
                        {
                            return accelResult1;
                        }
                    }

                    try
                    {
                        return benchSubMode switch
                        {
                            "encode" => await GoBenchEncode(switches),
                            "decode" => await GoBenchDecode(switches),
                            _ => await GoBenchRender(switches),
                        };
                    }
                    catch (TaskCanceledException)
                    {
                        Log("Benchmark task was canceled.");
                        return 255;
                    }
                case "reencode":
                    // 初始化 FFmpeg（与 render 和 bench 使用相同的模式）
                    Log("Initializing FFmpeg for reencode...");
                    DynamicallyLoadedBindings.EnableAutoInitialization = false;
                    FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
                    ffmpeg.RootPath = switches.GetOrAdd("FFmpegLibraryPath", AppContext.BaseDirectory);
                    if (FFmpeg.AutoGen.DynamicallyLoadedBindings.TryInitialize())
                    {
                        FFmpegHelper.SetupFFmpegLogging(ffmpeg.AV_LOG_INFO);
                        Log($"FFmpeg library: version {ffmpeg.av_version_info()}, {ffmpeg.avcodec_license()}");
                    }
                    else
                    {
                        Log($"FFmpeg library failed to load.", "error");
                        return 1;
                    }
                    FFmpegHelper.SetupFFmpegLogging();

                    if (switches.ContainsKey("preferHwAccelDecoder"))
                    {
                        var accelResult1 = InitAccel(switches);
                        if (accelResult1 != 0) return accelResult1;
                    }

                    try
                    {
                        return await GoReencode(switches);
                    }
                    catch (TaskCanceledException)
                    {
                        Log("Reencode task was canceled.");
                        return 255;
                    }
                case "list_accels":
                    Context context = Context.Create(builder => builder.Default().EnableAlgorithms());
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

        private static bool TryParseCpuIndexList(string value, out int[] cpuIndexes, out string error)
        {
            var result = new SortedSet<int>();
            foreach (var token in value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.Contains('-', StringComparison.Ordinal))
                {
                    var parts = token.Split('-', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], out int start)
                        || !int.TryParse(parts[1], out int end))
                    {
                        cpuIndexes = [];
                        error = $"Invalid CPU range '{token}'.";
                        return false;
                    }

                    if (start > end)
                    {
                        (start, end) = (end, start);
                    }

                    if (start < 0)
                    {
                        cpuIndexes = [];
                        error = $"CPU index cannot be negative in range '{token}'.";
                        return false;
                    }

                    for (int i = start; i <= end; i++)
                    {
                        result.Add(i);
                    }
                    continue;
                }

                if (!int.TryParse(token, out int cpuIndex) || cpuIndex < 0)
                {
                    cpuIndexes = [];
                    error = $"Invalid CPU index '{token}'.";
                    return false;
                }

                result.Add(cpuIndex);
            }

            if (result.Count == 0)
            {
                cpuIndexes = [];
                error = "No CPU indexes were provided.";
                return false;
            }

            cpuIndexes = result.ToArray();
            error = string.Empty;
            return true;
        }

        private static int InitAccel(ConcurrentDictionary<string, string> switches)
        {
            try
            {

                Context context = Context.Create(builder => builder.Default().EnableAlgorithms());
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
                AcceleratorsManager.AcceleratorsForRendering = accelerators;
                AcceleratorsManager.IsRendering = true;

                if (!switches.TryGetValue("PictureResizer", out var c) || c != "hwaccel") Drawing.Processing.Resizing.PictureResizer.Default = new Render.Effect.HwAccelPictureResizer();
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
            DynamicallyLoadedBindings.EnableAutoInitialization = false;
            FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            ffmpeg.RootPath = switches.GetOrAdd("FFmpegLibraryPath", AppContext.BaseDirectory);
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
            outputEncoder = outputOptions[4];
            if (!Enum.TryParse(outputOptions[3], out outputFormat) || outputFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            {
                Log($"ERROR: Pixel format {outputOptions[3]} not found in AVPixelFormat.");
                return 1;
            }
            var fmpBPP = FFmpegHelper.GetAVPixelFormatBitsPerPixel(outputFormat);
            var use16Bit = fmpBPP > 8;

            if (fmpBPP <= 0)
            {
                Log($"Cannot auto determine bits per pixel for pixel format {outputOptions[3]}, auto-fallback to 16bpp rendering mode.", "warn");
                use16Bit = trace;
            }

            var bpp = use16Bit ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture;

            if (!switches.ContainsKey("output"))
            {
                Log("ERROR: No output path specified. Use -output=<output file> to specify the output path.");
                return 1;
            }
            var outputPath = switches["output"].Replace("{CurrentTime}", DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            Log($"Output options: {width}x{height} @ {fps} fps, pixel format: {outputFormat}, encoder: {outputEncoder}, 16 bit render:{use16Bit}({fmpBPP} bpp output)");

            #endregion

            #region read args
            if (!switches.ContainsKey("project"))
            {
                Log("No project file specified. Use -project=<project root dir> to specify the project file.", "error");
                return 1;
            }
            var workingPath = switches["project"];
            if (string.IsNullOrWhiteSpace(workingPath) || !Directory.Exists(workingPath))
            {
                Log("Invalid project path.", "error");
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
                    Log($"Invalid GCOptions option '{gcopt}', must be 0, 1 or 2. Default to 0.", "warn");
                    GCOption = 0;
                }
            }

            if (GCOption == 2)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            }

            Log($"GC Option:{GCOption}");

            int maxPendingWriteFrames = 0; // 0 = use Renderer default
            if (switches.TryGetValue("maxPendingWriteFrames", out var mpwf))
            {
                if (!int.TryParse(mpwf, out maxPendingWriteFrames) || maxPendingWriteFrames < 0)
                {
                    Log($"Invalid maxPendingWriteFrames value '{mpwf}', must be a non-negative integer. Default will be used.", "warn");
                    maxPendingWriteFrames = 0;
                }
                else
                {
                    Log($"Max pending write frames: {maxPendingWriteFrames}");
                }
            }
            double maxPendingWriteFramesDouble = maxPendingWriteFrames;

            ConcurrentDictionary<string, AssetItem> assets = new();

            if (switches.TryGetValue("assetDbFile", out var assetDbPath))
            {
                var globalAsset = JsonSerializer.Deserialize<ConcurrentDictionary<string, AssetItem>>(File.ReadAllText(assetDbPath))
                    ?? new ConcurrentDictionary<string, AssetItem>();
                DecoderContextPJFCProject.GlobalAssetGetter = new(() => globalAsset);
                assets = new(globalAsset);
                Log($"Read {assets.Count} assets from asset database.");
            }

            string YesNo(bool b) => b ? "Yes" : "No";

            int maxParallelThreads = Environment.ProcessorCount;
            bool oneByOneRender = false, renderByLayer = false, prepareInWorker = false, enableThreadAffinity = true;
            int[]? renderWorkerAffinityCpuIndexes = null, preparerAffinityCpuIndexes = null;
            if (!bool.TryParse(switches.GetOrAdd("oneByOneRender", "false"), out oneByOneRender) && oneByOneRender) oneByOneRender = false;
            if (!bool.TryParse(switches.GetOrAdd("renderByLayer", "false"), out renderByLayer)) renderByLayer = false;
            if (!bool.TryParse(switches.GetOrAdd("prepareInWorker", "false"), out prepareInWorker)) prepareInWorker = false;
            if (!bool.TryParse(switches.GetOrAdd("enableThreadAffinity", "true"), out enableThreadAffinity)) enableThreadAffinity = true;
            if (switches.TryGetValue("renderWorkerAffinity", out var renderWorkerAffinityRaw) && !string.IsNullOrWhiteSpace(renderWorkerAffinityRaw))
            {
                if (!TryParseCpuIndexList(renderWorkerAffinityRaw, out renderWorkerAffinityCpuIndexes, out var renderWorkerAffinityError))
                {
                    Log($"ERROR: Invalid renderWorkerAffinity '{renderWorkerAffinityRaw}': {renderWorkerAffinityError}", "error");
                    return 1;
                }

                try
                {
                    ThreadAffinityHelper.BuildAffinityMask(renderWorkerAffinityCpuIndexes);
                }
                catch (Exception ex)
                {
                    Log($"ERROR: Invalid renderWorkerAffinity '{renderWorkerAffinityRaw}': {ex.Message}", "error");
                    return 1;
                }

                Log($"Manual render worker CPU affinity: {string.Join(", ", renderWorkerAffinityCpuIndexes)}");
            }

            if (!oneByOneRender)
            {
                if (enableThreadAffinity || (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0)
                {
                    try
                    {
                        var group = ThreadAffinityHelper.GetCpuCoreGroups();
                        foreach (var item in group)
                        {
                            Log($"CPU Cores ({string.Join(",", item.CpuIndexes)})'s Priority:{(item.Capacity ?? 0) + (item.EfficiencyClass ?? 0)}");
                        }
                        if (!int.TryParse(switches.TryGetValue("maxParallelThreads", out var p) ? p : "-1", out maxParallelThreads) || maxParallelThreads <= 0)
                            maxParallelThreads = (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0
                                ? renderWorkerAffinityCpuIndexes!.Length
                                : group.OrderBy(c => (c.Capacity ?? 0) + (c.EfficiencyClass ?? 0)).LastOrDefault()?.CpuIndexes?.Count ?? Environment.ProcessorCount;

                    }
                    catch
                    {
                        if (!int.TryParse(switches.TryGetValue("maxParallelThreads", out var p) ? p : "-1", out maxParallelThreads) || maxParallelThreads <= 0)
                            maxParallelThreads = (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0 ? renderWorkerAffinityCpuIndexes!.Length : Environment.ProcessorCount;
                    }
                    maxParallelThreads *= 2;
                    preparerAffinityCpuIndexes = renderWorkerAffinityCpuIndexes.ArrayAny() ? Enumerable.Range(0, Environment.ProcessorCount).Except(renderWorkerAffinityCpuIndexes).ToArray() : [];
                }
                else
                {
                    if (!int.TryParse(switches.TryGetValue("maxParallelThreads", out var p) ? p : "8", out maxParallelThreads)) maxParallelThreads = Environment.ProcessorCount;
                }

                var workerAffinityLabel = (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0
                    ? $"manual({string.Join(",", renderWorkerAffinityCpuIndexes ?? Array.Empty<int>())})"
                    : (enableThreadAffinity ? "auto" : "disabled");
                Log($"Working in parallel mode, max {maxParallelThreads} threads, Render in layer:{YesNo(renderByLayer)}, Prepare in worker:{YesNo(prepareInWorker)}, Preparer affinity:{YesNo(enableThreadAffinity)}, Worker affinity:{workerAffinityLabel}");

            }
            else
            {
                Log("Working in serial mode.");
            }


            bool hwAccelDecode = bool.TryParse(switches.GetOrAdd("preferHwAccelDecoder", "false"), out var hwAccelDecodeValue) && hwAccelDecodeValue;
            bool hwAccelEncode = bool.TryParse(switches.GetOrAdd("preferHwAccelEncoder", "false"), out var hwAccelEncodeValue) && hwAccelEncodeValue;
            InternalPluginBase.HWAccelDecodeOptionGetter = new(() => hwAccelDecode);
            InternalPluginBase.HWAccelEncodeOptionGetter = new(() => hwAccelEncode);

            PictureLifecycleTracker.Enabled = trace && !Renderer.IsProfilerAttached;
            PictureLifecycleTracker.TrackCollection = trace && !Renderer.IsProfilerAttached;

            if (switches.TryGetValue("VideoFrameDiskCacheRoot", out var vfdcRoot) && Directory.Exists(vfdcRoot))
            {
                IVideoSource.EnableDiskCache = true;
                VideoFrameDiskCache.CacheBaseDir = vfdcRoot;
            }
            else
            {
                IVideoSource.EnableDiskCache = false;
            }

            var diskCacheRoutingEnabled = bool.TryParse(switches.GetOrAdd("enableDiskCacheRouting", "false"), out var useDiskCache) && useDiskCache;
            if (diskCacheRoutingEnabled)
            {
                var threshold = switches.TryGetValue("diskCacheThreshold", out var dctStr)
                    && double.TryParse(dctStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dct)
                    ? Math.Clamp(dct, 0.1, 0.95) : 0.7;
                var cacheRoot = switches.TryGetValue("videoBuilderDiskCacheRoot", out var vcr) && !string.IsNullOrWhiteSpace(vcr) ? vcr : "(default temp)";
                var maxPending = maxPendingWriteFrames > 0 ? maxPendingWriteFrames.ToString() : "(auto)";
                var maxFrames = switches.TryGetValue("diskCacheMaxFrameCount", out var mfcStr) && int.TryParse(mfcStr, out var mfc) ? mfc : 0;
                Log($"VideoBuilder disk cache routing: Enabled, threshold: {threshold:P0} of max pending ({maxPending}), max frames on disk: {(maxFrames > 0 ? maxFrames.ToString() : "unlimited")}, cache root: {cacheRoot}");
            }

            Log($"Video decoding: Prefer HWAccel Decode: {YesNo(hwAccelDecode)} Encode: {YesNo(hwAccelEncode)}, Disk cache: {YesNo(IVideoSource.EnableDiskCache)} {(IVideoSource.EnableDiskCache ? $"(cache dir: {VideoFrameDiskCache.CacheBaseDir})" : "")}, Disk buffer: {YesNo(diskCacheRoutingEnabled)}");

            ClassicOverlayMixture.EnableApproximatePath = bool.TryParse(switches.GetOrAdd("ApproximateMixture", "false"), out var approximateMixture) && approximateMixture;

            Log($"ClassicOverlayMixture approximate path: {YesNo(ClassicOverlayMixture.EnableApproximatePath)}");

            if (Enum.TryParse<EffectImplementType>(switches.GetOrAdd("ForcePreferToType", "NotSpecified"), out var forcePreferToType) && forcePreferToType != EffectImplementType.NotSpecified)
            {
                EffectHelper.ForcePreferToType = forcePreferToType;
                Log($"Using forced effect implement type: {forcePreferToType}");
            }

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
            Renderer renderer = null!;
            var noSigInt = !Environment.GetCommandLineArgs().Contains("--noSigInt");
            var keyboardInterrupt = !Environment.GetCommandLineArgs().Contains("--keyboardInterrupt");
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
                    BlockWrite = oneByOneRender,
                    EnableDiskCacheRouting = bool.TryParse(switches.GetOrAdd("enableDiskCacheRouting", "false"), out var useDiskCache) && useDiskCache,
                    ForceUseDiskCache = bool.TryParse(switches.GetOrAdd("forceUseDiskCache", "false"), out var forceUseDiskCache) && forceUseDiskCache,
                    DiskCacheMaxPendingFrames = maxPendingWriteFrames > 0 ? maxPendingWriteFrames : 0,
                    DiskCacheMaxFrameCount = switches.TryGetValue("diskCacheMaxFrameCount", out var mfcStr) && int.TryParse(mfcStr, out var mfc) ? Math.Max(mfc, 0) : 0,
                    DiskCacheDirectory = switches.TryGetValue("videoBuilderDiskCacheRoot", out var cacheRoot) && !string.IsNullOrWhiteSpace(cacheRoot) ? cacheRoot : null,
                };

                if (builder.EnableDiskCacheRouting && switches.TryGetValue("diskCacheThreshold", out var dctStr)
                    && double.TryParse(dctStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dct))
                {
                    builder.DiskCacheThreshold = Math.Clamp(dct, 0.1, 0.95);
                }

                renderer = new Renderer
                {
                    builder = builder,
                    Clips = clips,
                    Duration = timeline.Duration,
                    LogProcessStack = !string.IsNullOrWhiteSpace(diagReportPath),
                    LogRenderState = (bool.TryParse(switches.TryGetValue("LogState", out var ls2) ? ls2 : "false", out var lsbool) && lsbool),
                    LogStaticsData = false,
                    GCOption = GCOption,
                    Use16Bit = use16Bit,
                    EnableRenderWatchdogForceStart = false,
                    MaxRenderScheduleTimeout = 0,
                    MinSchedulePreparedFrames = 1,
                    MaxThreads = maxParallelThreads,
                    MaxPendingWriteFrames = maxPendingWriteFrames > 0 ? maxPendingWriteFrames : 0,
                    RenderByLayers = renderByLayer,
                    PrepareInWorkerThreads = prepareInWorker,
                    OneByOneRender = oneByOneRender,
                    EnableThreadAffinity = enableThreadAffinity,
                    WorkerCPUCoreIndexs = renderWorkerAffinityCpuIndexes,
                };

#if DIAGHUB_ENABLE_TRACE_SYSTEM
                var FrameDoneMark = new UserMarks("ProgressMark");
#endif

                if (!Environment.GetCommandLineArgs().Contains("--nolog"))
                {
                    renderer.OnProgressChanged += (s, e) =>
                    {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
                        FrameDoneMark.Emit($"Progress: {s:p0} ({renderer.CurrentFinished}/{renderer.Duration}), ETA: {e:hh\\:mm\\:ss}, FPS: {renderer.CurrentFps:n2}");
#endif
                        double writeBufFree = maxPendingWriteFrames > 0 ? builder.PendingWriteCount / maxPendingWriteFramesDouble : 0;
                        if (renderer.CurrentSecondPerFrame <= 1.5)
                        {
                            Console.Write($"Rendering finished {s:p0}, ETA:{e:hh\\:mm\\:ss}, FPS:{renderer.CurrentFps:n2}, buffer: {writeBufFree:p2} on ram used and {builder.FramesOnDisk} on disk          \r");
                        }
                        else
                        {
                            Console.Write($"Rendering finished {s:p0}, ETA:{e:hh\\:mm\\:ss}, {(1 / renderer.CurrentFps):n2} second per frame, buffer: {writeBufFree:p2} on ram used and {builder.FramesOnDisk} on disk        \r");
                        }
                    };
                }

                builder?.Build(preparerAffinityCpuIndexes)?.Start();
                renderer.PrepareRender(cts.Token);
                Stopwatch sw1 = new();
                Log("Start render...");
                sw1.Restart();
                try
                {
                    await renderer.GoRender(cts.Token);
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
                builder?.Finish((i) => Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, i, width, height), i, width, height), timeline.Duration, (c, p) => Console.Write($"Frame #{c} added, completed {p:p2}.    \r"));

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
            Task? keyboardTask = null;
            int KeyQPressCount = 0;

            if (switches.TryGetValue("stopAfter", out var stopAfterStr) && int.TryParse(stopAfterStr, out var stopAfter)) //for Instrument testing, to avoid long time cause a lot huge log.
            {
                Log($"stopAfter is set, render will stop after {stopAfter}s.");

                Timer t = new(async (c) =>
                {
                    Log($"Time's up! stopAfter's {stopAfter}s timeout reached, exiting...");
                    await cts.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    builder?.Interrupt();
                    Environment.Exit(255);
                });

                t.Change(stopAfter * 1000, Timeout.Infinite);

            }
            if (keyboardInterrupt)
            {
                if (!Console.IsInputRedirected)
                {
                    keyboardTask = Task.Run(async () =>
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            if (Console.KeyAvailable)
                            {
                                var key = Console.ReadKey(intercept: true);

                                switch (key.Key)
                                {
                                    case ConsoleKey.Escape:
                                    case ConsoleKey.Q:
                                        KeyQPressCount++;
                                        if (KeyQPressCount > 5)
                                        {
                                            Console.WriteLine("Cancel signal receive! try cancelling render...");
                                            await cts.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10));
                                            builder?.Interrupt();
                                            Environment.Exit(255);
                                        }
                                        else
                                        {
                                            Console.WriteLine($"Hit Escape/Q again to cancel render.");
                                        }
                                        break;

                                    case ConsoleKey.S:
                                        if (renderer is null)
                                        {
                                            Log("Render is not initialized yet.");
                                        }
                                        else
                                        {
                                            Log(renderer.GetRendererStatusInfo(includeQueueAndWriterStats: true), "STAT");
                                        }
                                        break;
                                    case ConsoleKey.P:

                                        break;
                                }
                            }

                            await Task.Delay(50);
                        }
                    });

                    Console.WriteLine($"Render job starts. Hit Escape/Q to cancel, or S to show status.");
                }


            }
            else if (!noSigInt)
            {
                var cancelled = false;
                Console.CancelKeyPress += async (s, e) =>
                {
                    e.Cancel = true;
                    if (cancelled) Process.GetCurrentProcess().Kill();
                    cancelled = true;
                    Console.WriteLine("You hit Ctrl-C! try cancelling render...");
                    Console.WriteLine("Hit Ctrl-C again to stop immediately.");

                    await cts.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    builder?.Interrupt();
                    Environment.Exit(255);
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
                    string vidOutputPath = Path.Combine(outputDir, $"{project.ProjectName}_Intermediate_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                    string audOutputPath = Path.Combine(outputDir, $"{project.ProjectName}_Intermediate_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                    await composeVideo(vidOutputPath);
                    composeAudio(audOutputPath);
                    Console.WriteLine("Composing audio and video... this may take a few seconds.");
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

            Log($"All done! Your result file is here:{Environment.NewLine}{outputPath}");
            Environment.SetEnvironmentVariable("projectFrameCut_LastOutput", outputPath, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("projectFrameCut_RenderFinished", "1", EnvironmentVariableTarget.Process);
            return 0;
        }

        /// <summary>
        /// 运行内置基准测试：使用 <see cref="BenchmarkSourceGenerator.GetDraftStructure"/>
        /// 生成的测试项目进行渲染管线性能测试，输出详细的帧时间统计。
        /// </summary>
        private static async Task<int> GoBenchRender(ConcurrentDictionary<string, string> switches)
        {
            // ── 解析基准测试参数 ──────────────────────────────
            const int width = 1920;
            const int height = 1080;
            const int fps = 60;

            // ── 生成测试结构 ──────────────────────────────────
            var clips = BenchmarkSourceGenerator.GetDraftStructure();
            if (clips is null || clips.Length == 0)
            {
                Log("ERROR: Benchmark structure is empty.", "error");
                return 1;
            }
            Log($"Generated {clips.Length} test clips.");

            uint duration = 0;
            foreach (var clip in clips)
            {
                duration = Math.Max(clip.StartFrame + clip.Duration, duration);
            }

            Log($"Running benchmark with duration: {duration} frames, resolution: {width}x{height}, fps: {fps}.");

            Log("Initializing clips...");
            foreach (var clip in clips)
            {
                clip.ExtraData ??= new Dictionary<string, object>();
                clip.ReInit(IPicture.PicturePixelMode.BytePicture);
                Log($"Clip {clip.Name} initialized: {clip.ClipType}, StartFrame: {clip.StartFrame}, Duration: {clip.Duration}");
            }
            VideoBuilder? builder = null;

            if (switches.TryGetValue("writeToNull", out var writeToStr) && bool.TryParse(writeToStr, out var writeToNull) && writeToNull)
            {
                builder = new VideoBuilder("/dev/null", width, height, fps, "BlackHoleWriter", "")
                {
                    Duration = duration,
                };
                Log("Write to /dev/null (BlackholeVideoWriter) enabled.");
            }
            else
            {
                Log("Write to /dev/null (BlackholeVideoWriter) disabled.");
            }

            var cts = new CancellationTokenSource();

            #region read args
            var GCOption = 0;
            if (switches.TryGetValue("GCOptions", out var gcopt))
            {
                if (!int.TryParse(gcopt, out GCOption) || (GCOption < 0 || GCOption > 2))
                {
                    Log($"Invalid GCOptions option '{gcopt}', must be 0, 1 or 2. Default to 0.", "warn");
                    GCOption = 0;
                }
            }

            if (GCOption == 2)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            }

            int maxPendingWriteFrames = 0; // 0 = use Renderer default
            if (switches.TryGetValue("maxPendingWriteFrames", out var mpwf))
            {
                if (!int.TryParse(mpwf, out maxPendingWriteFrames) || maxPendingWriteFrames < 0)
                {
                    Log($"Invalid maxPendingWriteFrames value '{mpwf}', must be a non-negative integer. Default will be used.", "warn");
                    maxPendingWriteFrames = 0;
                }
                else
                {
                    Log($"Max pending write frames: {maxPendingWriteFrames}");
                }
            }

            string YesNo(bool b) => b ? "Yes" : "No";

            int maxParallelThreads = Environment.ProcessorCount;
            bool oneByOneRender = false, renderByLayer = false, prepareInWorker = false, enableThreadAffinity = true;
            int[]? renderWorkerAffinityCpuIndexes = null, preparerAffinityCpuIndexes = null;
            if (!bool.TryParse(switches.GetOrAdd("oneByOneRender", "false"), out oneByOneRender) && oneByOneRender) oneByOneRender = false;
            if (!bool.TryParse(switches.GetOrAdd("renderByLayer", "false"), out renderByLayer)) renderByLayer = false;
            if (!bool.TryParse(switches.GetOrAdd("prepareInWorker", "false"), out prepareInWorker)) prepareInWorker = false;
            if (!bool.TryParse(switches.GetOrAdd("enableThreadAffinity", "true"), out enableThreadAffinity)) enableThreadAffinity = true;
            if (!bool.TryParse(switches.GetOrAdd("boostMode", "false"), out var boostMode)) boostMode = false;
            if (switches.TryGetValue("renderWorkerAffinity", out var renderWorkerAffinityRaw) && !string.IsNullOrWhiteSpace(renderWorkerAffinityRaw))
            {
                if (!TryParseCpuIndexList(renderWorkerAffinityRaw, out renderWorkerAffinityCpuIndexes, out var renderWorkerAffinityError))
                {
                    Log($"ERROR: Invalid renderWorkerAffinity '{renderWorkerAffinityRaw}': {renderWorkerAffinityError}", "error");
                    return 1;
                }

                try
                {
                    ThreadAffinityHelper.BuildAffinityMask(renderWorkerAffinityCpuIndexes);
                }
                catch (Exception ex)
                {
                    Log($"ERROR: Invalid renderWorkerAffinity '{renderWorkerAffinityRaw}': {ex.Message}", "error");
                    return 1;
                }

                Log($"Manual render worker CPU affinity: {string.Join(", ", renderWorkerAffinityCpuIndexes)}");
            }

            if (!oneByOneRender)
            {
                if (enableThreadAffinity || (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0)
                {
                    try
                    {
                        var group = ThreadAffinityHelper.GetCpuCoreGroups();
                        foreach (var item in group)
                        {
                            Log($"CPU Cores ({string.Join(",", item.CpuIndexes)})'s Priority:{(item.Capacity ?? 0) + (item.EfficiencyClass ?? 0)}");
                        }
                        if (!int.TryParse(switches.TryGetValue("maxParallelThreads", out var p) ? p : "-1", out maxParallelThreads) || maxParallelThreads <= 0)
                            maxParallelThreads = (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0
                                ? renderWorkerAffinityCpuIndexes!.Length
                                : group.OrderBy(c => (c.Capacity ?? 0) + (c.EfficiencyClass ?? 0)).LastOrDefault()?.CpuIndexes?.Count ?? Environment.ProcessorCount;

                    }
                    catch
                    {
                        if (!int.TryParse(switches.TryGetValue("maxParallelThreads", out var p) ? p : "-1", out maxParallelThreads) || maxParallelThreads <= 0)
                            maxParallelThreads = (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0 ? renderWorkerAffinityCpuIndexes!.Length : Environment.ProcessorCount;
                    }
                    maxParallelThreads *= 2;
                    preparerAffinityCpuIndexes = renderWorkerAffinityCpuIndexes.ArrayAny() ? Enumerable.Range(0, Environment.ProcessorCount).Except(renderWorkerAffinityCpuIndexes).ToArray() : [];
                }
                else
                {
                    if (!int.TryParse(switches.TryGetValue("maxParallelThreads", out var p) ? p : "8", out maxParallelThreads)) maxParallelThreads = Environment.ProcessorCount;
                }

                var workerAffinityLabel = (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0
                    ? $"manual({string.Join(",", renderWorkerAffinityCpuIndexes ?? Array.Empty<int>())})"
                    : (enableThreadAffinity ? "auto" : "disabled");
                Log($"Working in parallel mode, max {maxParallelThreads} threads, Render in layer:{YesNo(renderByLayer)}, Prepare in worker:{YesNo(prepareInWorker)}, Preparer affinity:{YesNo(enableThreadAffinity)}, Worker affinity:{workerAffinityLabel}, Boost mode:{YesNo(boostMode)}");

            }
            else
            {
                Log("Working in serial mode.");
            }


            bool hwAccelDecode = bool.TryParse(switches.GetOrAdd("preferHwAccelDecoder", "false"), out var hwAccelDecodeValue) && hwAccelDecodeValue;
            bool hwAccelEncode = bool.TryParse(switches.GetOrAdd("preferHwAccelEncoding", "false"), out var hwAccelEncodeValue) && hwAccelEncodeValue;
            InternalPluginBase.HWAccelEncodeOptionGetter = new(() => hwAccelEncode);
            InternalPluginBase.HWAccelDecodeOptionGetter = new(() => hwAccelDecode);
            ClassicOverlayMixture.EnableApproximatePath = bool.TryParse(switches.GetOrAdd("ApproximateMixture", "false"), out var approximateMixture) && approximateMixture;
            if (Enum.TryParse<EffectImplementType>(switches.GetOrAdd("ForcePreferToType", "NotSpecified"), out var forcePreferToType) && forcePreferToType != EffectImplementType.NotSpecified)
            {
                EffectHelper.ForcePreferToType = forcePreferToType;
                Log($"Using forced effect implement type: {forcePreferToType}");
            }

            Log($"ClassicOverlayMixture approximate path: {YesNo(ClassicOverlayMixture.EnableApproximatePath)}");
            Log($"Prefer to HwAccel decode: {YesNo(hwAccelDecode)}");
            Log($"GC Option: {GCOption}");
            Log($"Drawing.Processing.Resizing.PictureResizer: {Drawing.Processing.Resizing.PictureResizer.Default.GetType().Name}");


            #endregion

            var renderer = new Renderer
            {
                builder = builder,
                Clips = clips,
                Duration = duration,
                LogProcessStack = true,
                LogRenderState = false,
                LogStaticsData = false,
                GCOption = GCOption,
                Use16Bit = false,
                EnableRenderWatchdogForceStart = false,
                MaxRenderScheduleTimeout = 0,
                RenderSchedulerIdleDelayMs = 0,
                MinSchedulePreparedFrames = 0,
                ThrottleThreshold = (int)(duration * 8),
                MaxThreads = boostMode ? (int)duration : maxParallelThreads,
                MaxPendingWriteFrames = maxPendingWriteFrames > 0 ? maxPendingWriteFrames : 0,
                BlockPreparingBeforeRendering = boostMode,
                DisableAllThrottleOptions = boostMode,
                RenderByLayers = renderByLayer,
                PrepareInWorkerThreads = prepareInWorker,
                OneByOneRender = oneByOneRender,
                EnableThreadAffinity = enableThreadAffinity,
                WorkerCPUCoreIndexs = renderWorkerAffinityCpuIndexes,
                TargetHeight = height,
                TargetWidth = width,
                ProjectRelativeWidth = width,
                ProjectRelativeHeight = height,
                EnableGPUBatchProcess = true,
                AllowReorderEffect = true,
                AutoSetupRenderContext = false,
                UseHDR = false,
                StartFrame = 0
            };

            // ── 进度回调 ─────────────────────────────────────
            renderer.OnProgressChanged += (progress, eta) =>
            {
                Console.Write($"\rRendering: {progress:P1}  ETA: {eta:hh\\:mm\\:ss}  FPS: {renderer.CurrentFps:N2}  Frame: {renderer.CurrentFinished}/{renderer.Duration}      ");
            };

            // ── Ctrl+C 处理 ──────────────────────────────────
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Log("\nCancelling benchmark...");
                cts.Cancel();
            };

            // ── 执行渲染 ─────────────────────────────────────
            Log("Starting benchmark render...");
            Console.CursorVisible = false;
            var sw = Stopwatch.StartNew();

            try
            {
                builder?.Build()?.Start();
                renderer.PrepareRender(cts.Token);
                await renderer.GoRender(cts.Token);
                sw.Stop();
                Log($"Render done,total elapsed {sw}, avg elapsed {renderer.EachElapsedForPreparing.Average(t => t.TotalSeconds)} spf to prepare and {renderer.EachElapsed.Average(t => t.TotalSeconds)} spf to render");
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
                Console.CursorVisible = true;
                Log("Benchmark was cancelled by user.");
                return 255;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.CursorVisible = true;
                Log(ex, "Benchmark render failed");
                return 1;
            }

            Console.CursorVisible = true;
            Console.WriteLine(); // 换行

            // ── 统计 ProcessStack 各步骤耗时 ─────────────────
            List<List<PictureProcessStack>> stacksSnapshot = renderer.FrameProcessStacks.Select(s => s.Value).ToList();

            static IEnumerable<PictureProcessStack> FlattenStacks(IEnumerable<PictureProcessStack>? steps)
            {
                if (steps is null) yield break;
                foreach (var step in steps)
                {
                    if (step is null) continue;
                    yield return step;
                    if (step is OverlayedPictureProcessStack overlay)
                    {
                        foreach (var s in FlattenStacks(overlay.TopSteps)) yield return s;
                        foreach (var s in FlattenStacks(overlay.BaseSteps)) yield return s;
                    }
                }
            }

            static string GetStepKey(PictureProcessStack step)
            {
                var name = step.OperationDisplayName;
                if (string.IsNullOrWhiteSpace(name)) name = step.Operator?.Name;
                return string.IsNullOrWhiteSpace(name) ? "(unknown)" : name;
            }

            var sumTicksByKey = new Dictionary<string, long>(StringComparer.Ordinal);
            var countByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            var orderedKeys = new List<string>();

            foreach (var frameStack in stacksSnapshot)
            {
                foreach (var step in FlattenStacks(frameStack))
                {
                    if (step?.Elapsed is not TimeSpan elapsed) continue;
                    var key = GetStepKey(step);
                    if (!orderedKeys.Contains(key)) orderedKeys.Add(key);
                    sumTicksByKey[key] = sumTicksByKey.GetValueOrDefault(key) + elapsed.Ticks;
                    countByKey[key] = countByKey.GetValueOrDefault(key) + 1;
                }
            }

            // ── 输出结果 ─────────────────────────────────────
            var avgTime = renderer.EachElapsed.Count > 0
                ? renderer.EachElapsed.Average(ts => ts.TotalMilliseconds)
                : 0;
            var avgPrepTime = renderer.EachElapsedForPreparing.Count > 0
                ? renderer.EachElapsedForPreparing.Average(ts => ts.TotalMilliseconds)
                : 0;
            var totalTime = sw.Elapsed;
            var renderedFrames = renderer.EachElapsed.Count;

            Log("");
            Log("========================================", "stat");
            Log("  Benchmark Results", "stat");
            Log("========================================", "stat");
            Log($"  Total frames rendered : {renderedFrames}", "stat");
            Log($"  Total time            : {totalTime.TotalSeconds:F2}s", "stat");
            Log($"  Overall FPS           : {renderedFrames / Math.Max(totalTime.TotalSeconds, 0.001):F2}", "stat");
            Log($"  Avg frame render time : {avgTime:F3}ms ({1000.0 / Math.Max(avgTime + avgPrepTime, 0.001):F1} FPS)", "stat");
            Log($"  Avg prepare time      : {avgPrepTime:F3}ms", "stat");
            Log($"  Avg total per frame   : {avgTime + avgPrepTime:F3}ms", "stat");

            if (orderedKeys.Count > 0)
            {
                Log("");
                Log("  Per-step breakdown:", "stat");
                for (int i = 0; i < orderedKeys.Count; i++)
                {
                    var key = orderedKeys[i];
                    var count = countByKey.GetValueOrDefault(key);
                    if (count <= 0) continue;
                    var avg = TimeSpan.FromTicks(sumTicksByKey.GetValueOrDefault(key) / count);
                    Log($"    Step #{i + 1}: {key}", "stat");
                    Log($"      Avg: {avg.TotalMilliseconds:F3}ms  (n={count})", "stat");
                }
            }

            Log("========================================", "stat");

            // ── 资源清理 ─────────────────────────────────────
            foreach (var clip in clips)
            {
                clip.Dispose();
            }
            renderer.builder = null;

            return 0;
        }

        /// <summary>
        /// 编码性能基准测试：使用 <see cref="VideoBuilder"/>，
        /// 生成随机帧并以乱序追加，以模拟真实渲染场景中的 Cache 访问模式。
        /// 指定 -output 时可写入实际视频文件（否则使用 BlackHoleWriter）。
        /// </summary>
        private static async Task<int> GoBenchEncode(ConcurrentDictionary<string, string> switches)
        {
            // ── 参数 ────────────────────────────────────────────
            const int width = 1920;
            const int height = 1080;
            const int defaultFps = 60;

            var totalFrames = uint.TryParse(switches.GetOrAdd("totalFrames", "600"), out var tf) ? tf : 600u;
            var fpsSetting = int.TryParse(switches.GetOrAdd("fps", defaultFps.ToString()), out var fps) ? fps : defaultFps;

            // 磁盘缓存路由
            var diskCacheThreshold = switches.TryGetValue("diskCacheThreshold", out var dctStr)
                && double.TryParse(dctStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dct)
                ? Math.Clamp(dct, 0.1, 0.95) : 0.85;
            var enableDiskCache = bool.TryParse(switches.GetOrAdd("enableDiskCacheRouting", "false"), out var edc) && edc;
            var maxPendingFrames = int.TryParse(switches.GetOrAdd("maxPendingWriteFrames", "0"), out var mpf) ? mpf : 0;

            // ── 输出路径 ────────────────────────────────────────
            var outputPath = switches.TryGetValue("output", out var outPath) && !string.IsNullOrWhiteSpace(outPath)
                ? outPath.Replace("{CurrentTime}", DateTime.Now.ToString("yyyyMMdd_HHmmss"))
                : null;

            var useRealEncoder = outputPath is not null;
            string encoder, pixelFormat;
            string builderLabel;

            if (useRealEncoder)
            {
                encoder = switches.GetOrAdd("encoder", "libx264");
                pixelFormat = switches.GetOrAdd("pixelFormat", "AV_PIX_FMT_YUV420P");
                builderLabel = $"{encoder} → {outputPath}";
                Log($"Output: {outputPath}, encoder: {encoder}, pixel format: {pixelFormat}");
            }
            else
            {
                encoder = "BlackHoleWriter";
                pixelFormat = "";
                builderLabel = "BlackHoleWriter (null output)";
                Log("No -output specified; using BlackHoleWriter (no real file written).");
            }

            string YesNo(bool b) => b ? "Yes" : "No";

            Log($"Encode bench: {totalFrames} frames, {width}x{height} @ {fps}fps");
            Log($"Disk cache: {YesNo(enableDiskCache)} (threshold: {diskCacheThreshold:P0})");
            // ── 初始化 FFmpeg ────────────────────────────────
            Log("Initializing FFmpeg for encode bench...");
            DynamicallyLoadedBindings.EnableAutoInitialization = false;
            FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            ffmpeg.RootPath = switches.GetOrAdd("FFmpegLibraryPath", AppContext.BaseDirectory);
            if (FFmpeg.AutoGen.DynamicallyLoadedBindings.TryInitialize())
            {
                FFmpegHelper.SetupFFmpegLogging(ffmpeg.AV_LOG_INFO);
                Log($"FFmpeg library: version {ffmpeg.av_version_info()}, {ffmpeg.avcodec_license()}");
            }
            else
            {
                Log($"FFmpeg library failed to load. ({ffmpeg.BindingVerificationResult?.Failures?.Aggregate("", (a, b) => $"{a}{Environment.NewLine}{b.FunctionName} failed to load in {b.LibraryName}: {b.Message}")})", "error");
                return 1;
            }
            FFmpegHelper.SetupFFmpegLogging();
            Log($"FFmpeg library path: {ffmpeg.RootPath}");

            // ── 创建 VideoBuilder ──────────────────────────────
            var builder = new VideoBuilder(outputPath ?? "/dev/null", width, height, fps, encoder, pixelFormat)
            {
                Duration = totalFrames,
                // 允许乱序追加
                StrictMode = false,
                AllowDuplicatedFrameWrite = false,
                DisposeFrameAfterEachWrite = true,
                EnableDiskCacheRouting = enableDiskCache,
                DiskCacheThreshold = diskCacheThreshold,
                DiskCacheMaxPendingFrames = maxPendingFrames,
                // 关闭自动预览日志避免干扰
                EnablePreview = false,
            };

            Log($"VideoBuilder initialized with {builderLabel}.");

            // ── 生成随机帧索引（Fisher-Yates 洗牌）─────────────
            var frameIndices = new uint[totalFrames];
            for (uint i = 0; i < totalFrames; i++) frameIndices[i] = i;
            var rng = Random.Shared;
            for (int i = frameIndices.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (frameIndices[i], frameIndices[j]) = (frameIndices[j], frameIndices[i]);
            }

            // ── 预生成帧数据 ──────────────────────────────────
            Log("Pre-generating frame data...");
            var frames = new IPicture[totalFrames];
            for (uint i = 0; i < totalFrames; i++)
            {
                var r = (byte)rng.Next(256);
                var g = (byte)rng.Next(256);
                var b = (byte)rng.Next(256);
                frames[i] = Picture8bpp.GenerateSolidColor(width, height, r, g, b, a: null);
                frames[i].Tag = $"generated frame #{i}";
            }
            Log($"Generated {totalFrames} frames.");

            // ── 启动写入线程 ──────────────────────────────────
            builder.Build()?.Start();
            Log("Writer thread started.");

            // ── 乱序追加 ──────────────────────────────────────
            Log("Appending frames in random order...");
            var swAppend = Stopwatch.StartNew();
            long peakPending = 0;

            for (int i = 0; i < frameIndices.Length; i++)
            {
                var idx = frameIndices[i];
                builder.Append(idx, frames[idx]);

                var pending = builder.PendingWriteCount;
                if (pending > peakPending) peakPending = pending;

                if ((i + 1) % Math.Max(totalFrames / 10, 1) == 0)
                {
                    var pct = (double)(i + 1) / totalFrames;
                    Console.Write($"\rAppending: {pct:P0} ({i + 1}/{totalFrames}), pending: {builder.PendingWriteCount}, disk: {builder.FramesOnDisk}         ");
                }
            }
            swAppend.Stop();
            Console.WriteLine();
            Log($"Append done in {swAppend.Elapsed.TotalSeconds:F3}s, peak pending: {peakPending}, final pending: {builder.PendingWriteCount}, disk: {builder.FramesOnDisk}");

            var appendFps = totalFrames / swAppend.Elapsed.TotalSeconds;

            // ── 等待写入/编码完成 ─────────────────────────────
            Log("Waiting for writer to drain (encode remaining frames)...");
            var swFlush = Stopwatch.StartNew();
            builder.Finish(
                regenerator: idx => Picture8bpp.GenerateSolidColor(width, height, 0, 0, 0, null),
                totalFrames: totalFrames,
                onWritingProgressUpdate: (c, p) => Console.Write($"\rFlushing: frame #{c} / {totalFrames} ({p:P2})    ")
            );
            swFlush.Stop();
            Console.WriteLine();

            var totalTime = swFlush.Elapsed;
            var encodeFps = totalFrames / Math.Max(totalTime.TotalSeconds, 0.001);

            // ── 结果统计 ──────────────────────────────────────
            Log("");
            Log("========================================", "stat");
            if (useRealEncoder)
                Log("  Encode Benchmark Results (real video)", "stat");
            else
                Log("  Encode Benchmark Results (null device)", "stat");
            Log("========================================", "stat");
            Log($"  Total frames       : {totalFrames}", "stat");
            Log($"  Resolution         : {width}x{height}", "stat");
            Log($"  FPS setting        : {fps}", "stat");
            if (useRealEncoder)
            {
                Log($"  Encoder            : {encoder}", "stat");
                Log($"  Pixel format       : {pixelFormat}", "stat");
                Log($"  Output file        : {outputPath}", "stat");
            }
            Log($"  Disk cache         : {YesNo(enableDiskCache)} (@ {diskCacheThreshold:P0} threshold)", "stat");
            Log($"  Append FPS         : {appendFps:F2}", "stat");
            Log($"  Encode FPS         : {encodeFps:F2}", "stat");
            if (useRealEncoder)
            {
                var bitsPerFrame = (long)width * height * 3 * 8; // approx YUV 4:2:0 → 12 bpp → 12 bits
                var bitrate = (long)(bitsPerFrame * fps);
                Log($"  Est. bitrate       : {bitrate / 1000.0 / 1000.0:F1} Mbps (raw, uncompressed)", "stat");
            }
            Log($"  Total time         : {totalTime.TotalSeconds:F3}s", "stat");
            Log($"  Peak pending count : {peakPending}", "stat");
            Log($"  Final cache cleared: {builder.WrittenFramesCount}/{builder.TotalFramesCount}", "stat");
            Log("========================================", "stat");

            builder.Dispose();
            return 0;
        }

        /// <summary>
        /// 解码性能基准测试：创建 <see cref="IVideoSource"/> 并顺序解码所有帧，
        /// 测量解码吞吐量（FPS）和速度倍率。
        /// </summary>
        private static async Task<int> GoBenchDecode(ConcurrentDictionary<string, string> switches)
        {
            // ── 参数 ────────────────────────────────────────────
            if (!switches.TryGetValue("source", out var sourcePath) || string.IsNullOrWhiteSpace(sourcePath))
            {
                Log("ERROR: Decode benchmark requires -source=<video file path>.", "error");
                return 1;
            }

            if (!File.Exists(sourcePath))
            {
                Log($"ERROR: Source file '{sourcePath}' not found.", "error");
                return 1;
            }

            var maxFrames = int.TryParse(switches.TryGetValue("maxFrames", out var mf) ? mf : "0", out var parsedMf) && parsedMf > 0 ? parsedMf : 0;
            var enableDiskCache = bool.TryParse(switches.GetOrAdd("VideoFrameDiskCache", "false"), out var edc) && edc;

            IVideoSource.EnableDiskCache = enableDiskCache;

            string YesNo(bool b) => b ? "Yes" : "No";

            Log($"Decode bench source: {sourcePath}");
            Log($"Disk cache: {YesNo(enableDiskCache)}");

            // ── 创建解码器 ──────────────────────────────────────
            Log("Creating video source...");
            IVideoSource? source = null;
            try
            {
                source = PluginManager.CreateVideoSource(sourcePath);
            }
            catch (Exception ex)
            {
                Log(ex, "Failed to create video source");
                return 1;
            }

            if (source is null)
            {
                Log("ERROR: Failed to create video source - no plugin supports this file.", "error");
                return 1;
            }

            source.Initialize();
            source.StrictMode = false;

            var totalFrames = source.TotalFrames > 0 ? source.TotalFrames : 0;
            if (totalFrames <= 0)
            {
                Log("WARN: Cannot determine total frame count, estimating via probing...", "warn");
                // 尝试探测帧数
                totalFrames = 0;
                for (uint probe = 0; probe < 100000; probe += 100)
                {
                    try
                    {
                        using var probeFrame = source.GetFrame(probe, false);
                        if (probeFrame is null) break;
                        totalFrames = probe + 1;
                    }
                    catch { break; }
                }
                if (totalFrames <= 0)
                {
                    Log("ERROR: Cannot determine total frame count, aborting.", "error");
                    source.Dispose();
                    return 1;
                }
            }

            var decodeTotal = maxFrames > 0 ? Math.Min(maxFrames, totalFrames) : totalFrames;

            Log($"Source: {source.Width}x{source.Height}, {source.Fps:F2}fps, {decodeTotal}/{totalFrames} frames, decoder: {source.TypeName}");

            // ── 热启动（丢弃前几帧加载开销）────────────────────
            var warmupCount = (int)Math.Min(30, decodeTotal / 10);
            Log($"Warming up ({warmupCount} frames)...");
            for (uint i = 0; i < warmupCount; i++)
            {
                using var warmupFrame = source.GetFrame(i, false);
            }
            Log("Warm-up done.");

            // ── 解码测试 ──────────────────────────────────────
            Log("Starting decode benchmark...");
            Console.CursorVisible = false;
            var sw = Stopwatch.StartNew();
            long decodedBytes = 0;
            int decodedCount = 0;

            for (uint i = 0; i < decodeTotal; i++)
            {
                using var frame = source.GetFrame(i, false);
                if (frame is not null)
                {
                    decodedCount++;
                    decodedBytes += frame.Width * frame.Height * (frame.BitPerPixel / 8);
                }

                if ((i + 1) % (Math.Max(decodeTotal / 50, 1)) == 0 || i == decodeTotal - 1)
                {
                    var pct = (double)(i + 1) / decodeTotal;
                    var elapsed = sw.Elapsed.TotalSeconds;
                    var currentFps = (i + 1) / Math.Max(elapsed, 0.001);
                    Console.Write($"\rDecoding: {pct:P1} ({i + 1}/{decodeTotal}), {currentFps:F1} FPS, elapsed: {elapsed:F1}s      ");
                }
            }
            sw.Stop();
            Console.WriteLine();
            Console.CursorVisible = true;

            // ── 结果统计 ──────────────────────────────────────
            var totalTime = sw.Elapsed;
            var decodeFps = decodedCount / Math.Max(totalTime.TotalSeconds, 0.001);
            var speedVsRealtime = decodeFps / Math.Max(source.Fps, 1.0);
            var bandwidthMbps = (decodedBytes / Math.Max(totalTime.TotalSeconds, 0.001)) / (1024.0 * 1024.0);

            Log("");
            Log("========================================", "stat");
            Log("  Decode Benchmark Results", "stat");
            Log("========================================", "stat");
            Log($"  Source file        : {Path.GetFileName(sourcePath)}", "stat");
            Log($"  Resolution         : {source.Width}x{source.Height}", "stat");
            Log($"  Source FPS         : {source.Fps:F2}", "stat");
            Log($"  Decoder            : {source.TypeName}", "stat");
            Log($"  Disk cache         : {YesNo(enableDiskCache)}", "stat");
            Log($"  Frames decoded     : {decodedCount}/{decodeTotal}", "stat");
            Log($"  Total time         : {totalTime.TotalSeconds:F3}s", "stat");
            Log($"  Decode FPS         : {decodeFps:F2}", "stat");
            Log($"  Speed vs realtime  : {speedVsRealtime:F2}x", "stat");
            Log($"  Bandwidth          : {bandwidthMbps:F1} MB/s (raw pixel)", "stat");
            Log($"  Avg per frame      : {(totalTime.TotalMilliseconds / Math.Max(decodedCount, 1)):F3}ms", "stat");
            Log("========================================", "stat");

            source.Dispose();
            return 0;
        }

        /// <summary>
        /// 重新编码模式：解码输入视频，然后使用指定编码器重新编码输出。
        /// 用于测试编解码器的正确性和性能。
        /// </summary>
        private static async Task<int> GoReencode(ConcurrentDictionary<string, string> switches)
        {
            // ── 参数 ────────────────────────────────────────────
            if (!switches.TryGetValue("source", out var sourcePath) || string.IsNullOrWhiteSpace(sourcePath))
            {
                Log("ERROR: Reencode requires -source=<input video>.", "error");
                return 1;
            }
            if (!switches.TryGetValue("output", out var outputPath) || string.IsNullOrWhiteSpace(outputPath))
            {
                Log("ERROR: Reencode requires -output=<output file>.", "error");
                return 1;
            }
            if (!File.Exists(sourcePath))
            {
                Log($"ERROR: Source file '{sourcePath}' not found.", "error");
                return 1;
            }

            bool hwAccelDecode = bool.TryParse(switches.GetOrAdd("preferHwAccelDecoder", "false"), out var hwAccelDecodeValue) && hwAccelDecodeValue;
            bool hwAccelEncode = bool.TryParse(switches.GetOrAdd("preferHwAccelEncoder", "false"), out var hwAccelEncodeValue) && hwAccelEncodeValue;
            InternalPluginBase.HWAccelDecodeOptionGetter = new(() => hwAccelDecode);
            InternalPluginBase.HWAccelEncodeOptionGetter = new(() => hwAccelEncode);
            Log($"Use hardware acceleration for decoding: {hwAccelDecode}, encoding: {hwAccelEncode}");

            var encoder = switches.GetOrAdd("encoder", "libx264");
            var pixelFormatStr = switches.GetOrAdd("pixelFormat", "AV_PIX_FMT_YUV420P");
            var maxFrames = int.TryParse(switches.TryGetValue("maxFrames", out var mf) ? mf : "0", out var parsedMf) && parsedMf > 0 ? parsedMf : 0;
            var bitRate = long.TryParse(switches.TryGetValue("bitRate", out var br) ? br : "0", out var parsedBr) && parsedBr > 0 ? parsedBr : 4_000_000L;

            // ── 创建解码器 ──────────────────────────────────────
            Log($"Creating decoder for: {sourcePath}");
            IVideoSource? source = null;
            try
            {
                source = PluginManager.CreateVideoSource(sourcePath);
                Log($"Created decoder: {source?.TypeName ?? "(null)"}");
            }
            catch (Exception ex)
            {
                Log(ex, "Failed to create video source");
                return 1;
            }

            if (source is null)
            {
                Log("ERROR: Failed to create video source - no plugin supports this file.", "error");
                return 1;
            }

            source.Initialize();
            source.StrictMode = false;

            var totalFrames = source.TotalFrames > 0 ? source.TotalFrames : 0;
            if (totalFrames <= 0)
            {
                Log("ERROR: Cannot determine total frame count.", "error");
                source.Dispose();
                return 1;
            }

            var decodeCount = maxFrames > 0 ? Math.Min(maxFrames, (int)totalFrames) : (int)totalFrames;
            var srcWidth = source.Width;
            var srcHeight = source.Height;
            var srcFps = source.Fps;

            if (srcWidth <= 0 || srcHeight <= 0 || srcFps <= 0)
            {
                Log($"ERROR: Invalid source dimensions ({srcWidth}x{srcHeight}) or FPS ({srcFps}).", "error");
                source.Dispose();
                return 1;
            }

            Log($"Source: {srcWidth}x{srcHeight}, {srcFps:F2}fps, {decodeCount}/{totalFrames} frames, decoder: {source.TypeName}");

            // ── 创建编码器 ──────────────────────────────────────
            outputPath = outputPath.Replace("{CurrentTime}", DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            Log($"Creating encoder: {encoder}, pixel format: {pixelFormatStr}, {srcWidth}x{srcHeight} @ {srcFps:F0}fps");

            IVideoWriter writer;
            if (hwAccelEncode)
            {
                writer = new VideoWriterHWAccel
                {
                    Width = srcWidth,
                    Height = srcHeight,
                    FramePerSecond = (int)Math.Round(srcFps),
                    CodecName = encoder,
                    PixelFormat = pixelFormatStr,
                    OutputPath = outputPath,
                    BitRate = bitRate,
                };
            }
            else
            {
                writer = new VideoWriter
                {
                    Width = srcWidth,
                    Height = srcHeight,
                    FramePerSecond = (int)Math.Round(srcFps),
                    CodecName = encoder,
                    PixelFormat = pixelFormatStr,
                    OutputPath = outputPath,
                    BitRate = bitRate,
                };
            }

            try
            {
                writer.Initialize();
            }
            catch (Exception ex)
            {
                Log(ex, "Failed to initialize encoder");
                source.Dispose();
                return 1;
            }

            Log($"Encoder initialized: {outputPath}");

            // ── 逐帧转码 ──────────────────────────────────────
            Log("Starting reencode...");
            var sw = Stopwatch.StartNew();
            int encodedCount = 0;

            for (uint i = 0; i < decodeCount; i++)
            {
                try
                {
                    using (var frame = source.GetFrame(i, false))
                    {
                        writer.Append(frame);
                        encodedCount++;
                    }

                    var elapsed = sw.Elapsed.TotalSeconds;
                    var fps = (i + 1) / Math.Max(elapsed, 0.001);
                    Console.Write($"\rReencoding: {((double)(i + 1) / decodeCount):P1}, {fps:F1} FPS， ETA: {TimeSpan.FromSeconds((decodeCount - (i + 1)) / Math.Max(fps, 0.001)):mm\\:ss} ");
                }
                catch (Exception ex)
                {
                    Log(ex, $"Failed to encode frame #{i}");
                }


            }
            sw.Stop();
            Console.WriteLine();

            // ── 完成编码 ──────────────────────────────────────
            Log("Finishing encoding...");
            try
            {
                writer.Finish();
            }
            catch (Exception ex)
            {
                Log(ex, "Error during encoder finish");
            }

            // ── 结果统计 ──────────────────────────────────────
            var totalTime = sw.Elapsed;
            var overallFps = encodedCount / Math.Max(totalTime.TotalSeconds, 0.001);

            Log("");
            Log("========================================", "stat");
            Log("  Reencode Results", "stat");
            Log("========================================", "stat");
            Log($"  Source file       : {Path.GetFileName(sourcePath)}", "stat");
            Log($"  Output file       : {outputPath}", "stat");
            Log($"  Resolution        : {srcWidth}x{srcHeight}", "stat");
            Log($"  FPS               : {srcFps:F2}", "stat");
            Log($"  Decoder           : {source.TypeName}", "stat");
            Log($"  Encoder           : {encoder}", "stat");
            Log($"  Pixel format      : {pixelFormatStr}", "stat");
            Log($"  Bitrate           : {bitRate / 1000.0 / 1000.0:F1} Mbps", "stat");
            Log($"  Frames encoded    : {encodedCount}", "stat");
            Log($"  Total time        : {totalTime.TotalSeconds:F3}s", "stat");
            Log($"  Overall FPS       : {overallFps:F2}", "stat");
            if (srcFps > 0)
                Log($"  Speed vs realtime : {(overallFps / srcFps):F2}x", "stat");
            Log($"  Avg per frame     : {(totalTime.TotalMilliseconds / Math.Max(encodedCount, 1)):F3}ms", "stat");
            Log("========================================", "stat");

            source.Dispose();
            writer.Dispose();
            return 0;
        }

        public static IClip[] JSONToIClips(DraftStructureJSON json, IDictionary<string, AssetItem> assets, IPicture.PicturePixelMode bpp)
        {
            var clips = json.Clips;
            if (clips is null || clips.Length == 0)
            {
                return Array.Empty<IClip>();
            }

            var clipsList = new List<IClip>();

            foreach (var clip in clips)
            {
                if (clip.ClipType == ClipMode.MarkingClip)
                {
                    continue;
                }

                var clipJson = JsonSerializer.SerializeToElement(clip);
                var clipInstance = PluginManager.CreateClip(clipJson);
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
                else if (string.IsNullOrEmpty(clipInstance.FilePath) && !string.IsNullOrEmpty(clip.FilePath) && clipInstance.NeedFilePath)
                {
                    try
                    {
                        clipInstance.FilePath = clip.FilePath;
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
            var tracks = json.SoundTracks;
            if (tracks is null || tracks.Length == 0)
            {
                return Array.Empty<ISoundTrack>();
            }

            var tracksList = new List<ISoundTrack>();

            foreach (var track in tracks)
            {
                var trackJson = JsonSerializer.SerializeToElement(track);
                var trackInstance = PluginManager.CreateSoundTrack(trackJson);
                trackInstance.ExtraData = track.MetaData ?? new();

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
                else if (string.IsNullOrEmpty(trackInstance.FilePath) && !string.IsNullOrEmpty(track.FilePath) && trackInstance.NeedFilePath)
                {
                    try
                    {
                        trackInstance.FilePath = track.FilePath;
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
