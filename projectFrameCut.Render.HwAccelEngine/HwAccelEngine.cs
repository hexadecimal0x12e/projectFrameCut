
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

        int IPluginBase.PluginAPIVersion => 1;

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
        static bool? forceSync = null;
        internal static bool disableWin2DRasterizer = false;

        Dictionary<string, Func<IComputer>> IPluginBase.ComputerProvider =>
            new Dictionary<string, Func<IComputer>>
            {
                {"OverlayComputer", new(() => new OverlayComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"ApproximateOverlayComputer", new(() => new ApproximateOverlayComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"RemoveColorComputer", new(() => new RemoveColorComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"ResizeComputer", new(() => new ResizeComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"CropComputer", new(() => new CropComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"PlaceComputer", new(() => new PlaceComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"AddComputer", new(() => new BlendAddComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"SubtractComputer", new(() => new BlendSubtractComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"MultiplyComputer", new(() => new BlendMultiplyComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"ScreenComputer", new(() => new BlendScreenComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"OverlayBlendComputer", new(() => new BlendOverlayBlendComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"DarkenComputer", new(() => new BlendDarkenComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"LightenComputer", new(() => new BlendLightenComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"DifferenceComputer", new(() => new BlendDifferenceComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"OpacityComputer", new(() => new OpacityComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"VignetteComputer", new(() => new VignetteComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"FlipComputer", new(() => new FlipComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"SharpenComputer", new(() => new SharpenComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"RotationComputer", new(() => new RotationComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"BlurComputer", new(() => new BlurComputer(AcceleratorsManager.Accelerators,forceSync)) },
                {"ColorAdjustmentComputer", new(() => new ColorAdjustmentComputer(AcceleratorsManager.Accelerators,forceSync)) }
            };

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
        Dictionary<string, Func<IComputer>> IPluginBase.ComputerProvider => GetProvider();

        private Dictionary<string, Func<IComputer>> GetProvider()
        {
            if (ComputerHelper.UseVulkanBackend)
            {
                return new Dictionary<string, Func<IComputer>>
                {
                    {"OverlayComputer", new(() => new VulkanOverlayComputer()) },
                    {"ApproximateOverlayComputer", new(() => new VulkanApproximateOverlayComputer()) },
                    {"RemoveColorComputer", new(() => new VulkanRemoveColorComputer()) },
                    {"ResizeComputer", new(() => new VulkanResizeComputer()) },
                    {"CropComputer", new(() => new VulkanCropComputer()) },
                    {"PlaceComputer", new(() => new VulkanPlaceComputer()) },
                    {"AddComputer", new(() => new VulkanBlendAddComputer()) },
                    {"SubtractComputer", new(() => new VulkanBlendSubtractComputer()) },
                    {"MultiplyComputer", new(() => new VulkanBlendMultiplyComputer()) },
                    {"ScreenComputer", new(() => new VulkanBlendScreenComputer()) },
                    {"OverlayBlendComputer", new(() => new VulkanBlendOverlayBlendComputer()) },
                    {"DarkenComputer", new(() => new VulkanBlendDarkenComputer()) },
                    {"LightenComputer", new(() => new VulkanBlendLightenComputer()) },
                    {"DifferenceComputer", new(() => new VulkanBlendDifferenceComputer()) },
                    {"OpacityComputer", new(() => new VulkanOpacityComputer()) },
                    {"VignetteComputer", new(() => new VulkanVignetteComputer()) },
                    {"FlipComputer", new(() => new VulkanFlipComputer()) },
                    {"SharpenComputer", new(() => new VulkanSharpenComputer()) },
                    {"RotationComputer", new(() => new VulkanRotationComputer()) },
                    {"BlurComputer", new(() => new VulkanBlurComputer()) },
                    {"ColorAdjustmentComputer", new(() => new VulkanColorAdjustmentComputer()) }
                };
            }
            else
            {
                return new Dictionary<string, Func<IComputer>>
                {
                    {"OverlayComputer", new(() => new OverlayComputer()) },
                    {"ApproximateOverlayComputer", new(() => new ApproximateOverlayComputer()) },
                    {"ResizeComputer", new(() => new ResizeComputer()) },
                    {"CropComputer", new(() => new CropComputer()) },
                    {"PlaceComputer", new(() => new PlaceComputer()) },
                    {"AddComputer", new(() => new BlendAddComputer()) },
                    {"SubtractComputer", new(() => new BlendSubtractComputer()) },
                    {"MultiplyComputer", new(() => new BlendMultiplyComputer()) },
                    {"ScreenComputer", new(() => new BlendScreenComputer()) },
                    {"OverlayBlendComputer", new(() => new BlendOverlayBlendComputer()) },
                    {"DarkenComputer", new(() => new BlendDarkenComputer()) },
                    {"LightenComputer", new(() => new BlendLightenComputer()) },
                    {"DifferenceComputer", new(() => new BlendDifferenceComputer()) },
                    {"OpacityComputer", new(() => new OpacityComputer()) },
                    {"VignetteComputer", new(() => new VignetteComputer()) },
                    {"FlipComputer", new(() => new FlipComputer()) },
                    {"SharpenComputer", new(() => new SharpenComputer()) },
                    {"RotationComputer", new(() => new RotationComputer()) },
                    {"BlurComputer", new(() => new BlurComputer()) },
                    {"ColorAdjustmentComputer", new(() => new ColorAdjustmentComputer()) }
                };
            }
        }

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
        public Dictionary<string, Func<IComputer>> ComputerProvider => new Dictionary<string, Func<IComputer>> { };
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

        Dictionary<string, Func<string, IVideoSource>> IPluginBase.VideoSourceProvider => new Dictionary<string, Func<string, IVideoSource>> { };
        public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider => new Dictionary<string, Func<string, string, ISoundTrack>> { };
        public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider => new Dictionary<string, Func<string, IAudioSource>> { };
        public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider => new Dictionary<string, Func<string, IVideoWriter>> { };
        public IMessagingService MessagingQueue { get; set; }

        public Dictionary<string, Func<Guid, Guid, RenderAPIBase.ClipAndTrack.ITransform>> TransformProvider => new Dictionary<string, Func<Guid, Guid, RenderAPIBase.ClipAndTrack.ITransform>> { };

        public IClip ClipCreator(JsonElement element)
        {
            throw new NotImplementedException();
        }

        public ISoundTrack SoundTrackCreator(JsonElement element)
        {
            throw new NotImplementedException();
        }
#if WINDOWS || LINUX

        bool IPluginBase.OnLoaded(out string FailedReason)
        {
            dataRootPath = this.GetPluginDataRoot();
            ApplyConfiguration();
            FailedReason = "";
            return true;
        }
        private void ApplyConfiguration()
        {
            forceSync = Configuration.TryGetValue("forceSync", out var forceSyncStr) && bool.TryParse(forceSyncStr, out var fs) ? fs : null;
            disableWin2DRasterizer = Configuration.TryGetValue("disableWin2DRasterizer", out var disableWin2DRasterizerStr) && bool.TryParse(disableWin2DRasterizerStr, out var r) && r;

            Logger.Log("[HwAccelEnginePlugin] ILGPU accelerators will be initialized on first use.");
            Logger.Log($"[HwAccelEnginePlugin] ForceSync: {forceSync?.ToString() ?? "default"}, Disable Win2D Rasterizer: {disableWin2DRasterizer}");
        }
#elif ANDROID
        bool IPluginBase.OnLoaded(out string FailedReason)
        {
            dataRootPath = this.GetPluginDataRoot();

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
#else
        private void ApplyConfiguration() { }
#endif


    }
}
