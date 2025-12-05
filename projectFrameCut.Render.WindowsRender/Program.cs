using FFmpeg.AutoGen;
using ILGPU;
using ILGPU.IR;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;
using projectFrameCut.Render.WindowsRender;
using projectFrameCut.Shared;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Accelerator = ILGPU.Runtime.Accelerator;
using Device = ILGPU.Runtime.Device;

namespace projectFrameCut.Render.RenderCLI
{
    internal class Program
    {
        public static ConcurrentBag<string> advancedFlags = new();

        public static object globalLocker = new object();


        static async Task<int> Main(string[] args)
        {
            if (args.Length > 1) Thread.Sleep(800);
            inprojectFrameCut = Environment.GetEnvironmentVariables().Contains("projectFrameCut");
#if DEBUG
            if (!Environment.GetEnvironmentVariables().Contains("pjfc_dbg")) goto run;
            SetSubProg("WaitForDbg");
            Console.WriteLine("DEBUG BUILD - Waiting for debugger. To disable: don't define 'pjfc_dbg' environment varable.");
            Stopwatch dbg_sw = Stopwatch.StartNew();
            while (true)
            {
                if (Debugger.IsAttached || dbg_sw.Elapsed.TotalSeconds > 15) break;
                Thread.Sleep(50);
            }
        run:
#endif
            SetSubProg("Init");

            Log($"projectFrameCut.Render - {Assembly.GetExecutingAssembly().GetName().Version} \r\n" + $"Copyright hexadecimal0x12e 2025.\r\n" +
                $"cmdline: {Environment.GetCommandLineArgs().Aggregate((a, b) => $"{a} {b}")}");

            if (args.Length == 0 || args.Any(x => x.Equals("-h") || x.Equals("--help")))
            {
                Console.WriteLine(
                    """
                    Usage: projectFrameCut.Render render
                            -draft=<draft file path>
                            -output=<output file/folder>
                            -output_options=<width>,<height>,<fps>,<pixel format>,<encoder>
                            [-maxParallelThreads=<number> or -oneByOneRender=<true|false>]
                            [-multiAccelerator=<true|false>]
                            [-acceleratorType=<auto|cuda|opencl|cpu> or -acceleratorDeviceId=<device id> or -acceleratorDeviceIds=<device ids>]
                            [-forceSync=<true|false|default>]
                            [-GCOptions=doLOHCompression|doNormalCollection|letCLRDoCollection]
                            [-preview=true|false]
                            [-previewPath=<path of preview output>]
                            [-advancedFlags=<flag1>[,<flag2>,...,<flagn>]

                       or: projectFrameCut.Render rpc_backend
                            -pipe=<name of pipe>
                            -tempFolder="<path to temp folder>" 
                            [-acceleratorType=<auto|cuda|opencl|cpu>]
                            [-acceleratorDeviceId=<device id>]
                            [-forceSync=<true|false|default>]
                            [-advancedFlags=<flag1>[,<flag2>,...,<flagn>]

                       or: projectFrameCut.Render list_accels

                       or: projectFrameCut.Render -h | --help
                    
                    Example: projectFrameCut.Render.exe -draft=draft.json -output=output.mkv -output_options=3840,2160,30,AV_PIX_FMT_GBRP16LE,ffv1 -acceleratorType=cuda -acceleratorDeviceId=0 -maxParallelThreads=8

                    See more at the document.
                    """);
                Console.ReadLine();
                return 0;
            }

            var runningMode = args[0];

            Log($"Running mode: {runningMode}");

            if (runningMode != "render" && runningMode != "rpc_backend" && runningMode != "list_accels")
            {
                Log($"ERROR: Mode {runningMode} doesn't defined.");
                Console.ReadLine();
                return 16;
            }


            ConcurrentDictionary<string, string> switches = new(args
                .Skip(1)
                .Select(x => x.Split('=', 2))
                .Where(x => x.Length == 2)
                .Select(x => new KeyValuePair<string, string>(x[0].TrimStart('-', '/'), x[1])));


            if (switches.ContainsKey("advancedFlags"))
            {
                foreach (var flag in switches["advancedFlags"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    advancedFlags.Add(flag);
                }
            }

            Log("Initializing accelerator context...");
            Context context = Context.CreateDefault();
            var devices = context.Devices.ToList();
            List<Device> picked = new();
            for (int i = 0; i < devices.Count; i++)
            {
                Console.WriteLine($"Accelerator device #{i}: {devices[i].Name} ({devices[i].AcceleratorType})");
            }

            if (runningMode == "list_accels")
            {
                List<AcceleratorInfo> listAccels = new();
                for (uint i = 0; i < devices.Count; i++)
                {
                    var item = devices[(int)i];
                    listAccels.Add(new AcceleratorInfo(i, item.Name, item.AcceleratorType.ToString()));
                }
                Console.Error.WriteLine(JsonSerializer.Serialize(listAccels, new JsonSerializerOptions { WriteIndented = true })
                    );
                return 0;
            }


            var multiAccel = bool.TryParse(switches.GetOrAdd("multiAccelerator", "false"), out var ma) ? ma : false;
            if (!multiAccel)
            {
                var acceleratorId = int.TryParse(switches.GetOrAdd("acceleratorDeviceId", "-1"), out var result1) ? result1 : -1;
                var accelType = switches.GetOrAdd("acceleratorType", "auto");
                var acc = PickOneAccel(accelType, acceleratorId, devices);
                if (acc is null)
                {
                    return 2;
                }
                picked.Add(acc);
                Console.WriteLine($"Selecting accelerator device #{devices.IndexOf(acc)}: {acc.Name} ({acc.AcceleratorType})");

            }
            else
            {
                var accelsIdsStr = switches.GetOrAdd("acceleratorDeviceIds", "");
                if (string.IsNullOrWhiteSpace(accelsIdsStr))
                {
                    Log("ERROR: multiAccelerator is set to true, but no acceleratorDeviceIds provided.");
                    return 2;
                }
                var accelsIds = accelsIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => int.TryParse(s, out var id) ? id : -1)
                    .Where(id => id >= 0)
                    .ToList();
                picked = accelsIds.Select(id =>
                {
                    var acc = PickOneAccel("auto", id, devices);
                    if (acc is null)
                    {
                        Log($"ERROR: Cannot pick accelerator device with id {id}.");
                    }
                    return acc;
                }).ToList()!;

            }

            if (picked == null || picked.Count == 0)
            {
                Log($"ERROR: No accelerator device picked. Check the configuration.");
                return 2;
            }

            foreach (var item in picked)
            {
                Console.WriteLine($"Picked accelerator {item.Name} : {item.AcceleratorType}");
                item.PrintInformation();
                Console.WriteLine();
            }

            Accelerator[] accelerators = picked.Select(d => d.CreateAccelerator(context)).ToArray();

            Log("Initiliazing FFmpeg...");
            ffmpeg.RootPath = switches.GetOrAdd("FFmpegLibraryPath", Path.Combine(AppContext.BaseDirectory, "FFmpeg", "8.x_internal"));
            FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            FFmpeg.AutoGen.DynamicallyLoadedBindings.Initialize();
            if (Program.advancedFlags.Contains("ffmpeg_loglevel_debug"))
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_DEBUG);
            else if (Program.advancedFlags.Contains("ffmpeg_loglevel_error"))
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            else if (Program.advancedFlags.Contains("ffmpeg_loglevel_none"))
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            else
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
            Log($"internal FFmpeg library: version {ffmpeg.av_version_info()}, {ffmpeg.avcodec_license()}\r\nconfiguration:{ffmpeg.avcodec_configuration()}");

