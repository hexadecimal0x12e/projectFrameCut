using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Render.ILGpu;
using projectFrameCut.Shared;
using SixLabors.ImageSharp.Formats.Png;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace projectFrameCut.Render.WindowsRender
{
    public class Rpc
    {
        public static async Task<int> go_rpcAsync(ConcurrentDictionary<string, string> switches, Accelerator accelerator, int width, int height)
        {

            var pipeName = switches.GetValueOrDefault("pipe");
            var rawDataPipeName = switches.GetValueOrDefault("rawDataPipe");
            var tempFolder = switches.GetValueOrDefault("tempFolder");

            if (pipeName is null)
            {
                Log("ERROR: -pipe argument is required.");
                return 1;
            }


            if (tempFolder is null)
            {
                Log("ERROR: -tempFolder argument is required.");
                return 1;
            }

            PngEncoder encoder = new()
            {
                BitDepth = PngBitDepth.Bit8
            };


            var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            IClip[] clips = Array.Empty<IClip>();

            Log("Warming up ILGPU...");

            {
                var krnl = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<ushort>, ArrayView<ushort>, ArrayView<ushort>>((i, a, b, c) => c[i] = (ushort)(a[i] + b[i]));
                var inA = accelerator.Allocate1D<ushort>(Enumerable.Range(0, 10).Select(Convert.ToUInt16).ToArray());
                var inB = accelerator.Allocate1D<ushort>(Enumerable.Range(0, 10).Select(Convert.ToUInt16).ToArray());
                var outC = accelerator.Allocate1D<ushort>(10);
                krnl(10,inA.View, inB.View, outC.View);
                accelerator.Synchronize();
                Log("ILGPU is ready.");
            }

            Log($"[RPC] Server start at pipe:{pipeName}");

            var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

            await server.WaitForConnectionAsync(cts.Token);

            Log("[RPC] Client connected.");

            using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), bufferSize: 8192, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            async Task Send(RpcProtocol.RpcMessage msg, Dictionary<string, object?> data)
            {
                var envelope = new RpcProtocol.RpcMessage
                {
                    Type = msg.Type,
                    RequestId = msg.RequestId,
                    Payload = JsonSerializer.SerializeToElement(data, RpcProtocol.JsonOptions)
                };

                var json = JsonSerializer.Serialize(envelope, RpcProtocol.JsonOptions);
                Log($"[RPC] Sending: {json} \r\n--- \r\n");
                await writer.WriteLineAsync(json);
            }
            var size = width * height;

            var disconnect = false;
            while (!cts.IsCancellationRequested && server.IsConnected && !disconnect)
            {
                try
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break;

                    Log($"[RPC] Received: {line} \r\n--- \r\n");

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
                                await Send(msg, new Dictionary<string, object?> { { "value", DateTime.Now } });
                                break;
                            }
                        case "RenderOne":
                            {
                                if (msg.Payload is null)
                                {
                                    Console.Error.WriteLine("[RPC] RenderOne missing payload.");
                                    break;
                                }
                                var frameIndex = msg.Payload.Value.GetUInt32();
                                Log($"[RPC] RenderOne request: frame #{frameIndex}");
                                var frameHash = Timeline.GetFrameHash(clips, frameIndex);
                                var destPath = Path.Combine(tempFolder, $"projectFrameCut_Render_{frameHash}.png");
                                Log($"[RPC] FrameHash:{frameHash}");
                                if (Path.Exists(destPath))
                                {

                                    Log($"[RPC] Frame already exist; skip");
                                    await Send(msg, new Dictionary<string, object?> { { "status", "completed" }, { "path", destPath } });
                                    Log($"[RPC] RenderOne completed");
                                    break;
                                }
                                else
                                {
                                    Log($"[RPC] Generating frame #{frameIndex} ({frameHash})...");
                                }
                                var layers = Timeline.GetFramesInOneFrame(clips, frameIndex, width, height);
                                var pic = Timeline.MixtureLayers(layers, accelerator, frameIndex, width, height);
                                pic.SetAlpha(false).SaveAsPng8bpc(destPath, encoder);
                                await Send(msg, new Dictionary<string, object?> { { "status", "completed" }, { "path", destPath } });
                                Log($"[RPC] RenderOne completed");
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

                                    var type = (ClipMode)clip.GetProperty("ClipType").GetInt32();
                                    Log($"Found clip {type}, name: {clip.GetProperty("Name").GetString()}, id: {clip.GetProperty("Id").GetString()}");
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
                                            throw new NotSupportedException($"Clip type {type} is not suported.");
                                    }
                                }

                                clips = clipsList.ToArray();

                                foreach (var clip in clips)
                                {
                                    clip.ReInit();
                                }

                                Log($"[RPC] Updated clips, total {clips.Length} clips.");
                                await Send(msg, new Dictionary<string, object?> { { "status", "ok" } });
                                break;
                            }
                        case "ShutDown":
                            {
                                Log("[RPC] Shutting down...");
                                disconnect = true;
                                await Send(msg, new Dictionary<string, object?> { { "status", "ok" } });
                                await server.DisposeAsync();
                                return 0;
                            }
                        default:
                            Log($"[RPC] Unknown message type: {msg.Type}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ERROR: a {ex.GetType()} exception happends:{ex.Message}");
                }
            }




            return 0;

        }

    }
}
