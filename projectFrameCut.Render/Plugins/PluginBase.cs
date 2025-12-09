using projectFrameCut.Shared;
using projectFrameCut.VideoMakeEngine;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.Plugins
{
    public interface IPluginBase
    {
        public int PluginAPIVersion { get; }
        public string Name { get; }
        public string Author { get; }
        public string Description { get; }
        public Version Version { get; }
        public string AuthorUrl { get; }

        public Dictionary<string, Func<IEffect>> EffectProvider { get; }
        public Dictionary<string, Func<IMixture>> MixtureProvider { get; }
        public Dictionary<string, Func<IComputer>> ComputerProvider { get; }
        public Dictionary<string, Func<string,string,IClip>> ClipProvider { get; }
        public IClip ClipCreator(JsonElement element);
    }

    public class InternalPluginBase : IPluginBase
    {
        public int PluginAPIVersion => 1;

        public string Name => "projectFrameCut's Internal Plugin";

        public string Author => "hexadecimal0x12e";

        public string Description => "The internal plugin for projectFrameCut.";

        public Version Version => Assembly.GetExecutingAssembly().GetName().Version ?? new(1,0,0,0);

        public string AuthorUrl => "https://hexadecimal0x12e.com";

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

        IClip IPluginBase.ClipCreator(JsonElement element) => IClip.FromJSON(element);
    }
}
