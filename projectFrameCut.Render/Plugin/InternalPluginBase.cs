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
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using projectFrameCut.Render.ClipsAndTracks.Text;

namespace projectFrameCut.Render.Plugin;


/// <summary>
/// This is the base plugin contains almost all fundamental components required by projectFrameCut.
/// </summary>
public class InternalPluginBase : IPluginBase
{
    public const string InternalPluginBaseID = "projectFrameCut.Render.Plugins.InternalPluginBase";

    public string PluginID => InternalPluginBaseID;

    public int PluginAPIVersion => IPluginBase.CurrentPluginAPIVersion;

    public int PluginAPIMinorVersion => 0;

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

    public Dictionary<string, Func<IEffectProvider>> EffectProviderProvider => new Dictionary<string, Func<IEffectProvider>>
    {
        { "ZoomIn", () => new ZoomInEffectProvider() },
        { "RemoveColor", () => new RemoveColorEffectProvider() },
        { "Jitter", () => new JitterEffectProvider() },
        { "Blur", () => new BlurEffectProvider() },
        { "Crop", () => new CropEffectProvider() },
        { "Place", () => new PlaceEffectProvider() },
        { "Resize", () => new ResizeEffectProvider() },
        { "Flip", () => new FlipEffectProvider() },
        { "Sharpen", () => new SharpenEffectProvider() },
        { "Vignette", () => new VignetteEffectProvider() },
        { "FadeOpacity", () => new FadeOpacityEffectProvider() },
        { "ColorAdjustment", () => new ColorAdjustmentEffectProvider() },
        { "ClassicSpeedVarianceProvider", () => new ClassicSpeedVarianceProviderEffectProvider() },
        { "ClassicOverlayMixture", () => new ClassicOverlayMixtureProvider() },
        { "ProgressPlacer", () => new ProgressPlacerProvider() },
        { "ProgressCrop", () => new ProgressCropProvider() },
        { "TextFadeIn", () => new TextFadeInEffectProvider() },
        { "IntArithmeticAdd", () => new IntArithmeticValueProviderProvider { Operation = IntArithmeticOperation.Add } },
        { "IntArithmeticSubtract", () => new IntArithmeticValueProviderProvider { Operation = IntArithmeticOperation.Subtract } },
        { "IntArithmeticMultiply", () => new IntArithmeticValueProviderProvider { Operation = IntArithmeticOperation.Multiply } },
        { "IntArithmeticDivide", () => new IntArithmeticValueProviderProvider { Operation = IntArithmeticOperation.Divide } },
        { "IntConstant", () => new IntConstantValueProviderProvider() },
        { "IntOverlay", () => new IntOverlayEffectProvider() },
        { "Rotation", () => new RotationEffectProvider() },
        { "BlendModeMixture", () => new BlendModeMixtureProvider { ProviderTypeName = "OverlayBlend" } },
        { "AddMixture", () => new BlendModeMixtureProvider { MixtureType = "Add" } },
        { "SubtractMixture", () => new BlendModeMixtureProvider { MixtureType = "Subtract" } },
        { "MultiplyMixture", () => new BlendModeMixtureProvider { MixtureType = "Multiply" } },
        { "ScreenMixture", () => new BlendModeMixtureProvider { MixtureType = "Screen" } },
        { "OverlayBlendMixture", () => new BlendModeMixtureProvider { MixtureType = "OverlayBlend" } },
        { "DarkenMixture", () => new BlendModeMixtureProvider { MixtureType = "Darken" } },
        { "LightenMixture", () => new BlendModeMixtureProvider { MixtureType = "Lighten" } },
        { "DifferenceMixture", () => new BlendModeMixtureProvider { MixtureType = "Difference" } },
    };

