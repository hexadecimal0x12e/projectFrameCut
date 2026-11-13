using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Render.Effect;

using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.HwAccelEngine;
using projectFrameCut.Render.WindowsRender;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.StandaloneRender
{
    public class ILGPUPlugin : IPluginBase
    {
        string IPluginBase.PluginID => "projectFrameCut.Render.HwAccelEngine";

        int IPluginBase.PluginAPIVersion => IPluginBase.CurrentPluginAPIVersion;

        string IPluginBase.Name => "ILGPU CUDA/OpenCL Accelerator Plugin";

        string IPluginBase.Author => "hexadecimal0x12e";

        string IPluginBase.Description => "A plugin for ILGPU-based CUDA/OpenCL accelerated rendering.";

        Version IPluginBase.Version => new Version(1, 2, 0, 1);

        string IPluginBase.AuthorUrl => "";

        string? IPluginBase.PublishingUrl => null;

        public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string>
        {
            { "IsInternalPlugin","true" }
        };

        public Dictionary<string, Dictionary<string, string>> LocalizationProvider => new Dictionary<string, Dictionary<string, string>>
        {

        };




        public Dictionary<string, Func<IEffectProvider>> EffectProviderProvider => new Dictionary<string, Func<IEffectProvider>> { };

        public IReadOnlyDictionary<EffectImplementationKey, Func<IEffect>> EffectImplementationProvider => HardwareEffectFactories.Create();


        //Dictionary<string, Func<string, string, IClip>> IPluginBase.ClipProvider => new Dictionary<string, Func<string, string, IClip>> { };
        Dictionary<string, IVideoSource> IPluginBase.VideoSourceProvider => new Dictionary<string, IVideoSource> { };
        public Dictionary<string, string> Configuration { get => new Dictionary<string, string>(); set { } }
        public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString => new Dictionary<string, Dictionary<string, string>> { };
        public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider => new Dictionary<string, Func<string, string, ISoundTrack>> { };
        public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider => new Dictionary<string, Func<string, IAudioSource>> { };
        public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider => new Dictionary<string, Func<string, IVideoWriter>> { };
        public IMessagingService MessagingQueue { get; set; }


        public IClip ClipCreator(JsonElement element)
        {
            throw new NotImplementedException();
        }

        public ISoundTrack SoundTrackCreator(JsonElement element)
        {
            throw new NotImplementedException();
        }

        bool IPluginBase.OnLoaded(out string FailedReason)
        {
            AcceleratorsManager.IsRendering = true;
            FailedReason = "";
            return true;
        }

        public void OnClosing() => AcceleratorsManager.ReleaseResources();
    }
}
