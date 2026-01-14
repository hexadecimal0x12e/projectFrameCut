using FFmpeg.AutoGen;
using ILGPU;
using ILGPU.IR;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.WindowsRender;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static projectFrameCut.Shared.Logger;

namespace projectFrameCut.StandaloneRender
{
    internal class Program
    {
        const int PluginAPIVersion = 1;

        static readonly JsonSerializerOptions savingOpts = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

        static async Task<int> Main(string[] args)
        {
            if(!args.Contains("--nolog"))
            {
                MyLoggerExtensions.OnLog += (m, l) => Console.WriteLine($"[{l}] {m}");
            }
            Log(GetInfo());
            Log($"cmdline: {Environment.GetCommandLineArgs().Aggregate((a, b) => $"{a} {b}")}");

            if (args.Length == 0 || args.Any(x => x.Equals("-h") || x.Equals("--help")))
            {
                Console.WriteLine(
                    """
                    Usage: projectFrameCut.StandaloneRender <mode> [<args>]
                    Available modes:
                        - render: Render video/audio/all from the given project file.
                        - bench: Benchmark hardware accelerators for rendering.
                        - list_accels: List available hardware accelerators.
                        - about: Show information about this program.

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
                        [-diagReportPath=<path to .csv file or output directory>]


                    Mode 'bench':
                        [-multiAccelerator=<true|false>]
                        [-acceleratorType=<auto|cuda|opencl|cpu> or -acceleratorDeviceId=<device id> or -acceleratorDeviceIds=<device ids|all>]    
                        
                    See more at the document.
                    """);
                Console.ReadLine();
                return 0;
            }

            var runningMode = args[0];

            Log($"Running mode: {runningMode}");

            ConcurrentDictionary<string, string> switches = new(args
               .Skip(1)
               .Select(x => x.Split('=', 2))
               .Where(x => x.Length == 2)
               .Select(x => new KeyValuePair<string, string>(x[0].TrimStart('-', '/'), x[1])));



            switch (runningMode)
            {
                case "render":
                    var accelResult = InitAccel(switches);
                    if (accelResult != 0)
                    {
                        return accelResult;
                    }
                    return await GoRender(switches);
                case "list_accels":
                    Context context = Context.CreateDefault();
                    var devices = context.Devices.ToList();
                    List<AcceleratorInfo> listAccels = new();
                    for (uint i = 0; i < devices.Count; i++)
                    {
                        var item = devices[(int)i];
                        listAccels.Add(new AcceleratorInfo(i, item.Name, item.AcceleratorType.ToString()));
                    }
                    Console.Error.WriteLine(JsonSerializer.Serialize(listAccels, new JsonSerializerOptions { WriteIndented = true })
                        );
                    return 0;

                case "about":
                    Console.WriteLine(GetInfo(true));
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
                    Log($"Picked accelerator {item.Name} : {item.AcceleratorType}\r\n");
                }

                Accelerator[] accelerators = picked.Select(d => d.CreateAccelerator(context)).ToArray();

                ILGPUPlugin.accelerators = accelerators;
                return 0;

            }
            catch (Exception ex)
            {
                Log(ex, "Get accels");
                return ex.HResult;
            }
        }

        private static async Task<int> GoRender(ConcurrentDictionary<string, string> switches)
        {


            #region init encoder
            Log("Initiliazing FFmpeg...");
            ffmpeg.RootPath = switches.GetOrAdd("FFmpegLibraryPath", AppContext.BaseDirectory);
            FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            FFmpeg.AutoGen.DynamicallyLoadedBindings.Initialize();
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

            Type pxfmtEnumType = typeof(FFmpeg.AutoGen.AVPixelFormat);
            var pxfmtFields = pxfmtEnumType.GetFields(BindingFlags.Public | BindingFlags.Static);
            var pxfmtInfo = pxfmtFields.Where((s) => s.Name == outputOptions[3]).FirstOrDefault(defaultValue: null);
            if (pxfmtInfo == null)
            {
                Log($"ERROR: Pixel format {outputOptions[3]} not found in AVPixelFormat.");
                return 1;
            }

            outputFormat = (FFmpeg.AutoGen.AVPixelFormat)Convert.ToInt64(pxfmtInfo.GetValue(null)!);
            outputEncoder = outputOptions[4];

            var use16Bit = bool.TryParse(switches.GetOrAdd("Use16bpp", "true"), out var b1) ? b1 : true;

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
                if (!int.TryParse(gcopt, out GCOption))
                {
                    Log("Invalid GCOptions value, must be 0, 1 or 2. Default to 0.");
                    GCOption = 0;
                }
                if (GCOption < 0 || GCOption > 2)
                {
                    Log("Invalid GCOptions value, must be 0, 1 or 2. Default to 0.");
                    GCOption = 0;
                }
            }

