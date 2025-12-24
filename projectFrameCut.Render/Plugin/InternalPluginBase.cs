using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.VideoMakeEngine;
using projectFrameCut.Render.Videos;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.Plugin;


public class InternalPluginBase : IPluginBase
{
    public string PluginID => "projectFrameCut.Render.Plugins.InternalPluginBase";

    public int PluginAPIVersion => 1;

    public string Name => "Internal fundamental plugin";

    public string Author => "hexadecimal0x12e";

    public string Description => "Plugin that provide fundamental components for projectFrameCut.";

    public Version Version => Assembly.GetExecutingAssembly().GetName().Version ?? new(1, 0, 0, 0);

    public string AuthorUrl => "https://hexadecimal0x12e.com";

    public string? PublishingUrl => null;

    public Dictionary<string, Dictionary<string, string>> LocalizationProvider => new Dictionary<string, Dictionary<string, string>>
    {
        {
            "zh-CN", 
            new Dictionary<string, string>
            {
                {"_PluginBase_Name_", "projectFrameCut 内部基础插件" },
                {"_PluginBase_Description_", "作为 projectFrameCut 的一部分，提供 projectFrameCut 的基本功能" }
            } 
        }
    };

    public Dictionary<string, Func<IEffect>> EffectProvider => new Dictionary<string, Func<IEffect>>
    {
        {"RemoveColor",  new(() => new RemoveColorEffect())},
        {"Place",  new(() => new PlaceEffect())},
        {"Crop", new(() => new CropEffect()) },
        {"Resize",  new(() => new ResizeEffect())}
    };

    public Dictionary<string, Func<IMixture>> MixtureProvider => new Dictionary<string, Func<IMixture>>
    {
        {"Overlay", new(() => new OverlayMixture()) }
    };

    public Dictionary<string, Func<IComputer>> ComputerProvider => new Dictionary<string, Func<IComputer>>
    {

    };

    public Dictionary<string, Func<IEffect>> ContinuousEffectProvider => new Dictionary<string, Func<IEffect>>
    {
        {"ZoomIn", new(() => new ZoomInContinuousEffect())  },
    };

    public Dictionary<string, Func<IEffect>> VariableArgumentEffectProvider => new Dictionary<string, Func<IEffect>>
    {

    };

    public Dictionary<string, Func<string, string, IClip>> ClipProvider => new Dictionary<string, Func<string, string, IClip>>
    {
        {"VideoClip", new((i,n) => new VideoClip{Id = i, Name = n}) },
        {"PhotoClip", new((i,n) => new PhotoClip{Id = i, Name = n}) },
        {"SolidColorClip", new((i,n) => new SolidColorClip{Id = i, Name = n}) },
        {"TextClip", new((i,n) => new TextClip{Id = i, Name = n}) }
    };

    public Dictionary<string, Func<string, IVideoSource>> VideoSourceProvider => new Dictionary<string, Func<string, IVideoSource>>
    {
        {"DecoderContext8Bit", new((p) => new DecoderContext8Bit(p)) },
        {"DecoderContext16Bit", new((p) => new DecoderContext16Bit(p)) }
    };


    public Dictionary<string, string> Configuration { get => new(); set { } }

    public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString => new Dictionary<string, Dictionary<string, string>> { };

    public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider => new Dictionary<string, Func<string, string, ISoundTrack>> { };

    public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider => new Dictionary<string, Func<string, IAudioSource>> { };

    //public IEffect EffectCreator(EffectAndMixtureJSONStructure stru) => EffectHelper.CreateFromJSONStructure(stru);

    IClip IPluginBase.ClipCreator(JsonElement element)
    {
        ClipMode type = (ClipMode)element.GetProperty("ClipType").GetInt32();
        Console.WriteLine($"Found clip {type}, name: {element.GetProperty("Name").GetString()}, id: {element.GetProperty("Id").GetString()}");
        return type switch
        {
            ClipMode.VideoClip => element.Deserialize<VideoClip>() ?? throw new NullReferenceException(),
            ClipMode.PhotoClip => element.Deserialize<PhotoClip>() ?? throw new NullReferenceException(),
            ClipMode.SolidColorClip => element.Deserialize<SolidColorClip>() ?? throw new NullReferenceException(),
            ClipMode.TextClip => element.Deserialize<TextClip>() ?? throw new NullReferenceException(),
            _ => throw new NotSupportedException($"Unknown or unsupported clip type {type}."),
        };
    }

    string? IPluginBase.ReadLocalizationItem(string key, string locate)
    {
        var loc = ISimpleLocalizerBase_PropertyPanel.GetMapping().FirstOrDefault(x => x.Key == locate, ISimpleLocalizerBase_PropertyPanel.GetMapping().First()).Value;
        var result = loc.DynamicLookup(key, "!!!NULL!!!");
        return result == "!!!NULL!!!" ? null : result;
    }



}

