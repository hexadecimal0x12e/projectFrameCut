using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;

return await ExternalRpcSourceClient.RunAsync(args);

internal static class ExternalRpcSourceClient
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var options = ParseOptions(args);
            using var lifetime = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                lifetime.Cancel();
            };

            var provider = new TestVideoSourceProvider(options);
            await using var client = new RenderClient(
                new NamedPipeRenderClientTransport(options.PipeName, options.ClientId.ToString("D")),
                options.ClientId.ToString("D"),
                provider);

            var capabilities = await client.GetCapabilitiesAsync(lifetime.Token);
            Console.WriteLine($"Connected: protocol={capabilities.ProtocolVersion}, backend={capabilities.BackendVersion}");
            await client.RegisterExternalVideoSourcesAsync(new() { Sources = provider.Sources.ToList() }, lifetime.Token);
            Console.WriteLine(JsonSerializer.Serialize(provider.Sources, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("External source registered. Add it from the project UI; press Ctrl+C to stop.");

            await Task.Delay(options.Duration, lifetime.Token);
            await client.UnregisterExternalVideoSourcesAsync(CancellationToken.None);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or RenderRpcException)
        {
            Console.Error.WriteLine($"External RPC source client failed: {ex.Message}");
            return 1;
        }
    }

    private static Options ParseOptions(string[] args)
    {
        string? pipe = null;
        Guid clientId = Guid.Empty;
        var sourceId = "test-source";
        var name = "External RPC test source";
        var width = 640;
        var height = 360;
        var frames = 300L;
        var fps = 30d;
        var bits = 8;
        var hdr = false;
        var alpha = false;
        var duration = Timeout.InfiniteTimeSpan;

        for (var i = 0; i < args.Length; i++)
        {
            string option = args[i];
            string value = option switch
            {
                "--pipe" => Next(args, ref i, "--pipe"),
                "--client-id" => Next(args, ref i, "--client-id"),
                "--source-id" => Next(args, ref i, "--source-id"),
                "--name" => Next(args, ref i, "--name"),
                "--width" => Next(args, ref i, "--width"),
                "--height" => Next(args, ref i, "--height"),
                "--frames" => Next(args, ref i, "--frames"),
                "--fps" => Next(args, ref i, "--fps"),
                "--bits" => Next(args, ref i, "--bits"),
                "--seconds" => Next(args, ref i, "--seconds"),
                "--hdr" => "true",
                "--alpha" => "true",
                _ => throw new ArgumentException($"Unknown option '{option}'.")
            };

            switch (option)
            {
                case "--pipe": pipe = value; break;
                case "--client-id": clientId = Guid.Parse(value); break;
                case "--source-id": sourceId = value; break;
                case "--name": name = value; break;
                case "--width": width = int.Parse(value); break;
                case "--height": height = int.Parse(value); break;
                case "--frames": frames = long.Parse(value); break;
                case "--fps": fps = double.Parse(value); break;
                case "--bits": bits = int.Parse(value); break;
                case "--seconds": duration = TimeSpan.FromSeconds(double.Parse(value)); break;
                case "--hdr": hdr = true; break;
                case "--alpha": alpha = true; break;
            }
        }

        if (string.IsNullOrWhiteSpace(pipe) || clientId == Guid.Empty)
            throw new ArgumentException("--pipe and --client-id are required.");
        if (width is < 1 or > 65536 || height is < 1 or > 65536 || frames < 0 || !double.IsFinite(fps) || fps < 0 || bits is not (8 or 16))
            throw new ArgumentException("Invalid source dimensions, frame count, fps or bit depth.");
        if (duration < TimeSpan.Zero)
            throw new ArgumentException("--seconds cannot be negative.");

        return new(pipe, clientId, sourceId, name, width, height, frames, fps, bits, hdr, alpha, duration);
    }

    private static string Next(string[] args, ref int index, string option) =>
        ++index < args.Length && !string.IsNullOrWhiteSpace(args[index])
            ? args[index]
            : throw new ArgumentException($"Option '{option}' requires a value.");

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: projectFrameCut.ExternalRpcSourceClient --pipe <name> --client-id <guid> [options]");
        Console.WriteLine("Options: --source-id <id> --name <name> --width <n> --height <n> --frames <n> --fps <n> --bits 8|16 --hdr --alpha --seconds <n>");
    }

    private sealed record Options(string PipeName, Guid ClientId, string SourceId, string Name,
        int Width, int Height, long Frames, double Fps, int Bits, bool Hdr, bool Alpha, TimeSpan Duration);

    private sealed class TestVideoSourceProvider(Options options) : IExternalVideoSourceProvider
    {
        private readonly ConcurrentDictionary<Guid, string> _instances = new();
        private readonly ExternalVideoSourceDescriptor _source = new()
        {
            SourceId = options.SourceId,
            Name = options.Name,
            DecoderName = "ExternalRpcTestDecoder",
            PreferredExtensions = [".pjfc-test"],
            TotalFrames = options.Frames,
            Fps = options.Fps,
            Width = options.Width,
            Height = options.Height,
            ResultBitsPerPixel = options.Bits,
            HasKnownResultBitsPerPixel = true,
            SupportsHdr = options.Hdr,
            SupportsAlpha = options.Alpha,
            Metadata = new() { ["generator"] = "projectFrameCut.ExternalRpcSourceClient" },
        };

        public IReadOnlyList<ExternalVideoSourceDescriptor> Sources => [_source];

        public ValueTask<ExternalVideoSourceInstance> CreateAsync(ExternalVideoSourceCreateRequest request, CancellationToken cancellationToken = default)
        {
            var instance = Guid.NewGuid();
            _instances[instance] = request.Source.SourceId;
            return ValueTask.FromResult(new ExternalVideoSourceInstance { InstanceId = instance, Descriptor = _source });
        }

        public ValueTask<ExternalVideoSourceInstance> InitializeAsync(ExternalVideoSourceStateRequest request, CancellationToken cancellationToken = default)
        {
            if (!_instances.ContainsKey(request.InstanceId)) throw new InvalidOperationException("Unknown source instance.");
            return ValueTask.FromResult(new ExternalVideoSourceInstance { InstanceId = request.InstanceId, Descriptor = _source });
        }

        public ValueTask<ExternalVideoFrame> ReadFrameAsync(ExternalVideoSourceReadRequest request, CancellationToken cancellationToken = default)
        {
            if (!_instances.ContainsKey(request.State.InstanceId)) throw new InvalidOperationException("Unknown source instance.");
            int width = request.TargetWidth > 0 ? request.TargetWidth : options.Width;
            int height = request.TargetHeight > 0 ? request.TargetHeight : options.Height;
            var frame = CreateFrame(width, height, request.TargetFrame, request.RequestHdr && options.Hdr, request.HasAlpha && options.Alpha);
            return ValueTask.FromResult(frame);
        }

        public ValueTask ReleaseAsync(ExternalVideoSourceStateRequest request, CancellationToken cancellationToken = default)
        {
            _instances.TryRemove(request.InstanceId, out _);
            return ValueTask.CompletedTask;
        }

        private ExternalVideoFrame CreateFrame(int width, int height, uint frameIndex, bool hdr, bool alpha)
        {
            int pixels = checked(width * height);
            var frame = new ExternalVideoFrame { Width = width, Height = height, BitsPerChannel = options.Bits,
                Red = new byte[pixels * options.Bits / 8], Green = new byte[pixels * options.Bits / 8], Blue = new byte[pixels * options.Bits / 8] };
            for (var i = 0; i < pixels; i++)
            {
                int offset = i * options.Bits / 8;
                int value = (i + (int)frameIndex * 7) % 256;
                if (options.Bits == 8)
                {
                    frame.Red[offset] = (byte)value;
                    frame.Green[offset] = (byte)((value + 85) % 256);
                    frame.Blue[offset] = (byte)((value + 170) % 256);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(frame.Red.AsSpan(offset), (ushort)(value * 257));
                    BinaryPrimitives.WriteUInt16LittleEndian(frame.Green.AsSpan(offset), (ushort)(((value + 85) % 256) * 257));
                    BinaryPrimitives.WriteUInt16LittleEndian(frame.Blue.AsSpan(offset), (ushort)(((value + 170) % 256) * 257));
                }
            }
            if (alpha) frame.Alpha = CreateFloatPlane(pixels, 1f);
            if (hdr) { frame.Brightness = CreateFloatPlane(pixels, 1f); frame.MaximumBrightness = 1000; }
            return frame;
        }

        private static byte[] CreateFloatPlane(int length, float value)
        {
            var result = new byte[length * sizeof(float)];
            for (var i = 0; i < length; i++) BitConverter.TryWriteBytes(result.AsSpan(i * sizeof(float)), value);
            return result;
        }
    }
}
