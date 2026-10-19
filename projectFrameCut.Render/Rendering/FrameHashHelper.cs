using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
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
    public FrameHashMode Mode { get; init; } = FrameHashMode.Dynamic;
    public int Width { get; init; }
    public int Height { get; init; }
    public int ProjectWidth { get; init; }
    public int ProjectHeight { get; init; }
    public double FrameRate { get; init; } = 30;
    public int PixelMode { get; init; } = 8;
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
    private sealed class RenderRequiredException : Exception { }
    private static readonly JsonSerializerOptions jsonOptions = CreateJsonOptions(true);
    private static readonly JsonSerializerOptions configurationOptions = CreateJsonOptions(false);
    private static readonly HashSet<string> runtimeProperties = new(StringComparer.Ordinal)
    {
        "EffectsInstances", "EffectProvidersInstances", "SpeedVarianceProviderInstance", "MixtureInstance",
        "AlternativeSource", "ArgumentFields", "Decoder", "VirtualSource", "HasCurrentDecoder",
        "NodeEffects", "ParametersNeeded", "ParametersType", "BindedEffectProvidingSystemID",
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

    public static FrameHashResult GetClipHash(IReadOnlyList<IClip> clips, IClip clip, uint frame,
        FrameHashOptions? options = null, Func<object?>? render = null, CancellationToken token = default)
    {
        options ??= new();
        token.ThrowIfCancellationRequested();
        bool rendering = false;
        try
        {
            var dependencies = Timeline.CollectHashDependencies(clips.ToArray(), clip, new HashSet<Guid> { clip.Id });
            if (ClipInitializationFailure.IsMarked(clip) || dependencies.Any(ClipInitializationFailure.IsMarked))
                return FrameHashResult.Uncacheable();
            var needsRender = NeedsRendering(clip) || dependencies.Any(NeedsRendering);
            if (needsRender && options.Mode == FrameHashMode.Static) return FrameHashResult.Uncacheable();
            object? configuration = null;
            try
            {
                configuration = new
                {
                    Clip = DescribeClip(clip),
                    Dependencies = dependencies.Select(DescribeClip).ToArray(),
                };
                // Materialize now so an opaque value cannot silently become a stable placeholder.
                configuration = JsonSerializer.SerializeToElement(configuration, configurationOptions);
            }
            catch (RenderRequiredException) { configuration = null; needsRender = true; }
            catch (JsonException) { configuration = null; needsRender = true; }
            catch (NotSupportedException) { configuration = null; needsRender = true; }
            if (needsRender && (options.Mode == FrameHashMode.Static || render is null))
                return FrameHashResult.Uncacheable();
            rendering = needsRender;
            var output = needsRender ? render!() : null;
            rendering = false;
            token.ThrowIfCancellationRequested();
            if (ClipInitializationFailure.IsMarked(clip) || dependencies.Any(ClipInitializationFailure.IsMarked))
                return FrameHashResult.Uncacheable();
            return new(HashValue(new { Schema = "frame-content", Frame = clip is IImmutableContentClip ? 0u : frame,
                Options = options, Configuration = configuration, Output = output }), true);
        }
        catch (OperationCanceledException) { throw; }
        catch (RenderRequiredException) when (!rendering) { return FrameHashResult.Uncacheable(); }
        catch (Exception ex)
        {
            if (rendering) throw;
            Logger.Log(ex, $"Hash clip {clip.Id}, frame {frame}", nameof(FrameHashHelper));
            return FrameHashResult.Uncacheable();
        }
    }

    public static FrameHashResult GetFrameHash(IReadOnlyList<IClip> clips, uint frame,
        FrameHashOptions? options = null, Func<object?>? render = null, CancellationToken token = default)
    {
        options ??= new() { Stage = "timeline" };
        token.ThrowIfCancellationRequested();
        // Determining speed-adjusted membership can itself execute an effect.
        bool runtimeTiming = clips.Any(c => c.SpeedVarianceProviderInstance is not null);
        if (runtimeTiming && (options.Mode == FrameHashMode.Static || render is null)) return FrameHashResult.Uncacheable();
        var active = clips.Where(c => runtimeTiming || c.ExtendToWholeDraft
            || frame >= c.StartFrame && (ulong)frame < (ulong)c.StartFrame + c.Duration)
            .OrderBy(c => c, ClipLayerOrder.BottomToTop).ToArray();
        FrameHashResult[] hashes = runtimeTiming ? [] : active.Select(c => GetClipHash(clips, c, frame, options, token: token)).ToArray();
        if (!runtimeTiming && hashes.All(h => h.CanReuse))
            return new(HashValue(new { Schema = "timeline-content", Frame = frame, Options = options, Clips = hashes.Select(h => h.Hash).ToArray() }), true);
        if (options.Mode == FrameHashMode.Static || render is null) return FrameHashResult.Uncacheable();
        var output = render();
        token.ThrowIfCancellationRequested();
        if (active.Any(ClipInitializationFailure.IsMarked)) return FrameHashResult.Uncacheable();
        return new(HashValue(new { Schema = "timeline-content", Frame = frame, Options = options, Output = output }), true);
    }

    public static bool NeedsRendering(IClip clip)
    {
        if (clip is MaskClip or VirtualSourceVideoClip || clip is VideoClip { Decoder: EncodeAndDecode.DecoderContextPJFCProject }
            || clip.FromPlugin != Plugin.InternalPluginBase.InternalPluginBaseID || !Timeline.CanCacheClipSource(clip)) return true;
        var providers = clip.EffectProvidersInstances;
        if (clip.EffectProviders is { Length: > 0 } && providers is not { Length: > 0 }) return true;
        if (clip.Effects is { Length: > 0 } && clip.EffectsInstances is not { Length: > 0 }) return true;
        foreach (var provider in providers ?? [])
        {
            if (!provider.Enabled) continue;
            if (!provider.IsDeterministic || provider is IClipValueEndpointProvider
                || provider.TypeOfEffect == EffectType.Transform) return true;
            if (provider.Fields.Values.OfType<IEffect>().Any(e => !e.IsDeterministic)) return true;
            if (DynamicEffectBindings.InputFields(provider).Any(p => p.Value.FieldType.IsContent()
                && (p.Key != EffectProviderAnchorExtensions.InputKey
                    || provider.TypeOfEffect == EffectType.NonIPictureOutputValueProvider))) return true;
            if (DynamicEffectBindings.InputFields(provider).Values.Any(f => f.FieldType.HasFlag(EffectArgumentFieldType.Any)))
                return true;
        }
        return clip.SpeedVarianceProviderInstance is { IsDeterministic: false }
            || clip.MixtureInstance is { IsDeterministic: false } || clip.AlternativeSource is { IsDeterministic: false }
            || (clip.EffectsInstances ?? []).Any(e => e is { Enabled: true, IsDeterministic: false });
    }

    private static object DescribeClip(IClip clip) => new
    {
        State = ReadProperties(clip),
        Module = clip.GetType().Module.ModuleVersionId,
        Font = clip is TextClip text ? SourceFingerprint(text.FontPath) : null,
        Source = SourceFingerprint(clip.FilePath),
        Arguments = clip.ArgumentFields.ToDictionary(p => p.Key, p => p.Value.ReadValue(clip)),
        Effects = (clip.EffectsInstances ?? []).OrderBy(e => e.Index).Select(e => DescribeEffect(e,
            clip.EffectProvidersInstances?.Any(p => p.Id.ToString() == e.BindedEffectProvidingSystemID) == true)).ToArray(),
        Providers = (clip.EffectProvidersInstances ?? []).OrderBy(p => p.Id).Select(DescribeProvider).ToArray(),
        Replacement = clip.AlternativeSource is { } source ? DescribeEffect(source, false) : null,
        Mixture = clip.MixtureInstance is { } mixture ? DescribeEffect(mixture, false) : null,
        Speed = clip.SpeedVarianceProviderInstance is { } speed ? DescribeEffect(speed, false) : null,
        Rotation = VideoClipRotation.GetAngle(clip),
        ForcedImplementation = EffectHelper.ForcePreferToType,
        DefaultImplementations = EffectHelper.DefaultImplementsType,
        PreferredPlugins = RenderAPIBase.Plugins.IPluginBase.EffectImplementations.PreferredPlugins
            .OrderBy(p => p.Key.TypeName, StringComparer.Ordinal).ThenBy(p => p.Key.ImplementType).Select(p => new { p.Key, p.Value }).ToArray(),
        ApproximateMixture = Compose.ClassicOverlayMixture.EnableApproximatePath,
    };

    internal static object DescribeProvider(IEffectProvider provider) => new
    {
        provider.Id, provider.TypeName, provider.FromPlugin, provider.Enabled, provider.IsDeterministic,
        Module = provider.GetType().Module.ModuleVersionId,
        provider.TypeOfEffect, provider.Target, provider.AnchorsBindingState, provider.MetaData,
        Inputs = provider.InFields,
        Outputs = provider.GetOutputFields(),
        Fields = provider.Fields.ToDictionary(p => p.Key, p => DescribeField(p.Value)),
    };

    private static object DescribeField(IEffectArgumentField field) => new
    {
        field.TypeName, field.FromPlugin, field.FieldType, field.IsDynamic, field.IsDynamicAtRenderTime,
        Binding = (field as DynamicEffectParamField)?.BoundProviderId,
        Value = field switch
        {
            DynamicEffectParamField f => f.StaticFallbackValue,
            EffectArgumentFieldDescriptor => field.DefaultValue,
            IEffect effect => DescribeEffect(effect, true),
            _ when !field.IsDynamic && !field.IsDynamicAtRenderTime => field.GetGetter()(),
            _ => throw new RenderRequiredException(),
        },
    };

    internal static object DescribeEffect(IEffect effect, bool bound = false)
    {
        var state = ReadProperties(effect);
        state["TypeName"] = effect.TypeName;
        state["FromPlugin"] = effect.FromPlugin;
        state["TypeOfEffect"] = effect.TypeOfEffect;
        state["ImplementType"] = effect.ImplementType;
        state["IsDeterministic"] = effect.IsDeterministic;
        state["Module"] = effect.GetType().Module.ModuleVersionId;
        state.Remove(nameof(IEffect.Id));
        state.Remove(nameof(IEffect.Parameters));
        state["Parameters"] = effect.Parameters.ToDictionary(p => p.Key, p => IsRuntimeValue(p.Value)
            ? bound ? (object)new { BoundParameter = p.Key } : throw new RenderRequiredException() : p.Value);
        return state;
    }

    private static Dictionary<string, object?> ReadProperties(object value) => value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !runtimeProperties.Contains(p.Name)
            && p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
        .GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().GetValue(value));

    internal static object? SourceFingerprint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
            return new { Path = path, Revision = ProjectExternalSourceRuntime.Current.CacheToken,
                Video = ExternalVideoSourceRegistry.CacheToken, Mask = ExternalMaskSourceRegistry.CacheToken };
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
        if (picture is IHwAccelPicture) throw new RenderRequiredException();
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
        if (picture is IHDRPicture<ushort> hdr)
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
            if (!allowPictures) throw new RenderRequiredException();
            writer.WriteStringValue(HashPicture(value));
        }
    }

    private sealed class ValueConverter(bool allowPictures) : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => JsonSerializer.Deserialize<JsonElement>(ref reader, options);

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (IsRuntimeValue(value)) throw new RenderRequiredException();
            if (value is IPicture picture)
            {
                if (!allowPictures) throw new RenderRequiredException();
                writer.WriteStringValue(HashPicture(picture));
            }
            else if (value.GetType() == typeof(object)) throw new RenderRequiredException();
            else JsonSerializer.Serialize(writer, value, value.GetType(), options);
        }
    }
}
