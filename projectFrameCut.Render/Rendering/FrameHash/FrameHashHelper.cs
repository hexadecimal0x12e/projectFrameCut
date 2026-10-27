using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RPCProtocol;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.Rendering;

public readonly record struct FrameHashResult(string Hash, bool CanReuse)
{
    public static FrameHashResult Uncacheable() => new("fh-" + Guid.NewGuid().ToString("N"), false);
}

public sealed record FrameHashOptions
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int ProjectWidth { get; init; }
    public int ProjectHeight { get; init; }
    public double FrameRate { get; init; } = 30;
    public int PixelMode { get; init; } = IPicture.PicturePixelMode.Float16Picture;
    public bool Hdr { get; init; }
    public int SdrBrightness { get; init; } = 203;
    public int PeakBrightness { get; init; } = 1000;
    public string Stage { get; init; } = "clip";
    public bool ProcessFromCanvas { get; init; } = true;
    public bool AutoCenter { get; init; }
}

/// <summary>Builds cache identities without treating runtime delegates as constant inputs.</summary>
public static class FrameHashHelper
{
    internal sealed class UnhashableInputException : Exception { }
    private static readonly JsonSerializerOptions jsonOptions = CreateJsonOptions(true);
    private static readonly JsonSerializerOptions configurationOptions = CreateJsonOptions(false);
    private static readonly HashSet<string> runtimeProperties = new(StringComparer.Ordinal)
    {
        "EffectsInstances", "EffectProvidersInstances", "SpeedVarianceProviderInstance", "MixtureInstance",
        "AlternativeSource", "ArgumentFields", "Decoder", "VirtualSource", "HasCurrentDecoder",
        "NodeEffects", "ParametersNeeded", "ParametersType", "BindedEffectProvidingSystemID",
        "EffectStagePlan", "AudioSource", "SourceArguments",
    };

    public static string CreateRenderResumeSignature(ProjectJSONStructure project, DraftStructureJSON timeline, IEnumerable<AssetItem> assets)
        => HashValue(new
        {
            Project = new
            {
                project.ProjectUniqueId, project.ProjectName, project.RelativeWidth, project.RelativeHeight,
                project.TargetFrameRate, project.Properties, project.UserDefinedProperties,
                Plugins = project.ProjectPlugins.Select(p => new { Reference = p, Source = SourceFingerprint(p.PackagePath) }).ToArray(),
                Cover = SourceFingerprint(project.ThumbPath),
            },
            Timeline = new { timeline.Clips, timeline.SoundTracks, timeline.Duration, timeline.AudioDuration },
            Assets = assets.OrderBy(a => a.AssetId, StringComparer.Ordinal)
                .Select(a => new { Asset = a, Source = SourceFingerprint(a.Path) }).ToArray(),
        });

    public static bool CanPersistClipHash(IReadOnlyList<IClip> clips, IClip clip,
        FrameHashOptions? options = null, CancellationToken token = default)
        => DescribeClip(clips, clip, false, token).HasValue;

    public static FrameHashResult GetClipHash(IReadOnlyList<IClip> clips, IClip clip, uint frame,
        FrameHashOptions? options = null, CancellationToken token = default)
        => GetClipHash(clips, clip, frame, options ?? new(), false, token);

    internal static FrameHashResult GetClipOutputHash(IReadOnlyList<IClip> clips, IClip clip, uint frame,
        FrameHashOptions options, CancellationToken token = default)
        => GetClipHash(clips, clip, frame, options, true, token);

    private static FrameHashResult GetClipHash(IReadOnlyList<IClip> clips, IClip clip, uint frame,
        FrameHashOptions options, bool layout, CancellationToken token)
    {
        var configuration = DescribeClip(clips, clip, layout, token);
        if (!configuration.HasValue) return FrameHashResult.Uncacheable();
        return new(HashValue(new
        {
            Schema = layout ? "timeline-content-v2" : "clip-content-v2",
            Frame = IsFrameInvariant(clip, layout) ? 0u : frame,
            Options = options, Configuration = configuration.Value,
        }), true);
    }

