using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.AndroidOpenGL.Platforms.Android
{
    public class OpenGLPlugin : IPluginBase
    {
        private readonly Dictionary<string, string> _configuration = new()
        {
            ["enableGLWorkScheduler"] = "false",
            ["maxGLJobTimeout"] = "60000",
        };

        public string DefaultComputeBackend { get; set; } = "vulkan";

        string IPluginBase.PluginID => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLPlugin";

        int IPluginBase.PluginAPIVersion => IPluginBase.CurrentPluginAPIVersion;

        string IPluginBase.Name => "Android platform Accelerator Plugin";

        string IPluginBase.Author => "hexadecimal0x12e";

        string IPluginBase.Description => "A plugin for OpenGL/Vulkan accelerated rendering.";

        Version IPluginBase.Version => new Version(1, 4, 0, 2);

        string IPluginBase.AuthorUrl => "";

        string? IPluginBase.PublishingUrl => null;

        public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string>
        {
            { "IsInternalPlugin","true" }
        };

        public Dictionary<string, Dictionary<string, string>> LocalizationProvider => new Dictionary<string, Dictionary<string, string>>
        {

        };

        Dictionary<string, Func<IEffect>> IPluginBase.EffectProvider => new Dictionary<string, Func<IEffect>> { };
        public Dictionary<string, Func<IEffect>> ContinuousEffectProvider => new Dictionary<string, Func<IEffect>>
        {

        };

        public Dictionary<string, Func<IEffect>> BindableArgumentEffectProvider => new Dictionary<string, Func<IEffect>>
        {

        };



        Dictionary<string, Func<IComputer>> IPluginBase.ComputerProvider => GetProvider();

        private Dictionary<string, Func<IComputer>> GetProvider()
        {
            if (ComputerHelper.UseVulkanBackend)
            {
                return new Dictionary<string, Func<IComputer>>
                {
                    {"OverlayComputer", new(() => new VulkanOverlayComputer()) },
                    {"RemoveColorComputer", new(() => new VulkanRemoveColorComputer()) },
                    {"ResizeComputer", new(() => new VulkanResizeComputer()) },
                    {"CropComputer", new(() => new VulkanCropComputer()) },
                    {"PlaceComputer", new(() => new VulkanPlaceComputer()) }
                };
            }
            else
            {
                return new Dictionary<string, Func<IComputer>>
                {
                    {"OverlayComputer", new(() => new OverlayComputer()) },
                    {"RemoveColorComputer", new(() => new RemoveColorComputer()) },
                    {"ResizeComputer", new(() => new ResizeComputer()) },
                    {"CropComputer", new(() => new CropComputer()) },
                    {"PlaceComputer", new(() => new PlaceComputer()) }
                };
            }
        }


        //Dictionary<string, Func<string, string, IClip>> IPluginBase.ClipProvider => new Dictionary<string, Func<string, string, IClip>> { };
        Dictionary<string, Func<string, IVideoSource>> IPluginBase.VideoSourceProvider => new Dictionary<string, Func<string, IVideoSource>> { };
        public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider => new Dictionary<string, Func<string, IAudioSource>> { };
        public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider => new Dictionary<string, Func<string, string, ISoundTrack>> { };
        public Dictionary<string, string> Configuration
        {
            get => _configuration;
            set
            {
                if (value is null)
                {
                    return;
                }

                foreach (var kvp in value)
                {
                    _configuration[kvp.Key] = kvp.Value;
                }

                ApplyConfiguration();
            }
        }

        public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString => new()
        {
            ["en-US"] = new Dictionary<string, string>
            {
                ["useVulkan"] = "(Edit this in Render setting page, modify the option there is NOT PRESISTED to the disk) use Vulkan as Compute backend",
                ["enableGLWorkScheduler"] = "Enable GL Work Scheduler (True/False)",
                ["maxGLJobTimeout"] = "worker thread's timeout(ms)"
            },
            ["zh-CN"] = new Dictionary<string, string>
            {
                ["useVulkan"] = "(请在‘渲染’设置页面配置此选项，在此处的修改不会持久保存) 使用Vulkan作为计算后端",
                ["enableGLWorkScheduler"] = "启用 GL 工作调度器 (True/False)",
                ["maxGLJobTimeout"] = "工作线程超时(毫秒)"
            }
        };
        public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider => new Dictionary<string, Func<string, IVideoWriter>> { };
        public Dictionary<string, IEffectFactory> ContinuousEffectFactoryProvider => new Dictionary<string, IEffectFactory> { };
        public Dictionary<string, IEffectFactory> BindableArgumentEffectFactoryProvider => new Dictionary<string, IEffectFactory> { };
        public Dictionary<string, IEffectFactory> EffectFactoryProvider => new Dictionary<string, IEffectFactory> { };

        public Dictionary<string, Func<Guid, Guid, RenderAPIBase.ClipAndTrack.ITransform>> TransformProvider => new Dictionary<string, Func<Guid, Guid, RenderAPIBase.ClipAndTrack.ITransform>> { };

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
            Configuration["computeBackend"] = DefaultComputeBackend;
            ApplyConfiguration();
            FailedReason = string.Empty;
            Logger.Log($"use vulkan: {ComputerHelper.UseVulkanBackend}");
            Configuration["useVulkan"] = ComputerHelper.UseVulkanBackend.ToString();
            return true;
        }

        private void ApplyConfiguration()
        {
            ComputerHelper.SetPreferredBackend(Configuration.TryGetValue("computeBackend", out var backend) ? backend : "OpenGL");
            ComputerHelper.Timeout = uint.TryParse(Configuration.TryGetValue("maxGLJobTimeout", out var timeout) ? timeout : "30000", out var to) && to < int.MaxValue ? (int)to : 30000;
        }
    }
}
