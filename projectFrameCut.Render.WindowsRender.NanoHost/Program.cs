using Microsoft.AspNetCore.Http.HttpResults;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace projectFrameCut.Render.WindowsRender.NanoHost
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateSlimBuilder(args);

            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, RpcMessageJsonContext.Default);
            });

            // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
            builder.Services.AddOpenApi();

            var app = builder.Build();

            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            var basePath = Environment.GetEnvironmentVariable("ThumbServicesBasePath");
            var token = Environment.GetEnvironmentVariable("AccessToken") ?? throw new NullReferenceException("please set a token.");

            if (basePath is not null)
            {
                FileServer.ContentRoot = basePath;
                app.MapGet("/ThumbReadService/{fid}/{accessToken}", (string fid, string accessToken) => accessToken == token ? FileServer.ReadThumb(fid) : Results.StatusCode(403));

            }
            else
            {
                app.MapGet("/ThumbReadService/{fid}/{accessToken}", (string fid, string accessToken) => Results.StatusCode(503));

            }



            var RpcPipe = Environment.GetEnvironmentVariable("EnableRemoteRPC");
            if (RpcPipe is not null)
            {
                Console.WriteLine("Remote RPC Service Enabled");
                 _pipe = new NamedPipeClientStream(".", RpcPipe, PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                 _reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
                 _writer = new StreamWriter(_pipe, new UTF8Encoding(false), bufferSize: 8192, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };
                app.MapPost("/RemoteRPC/Send", async (HttpContext c) =>
                {
                    if(c.Request.Query["accessToken"] != token)
                    {
                        c.Response.StatusCode = 403;
                        return;
                    }
                    var req = await c.Request.ReadFromJsonAsync<RpcProtocol.RpcMessage>();
                    var rsp = await SendReceiveAsync(req, default);
                    if (rsp is not null)
                    {
                        await c.Response.WriteAsJsonAsync(rsp, RpcProtocol.JsonOptions);
                    }
                    else
                    {
                        c.Response.StatusCode = 500;
                    }
                });
            }
            else
            {
                app.MapPost("/RemoteRPC/Send", async (HttpContext c) =>
                {
                    c.Response.StatusCode = 503;
                });
            }

            
            app.Run();
        }

        static readonly SemaphoreSlim _ioLock = new(1, 1);
        static NamedPipeClientStream? _pipe;
        static StreamReader? _reader;
        static StreamWriter? _writer;

        private static async Task<RpcProtocol.RpcMessage?> SendReceiveAsync(RpcProtocol.RpcMessage req, CancellationToken ct)
        {
            if (_writer is null || _reader is null) throw new InvalidOperationException("RPC doesn't connected");

            await _ioLock.WaitAsync(ct);
            try
            {
                var json = JsonSerializer.Serialize(req, RpcProtocol.JsonOptions);
                await _writer.WriteLineAsync(json);
                var line = await _reader.ReadLineAsync(ct);
                if (line is null) return null;
                return JsonSerializer.Deserialize<RpcProtocol.RpcMessage>(line, RpcProtocol.JsonOptions);
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "SendReceive");
                return null;
            }
            finally
            {
                _ioLock.Release();
            }
        }
    }

    
}