    public IReadOnlyDictionary<EffectImplementationKey, Func<IEffect>> EffectImplementationProvider =>
        new Dictionary<EffectImplementationKey, Func<IEffect>>
        {
            [new("Place", EffectImplementType.IPicture)] = () => new PlaceEffect_IPicture(),
            [new("RemoveColor", EffectImplementType.IPicture)] = () => new RemoveColorEffect_IPicture(),
            [new("ClassicOverlayMixture", EffectImplementType.IPicture)] = () => new ClassicOverlayMixture(),
            [new("AddMixture", EffectImplementType.IPicture)] = () => new AddMixture(),
            [new("SubtractMixture", EffectImplementType.IPicture)] = () => new SubtractMixture(),
            [new("MultiplyMixture", EffectImplementType.IPicture)] = () => new MultiplyMixture(),
            [new("ScreenMixture", EffectImplementType.IPicture)] = () => new ScreenMixture(),
            [new("OverlayBlendMixture", EffectImplementType.IPicture)] = () => new OverlayBlendMixture(),
            [new("DarkenMixture", EffectImplementType.IPicture)] = () => new DarkenMixture(),
            [new("LightenMixture", EffectImplementType.IPicture)] = () => new LightenMixture(),
            [new("DifferenceMixture", EffectImplementType.IPicture)] = () => new DifferenceMixture(),
            [new("Blur", EffectImplementType.IPicture)] = () => new BlurEffect_IPicture(),
            [new("Crop", EffectImplementType.IPicture)] = () => new CropEffect_IPicture(),
            [new("ProgressCrop", EffectImplementType.IPicture)] = () => new ProgressCropper_IPicture(),
            [new("Resize", EffectImplementType.IPicture)] = () => new ResizeEffect_IPicture(),
            [new("Flip", EffectImplementType.IPicture)] = () => new FlipEffect_IPicture(),
            [new("Sharpen", EffectImplementType.IPicture)] = () => new SharpenEffect_IPicture(),
            [new("Vignette", EffectImplementType.IPicture)] = () => new VignetteEffect_IPicture(),
            [new("FadeOpacity", EffectImplementType.IPicture)] = () => new FadeOpacityEffect_IPicture(),
            [new("ColorAdjustment", EffectImplementType.IPicture)] = () => new ColorAdjustmentEffect_IPicture(),
            [new("Rotation", EffectImplementType.IPicture)] = () => new RotationEffect_IPicture(),
            [new("ZoomIn", EffectImplementType.IPicture)] = () => new ZoomInContinuousEffect(),
            [new("TextFadeIn", EffectImplementType.IPicture)] = () => new TextFadeInContinuousEffect(),
            [new("Jitter", EffectImplementType.NotSpecified)] = () => new JitterEffect(),
            [new("ProgressPlacer", EffectImplementType.NotSpecified)] = () => new ProgressPlacer(),
            [new("ClassicSpeedVarianceProvider", EffectImplementType.NotSpecified)] = () => new ClassicSpeedVarianceProvider(),
            [new("IntConstant", EffectImplementType.NotSpecified)] = () => new IntConstantValueProviderEffect(),
            [new("IntOverlay", EffectImplementType.NotSpecified)] = () => new IntOverlayEffect(),
            [new("IntArithmeticAdd", EffectImplementType.NotSpecified)] = () => new IntArithmeticValueProviderEffect { TypeName = "IntArithmeticAdd", Operation = IntArithmeticOperation.Add },
            [new("IntArithmeticSubtract", EffectImplementType.NotSpecified)] = () => new IntArithmeticValueProviderEffect { TypeName = "IntArithmeticSubtract", Operation = IntArithmeticOperation.Subtract },
            [new("IntArithmeticMultiply", EffectImplementType.NotSpecified)] = () => new IntArithmeticValueProviderEffect { TypeName = "IntArithmeticMultiply", Operation = IntArithmeticOperation.Multiply },
            [new("IntArithmeticDivide", EffectImplementType.NotSpecified)] = () => new IntArithmeticValueProviderEffect { TypeName = "IntArithmeticDivide", Operation = IntArithmeticOperation.Divide },
        };





