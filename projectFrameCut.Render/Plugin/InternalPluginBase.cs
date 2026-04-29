using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Benchmark;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Transform;

namespace projectFrameCut.Render.Plugin;


/// <summary>
/// This is the base plugin contains almost all fundamental components required by projectFrameCut.
/// </summary>
public class InternalPluginBase : IPluginBase
{
    public const string InternalPluginBaseID = "projectFrameCut.Render.Plugins.InternalPluginBase";

    public string PluginID => InternalPluginBaseID;

    public int PluginAPIVersion => IPluginBase.CurrentPluginAPIVersion;

    public int PluginAPIMinorVersion => 1;

    public string Name => "Internal fundamental plugin";

    public string Author => "hexadecimal0x12e";

    public string Description => "Plugin that provide fundamental components for projectFrameCut.";

    public Version Version => Assembly.GetExecutingAssembly().GetName().Version ?? new(1, 0, 0, 0);

    public string AuthorUrl => "https://hexadecimal0x12e.com";

    public string? PublishingUrl => null;

    public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string>
    {
        { "IsFFmpegLibraryProvider","false" },
        { "IsInternalPlugin","true" }
    };

    public Dictionary<string, Dictionary<string, string>> LocalizationProvider => new Dictionary<string, Dictionary<string, string>>
    {

    };

    public Dictionary<string, Func<IEffect>> EffectProvider => new Dictionary<string, Func<IEffect>>
    {
        {"RemoveColor",  new(() => new RemoveColorEffect_HwAccel())},
        {"Place",  new(() => new PlaceEffect_IPicture())},
        {"Crop",  new(() => new CropEffect_ImageSharp())},
        {"Resize",  new(() => new ResizeEffect_ImageSharp())},
        {"Blur",  new(() => new BlurEffect_ImageSharp())},
        {"Rotation",  new(() => new RotationEffect_ImageSharp())},
        {"ClassicSpeedVarianceProvider", new(() => new RenderAPIBase.EffectAndMixture.ClassicSpeedVarianceProvider()) },
        {"ColorAdjustment", new(() => new ColorAdjustmentEffect_ImageSharp()) }
    };

    public Dictionary<string, IEffectFactory> EffectFactoryProvider => new Dictionary<string, IEffectFactory>
    {
        {"Place", new PlaceEffectFactory()},
        {"Crop", new CropEffectFactory()},
        {"Resize", new ResizeEffectFactory()},
        {"RemoveColor", new RemoveColorEffectFactory()},
        {"Blur", new BlurEffectFactory()},
        {"Rotation", new RotationEffectFactory()},
        {"ClassicSpeedVarianceProvider", new ClassicSpeedVarianceProviderFactory()},
        {"ColorAdjustment", new ColorAdjustmentEffectFactory()},
        {"Jitter", new JitterContinuousEffectFactory()},

    };

    public Dictionary<string, Func<IComputer>> ComputerProvider => new Dictionary<string, Func<IComputer>>
    {

    };

    public Dictionary<string, Func<IEffect>> ContinuousEffectProvider => new Dictionary<string, Func<IEffect>>
    {
        {"ZoomIn", new(() => new ZoomInContinuousEffect())  },
        {"Jitter", new(() => new JitterEffect()) }
    };

    public Dictionary<string, IEffectFactory> ContinuousEffectFactoryProvider => new Dictionary<string, IEffectFactory>
    {
        {"ZoomIn", new ZoomInContinuousEffectFactory()},
    };

    public Dictionary<string, Func<IEffect>> BindableArgumentEffectProvider => new Dictionary<string, Func<IEffect>>
    {
        { "SubjectMattingMaskGenerator", () => new SubjectMattingMaskGenerator() },
        { "MaskApplier", () => new MaskApplier() },
        { "StraightLineMovementValueProducer",() => new StraightLineMovementValueProducer() },
        { "PointPlacer",() => new PointPlacer() },

    };

    public Dictionary<string, IEffectFactory> BindableArgumentEffectFactoryProvider => new Dictionary<string, IEffectFactory>
    {
        { "SubjectMattingMaskGenerator", new SubjectMattingMaskGeneratorFactory() },
        { "MaskApplier", new MaskApplierFactory() },
        { "StraightLineMovementValueProducer",new StraightLineMovementValueProducerFactory() },
        { "PointPlacer", new PointPlacerFactory() },

    };



    public Dictionary<string, Func<string, IVideoSource>> VideoSourceProvider =>
        (HWAccelOptionGetter() ? new List<KeyValuePair<string, Func<string, IVideoSource>>>([new("DecoderContextHW", new((p) => new DecoderContextHW(p)))])
            : new List<KeyValuePair<string, Func<string, IVideoSource>>>([]))
        .Append(new KeyValuePair<string, Func<string, IVideoSource>>("DecoderContext8Bit", new((p) => new DecoderContext8Bit(p))))
        .Append(new KeyValuePair<string, Func<string, IVideoSource>>("DecoderContext16Bit", new((p) => new DecoderContext16Bit(p))))
        .Append(new KeyValuePair<string, Func<string, IVideoSource>>("HDRDecoderContext", new((p) => new HDRDecoderContext(p))))
        .Append(new KeyValuePair<string, Func<string, IVideoSource>>("HttpDecoderContext", new((p) => new HttpDecoderContext(p))))
        .Append(new KeyValuePair<string, Func<string, IVideoSource>>("FFmpegDeviceDecoderContext", new((p) => new FFmpegDeviceDecoderContext(p))))
        .Append(new KeyValuePair<string, Func<string, IVideoSource>>("RPSVDecoderContext", new((p) => new RawPictureSequenceStreamVideoDecoderContext(p))))
        .ToDictionary();



