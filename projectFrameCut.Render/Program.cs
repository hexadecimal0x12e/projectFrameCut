using FFmpeg.AutoGen;
using ILGPU;
using ILGPU.IR;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.Cuda;
using ILGPU.Runtime.OpenCL;
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

namespace projectFrameCut.Render
{
    internal class Program
    {
        public static ConcurrentBag<string> advancedFlags = new();

        static async Task<int> Main(string[] args)
        {
            Log($"projectFrameCut.Render - {Assembly.GetExecutingAssembly().GetName().Version} \r\n" + $"Copyright hexadecimal0x12e 2025.\r\n"+
                $"cmdline: {Environment.GetCommandLineArgs().Aggregate((a,b) => $"{a} {b}")}");

            if(args.Length == 0 || args.Any(x => x.Equals("-h") || x.Equals("--help")))
            {
                Console.WriteLine(
                    """
                    Usage: projectFrameCut.Render
                            -draft=<draft file path>
                            -range=<frame range>
                            -output=<output file/folder>
                            [-yieldSaveMode=<video|png_16bpc|png_8bpc|png_16bpc_alpha|png_8bpc_alpha>]                 
                            -output_options=<width>,<height>,<fps>,<pixel format>,<encoder>
                            [-acceleratorType=<auto|cuda|opencl|cpu>]
                            [-acceleratorDeviceId=<device id>]
                            [-forceSync=<true|false|default>]
                            [-renderManagerPipe=<named pipe path>]
                            [-maxParallelThreads=<number>]
                            [-StrictMode=true|false]
                            [-GCOptions=0|1|2|<negative integer>]
                            [-advancedFlags=<flag1>[,<flag2>,...,<flagn>]]
                    
                    Example: projectFrameCut.Render.exe -draft=draft.json -range=0-299 -output=output.mkv -output_options=3840,2160,30,AV_PIX_FMT_GBRP16LE,ffv1 -acceleratorType=cuda -acceleratorDeviceId=0 -maxParallelThreads=8

                    See more at the document.
                    """);
                return 0;
            }

            ConcurrentDictionary<string, string> switches = new(args
                .Select(x => x.Split('=', 2))
                .Where(x => x.Length == 2)
                .Select(x => new KeyValuePair<string, string>(x[0].TrimStart('-', '/'), x[1])));


            if(switches.ContainsKey("advancedFlags"))
            {
                foreach(var flag in switches["advancedFlags"].Split(',',StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    advancedFlags.Add(flag);
                }
            }

            NamedPipeClientStream? pipeClient = null;

            if (switches.ContainsKey("renderManagerPipe"))
            {
                Log("Initiliaze the renderManagerPipe...");
                pipeClient = new(".", switches["renderManagerPipe"], System.IO.Pipes.PipeDirection.InOut);
                pipeClient.Connect(5000);
                if (pipeClient.IsConnected)
                {
                    Log("renderManagerPipe connected.");
                }
                else
                {
                    Log("ERROR: renderManagerPipe connect failed.");
                    return 16;
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

            int maxParallelThreads = int.TryParse(switches.GetOrAdd("maxParallelThreads", "8"), out result) ? result : 8;

            var frameRange = RangeParser.ParseToSequence(switches["range"]);

            var outputOptions = switches["output_options"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

       
            var yieldSaveMode = switches.GetOrAdd("yieldSaveMode", "video").ToLower();

            int width = 0,height = 0,fps = 0;
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

            if(bool.TryParse(switches.GetOrAdd("forceSync", "default"),out var fSync))
            {
                Mixture.ForceSync = fSync;
                Effect.ForceSync = fSync;
                Console.WriteLine($"Force synchronize is set to {fSync}");
            }
            else //默认值
            {
                if(accelerator.AcceleratorType == AcceleratorType.OpenCL)
                {
                    Mixture.ForceSync = true;
                    Effect.ForceSync = true;
                    Console.WriteLine($"Force synchronize is default set to true because of OpenCL accelerator is selected.");
                }
                else
                {
                    Mixture.ForceSync = false;
                    Effect.ForceSync = false;
                    Console.WriteLine($"Force synchronize is set to false");
                }
            }

            var encoder = new PngEncoder()
            {
                BitDepth = PngBitDepth.Bit16,           
            };
             
            var draft = JsonSerializer.Deserialize<DraftStructure
                >(File.ReadAllText(switches["draft"])); 

            if (draft == null)
            {
                Log("ERROR: Draft is null");
                return 1;
            }
            Log("Initiliazing FFmpeg...");
            FFmpegHelper.RegisterFFmpeg();

            Log("Initiliazing all source video stream...");
            for (int i = 0; i < draft.Clips.Length; i++)
            {
                draft.Clips[i].Decoder = new VideoDecoder(draft.Clips[i].filePath).Decoder;
            }

            

            Stopwatch sw1 = new();
            ConcurrentBag<long> avgTime = new();
            Action<uint> render;
            VideoBuilder? builder = null;

            switch (yieldSaveMode)
            {
                case "video":
                    {
                        builder = new VideoBuilder(switches["output"], width, height, fps, outputEncoder, outputFormat);
                        builder.StrictMode = bool.TryParse(switches.GetOrAdd("StrictMode", "0"), out var strictMode) ? strictMode : false;
                        builder.Build().Start();

                        render = new((uint i) =>
                        {
                            Log($"[#{i:d6}] Start processing frame {i}...");
                            Stopwatch sw = Stopwatch.StartNew();
                            builder.Append(i,
                                Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(draft.Clips, i), accelerator, i)
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
                            Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(draft.Clips, i), accelerator, i).SetAlpha(false).SaveAsPng16bpc($"{switches["output"]}frame_{i:D6}.png",encoder);

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
                            Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(draft.Clips, i), accelerator, i).SetAlpha(false).SaveAsPng8bpc($"{switches["output"]}_{i:D6}.png",encoder);

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
                            Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(draft.Clips, i), accelerator, i).SetAlpha(true).SaveAsPng16bpc($"{switches["output"]}_{i:D6}.png", encoder);

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
                            Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(draft.Clips, i), accelerator, i).SetAlpha(true).SaveAsPng8bpc($"{switches["output"]}_{i:D6}.png", encoder);

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
            if (switches.ContainsKey("renderManagerPipe"))
            {
                task.ActionAfterExectued = (uint i) =>
                {
                    try
                    {
                        if (pipeClient!.IsConnected)
                        {
                            byte[] buffer = Encoding.UTF8.GetBytes($"frame_done:{i}\n");
                            pipeClient.Write(buffer, 0, buffer.Length);
                            pipeClient.Flush();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Pipe] ERROR: {ex.Message}");
                    }
                };
            }

            Log("Start render...");

            sw1.Restart();
            await task.Start(maxParallelThreads, frameRange);

            Log($"Render done,total elapsed {sw1}, avg elapsed {avgTime.Average() / 1000} spf");

            GC.Collect(2, GCCollectionMode.Forced, true); //减少内存占用，防止太卡影响后续的写入操作
            GC.WaitForFullGCComplete();

            Log("Finish writing video...");
            builder?.Finish(accelerator,draft.Clips);
            
            Log($"Releasing resources...");

            foreach (var item in draft.Clips)
            {
                item?.Decoder?.Dispose();
            }
            accelerator.Dispose(); 
            context.Dispose();  

            Log($"All done! Total elapsed {sw1}.");

            return 0;
        }


    }
}