    public Dictionary<string, IVideoSource> VideoSourceProvider =>
        new Dictionary<string, (Func<bool>, IVideoSource)>
        {
            { "DecoderContextHW", (HWAccelDecodeOptionGetter, new DecoderContextHW()) },
            { "DecoderContext8Bit", (AlwaysTrue, new DecoderContext8Bit())},
            { "DecoderContext16Bit", (AlwaysTrue, new DecoderContext16Bit()) },
            { "HDRDecoderContext", (AlwaysTrue, new HDRDecoderContext())},
            { "AlphaBrightnessDecoderContext", (AlwaysTrue, new AlphaBrightnessDecoderContext()) },
            { "HttpDecoderContext", (AlwaysTrue, new HttpDecoderContext()) },
            { "FFmpegDeviceDecoderContext", (AlwaysTrue, new FFmpegDeviceDecoderContext()) },
            { "RPSVDecoderContext", (AlwaysTrue, new RawPictureSequenceStreamVideoDecoderContext()) },
            { "DecoderContextPJFCProject", (AlwaysTrue, new DecoderContextPJFCProject()) }
        }.ComputeCondition();

    public IVideoSource VideoSourceCreator(string filePath)
    {
        if (AlphaBrightnessDecoderContext.IsAlphaBrightnessVideo(filePath)) return new AlphaBrightnessDecoderContext(filePath);
        var prefered = VideoSourceProvider.Values.Where((k) => k.PreferredExtension.Contains(Path.GetExtension(filePath)));
        if (prefered.Any())
        {
            return prefered.First().CreateNew(filePath);
        }
        else
        {
            foreach (var provider in VideoSourceProvider.Values)
            {
                var instance = provider.CreateNew(filePath);
                if (instance.TryInitialize())
                {
                    return instance;
                }
                else
                {
                    instance.Dispose();
                }
            }
        }
        throw new NotSupportedException($"No suitable video source found for the given file '{filePath}'.");
    }



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

    public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider =>
        new Dictionary<string, (Func<bool>, Func<string, IVideoWriter>)>
        {
            { "VideoWriterHWAccel", ((() => HWAccelEncodeOptionGetter() && !(OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())), new((_) => new VideoWriterHWAccel())) },
            { "VideoWriter", (AlwaysTrue, new((_) => new VideoWriter())) },
            { "HDRVideoWriter", (AlwaysTrue, new((_) => new HDRVideoWriter())) },
            { "AlphaBrightnessVideoWriter", (AlwaysTrue, new((_) => new AlphaBrightnessVideoWriter())) },
            { "BlackHoleWriter", (AlwaysTrue, new((_) => new BlackholeVideoWriter())) }
        }.ComputeCondition();