            bool fSync = false;
            foreach (var accelerator in accelerators)
            {
                if (bool.TryParse(switches.GetOrAdd("forceSync", "default"), out fSync))
                {
                    Console.WriteLine($"Force synchronize is set to {fSync}");
                }
                else //默认值
                {
                    if (accelerator.AcceleratorType == AcceleratorType.OpenCL)
                    {
                        fSync = true;
                        Console.WriteLine($"Force synchronize is default set to true because of OpenCL accelerator is selected.");//不这么做Render会炸
                    }
                    else
                    {
                        fSync = false;
                        Console.WriteLine($"Force synchronize is default set to false");
                    }
                }
            }



            RegisterComputerGetter(accelerators, fSync);

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

            Console.WriteLine($"Output options: {width}x{height} @ {fps} fps, pixel format: {outputFormat}, encoder: {outputEncoder}, bitPerPixel:{(use16Bit ? "16" :"8")}");


            if (runningMode == "rpc_backend")
            {
                Rpc.RunRPC(switches, accelerators[0], width, height);
                Console.WriteLine($"RPC server exited with code {Rpc.RpcReturnCode}. Exiting...");
                return Rpc.RpcReturnCode;
            }


            var draftSrc = JsonSerializer.Deserialize<DraftStructureJSON
                >(File.ReadAllText(switches["draft"])) ?? throw new NullReferenceException();

            Console.WriteLine($"Draft loaded: duration {draftSrc.Duration}, saved on {draftSrc.SavedAt}, {draftSrc.Clips.Length} clips.");


            var duration = draftSrc.Duration;

            var frameRange = Enumerable.Range(0, (int)duration).Select(i => (uint)i).ToArray();


            List<JsonElement> clipsJson = draftSrc.Clips.Select(c => (JsonElement)c).ToList();

            var clipsList = new List<IClip>();


            foreach (var clip in clipsJson)
            {
                clipsList.Add(IClip.FromJSON(clip));
            }

