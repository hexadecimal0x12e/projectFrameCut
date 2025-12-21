using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.AndroidOpenGL.Platforms.Android
{
    public class OpenGLPlugin : IPluginBase
    {
        string IPluginBase.PluginID => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLPlugin";

        int IPluginBase.PluginAPIVersion => 1;

        string IPluginBase.Name => "OpenGL Accelerator Plugin";

        string IPluginBase.Author => "hexadecimal0x12e";

        string IPluginBase.Description => "A plugin for OpenGL accelerated rendering.";

        Version IPluginBase.Version => new Version(1, 2, 0, 1);

        string IPluginBase.AuthorUrl => "";

        string? IPluginBase.PublishingUrl => null;

        public Dictionary<string, Dictionary<string, string>> LocalizationProvider => new();

        Dictionary<string, Func<IEffect>> IPluginBase.EffectProvider => new Dictionary<string, Func<IEffect>> { };
        public Dictionary<string, Func<IEffect>> ContinuousEffectProvider => new Dictionary<string, Func<IEffect>>
        {

        };

        public Dictionary<string, Func<IEffect>> VariableArgumentEffectProvider => new Dictionary<string, Func<IEffect>>
        {

        };
        Dictionary<string, Func<IMixture>> IPluginBase.MixtureProvider => new Dictionary<string, Func<IMixture>> { };

        Dictionary<string, Func< IComputer>> IPluginBase.ComputerProvider => 
            new Dictionary<string, Func< IComputer>> 
            {
                {"OverlayComputer", new(() => new OverlayComputer()) },
                {"RemoveColorComputer", new(() => new RemoveColorComputer()) }
            };

        Dictionary<string, Func<string, string, IClip>> IPluginBase.ClipProvider => new Dictionary<string, Func<string, string, IClip>> { };

        Dictionary<string, Func<string, IVideoSource>> IPluginBase.VideoSourceProvider => new Dictionary<string, Func<string, IVideoSource>> { };

        public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider => new Dictionary<string, Func<string, IAudioSource>> { };

        public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider => new Dictionary<string, Func<string, string, ISoundTrack>> { };

        public Dictionary<string, string> Configuration { get => new Dictionary<string, string>(); set { } }

        public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString => new Dictionary<string, Dictionary<string, string>> { };
    }
}
