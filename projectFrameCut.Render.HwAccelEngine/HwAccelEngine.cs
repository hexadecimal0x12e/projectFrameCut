
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System.Text.Json;




#if WINDOWS || LINUX
using projectFrameCut.Render.WindowsRender;
using ILGPU;
using ILGPU.Runtime;
#elif ANDROID
using Android.Content;
using projectFrameCut.Render.HwAccelEngine.Platforms.Android;

#endif

namespace projectFrameCut.Render.HwAccelEngine
{
    public class HwAccelEnginePlugin : IPluginBase
    {
        public static string dataRootPath { get; private set; } = null!;

        string IPluginBase.PluginID => "projectFrameCut.Render.HwAccelEngine";

        int IPluginBase.PluginAPIVersion => IPluginBase.CurrentPluginAPIVersion;

        string IPluginBase.Name => "GPU Accelerator provider Plugin";

        string IPluginBase.Author => "hexadecimal0x12e";

        string IPluginBase.Description => "A plugin for GPU accelerated rendering.";

        Version IPluginBase.Version => typeof(HwAccelEnginePlugin).Assembly.GetName().Version ?? new Version(1, 0, 0);

        string IPluginBase.AuthorUrl => "";

        string? IPluginBase.PublishingUrl => null;

        public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string>
        {
            { "IsInternalPlugin","true" }
        };

        public Dictionary<string, Dictionary<string, string>> LocalizationProvider => new Dictionary<string, Dictionary<string, string>>
        {

        };

#if WINDOWS || LINUX
        internal static bool? ForceSync;
        internal static bool disableWin2DRasterizer = false;



        private readonly Dictionary<string, string> _configuration = new()
        {
            ["forceSync"] = "Disable",
            ["disableWin2DRasterizer"] = "False",
        };

        public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString => new()
        {
            ["en-US"] = new Dictionary<string, string>
            {
                ["forceSync"] = "override synchronization configuration to True/False (True/False, or Disable to keep default behavior)",
                ["disableWin2DRasterizer"] = "Disable Win2D Rasterizer, use ILGPU Rasterizer (True/False)",
            },
            ["zh-CN"] = new Dictionary<string, string>
            {
                ["forceSync"] = "覆盖同步配置 (True/False, 或 Disable 以保持默认行为)",
                ["disableWin2DRasterizer"] = "使用 ILGPU 光栅化器，而不是平台API Win2D 光栅化器 (True/False)",
            }
        };
#elif ANDROID




        public string DefaultComputeBackend { get; set; } = "vulkan";

        private readonly Dictionary<string, string> _configuration = new()
        {
            ["enableGLWorkScheduler"] = "false",
            ["maxGLJobTimeout"] = "60000",
        };

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
#else

        private readonly Dictionary<string, string> _configuration = new()
        {
        };
        public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString => new();
#endif

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


        public Dictionary<string, Func<IEffectProvider>> EffectProviderProvider => new Dictionary<string, Func<IEffectProvider>> { };

        public IReadOnlyDictionary<EffectImplementationKey, Func<IEffect>> EffectImplementationProvider => HardwareEffectFactories.Create();



        Dictionary<string, IVideoSource> IPluginBase.VideoSourceProvider => new();
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
#if WINDOWS || LINUX

        public void OnClosing() => AcceleratorsManager.ReleaseResources();

        bool IPluginBase.OnLoaded(out string FailedReason)
        {
            dataRootPath = this.GetPluginDataRoot();
            ApplyConfiguration();
            FailedReason = "";
            return true;
        }
        private void ApplyConfiguration()
        {
            projectFrameCut.Render.Effect.EffectHelper.InvalidateImplementations();
            ForceSync = Configuration.TryGetValue("forceSync", out var forceSyncStr) && bool.TryParse(forceSyncStr, out var fs) ? fs : null;
            ILGPUExecutionHelper.SyncOverride = ForceSync;
            disableWin2DRasterizer = Configuration.TryGetValue("disableWin2DRasterizer", out var disableWin2DRasterizerStr) && bool.TryParse(disableWin2DRasterizerStr, out var r) && r;

            Logger.Log("[HwAccelEnginePlugin] ILGPU accelerators will be initialized on first use.");
            Logger.Log($"[HwAccelEnginePlugin] ForceSync: {ForceSync?.ToString() ?? "default"}, Disable Win2D Rasterizer: {disableWin2DRasterizer}");
        }
#elif ANDROID
        bool IPluginBase.OnLoaded(out string FailedReason)
        {
            dataRootPath = this.GetPluginDataRoot();

            Configuration["computeBackend"] = DefaultComputeBackend;
            ApplyConfiguration();
            FailedReason = string.Empty;
            Logger.Log($"use vulkan: {AndroidExecutionHelper.UseVulkanBackend}");
            Configuration["useVulkan"] = AndroidExecutionHelper.UseVulkanBackend.ToString();
            return true;
        }

        private void ApplyConfiguration()
        {
            projectFrameCut.Render.Effect.EffectHelper.InvalidateImplementations();
            AndroidExecutionHelper.SetPreferredBackend(Configuration.TryGetValue("computeBackend", out var backend) ? backend : "OpenGL");
            AndroidExecutionHelper.Timeout = uint.TryParse(Configuration.TryGetValue("maxGLJobTimeout", out var timeout) ? timeout : "30000", out var to) && to < int.MaxValue ? (int)to : 30000;
        }
#else
        private void ApplyConfiguration() { }
#endif


    }
}
