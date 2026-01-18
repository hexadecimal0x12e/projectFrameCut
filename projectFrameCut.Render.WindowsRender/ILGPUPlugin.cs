using ILGPU.Runtime;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.WindowsRender
{
    public class ILGPUPlugin : IPluginBase
    {
        public static Accelerator[] accelerators = Array.Empty<Accelerator>();


        string IPluginBase.PluginID => "projectFrameCut.Render.WindowsRender.ILGPUPlugin";

        int IPluginBase.PluginAPIVersion => 1;

        string IPluginBase.Name => "ILGPU CUDA/OpenCL Accelerator Plugin";

        string IPluginBase.Author => "hexadecimal0x12e";

        string IPluginBase.Description => "A plugin for ILGPU-based CUDA/OpenCL accelerated rendering.";

        Version IPluginBase.Version => new Version(1, 2, 0, 1);

        string IPluginBase.AuthorUrl => "";

        string? IPluginBase.PublishingUrl => null;

        public Dictionary<string, string> Properties = new Dictionary<string, string>
        {
            { "_IsInternalPlugin","true" }
        };

        public Dictionary<string, Dictionary<string, string>> LocalizationProvider => new Dictionary<string, Dictionary<string, string>>
        {

        };

        Dictionary<string, Func<IComputer>> IPluginBase.ComputerProvider =>
            new Dictionary<string, Func<IComputer>>
            {
                {"OverlayComputer", new(() => new OverlayComputer(accelerators,null)) },
                {"RemoveColorComputer", new(() => new RemoveColorComputer(accelerators,null)) },
                {"ResizeComputer", new(() => new ResizeComputer(accelerators,null)) }
            };


        Dictionary<string, Func<IEffect>> IPluginBase.EffectProvider => new Dictionary<string, Func<IEffect>> { };
        public Dictionary<string, Func<IEffect>> ContinuousEffectProvider => new Dictionary<string, Func<IEffect>> { };
        public Dictionary<string, Func<IEffect>> BindableArgumentEffectProvider => new Dictionary<string, Func<IEffect>> { };
        Dictionary<string, Func<IMixture>> IPluginBase.MixtureProvider => new Dictionary<string, Func<IMixture>> { };
        Dictionary<string, Func<string, string, IClip>> IPluginBase.ClipProvider => new Dictionary<string, Func<string, string, IClip>> { };
        Dictionary<string, Func<string, IVideoSource>> IPluginBase.VideoSourceProvider => new Dictionary<string, Func<string, IVideoSource>> { };
        public Dictionary<string, string> Configuration { get => new Dictionary<string, string>(); set { } }
        public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString => new Dictionary<string, Dictionary<string, string>> { };
        public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider => new Dictionary<string, Func<string, string, ISoundTrack>> { };
        public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider => new Dictionary<string, Func<string, IAudioSource>> { };
        public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider => new Dictionary<string, Func<string, IVideoWriter>> { };
        public Dictionary<string, IEffectFactory> ContinuousEffectFactoryProvider => new Dictionary<string, IEffectFactory> { };
        public Dictionary<string, IEffectFactory> BindableArgumentEffectFactoryProvider => new Dictionary<string, IEffectFactory> { };
        public IMessagingService MessagingQueue { get; set; }


        public IClip ClipCreator(JsonElement element)
        {
            throw new NotImplementedException();
        }

        public ISoundTrack SoundTrackCreator(JsonElement element)
        {
            throw new NotImplementedException();
        }
    }
}