    public Dictionary<string, Func<Guid, Guid, ITransform>> TransformProvider => new Dictionary<string, Func<Guid, Guid, ITransform>>
    {
        { "Fade", (clipId, _) => new FadeTransform { BindedLeftClip = clipId } },
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
            ClipMode.VideoClip => HandleVideoClip(element),
            ClipMode.PhotoClip => HandlePhotoClip(element),
            ClipMode.SolidColorClip => element.Deserialize<SolidColorClip>() ?? throw new NullReferenceException(),
            ClipMode.TextClip => element.Deserialize<TextClip>() ?? throw new NullReferenceException(),
            ClipMode.AudioClip => element.Deserialize<SoundTrackToClipWrapper>() ?? throw new NullReferenceException(),
            ClipMode.MarkingClip => element.Deserialize<MarkingClip>() ?? throw new NullReferenceException(),
            ClipMode.TransformClip => throw new NotSupportedException("Standalone transform clips are no longer supported."),
            ClipMode.VectorCanvasClip => element.Deserialize<VectorCanvasClip>() ?? throw new NullReferenceException(),
            ClipMode.VectorComponentClip => element.Deserialize<VectorComponentClip>() ?? throw new NullReferenceException(),
            _ => throw new NotSupportedException($"Unknown or unsupported clip type {type}."),
        };
    }

    private static IClip HandleVideoClip(JsonElement element)
    {
        if (element.TryGetProperty("TypeName", out var e) && e.GetString() == "VirtualSourceVideoClip")
        {
            return element.Deserialize<VirtualSourceVideoClip>() ?? throw new NullReferenceException();
        }
        return element.Deserialize<VideoClip>() ?? throw new NullReferenceException();
    }

    private static IClip HandlePhotoClip(JsonElement element)
    {
        bool isVect = false;
        try
        {
            isVect = element.GetProperty("IsVector").GetBoolean();

        }
        catch { }

        if (isVect || (element.TryGetProperty("FilePath", out var filePathProperty) && !string.IsNullOrEmpty(filePathProperty.GetString()) && (Path.GetExtension(filePathProperty.GetString()) ?? "").ToLowerInvariant() == ".svg"))
        {
            return element.Deserialize<VectorPhotoClip>() ?? throw new NullReferenceException();
        }
        else
        {
            return element.Deserialize<PhotoClip>() ?? throw new NullReferenceException();
        }
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
            "Fade" => element.Deserialize<FadeTransform>() ?? throw new NullReferenceException("Failed to deserialize FadeTransform."),
            "Crossfade" => element.Deserialize<CrossfadeTransform>() ?? throw new NullReferenceException("Failed to deserialize CrossfadeTransform."),
            "ExternalSourceTransform" => element.Deserialize<ExternalSourceTransform>() ?? throw new NullReferenceException("Failed to deserialize ExternalSourceTransform."),
            _ => throw new NotSupportedException($"Unknown or unsupported transform type '{typeName}'.")
        };
    }

    IVectorComponent IPluginBase.VectComponentCreator(JsonElement element)
    {
        var typeName = element.GetProperty("TypeName").GetString();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return typeName switch
        {
            "Rectangle" => element.Deserialize<RectangleComponent>(options)!,
            "RoundedRectangle" => element.Deserialize<RoundedRectangleComponent>(options)!,
            "Ellipse" => element.Deserialize<EllipseComponent>(options)!,
            "Line" => element.Deserialize<LineComponent>(options)!,
            "CubicBezier" => element.Deserialize<CubicBezierComponent>(options)!,
            "QuadraticBezier" => element.Deserialize<QuadraticBezierComponent>(options)!,
            "Arc" => element.Deserialize<ArcComponent>(options)!,
            "Polygon" => element.Deserialize<PolygonComponent>(options)!,
            "Polyline" => element.Deserialize<PolylineComponent>(options)!,
            "ComponentGroup" => element.Deserialize<ComponentGroup>(options)!,
            "EvaluatedVectorComponent" => element.Deserialize<projectFrameCut.Render.VectorContent.EvaluatedVectorComponent>(options)!,
            "Text" => element.Deserialize<TextComponent>(options)!,
            _ => throw new NotSupportedException($"Unknown component type: {typeName}"),
        };
    }

    string? IPluginBase.ReadLocalizationItem(string key, string locate)
    {
        if (key == "DisplayName_Transform_Fade" && locate == "zh-CN") return "淡入淡出";
        var loc = ISimpleLocalizerBase_PropertyPanel.GetMapping().FirstOrDefault(x => x.Key == locate, ISimpleLocalizerBase_PropertyPanel.GetMapping().First()).Value;
        if (!loc.IsItemExist(key)) return null;
        return loc.DynamicLookup(key, key);
    }

    bool IPluginBase.OnLoaded(out string FailedReason)
    {
        projectFrameCut.Drawing.Processing.Resizing.PictureResizer.Default = new EffectPictureResizer();
        try
        {
            TextClipFontRegistry.Initialize();
        }
        catch (Exception ex)
        {
            Log(ex, "Init Fone cache", this);
        }
        FailedReason = "";
        return true;
    }
    public static Func<bool> HWAccelDecodeOptionGetter = new(() => ((GlobalPluginHelper.MessagingService?.Call("projectFrameCut.Program", "GetSetting", ["codec_PreferredHWAccelDecoding"]) ?? "true") is string hwaccel && bool.TryParse(hwaccel, out var result) && result));
    public static Func<bool> HWAccelEncodeOptionGetter = new(() => ((GlobalPluginHelper.MessagingService?.Call("projectFrameCut.Program", "GetSetting", ["codec_PreferredHWAccelEncoding"]) ?? "true") is string hwaccel && bool.TryParse(hwaccel, out var result) && result));
    private static bool AlwaysTrue() => true;
}