    public Dictionary<string, string> Configuration { get => new(); set { } }

    public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString => new Dictionary<string, Dictionary<string, string>> { };

    public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider => new Dictionary<string, Func<string, string, ISoundTrack>>
    {
        {"NormalTrack", new((i,n) => new NormalSoundTrack{Id = i, Name = n, Ratio = 1f, Volume = 1f}) }
    };

    public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider => new Dictionary<string, Func<string, IAudioSource>>
    {
        {"AudioDecoder", (s) => new Float32bitAudioDecoder(s) }
    };

    public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider => new Dictionary<string, Func<string, IVideoWriter>>
    {
        {"VideoWriter", new((_) => new VideoWriter()) },
        {"HDRVideoWriter", new((_) => new HDRVideoWriter()) },
        {"HDRWriter", new((_) => new HDRVideoWriter()) },
        {"BlackHoleWriter", new((_) => new BlackholeVideoWriter()) }
    };

    public Dictionary<string, Func<Guid, Guid, ITransform>> TransformProvider => new Dictionary<string, Func<Guid, Guid, ITransform>>
    {
        {
            "Crossfade",
            (prevId, nextId) => new CrossfadeTransform { PreviousClipId = prevId, NextClipId = nextId }
        }
    };

    IClip IPluginBase.ClipCreator(JsonElement element)
    {
        ClipMode type = (ClipMode)element.GetProperty("ClipType").GetInt32();
        Logger.Log($"Found clip {type}, name: {element.GetProperty("Name").GetString()}, id: {element.GetProperty("Id").GetString()}");
        return type switch
        {
            ClipMode.VideoClip => element.Deserialize<VideoClip>() ?? throw new NullReferenceException(),
            ClipMode.PhotoClip => element.Deserialize<PhotoClip>() ?? throw new NullReferenceException(),
            ClipMode.SolidColorClip => element.Deserialize<SolidColorClip>() ?? throw new NullReferenceException(),
            ClipMode.TextClip => element.Deserialize<TextClip>() ?? throw new NullReferenceException(),
            ClipMode.AudioClip => element.Deserialize<SoundTrackToClipWrapper>() ?? throw new NullReferenceException(),
            ClipMode.MarkingClip => element.Deserialize<MarkingClip>() ?? throw new NullReferenceException(),
            ClipMode.TransformClip => element.Deserialize<TransformContainer>() ?? throw new NullReferenceException(),
            _ => throw new NotSupportedException($"Unknown or unsupported clip type {type}."),
        };
    }

    ISoundTrack IPluginBase.SoundTrackCreator(JsonElement element)
    {
        TrackMode type = (TrackMode)element.GetProperty("TrackType").GetInt32();
        Logger.Log($"Found sound track {type}, name: {element.GetProperty("Name").GetString()}, id: {element.GetProperty("Id").GetString()}");
        return type switch
        {
            TrackMode.NormalTrack => element.Deserialize<NormalSoundTrack>() ?? throw new NullReferenceException(),
            _ => throw new NotSupportedException($"Unknown or unsupported sound track type {type}."),
        };
    }

    ITransform IPluginBase.TransformCreator(JsonElement element)
    {
        var typeName = element.GetProperty("TypeName").GetString();
        return typeName switch
        {
            "Crossfade" => element.Deserialize<CrossfadeTransform>() ?? throw new NullReferenceException("Failed to deserialize CrossfadeTransform."),
            "ExternalSourceTransform" => element.Deserialize<ExternalSourceTransform>() ?? throw new NullReferenceException("Failed to deserialize ExternalSourceTransform."),
            _ => throw new NotSupportedException($"Unknown or unsupported transform type '{typeName}'.")
        };
    }

    string? IPluginBase.ReadLocalizationItem(string key, string locate)
    {
        var loc = ISimpleLocalizerBase_PropertyPanel.GetMapping().FirstOrDefault(x => x.Key == locate, ISimpleLocalizerBase_PropertyPanel.GetMapping().First()).Value;
        if (!loc.IsItemExist(key)) return null;
        return loc.DynamicLookup(key, key);
    }

    bool IPluginBase.OnLoaded(out string FailedReason)
    {
        try
        {
            TextClip.GetFont(); //build font cache
        }
        catch (Exception ex)
        {
            Log(ex, "Init Fone cache", this);
        }
        FailedReason = "";
        return true;
    }


    public static Func<bool> HWAccelOptionGetter = new(() => ((GlobalPluginHelper.MessagingService?.Call("projectFrameCut.Program", "GetSetting", ["codec_PreferredHWAccel"]) ?? "true") is string hwaccel && bool.TryParse(hwaccel, out var result) && result));

}

