using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Render.ILGpu;
using projectFrameCut.Shared;
using SixLabors.ImageSharp.Formats.Png;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;


internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        Log($"projectFrameCut.Render - {Assembly.GetExecutingAssembly().GetName().Version} \r\n" + $"Copyright hexadecimal0x12e 2025.\r\n" +
                $"cmdline: {Environment.GetCommandLineArgs().Aggregate((a, b) => $"{a} {b}")}");

        if (args.Length == 0 || args.Any(x => x.Equals("-h") || x.Equals("--help")))
        {
            Console.WriteLine(
                """
                    Usage: projectFrameCut.RPCBackend
                            -pipe="<name of pipe>"
                            -rawDataPipe="<name of raw data pipe>"
                            -output_options=<width>,<height>
                            -tempFolder="<path to temp folder>" 
                            [-acceleratorType=<auto|cuda|opencl|cpu>]
                            [-acceleratorDeviceId=<device id>]
                            [-forceSync=<true|false|default>]
                    Example: projectFrameCut.Render.exe -pipe="projectFrameCutPipe_abc"

                    See more at the document.
                    """);
            return 0;
        }

        if (Directory.GetFiles(Environment.CurrentDirectory, "av*.dll").Length == 0)
        {
            Log("ERROR: ffmpeg binaries not found. Please reinstall projectFrameCut.");
            return 1;
        }



        ConcurrentDictionary<string, string> switches = new(args
            .Select(x => x.Split('=', 2))
            .Where(x => x.Length == 2)
            .Select(x => new KeyValuePair<string, string>(x[0].TrimStart('-', '/'), x[1])));

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

        int width = 0, height = 0;
        var outputOptions = switches["output_options"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (outputOptions.Length != 2)
        {
            Log("ERROR: output_options must contain 5 values: width,height");
            return 1;
        }
        width = int.Parse(outputOptions[0]);
        height = int.Parse(outputOptions[1]);
        string size = (width * height).ToString();

        var pipeName = switches.GetValueOrDefault("pipe");
        var rawDataPipeName = switches.GetValueOrDefault("rawDataPipe");
        var tempFolder = switches.GetValueOrDefault("tempFolder");

        if (pipeName is null)
        {
            Log("ERROR: -pipe argument is required.");
            return 1;
        }

        if(rawDataPipeName is null)
        {
            Log("ERROR: -rawDataPipe argument is required.");
            return 1;
        }

        if(tempFolder is null)
        {
            Log("ERROR: -tempFolder argument is required.");
            return 1;
        }

        PngEncoder encoder = new();


        Console.OutputEncoding = Encoding.UTF8;

        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine($"[RPC] Backend starting. Pipe: {pipeName} rawPipe:{rawDataPipeName}");

        try
        {
            IClip[] clips = Array.Empty<IClip>();

            // 循环接受多个客户端（一次一个）
            while (!cts.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                Console.WriteLine("[RPC] Waiting for client...");
                await server.WaitForConnectionAsync(cts.Token);
                Console.WriteLine("[RPC] Client connected.");

                await using var rawDataServer = new NamedPipeServerStream(
                   rawDataPipeName,
                   PipeDirection.InOut,
                   maxNumberOfServerInstances: 1,
                   PipeTransmissionMode.Byte,
                   PipeOptions.Asynchronous);

                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), bufferSize: 8192, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                async Task Send(RpcProtocol.RpcMessage msg, Dictionary<string,string> data)
                {
                    var envelope = new RpcProtocol.RpcMessage
                    {
                        Type = msg.Type,
                        RequestId = msg.RequestId,
                        Payload = JsonSerializer.SerializeToElement(data, RpcProtocol.JsonOptions)
                    };

                    var json = JsonSerializer.Serialize(envelope, RpcProtocol.JsonOptions);
                    await writer.WriteLineAsync(json);
                }

                var disconnect = false;
                while (!cts.IsCancellationRequested && server.IsConnected && !disconnect)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break;

                    RpcProtocol.RpcMessage? msg = null;
                    try
                    {
                        msg = JsonSerializer.Deserialize<RpcProtocol.RpcMessage>(line, RpcProtocol.JsonOptions);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RPC] Bad JSON: {ex.Message}");
                        continue;
                    }

                    if (msg is null || string.IsNullOrWhiteSpace(msg.Type))
                        continue;

                    switch (msg.Type)
                    {
                        case "ping":
                            {
                                await Send(msg, new Dictionary<string, string> { { "value" , DateTime.Now.ToString() } });
                                break;
                            }
                        case "RenderOne":
                            {
                                if (msg.Payload is null)
                                {
                                    Console.WriteLine("[RPC] RenderOne missing payload.");
                                    break;
                                }
                                var frameIndex = msg.Payload.Value.GetUInt32();
                                Console.WriteLine($"[RPC] RenderOne request: frame #{frameIndex}");

                                var layers = Timeline.GetFramesInOneFrame(clips, frameIndex, width, height);
                                var pic = Timeline.MixtureLayers(layers, accelerator, frameIndex, width, height);
                                var destPath = Path.Combine(tempFolder, $"projectFrameCut_Render_{Guid.NewGuid():N}_{size}_{frameIndex}.png");
                                pic.SetAlpha(false).SaveAsPng8bpc(destPath,encoder);
                                await Send(msg, new Dictionary<string, string> { { "status", "completed" }, { "path",destPath} });
                                Console.WriteLine($"[RPC] RenderOne completed");
                                break;
                            }

                        case "UpdateClips":
                            {
                                if (msg.Payload is null)
                                {
                                    Console.WriteLine("[RPC] UpdateClips missing payload.");
                                    break;
                                }
                                Console.WriteLine("[RPC] Updating clips...");
                                var draftSrc = msg.Payload.Value.Deserialize<DraftStructureJSON>(RpcProtocol.JsonOptions) ?? throw new Exception("An error happends.");

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

                                clips = clipsList.ToArray();
                                Console.WriteLine($"[RPC] Updated clips, total {clips.Length} clips.");
                                await Send(msg, new Dictionary<string, string> { { "status", "ok" } });
                                break;
                            }
                        default:
                            Console.WriteLine($"[RPC] Unknown message type: {msg.Type}");
                            // 未知命令，忽略
                            break;
                    }
                }

                try
                {
                    server.Disconnect();
                }
                catch { /* ignore */ }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RPC] Fatal: {ex}");
            return 1;
        }

        Console.WriteLine("[RPC] Backend stopped.");
        return 0;
    }
}