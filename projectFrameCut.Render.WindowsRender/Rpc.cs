using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.OpenCL;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.VideoMakeEngine;
using projectFrameCut.Render.Videos;
using projectFrameCut.Shared;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace projectFrameCut.Render.WindowsRender
{
    public class Rpc
    {
        public static int RpcReturnCode = 0;

        public static CancellationTokenSource RpcCts = new CancellationTokenSource();

        private static CancellationTokenSource? _currentTaskCts;
        private static readonly SemaphoreSlim _writerSemaphore = new SemaphoreSlim(1, 1);

        public static bool diagMode { get; private set; }

        public static void RunRPC(ConcurrentDictionary<string, string> switches, Accelerator accelerator, int width, int height)
        {

            var pipeName = switches.GetValueOrDefault("pipe");
            var rawDataPipeName = switches.GetValueOrDefault("rawDataPipe");
            var tempFolder = switches.GetValueOrDefault("tempFolder");
            //var accessToken = switches.GetValueOrDefault("accessToken");
            //var backendPort = switches.GetValueOrDefault("port");

            if (switches.ContainsKey("ParentPID"))
            {
                var parentPidStr = switches["ParentPID"];
                if (int.TryParse(parentPidStr, out var parentPid))
                {
                    Task.Run(() =>
                    {
                        while (true)
                        {
                            try
                            {
                                var parentProc = Process.GetProcessById(parentPid);
                                parentProc.WaitForExit();
                                Log($"Parent process {parentPid} exited, shutting down RPC server.");
                                RpcCts.Cancel();
                                Environment.Exit(0);
                            }
                            catch
                            {
                                // ignored
                            }
                        }
                    });
                }
            }

#if DEBUG
            //IPicture.DiagImagePath = tempFolder;
#endif

            if (pipeName is null)
            {
                Log("ERROR: -pipe argument is required.");
                RpcReturnCode = 16;
                RpcCts.Cancel();
                return;
            }


            if (tempFolder is null)
            {
                Log("ERROR: -tempFolder argument is required.");
                RpcReturnCode = 16;
                RpcCts.Cancel();
                return;
            }

            if (switches.ContainsKey("UseCheckerboardBackgroundForNoContent"))
            {
                IPicture Checkerboard = new Picture(Path.Combine(AppContext.BaseDirectory, "FallbackResources", "NoContent.png"));
                Timeline.FallBackImageGetter = (w, h) => Checkerboard.Resize(w, h, false);
            }

            PngEncoder encoder = new()
            {
                BitDepth = PngBitDepth.Bit8,
            };


            var cts = new CancellationTokenSource();

            IClip[] clips = Array.Empty<IClip>();

            Log("Warming up ILGPU...");


            var krnl = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<ushort>, ArrayView<ushort>, ArrayView<ushort>>((i, a, b, c) => c[i] = (ushort)(a[i] + b[i]));
            var inA = accelerator.Allocate1D<ushort>(Enumerable.Range(0, 10).Select(Convert.ToUInt16).ToArray());
            var inB = accelerator.Allocate1D<ushort>(Enumerable.Range(0, 10).Select(Convert.ToUInt16).ToArray());
            var outC = accelerator.Allocate1D<ushort>(10);
            krnl(10, inA.View, inB.View, outC.View);
            accelerator.Synchronize();
            Log("ILGPU is ready.");


            //Log($"[RPC] Booting nanohost...");
            //BootBackendNanoHost(int.TryParse(backendPort, out var p) ? p : -1, tempFolder, accessToken);

            Log($"[RPC] Server start at pipe:{pipeName}");

            var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

            server.WaitForConnectionAsync(cts.Token).Wait();

            if (!server.IsConnected)
            {
                Log("ERROR: client connection timeout. Exiting");
                RpcReturnCode = 32;
                RpcCts.Cancel();
                return;
            }

            Log("[RPC] Client connected.");

            using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), bufferSize: 8192, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            void Send(RpcProtocol.RpcMessage msg, Dictionary<string, object?> data)
            {
                var envelope = new RpcProtocol.RpcMessage
                {
                    Type = msg.Type,
                    RequestId = msg.RequestId,
                    Payload = JsonSerializer.SerializeToElement(data, RpcProtocol.JsonOptions)
                };

                var json = JsonSerializer.Serialize(envelope, RpcProtocol.JsonOptions);
                if (diagMode) Log($"[RPC] Sending: {json} \r\n--- \r\n");
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(10000);
                var lockTaken = false;
                try
                {
                    _writerSemaphore.Wait(cts.Token);
                    lockTaken = true;
                    try
                    {
                        // Use synchronous write to avoid AggregateException from Task.Wait() on cancellation.
                        writer.WriteLine(json);
                    }
                    finally
                    {
                        if (lockTaken)
                            _writerSemaphore.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine($"Error: failed to answer request #{msg.RequestId} because of timeout.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: failed to answer request #{msg.RequestId} because of exception {ex.GetType().Name}:{ex.Message}.");

                    throw;
                }
            }
            var size = width * height;
            var disconnect = false;
            while (!cts.IsCancellationRequested && server.IsConnected && !disconnect)
            {
#if !DEBUG
                try
                {
#endif
                if (diagMode) Log("[RPC] Waiting for message...");
                var line = reader.ReadLine();
                if (line is null) break;

                if (diagMode) Log($"[RPC] Received: {line} \r\n--- \r\n");

                RpcProtocol.RpcMessage? msg = null;
                try
                {
                    msg = JsonSerializer.Deserialize<RpcProtocol.RpcMessage>(line, RpcProtocol.JsonOptions);
                }
                catch (Exception ex)
                {
                    Log($"[RPC] Bad JSON: {ex.Message}");
                    continue;
                }

                if (msg is null || string.IsNullOrWhiteSpace(msg.Type))
                    continue;

                switch (msg.Type)
                {
                    case "ping":
                        {
                            Send(msg, new Dictionary<string, object?> { { "value", DateTime.Now } });
                            break;
                        }
                    case "CancelTask":
                        {
                            Log("[RPC] CancelTask requested.");
                            _currentTaskCts?.Cancel();
                            Send(msg, new Dictionary<string, object?> { { "status", "ok" } });
                            break;
                        }
                    case "RenderOne":
                        {
                            if (msg.Payload is null)
                            {
                                Console.Error.WriteLine("[RPC] RenderOne missing payload.");
                                break;
                            }
                            var frameIndex = msg.Payload.Value.GetProperty("frame").GetUInt32();
                            var targetWidth = msg.Payload.Value.GetProperty("width").GetInt32();
                            var targetHeight = msg.Payload.Value.GetProperty("height").GetInt32();

                            _currentTaskCts?.Cancel();
                            _currentTaskCts = new CancellationTokenSource();
                            var token = _currentTaskCts.Token;
                            var currentClips = clips;

                            _ = Task.Run(() =>
                            {
                                try
                                {
                                    Log($"[RPC] RenderOne request: frame #{frameIndex}");
                                    var frameHash = Timeline.GetFrameHash(currentClips, frameIndex);
                                    var destPath = Path.Combine(tempFolder, $"projectFrameCut_Render_{frameHash}.png");
                                    Log($"[RPC] FrameHash:{frameHash}");
                                    if (Path.Exists(destPath))
                                    {
                                        LogDiagnostic($"[RPC] Frame already exist; skip");
                                        Send(msg, new Dictionary<string, object?> { { "status", "ok" }, { "path", destPath } });
                                        LogDiagnostic($"[RPC] RenderOne completed");
                                        return;
                                    }
                                    else
                                    {
                                        LogDiagnostic($"[RPC] Generating frame #{frameIndex} ({frameHash})...");
                                    }

                                    if (token.IsCancellationRequested) return;

                                    var layers = Timeline.GetFramesInOneFrame(currentClips, frameIndex, targetWidth, targetHeight, true);
                                    
                                    if (token.IsCancellationRequested) return;

                                    LogDiagnostic($"Clips in frame #{frameIndex}:\r\n{GetFrameInfo(layers)}\r\n---");
                                    var pic = Timeline.MixtureLayers(layers, frameIndex, targetWidth, targetHeight);
                                    
                                    if (token.IsCancellationRequested) return;

                                    pic.SaveAsPng8bpp(destPath, encoder);
                                    Send(msg, new Dictionary<string, object?> { { "status", "ok" }, { "frameHash", frameHash }, { "path", destPath } });
                                    LogDiagnostic($"[RPC] RenderOne completed");
                                }
                                catch (Exception ex)
                                {
                                    Log(ex);
                                    Send(msg, new Dictionary<string, object?> { { "status", "error" }, { "path", Path.Combine(AppContext.BaseDirectory, "FallbackResources", "MediaNotAvailable.png") }, { "error", $"ERROR: a {ex.GetType()} exception happends:{ex.Message}" } });
                                }
                            }, token);
                            break;
                        }
                    case "RenderSomeFrames":
                        {
                            if (msg.Payload is null)
                            {
                                Console.Error.WriteLine("[RPC] RenderSomeFrames missing payload.");
                                break;
                            }
                            var startIndex = msg.Payload.Value.GetProperty("start").GetInt32();
                            var length = msg.Payload.Value.GetProperty("length").GetInt32();
                            var targetWidth = msg.Payload.Value.GetProperty("width").GetInt32();
                            var targetHeight = msg.Payload.Value.GetProperty("height").GetInt32();
                            var targetFramerate = msg.Payload.Value.GetProperty("framerate").GetInt32();

                            _currentTaskCts?.Cancel();
                            _currentTaskCts = new CancellationTokenSource();
                            var token = _currentTaskCts.Token;
                            var currentClips = clips;

                            _ = Task.Run(() =>
                            {
                                try
                                {
                                    // libx264 + yuv420p requires even width/height (4:2:0 chroma subsampling)
                                    // Otherwise avcodec_open2 may fail with AVERROR_EXTERNAL ("Generic error in an external library").
                                    var encodeWidth = (targetWidth % 2 == 0) ? targetWidth : targetWidth - 1;
                                    var encodeHeight = (targetHeight % 2 == 0) ? targetHeight : targetHeight - 1;
                                    if (encodeWidth <= 0 || encodeHeight <= 0)
                                    {
                                        Send(msg, new Dictionary<string, object?>
                                        {
                                            { "status", "error" },
                                            { "message", $"Invalid output size {targetWidth}x{targetHeight}. For libx264/yuv420p, width/height must be positive and even." }
                                        });
                                        return;
                                    }
                                    if (encodeWidth != targetWidth || encodeHeight != targetHeight)
                                    {
                                        Log($"[RPC] RenderSomeFrames: adjusted output size {targetWidth}x{targetHeight} -> {encodeWidth}x{encodeHeight} for libx264/yuv420p.");
                                    }

                                    var destPath = Path.Combine(tempFolder, $"projectFrameCut_Render_{Guid.NewGuid()}.mp4");
                                    Log($"[RPC] RenderSomeFrames request: frame #{startIndex}, length {length}");
                                    using var builder = new VideoWriter(destPath, encodeWidth, encodeHeight, targetFramerate, "libx264", FFmpeg.AutoGen.AVPixelFormat.AV_PIX_FMT_YUV420P);
                                    foreach (var item in Enumerable.Range(startIndex, length))
                                    {
                                        if (token.IsCancellationRequested)
                                        {
                                            Log("[RPC] RenderSomeFrames cancelled.");
                                            Send(msg, new Dictionary<string, object?> { { "status", "cancelled" } });
                                            return;
                                        }
                                        builder.Append(
                                            Timeline.MixtureLayers(
                                                Timeline.GetFramesInOneFrame(currentClips, (uint)item, encodeWidth, encodeHeight, true),
                                                (uint)item, encodeWidth, encodeHeight));
                                        LogDiagnostic($"Frame {item} in sequence rendered.");
                                    }
                                    builder.Finish();
                                    Send(msg, new Dictionary<string, object?>
                                    {
                                        { "status", "ok" },
                                        { "path", destPath },
                                        { "width", encodeWidth },
                                        { "height", encodeHeight }
                                    });
                                    Log($"[RPC] RenderSomeFrames completed");
                                }
                                catch (Exception ex)
                                {
                                    Log(ex);
                                    Send(msg, new Dictionary<string, object?> { { "status", "error" }, { "message", ex.Message } });
                                }
                            }, token);
                            break;
                        }

                    case "UpdateClips":
                        {
                            if (msg.Payload is null)
                            {
                                Log("[RPC] UpdateClips missing payload.");
                                break;
                            }
                            Log("[RPC] Updating clips...");
                            var draftSrc = msg.Payload.Value.Deserialize<DraftStructureJSON>(RpcProtocol.JsonOptions) ?? throw new Exception("An error happends.");

                            List<JsonElement> clipsJson = draftSrc.Clips.Select(c => (JsonElement)c).ToList();

                            var clipsList = new List<IClip>();

                            foreach (var clip in clipsJson)
                            {
                                clipsList.Add(PluginManager.CreateClip(clip));
                            }

                            clips = clipsList.ToArray();

                            foreach (var clip in clips)
                            {
                                clip.ReInit();
                            }

                            Log($"[RPC] Updated clips, total {clips.Length} clips.");
                            Send(msg, new Dictionary<string, object?> { { "status", "ok" } });
                            break;
                        }
                    case "GetAFrameData":
                        {
                            if (msg.Payload is null)
                            {
                                Console.Error.WriteLine("[RPC] GetAFrameData missing payload.");
                                break;
                            }
                            var frameIndex = msg.Payload.Value.GetUInt32();

                            _currentTaskCts?.Cancel();
                            _currentTaskCts = new CancellationTokenSource();
                            var token = _currentTaskCts.Token;
                            var currentClips = clips;

                            _ = Task.Run(() =>
                            {
                                try
                                {
                                    Log($"[RPC] GetAFrameData request: frame #{frameIndex}");
                                    var frameHash = Timeline.GetFrameHash(currentClips, frameIndex);
                                    
                                    if (token.IsCancellationRequested) return;

                                    var layers = Timeline.GetFramesInOneFrame(currentClips, frameIndex, width, height, true);
                                    
                                    if (token.IsCancellationRequested) return;

                                    Log($"Clips in frame #{frameIndex}:\r\n{JsonSerializer.Serialize(layers)}\r\n---");

                                    Send(msg, new Dictionary<string, object?> { { "status", "ok" }, { "json", JsonSerializer.Serialize(layers, new JsonSerializerOptions
                                        {
                                            WriteIndented = true,
                                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals

                                        } ) } });
                                    Log($"[RPC] GetAFrameData completed");
                                }
                                catch (Exception ex)
                                {
                                    Log(ex);
                                    Console.Error.WriteLine($"ERROR: a {ex.GetType()} exception happends:{ex.Message}");
                                    Send(msg, new Dictionary<string, object?> { { "status", "error" }, { "error", $"ERROR: a {ex.GetType()} exception happends:{ex.Message}" } });
                                }
                            }, token);

                            break;
                        }

                    case "GetVideoFileInfo":
                        {
                            if (msg.Payload is null)
                            {
                                Log("[RPC] GetVideoFileInfo missing payload.");
                                break;
                            }
                            var path = msg.Payload.Value.GetString();
                            if (string.IsNullOrWhiteSpace(path) || !Path.Exists(path))
                            {
                                Send(msg, new Dictionary<string, object?> { { "status", "error" }, { "message", "File not found." } });
                                break;
                            }
                            try
                            {
                                var vid = new Video(path);
                                Send(msg, new Dictionary<string, object?> { { "status", "ok" }, { "frameCount", vid.Decoder.TotalFrames }, { "fps", vid.Decoder.Fps }, { "width", vid.Decoder.Width }, { "height", vid.Decoder.Height } });
                            }
                            catch (Exception ex)
                            {
                                Send(msg, new Dictionary<string, object?> { { "status", "error" }, { "message", ex.Message } });
                            }
                            break;
                        }
                    case "ReadAFrame":
                        {
                            if (msg.Payload is null)
                            {
                                Log("[RPC] GetVideoFileInfo missing payload.");
                                break;
                            }
                            var path = msg.Payload.Value.GetProperty("path").GetString();
                            var FrameToRead = msg.Payload.Value.GetProperty("frameToRead").GetUInt32();
                            if (string.IsNullOrWhiteSpace(path) || !Path.Exists(path))
                            {
                                Send(msg, new Dictionary<string, object?> { { "status", "error" }, { "message", "File not found." } });
                                break;
                            }

                            try
                            {
                                var vid = new Video(path);
                                if (FrameToRead > vid.Decoder.TotalFrames)
                                {
                                    Send(msg, new Dictionary<string, object?> { { "status", "error" }, { "message", "Invaild length." } });
                                    break;
                                }
                                var frame = vid.Decoder.GetFrame(FrameToRead, true);
                                var tmpPath = Path.Combine(tempFolder, $"extractedFrame-{Path.GetFileNameWithoutExtension(path)}-{FrameToRead}.png");
                                if (msg.Payload.Value.TryGetProperty("size", out var destSize))
                                {
                                    string? sizeStr = "";
                                    if ((sizeStr = destSize.GetString()) is not null)
                                    {
                                        int w = int.Parse(sizeStr.Split('x')[0]);
                                        int h = int.Parse(sizeStr.Split('x')[1]);

                                        frame = frame.Resize(w, h);
                                    }
                                }
                                frame.SaveAsPng16bpp(tmpPath, encoder);
                                Send(msg, new Dictionary<string, object?> { { "status", "ok" }, { "path", tmpPath } });
                            }
                            catch (Exception ex)
                            {
                                Send(msg, new Dictionary<string, object?> { { "status", "error" }, { "message", ex.Message } });
                            }
                            break;
                        }
                    case "ConfigurePreview":
                        {
                            if (msg.Payload is null)
                            {
                                Log("[RPC] ConfigurePreview missing payload.");
                                break;
                            }
                            var prevWidth = msg.Payload.Value.GetProperty("width").GetInt32();
                            var prevHeight = msg.Payload.Value.GetProperty("height").GetInt32();
                            width = prevWidth;
                            height = prevHeight;
                            Send(msg, new Dictionary<string, object?> { { "status", "ok" } });
                            break;
                        }


                    case "ShutDown":
                        {
                            Log("[RPC] Shutting down...");
                            disconnect = true;
                            Send(msg, new Dictionary<string, object?> { { "status", "ok" } });
                            server.DisposeAsync();
                            nanoHostProc?.Kill();
                            RpcCts.Cancel();
                            return;
                        }

                    default:
                        Log($"[RPC] Unknown message type: {msg.Type}");
                        break;
                }
#if !DEBUG

                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ERROR: a {ex.GetType()} exception happends:{ex.Message}");
                    Log(ex, $"Processing RPC Message", "RPC Client");
                }
                continue;

#endif

            }

            cts.Cancel();
            RpcCts.Cancel();
            return;

        }

        private static string GetFrameInfo(IEnumerable<OneFrame> layers)
        {
            if (!layers.Any()) return "Null frame";
            string result =
                $"""
                Frame {layers.First().FrameNumber}:
                - Total {layers.Count()} clips.

                """;
            foreach (var frame in layers.OrderBy(x => x.LayerIndex))
            {
                var clip = frame.ParentClip;
                result +=
                    $"""
                    Clip {clip.Name}/{clip.Id}:
                    - start in {clip.StartFrame}, duration {clip.Duration}
                    - in layer {frame.LayerIndex}
                    - clip's picture info:
                    {frame.Clip.GetDiagnosticsInfo()}

                    ---
                    """;
            }
            return result;
        }

        private static Process nanoHostProc;
        private static int backendPort = -1;

        private static void BootBackendNanoHost(int port, string tempDir, string token)
        {
            var backendPath = Path.Combine(AppContext.BaseDirectory, "projectFrameCut.Render.WindowsRender.NanoHost.exe");

            if (port == -1)
            {
                throw new InvalidOperationException("No port available.");
            }
            var info = new ProcessStartInfo
            {
                FileName = backendPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.EnvironmentVariables.Add("ASPNETCORE_URLS", $"http://+:{port}");
            info.EnvironmentVariables.Add("ThumbServicesBasePath", tempDir);
            info.EnvironmentVariables.Add("AccessToken", token);
            nanoHostProc = Process.Start(info);
            Log($"Backend NanoHost started on port {port}");

        }


    }
}