            var clips = clipsList.ToArray();

            if (clips == null || clips.Length == 0)
            {
                Log("ERROR: No clips in the whole draft.");
                return 1;
            }

            SetSubProg("PrepareDraft");

            Log("Initializing all source video stream...");
            for (int i = 0; i < clips.Length; i++)
            {
                clips[i].ReInit();
            }

            Log("Setting up renderer and check frames to render...");
            VideoBuilder builder = new VideoBuilder(switches["output"], width, height, fps, outputEncoder, outputFormat)
            {
                EnablePreview = bool.TryParse(switches.GetOrAdd("preview", "false"), out var preview) ? preview : false,
                PreviewPath = switches.GetOrAdd("previewPath", "nope"),
                DoGCAfterEachWrite = true,
                DisposeFrameAfterEachWrite = true,
                Duration = duration,
                
            };

            int maxParallelThreads = int.TryParse(switches.GetOrAdd("maxParallelThreads", "8"), out var result) ? result : 8;

            if (bool.TryParse(switches.GetOrAdd("oneByOneRender", "false"), out var oneByOneRender) && oneByOneRender)
            {
                maxParallelThreads = 1;
                builder.BlockWrite = true;
            }
            else
            {
                builder.BlockWrite = false;
            }

            Renderer renderer = new Renderer
            {
                builder = builder,
                Clips = clips,
                Duration = duration,
                MaxThreads = maxParallelThreads,
                LogState = inprojectFrameCut,
                LogStatToLogger = true,
                Use16Bit = use16Bit,
            };

            switch (switches.GetOrAdd("GCOptions", "doLOHCompression"))
            {
                case "letCLRDoCollection":
                    builder.DoGCAfterEachWrite = false;
                    renderer.GCOption = 0;
                    break;
                case "doNormalCollection":
                    builder.DoGCAfterEachWrite = false;
                    renderer.GCOption = 1;
                    break;
                case "doLOHCompression":
                doLOHCompression:
                    builder.DoGCAfterEachWrite = true;
                    renderer.GCOption = 2;
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce; //avoid Picture eating too much memory cause hang or crash
                    break;
                default:
                    Console.WriteLine($"WARN: Undefined GC option {switches["GCOptions"]}, use doLOHCompression.");
                    goto doLOHCompression;
            }

            builder?.Build()?.Start();
            renderer.PrepareRender(CancellationToken.None);

            Stopwatch sw1 = new();
            SetSubProg("Render");
            Log("Start render...");

            sw1.Restart();
            await renderer.GoRender(CancellationToken.None);

            Log($"Render done,total elapsed {sw1}, avg elapsed {renderer.EachElapsedForPreparing.Average(t => t.TotalSeconds)} spf to prepare and {renderer.EachElapsed.Average(t => t.TotalSeconds)} spf to render");

            GC.Collect(2, GCCollectionMode.Forced, true); //减少内存占用，防止太卡影响后续的写入操作
            GC.WaitForFullGCComplete();

            SetSubProg("WriteVideo");
            Log("Finish writing video...");
            builder?.Finish((i) => Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, i, width, height), i, width, height), duration);

            Log($"Releasing resources...");

            foreach (var item in clips)
            {
                item?.Dispose();
            }
            foreach (var item in accelerators)
            {
                item?.Dispose();
            }
            context.Dispose();

            Log($"All done! Total elapsed {sw1}.");

            return 0;
        }

        private static Device? PickOneAccel(string accelType, int acceleratorId, List<Device> devices)
        {
            Device? pick = null;
            if (acceleratorId >= 0)
                pick = devices[acceleratorId];
            else if (accelType == "cuda")
                pick = devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda);
            else if (accelType == "opencl")
                pick = devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL
                            && (d.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase))) //优先用独显
                        ?? devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL);
            else if (accelType == "cpu")
                pick = devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.CPU);
            else if (accelType == "auto")
                pick =
                    devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda)
                    ?? devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL && (d.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase)))
                    ?? devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL)
                    ?? devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.CPU);
            else
            {
                Log($"ERROR: acceleratorType {accelType} is not supported.");
            }
            return pick;
        }

        private static void RegisterComputerGetter(Accelerator[] accels, bool sync)
        {
            AcceleratedComputerBridge.RequireAComputer = new((name) =>
            {
                switch (name)
                {
                    case "Overlay":
                        return new OverlayComputer(accels, sync);
                    case "RemoveColor":
                        return new RemoveColorComputer(accels, sync);
                    default:
                        Log($"Computer {name} not found.", "Error");
                        return null;

                }
            });
        }

        public static bool inprojectFrameCut = false;

        public static void SetSubProg(string progName)
        {
            if (!inprojectFrameCut) return;
            Console.Error.WriteLine($"##{progName}");
        }
    }
}