            if (GCOption == 2)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            }

            ConcurrentDictionary<string, AssetItem> assets = new();

            if (switches.TryGetValue("assetDbFile", out var assetDbPath))
            {
                assets = JsonSerializer.Deserialize<ConcurrentDictionary<string, AssetItem>>(File.ReadAllText(assetDbPath))
                    ?? new ConcurrentDictionary<string, AssetItem>();
                Log($"Read {assets.Count} assets from asset database.");
            }

            int maxParallelThreads = int.TryParse(switches.GetOrAdd("maxParallelThreads", "8"), out var result) ? result : 8;
            bool blockWrite = false;
            if (bool.TryParse(switches.GetOrAdd("oneByOneRender", "false"), out var oneByOneRender) && oneByOneRender)
            {
                maxParallelThreads = 1;
                blockWrite = true;
            }
            else
            {
                blockWrite = false;
            }
            #endregion

            #region read draft
            ProjectJSONStructure project = new();
            DraftStructureJSON timeline = new();
            if (File.Exists(Path.Combine(workingPath, "project.json")))
            {
                project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(workingPath, "project.json")), savingOpts) ?? new();
            }
            else
            {
                Log("ERROR: project.json not found in project directory.");
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
            #endregion

            async Task composeVideo(string resultPath)
            {
                var clips = JSONToIClips(timeline, assets);

                switches.TryGetValue("diagReportPath", out var diagReportPath);

                VideoBuilder builder = new VideoBuilder(resultPath, width, height, fps, outputEncoder, outputFormat.ToString())
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
                    LogState = (bool.TryParse(switches.TryGetValue("LogState", out var ls2) ? ls2 : "false", out var lsbool) && lsbool),
                    LogStatToLogger = true,
                    GCOption = GCOption
                };
                builder?.Build()?.Start();
                renderer.PrepareRender(CancellationToken.None);
                Stopwatch sw1 = new();
                Log("Start render...");
                sw1.Restart();
                await renderer.GoRender(CancellationToken.None);

                Log($"Render done,total elapsed {sw1}, avg elapsed {renderer.EachElapsedForPreparing.Average(t => t.TotalSeconds)} spf to prepare and {renderer.EachElapsed.Average(t => t.TotalSeconds)} spf to render");

                if (!string.IsNullOrWhiteSpace(diagReportPath))
                {
                    try
                    {
                        DiagReportExporter.ExportCsv(diagReportPath!, renderer);
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "Export diagReportPath CSV");
                    }
                }

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
                var clips = JSONToIClips(timeline, assets).Where(c => c.ClipType == ClipMode.AudioClip || c.ClipType == ClipMode.VideoClip).ToArray();

                if (clips == null || clips.Length == 0)
                {
                    Log("No sound clips in the whole draft. returning...");
                    return;
                }

                Log($"Found {clips.Length} audio clips.");

                Log("Initializing all clips...");
                foreach (IClip clip in clips)
                {
                    clip.ReInit();
                }

                var buf = AudioComposer.Compose(clips, null, (int)project.targetFrameRate, 48000, 2);
                AudioWriter writer = new(resultPath, 48000, 2);
                writer.Append(buf);
                writer.Finish();
                writer.Dispose();
                foreach (var item in clips)
                {
                    item?.Dispose();
                }
                return;
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
                    string vidOutputPath = Path.Combine(outputDir, $"{project.projectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                    string audOutputPath = Path.Combine(outputDir, $"{project.projectName}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
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

        public static IClip[] JSONToIClips(DraftStructureJSON json, IDictionary<string, AssetItem> assets)
        {
            var elements = (JsonSerializer.SerializeToElement(json).Deserialize<DraftStructureJSON>()?.Clips) ?? throw new NullReferenceException("Failed to cast ClipDraftDTOs to IClips."); //I don't want to write a lot of code to clone attributes from dto to IClip, it's too hard and may cause a lot of mystery bugs.

            var clipsList = new List<IClip>();

            foreach (var clip in elements.Cast<JsonElement>())
            {
                var clipInstance = PluginManager.CreateClip(clip);
                if (clipInstance.FilePath?.StartsWith('$') ?? false)
                {
                    try
                    {
                        clipInstance.FilePath = assets[clipInstance.FilePath.Substring(1)].Path;
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
                clipInstance.ReInit();
                clipsList.Add(clipInstance);

            }
            return clipsList.ToArray();
        }

        public static string GetInfo(bool all = false)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"projectFrameCut.StandaloneRender - {Assembly.GetExecutingAssembly().GetName().Version}");
            builder.AppendLine($"Copyright hexadecimal0x12e 2025-2026.");
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
            builder.AppendLine($"APIBase Version: {IPluginBase.CurrentPluginAPIVersion}");
            builder.AppendLine($"Render: {renderType.FullName}, hash:{renderHash}");
            builder.AppendLine($"APIBase: {baseType.FullName}, hash:{baseHash}");
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
            }
            return builder.ToString();
        }
    }
}
