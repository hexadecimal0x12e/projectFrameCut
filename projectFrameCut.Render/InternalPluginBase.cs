using projectFrameCut.Render.RenderAPIBase;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.VideoMakeEngine;
using projectFrameCut.Shared;
using projectFrameCut.VideoMakeEngine;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render;


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

    public IEffect EffectCreator(EffectAndMixtureJSONStructure stru) => EffectHelper.CreateFromJSONStructure(stru);

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
}