    private static JsonElement? DescribeClip(IReadOnlyList<IClip> clips, IClip clip, bool layout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var state = new FrameHashAnalysis(clips, token).DescribeClip(clip, layout);
            var configuration = JsonSerializer.SerializeToElement(layout ? new[] { state } : state, configurationOptions);
            token.ThrowIfCancellationRequested();
            return configuration;
        }
        catch (OperationCanceledException) { throw; }
        catch (UnhashableInputException) { return null; }
        catch (Exception ex)
        {
            Logger.Log(ex, $"Describe clip {clip.Id} for hashing", nameof(FrameHashHelper));
            return null;
        }
    }

    public static FrameHashResult GetFrameHash(IReadOnlyList<IClip> clips, uint frame,
        FrameHashOptions? options = null, CancellationToken token = default)
    {
        options ??= new() { Stage = "timeline" };
        token.ThrowIfCancellationRequested();
        try
        {
            var analysis = new FrameHashAnalysis(clips, token);
            // Speed maps can execute effects. Include all clips when membership is not statically known.
            bool runtimeTiming = clips.Any(c => c.SpeedVarianceProviderInstance is not null);
            var active = clips.Where(c => runtimeTiming || c.ExtendToWholeDraft
                || frame >= c.StartFrame && (ulong)frame < (ulong)c.StartFrame + c.Duration)
                .OrderBy(c => c, ClipLayerOrder.BottomToTop);
            var configuration = JsonSerializer.SerializeToElement(active.Select(c => analysis.DescribeClip(c, true)).ToArray(), configurationOptions);
            token.ThrowIfCancellationRequested();
            return new(HashValue(new { Schema = "timeline-content-v2", Frame = frame,
                Options = options, Configuration = configuration }), true);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnhashableInputException) { return FrameHashResult.Uncacheable(); }
        catch (Exception ex)
        {
            Logger.Log(ex, $"Hash timeline frame {frame}", nameof(FrameHashHelper));
            return FrameHashResult.Uncacheable();
        }
    }

    internal static FrameHashResult GetEffectHash(IEffect effect, EffectExecutionContext context,
        IEffectProvider? provider, int effectIndex, FrameHashOptions options, CancellationToken token)
    {
        if (context.Clip is not { } clip || !effect.IsDeterministic) return FrameHashResult.Uncacheable();
        try
        {
            var clips = FrameHashContext.Current?.Clips ?? [clip];
            var analysis = new FrameHashAnalysis(clips, token);
            var configuration = provider is null ? analysis.DescribeClip(clip, true)
                : analysis.DescribeProvider(clip, provider, true);
            return new(HashValue(new
            {
                Schema = "effect-input-v2", Options = options, Index = effectIndex,
                Clip = new { clip.PositionTuple, clip.LayerIndex, clip.SubLayerIndex },
                Configuration = JsonSerializer.SerializeToElement(configuration, configurationOptions),
                Effect = JsonSerializer.SerializeToElement(DescribeEffect(effect, provider is not null), configurationOptions),
                Parameters = JsonSerializer.SerializeToElement(context.Parameters.Where(p => !FrameHashAnalysis.IsMutableInput(p.Value))
                    .ToDictionary(p => p.Key, p => p.Value), configurationOptions),
                context.FrameIndex, context.Progress, context.ClipProgress,
                context.TargetWidth, context.TargetHeight, context.RelativeWidth, context.RelativeHeight,
            }), true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return FrameHashResult.Uncacheable(); }
    }

    internal static FrameHashResult GetAudioHash(Compose.AudioSampleSource source, CancellationToken token)
    {
        try
        {
            return new(HashValue(JsonSerializer.SerializeToElement(source.DescribeHashInputs(token), configurationOptions)), true);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnhashableInputException) { return FrameHashResult.Uncacheable(); }
        catch (Exception ex)
        {
            Logger.Log(ex, "Describe mixed audio for hashing", nameof(FrameHashHelper));
            return FrameHashResult.Uncacheable();
        }
    }

    internal static object DescribeEffect(IEffect effect, bool bound = false)
    {
        var state = ReadProperties(effect, effect.Parameters.Keys.Prepend(nameof(IEffect.Parameters)).ToArray());
        state["TypeName"] = effect.TypeName;
        state["FromPlugin"] = effect.FromPlugin;
        state["TypeOfEffect"] = effect.TypeOfEffect;
        state["ImplementType"] = effect.ImplementType;
        state["IsDeterministic"] = effect.IsDeterministic;
        state["Module"] = effect.GetType().Module.ModuleVersionId;
        state.Remove(nameof(IEffect.Id));
        state.Remove(nameof(IEffect.Parameters));
        state.Remove(nameof(IEffect.Index));
        state["Parameters"] = effect.Parameters.ToDictionary(p => p.Key, p => FrameHashAnalysis.IsMutableInput(p.Value)
            ? bound ? (object)new { BoundParameter = p.Key } : throw new UnhashableInputException() : p.Value);
        return state;
    }

    internal static bool IsFrameInvariant(IClip clip, bool layout = false) => clip is IImmutableContentClip
        && clip.AlternativeSource is null && clip.SpeedVarianceProviderInstance is null && clip.MixtureInstance is null
        && !(clip.EffectProvidersInstances ?? []).Any(p => p.Enabled && (layout
            || p.TypeOfEffect is not (Shared.EffectType.ClipPositionProvider or Shared.EffectType.ContinuousClipPositionProvider)))
        && !(clip.EffectsInstances ?? []).Any(e => e.Enabled && (layout
            || e is not (IClipPositionProvider or IContinuousClipPositionProvider)));

    internal static string GetPreviewSourceHash(IClip clip, uint sourceFrame, int width, int height, int pixelMode)
    {
        object state;
        if (clip is VideoClip video)
            state = new { video.StartingX, video.StartingY, video.TargetDecoder, video.HDRBrightnessOffset,
                Decoder = video.Decoder?.GetType().AssemblyQualifiedName,
                DecoderModule = video.Decoder?.GetType().Module.ModuleVersionId,
                Crop = clip.ExtraData?.Where(p => p.Key.StartsWith("__Internal_DirectCrop", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value) };
        else if (clip is PhotoClip photo)
            state = new { photo.StartingX, photo.StartingY,
                Crop = clip.ExtraData?.Where(p => p.Key.StartsWith("__Internal_DirectCrop", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value) };
        else
        {
            var properties = ReadProperties(clip);
            foreach (string name in new[] { "Effects", "EffectProviders", "EffectStagePlan", "Rotation", "TargetX", "TargetY" })
                properties.Remove(name);
            state = properties;
        }
        return HashValue(JsonSerializer.SerializeToElement(new { Schema = "preview-source-v2", Module = clip.GetType().Module.ModuleVersionId,
            DrawingModule = typeof(IPicture).Module.ModuleVersionId,
            Source = SourceFingerprint(clip.FilePath), Font = clip is TextClip text ? SourceFingerprint(text.FontPath) : null,
            Frame = clip is IImmutableContentClip ? 0u : sourceFrame,
            width, height, pixelMode, State = state,
            EffectHelper.ForcePreferToType, EffectHelper.DefaultImplementsType,
            PreferredPlugins = RenderAPIBase.Plugins.IPluginBase.EffectImplementations.PreferredPlugins
                .OrderBy(p => p.Key.TypeName, StringComparer.Ordinal).ThenBy(p => p.Key.ImplementType).Select(p => new { p.Key, p.Value }).ToArray(),
            Rasterizer = RenderAPIBase.ClipAndTrack.IVectorContentClip.GlobalDefaultRasterizer.GetType().AssemblyQualifiedName,
            AntiAlias = RenderAPIBase.ClipAndTrack.IVectorContentClip.GlobalDefaultAntiAliasMode }, configurationOptions));
    }

    internal static Dictionary<string, object?> ReadProperties(object value, params string[] excluded) => value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !runtimeProperties.Contains(p.Name) && !excluded.Contains(p.Name)
            && p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
        .GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().GetValue(value));

    internal static object? SourceFingerprint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.StartsWith('#') || Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
            return new { Path = path, Revision = ProjectExternalSourceRuntime.Current.CacheToken,
                Video = ExternalVideoSourceRegistry.CacheToken, Audio = ExternalAudioSourceRegistry.CacheToken, Mask = ExternalMaskSourceRegistry.CacheToken };
        var file = new FileInfo(path);
        return new { Path = Path.GetFullPath(path), Exists = file.Exists, Length = file.Exists ? file.Length : 0,
            Modified = file.Exists ? file.LastWriteTimeUtc.Ticks : 0 };
    }

    internal static bool IsRuntimeValue(object? value) => value is Delegate || value is not null && DynamicParam.IsDynamicValue(value);

    public static string HashValue(object? value)
    {
        var element = JsonSerializer.SerializeToElement(value, jsonOptions);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, element);
        return "fh-" + Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(p.Name);
                WriteCanonical(writer, p.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    public static string HashPicture(IPicture picture)
    {
        if (picture is IHwAccelPicture) throw new UnhashableInputException();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{picture.Width}/{picture.Height}/{picture.BitPerPixel.Value}/{picture.HasAlphaChannel}/"));
        void Append(Array? channel)
        {
            if (channel is null) { hash.AppendData([0]); return; }
            hash.AppendData(Encoding.UTF8.GetBytes($"{channel.GetType().Name}/{channel.Length}/"));
            switch (channel)
            {
                case bool[] b: hash.AppendData(b.Select(v => v ? (byte)1 : (byte)0).ToArray()); break;
                case byte[] b: hash.AppendData(b); break;
                case ushort[] u: hash.AppendData(MemoryMarshal.AsBytes(u.AsSpan())); break;
                case float[] f: hash.AppendData(MemoryMarshal.AsBytes(f.AsSpan())); break;
                case Half[] h: hash.AppendData(MemoryMarshal.AsBytes(h.AsSpan())); break;
                default: throw new NotSupportedException($"Unsupported picture channel {channel.GetType()}.");
            }
        }
        void AppendReadOnly<T>(ReadOnlySpan<T> values) where T : unmanaged
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{typeof(T[]).Name}/{values.Length}/"));
            hash.AppendData(MemoryMarshal.AsBytes(values));
        }
        if (picture is IReadOnlyPictureChannels<byte> read8)
        {
            AppendReadOnly(read8.ReadRed);
            AppendReadOnly(read8.ReadGreen);
            AppendReadOnly(read8.ReadBlue);
            if (picture.HasAlphaChannel) AppendReadOnly(read8.ReadAlpha);
        }
        else if (picture is IReadOnlyPictureChannels<ushort> read16)
        {
            AppendReadOnly(read16.ReadRed);
            AppendReadOnly(read16.ReadGreen);
            AppendReadOnly(read16.ReadBlue);
            if (picture.HasAlphaChannel) AppendReadOnly(read16.ReadAlpha);
        }
        else
        {
            if (picture is IMonoChromePicture<byte> or IMonoChromePicture<ushort>) Append(picture.GetSpecificChannel(IPicture.ChannelId.MonoChrome) as Array);
            else
                foreach (var id in new[] { IPicture.ChannelId.Red, IPicture.ChannelId.Green, IPicture.ChannelId.Blue })
                    Append(picture.GetSpecificChannel(id) as Array);
            if (picture.HasAlphaChannel) Append(picture.GetSpecificChannel(IPicture.ChannelId.Alpha) as Array);
        }
        if (picture is IHDRPicture<Half> hdr)
        {
            if (picture is IReadOnlyPictureChannels<ushort> read) AppendReadOnly(read.ReadBrightness);
            else Append(hdr.Brightness);
            hash.AppendData(BitConverter.GetBytes(hdr.MaximumBrightness));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static JsonSerializerOptions CreateJsonOptions(bool allowPictures)
    {
        var options = new JsonSerializerOptions { IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        options.Converters.Add(new PictureConverter(allowPictures));
        options.Converters.Add(new ValueConverter(allowPictures));
        return options;
    }

    private sealed class PictureConverter(bool allowPictures) : JsonConverter<IPicture>
    {
        public override IPicture Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, IPicture value, JsonSerializerOptions options)
        {
            if (!allowPictures) throw new UnhashableInputException();
            writer.WriteStringValue(HashPicture(value));
        }
    }

    private sealed class ValueConverter(bool allowPictures) : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => JsonSerializer.Deserialize<JsonElement>(ref reader, options);

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (!allowPictures && FrameHashAnalysis.IsMutableInput(value)) throw new UnhashableInputException();
            if (IsRuntimeValue(value)) throw new UnhashableInputException();
            if (value is IPicture picture)
            {
                if (!allowPictures) throw new UnhashableInputException();
                writer.WriteStringValue(HashPicture(picture));
            }
            else if (value is projectFrameCut.Shared.IAudioSamples<float> audio)
            {
                if (!allowPictures) throw new UnhashableInputException();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(BitConverter.GetBytes(audio.channelCount));
                hash.AppendData(BitConverter.GetBytes(audio.SampleCount));
                hash.AppendData(BitConverter.GetBytes(audio.SamplePerSecond));
                for (int c = 0; c < audio.channelCount; c++)
                    hash.AppendData(MemoryMarshal.AsBytes(audio.GetSamples(c).AsSpan(0, audio.SampleCount)));
                writer.WriteStringValue(Convert.ToHexString(hash.GetHashAndReset()));
            }
            else if (value.GetType() == typeof(object)) throw new UnhashableInputException();
            else JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }
    }
}
