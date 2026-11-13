#if !HEADLESS
using System.Globalization;

namespace projectFrameCut.Services;

internal sealed class GuiRenderOptions
{
    public string? OutputPath { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? FrameRate { get; init; }
    public int? BitDepth { get; init; }
    public string? Encoder { get; init; }
    public string? BitRateMbps { get; init; }
    public int? MaxParallelThreads { get; init; }
    public bool? IncrementalRendering { get; init; }
    public bool Overwrite { get; init; }
    public bool HasOverrides => OutputPath is not null || Width.HasValue || Height.HasValue
        || FrameRate.HasValue || BitDepth.HasValue || Encoder is not null || BitRateMbps is not null
        || MaxParallelThreads.HasValue || IncrementalRendering.HasValue;

    public static GuiRenderOptions Parse(string[] args)
    {
        string? Read(string name)
        {
            if (args.Contains("--" + name, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Use --{name}=<value>.");
            var arg = args.LastOrDefault(c => c.StartsWith("--" + name + "=", StringComparison.OrdinalIgnoreCase));
            if (arg is null) return null;
            var value = arg[(arg.IndexOf('=') + 1)..];
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"--{name} requires a value.");
            return value;
        }

        int? ReadInt(string name, int min = 1, int max = int.MaxValue)
        {
            var value = Read(name);
            if (value is null) return null;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number < min || number > max)
                throw new ArgumentException($"--{name} must be an integer between {min} and {max}.");
            return number;
        }

        bool? ReadBool(string name)
        {
            var value = Read(name);
            if (value is null) return null;
            if (!bool.TryParse(value, out var result)) throw new ArgumentException($"--{name} must be true or false.");
            return result;
        }

        var bitDepth = ReadInt("bitDepth");
        if (bitDepth.HasValue && bitDepth is not (8 or 10 or 12))
            throw new ArgumentException("--bitDepth must be 8, 10, or 12.");
        var bitRate = Read("bitRate")?.Trim();
        if (bitRate is not null && !bitRate.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && (!decimal.TryParse(bitRate, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mbps)
                || mbps < 0.000001m || mbps > long.MaxValue / 1_000_000m))
            throw new ArgumentException("--bitRate must be auto or a positive bitrate in Mbps.");

        var output = Read("output");
        if (output is not null)
        {
            // HomePage and project loading can change the process working directory.
            output = Path.GetFullPath(output, MauiProgram.LaunchWorkingDirectory);
            if (Directory.Exists(output) || string.IsNullOrWhiteSpace(Path.GetExtension(output)))
                throw new ArgumentException("--output must specify a video file path including its extension.");
        }

        return new GuiRenderOptions
        {
            OutputPath = output,
            Width = ReadInt("width"),
            Height = ReadInt("height"),
            FrameRate = ReadInt("fps"),
            BitDepth = bitDepth,
            Encoder = Read("encoder")?.Trim().ToLowerInvariant(),
            BitRateMbps = bitRate,
            MaxParallelThreads = ReadInt("maxParallelThreads", 1, 64),
            IncrementalRendering = ReadBool("incrementalRendering"),
            Overwrite = ReadBool("overwrite") ?? false
        };
    }
}
#endif
