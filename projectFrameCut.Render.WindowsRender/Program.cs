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

            if (args.Length <= 1 || args.Any(x => x.Equals("-h") || x.Equals("--help")))
            {
                Console.WriteLine(
                    """
                    Usage: projectFrameCut.Render render
                            -draft=<draft file path>
                            -duration=<frame range>
                            -output=<output file/folder>
                            [-yieldSaveMode=<video|png_16bpc|png_8bpc|png_16bpc_alpha|png_8bpc_alpha>]                 
                            -output_options=<width>,<height>,<fps>,<pixel format>,<encoder>
                            [-acceleratorType=<auto|cuda|opencl|cpu>]
                            [-acceleratorDeviceId=<device id>]
                            [-forceSync=<true|false|default>]
                            [-maxParallelThreads=<number>]
                            [-StrictMode=true|false]
                            [-GCOptions=0|1|2|<negative integer>]
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
                    
                    Example: projectFrameCut.Render.exe -draft=draft.json -range=0-299 -output=output.mkv -output_options=3840,2160,30,AV_PIX_FMT_GBRP16LE,ffv1 -acceleratorType=cuda -acceleratorDeviceId=0 -maxParallelThreads=8

                    See more at the document.
                    """);
                Console.ReadLine();
                return 0;
            }

            if (Directory.GetFiles(Environment.CurrentDirectory, "av*.dll").Length == 0)
            {
                Log("ERROR: ffmpeg binaries not found. Please reinstall projectFrameCut.");
                return 1;
            }

            Log($"internal FFmpeg library: version {ffmpeg.av_version_info()}, {ffmpeg.avcodec_license()}\r\nconfiguration:{ffmpeg.avcodec_configuration()}");

            var runningMode = args[0];

            Log($"Running mode: {runningMode}");

            if (runningMode != "render" && runningMode != "rpc_backend")
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

            var acceleratorId = int.TryParse(switches.GetOrAdd("acceleratorDeviceId", "-1"), out var result) ? result : -1;
            var accelType = switches.GetOrAdd("acceleratorType", "auto");
            var devices = context.Devices.ToList();
            for (int i = 0; i < devices.Count; i++)
            {
                Console.WriteLine($"Accelerator device #{i}: {devices[i].Name} ({devices[i].AcceleratorType})");
            }

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
                return 2;
            }

            if (pick == null)
            {
                Log($"ERROR: No accelerator device found.");
                return 2;
            }

            Console.WriteLine($"Selecting accelerator device #{devices.IndexOf(pick)}: {pick.Name} ({pick.AcceleratorType})");

            Accelerator accelerator = (pick ?? devices.First()).CreateAccelerator(context);

            Console.WriteLine($"Accelerator info:");
            accelerator.PrintInformation();

            Log("Initiliazing FFmpeg...");
            if (Program.advancedFlags.Contains("ffmpeg_loglevel_debug"))
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_DEBUG);
            else if (Program.advancedFlags.Contains("ffmpeg_loglevel_error"))
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            else if (Program.advancedFlags.Contains("ffmpeg_loglevel_none"))
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            else
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);


            if (bool.TryParse(switches.GetOrAdd("forceSync", "default"), out var fSync))
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


            RegisterComputerGetter(accelerator,fSync);

            var outputOptions = switches["output_options"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var yieldSaveMode = switches.GetOrAdd("yieldSaveMode", "video").ToLower();

            int width = 0, height = 0, fps = 0;
            FFmpeg.AutoGen.AVPixelFormat outputFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            string outputEncoder = "";

            if (yieldSaveMode == "video")
            {
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

                Console.WriteLine($"Output options: video, {width}x{height} @ {fps} fps, pixel format: {outputFormat}, encoder: {outputEncoder}");

            }
            else
            {
                Console.WriteLine($"Output options: image sequence, mode:{yieldSaveMode}");
            }


            if (runningMode == "rpc_backend")
            {
                Rpc.go_rpcAsync(switches, accelerator, width, height);
                Console.WriteLine($"RPC server exited with code {Rpc.RpcReturnCode}. Exiting...");
                return Rpc.RpcReturnCode;
            }

            PngEncoder encoder = yieldSaveMode switch
            {
                "png_8bpc" or "png_8bpc_alpha" => new PngEncoder { BitDepth = PngBitDepth.Bit8 },
                _ => new PngEncoder { BitDepth = PngBitDepth.Bit16 }
            };

            int maxParallelThreads = int.TryParse(switches.GetOrAdd("maxParallelThreads", "8"), out result) ? result : 8;

            //var frameRange = RangeParser.ParseToSequence(switches["range"]);

            var duration = int.Parse(switches.GetOrAdd("duration", "0"));

            var frameRange = Enumerable.Range(0, duration).Select(i => (uint)i).ToArray();

            var draftSrc = JsonSerializer.Deserialize<DraftStructureJSON
                >(File.ReadAllText(switches["draft"])) ?? throw new NullReferenceException();

            List<JsonElement> clipsJson = draftSrc.Clips.Select(c => (JsonElement)c).ToList();

            var clipsList = new List<IClip>();


            foreach (var clip in clipsJson)
            {

                var type = (ClipMode)clip.GetProperty("ClipType").GetInt32();
                Console.WriteLine($"Found clip {type}, name: {clip.GetProperty("Name").GetString()}, id: {clip.GetProperty("Id").GetString()}");
                switch (type)
                {
                    case ClipMode.VideoClip:
                        {
                            clipsList.Add(clip.Deserialize<VideoClip>() ?? throw new NullReferenceException());
                            break;
                        }
                    case ClipMode.PhotoClip:
                        {
                            clipsList.Add(clip.Deserialize<PhotoClip>() ?? throw new NullReferenceException());
                            break;
                        }
                    case ClipMode.SolidColorClip:
                        {
                            clipsList.Add(clip.Deserialize<SolidColorClip>() ?? throw new NullReferenceException());
                            break;
                        }
                    default:
                        Log("ERROR: Unknown clip type.");
                        return 1;
                }
            }

            var clips = clipsList.ToArray();

            if (clips == null || clips.Length == 0)
            {
                Log("ERROR: No clips in the whole draft.");
                return 1;
            }

            SetSubProg("PrepareDraft");

            Log("Initiliazing all source video stream...");
            for (int i = 0; i < clips.Length; i++)
            {
                clips[i].ReInit();
            }



            Stopwatch sw1 = new();
            ConcurrentBag<long> avgTime = new();
            Action<uint> render;
            VideoBuilder? builder = null;

            switch (yieldSaveMode)
            {
                case "video":
                    {
                        builder = new VideoBuilder(switches["output"], width, height, fps, outputEncoder, outputFormat)
                        {
                            EnablePreview = bool.TryParse(switches.GetOrAdd("preview", "false"), out var preview) ? preview : false,
                            PreviewPath = switches.GetOrAdd("previewPath", "nope"),
                        };
                        builder.StrictMode = bool.TryParse(switches.GetOrAdd("StrictMode", "0"), out var strictMode) ? strictMode : false;
                        builder.Build().Start();

                        render = new((uint i) =>
                        {
                            Log($"[#{i:d6}] Start processing frame {i}...");
                            Stopwatch sw = Stopwatch.StartNew();
                            builder.Append(i,
                                Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, i, width, height), i, width, height)
                            );

                            sw.Stop();
                            avgTime.Add(sw.ElapsedMilliseconds);
                            Log($"[#{i:d6}] frame {i} render done, elapsed {sw.Elapsed}");
                        });
                        break;
                    }

                case "png_16bpc":
                    {
                        render = new((uint i) =>
                        {
                            Log($"[#{i:d6}] Start processing frame {i}...");
                            Stopwatch sw = Stopwatch.StartNew();
                            Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, i, width, height), i, width, height).SetAlpha(false).SaveAsPng16bpc($"{switches["output"]}frame_{i:D6}.png", encoder);

                            sw.Stop();
                            avgTime.Add(sw.ElapsedMilliseconds);
                            Log($"[#{i:d6}] frame {i} render done, elapsed {sw.Elapsed}");
                        });
                        break;
                    }

                case "png_8bpc":
                    {
                        render = new((uint i) =>
                        {
                            Log($"[#{i:d6}] Start processing frame {i}...");
                            Stopwatch sw = Stopwatch.StartNew();
                            Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, i, width, height), i, width, height).SetAlpha(false).SaveAsPng8bpc($"{switches["output"]}_{i:D6}.png", encoder);

                            sw.Stop();
                            avgTime.Add(sw.ElapsedMilliseconds);
                            Log($"[#{i:d6}] frame {i} render done, elapsed {sw.Elapsed}");
                        });
                        break;
                    }

                case "png_16bpc_alpha":
                    {
                        render = new((uint i) =>
                        {
                            Log($"[#{i:d6}] Start processing frame {i}...");
                            Stopwatch sw = Stopwatch.StartNew();
                            Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, i, width, height), i, width, height).SetAlpha(true).SaveAsPng16bpc($"{switches["output"]}_{i:D6}.png", encoder);

                            sw.Stop();
                            avgTime.Add(sw.ElapsedMilliseconds);
                            Log($"[#{i:d6}] frame {i} render done, elapsed {sw.Elapsed}");
                        });
                        break;
                    }

                case "png_8bpc_alpha":
                    {
                        render = new((uint i) =>
                        {
                            Log($"[#{i:d6}] Start processing frame {i}...");
                            Stopwatch sw = Stopwatch.StartNew();
                            Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, i, width, height), i, width, height).SetAlpha(true).SaveAsPng8bpc($"{switches["output"]}_{i:D6}.png", encoder);

                            sw.Stop();
                            avgTime.Add(sw.ElapsedMilliseconds);
                            Log($"[#{i:d6}] frame {i} render done, elapsed {sw.Elapsed}");
                        });
                        break;
                    }
                default:
                    {
                        Log($"ERROR: yieldSaveMode {yieldSaveMode} is not supported.");
                        return 1;
                    }

            }

            MultiTask<uint> task = new MultiTask<uint>(render);
            task.ThrowOnAnyError = bool.TryParse(switches.GetOrAdd("StrictMode", "0"), out var value) ? value : false;
            task.ThrowOnErrorHappensImmediately = bool.TryParse(switches.GetOrAdd("StopOnAnyError", "0"), out var stopOnErr) ? stopOnErr : false;
            task.GCOptions = int.TryParse(switches.GetOrAdd("GCOptions", "0"), out var value1) ? value1 : 0;
            task.InternalLogging = inprojectFrameCut;

            Log("Start render...");

            sw1.Restart();
            SetSubProg("Render");
            await task.Start(maxParallelThreads, frameRange);

            Log($"Render done,total elapsed {sw1}, avg elapsed {avgTime.Average() / 1000} spf");

            GC.Collect(2, GCCollectionMode.Forced, true); //减少内存占用，防止太卡影响后续的写入操作
            GC.WaitForFullGCComplete();

            SetSubProg("WriteVideo");
            Log("Finish writing video...");
            builder?.Finish(clips.ToArray(), (i) => Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, i, width, height), i, width, height), (uint)duration);

            Log($"Releasing resources...");

            foreach (var item in clips)
            {
                item?.Dispose();
            }
            accelerator.Dispose();
            context.Dispose();

            Log($"All done! Total elapsed {sw1}.");

            return 0;
        }

        private static void RegisterComputerGetter(Accelerator accel, bool sync)
        {
            AcceleratedComputerBridge.RequireAComputer = new((name) =>
            {
                switch (name)
                {
                    case "Overlay":
                        return new OverlayComputer(accel, sync);
                    case "RemoveColor":
                        return new RemoveColorComputer(accel, sync);
                    default:
                        Log($"Computer {name} not found.","Error");
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
