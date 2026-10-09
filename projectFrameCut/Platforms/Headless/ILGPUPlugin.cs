using projectFrameCut.Render.HwAccelEngine;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.Platforms.Headless;

internal sealed class ILGPUPlugin : IPluginBase
{
    public string PluginID => "projectFrameCut.Render.HwAccelEngine";
    public int PluginAPIVersion => IPluginBase.CurrentPluginAPIVersion;
    public string Name => "ILGPU CUDA/OpenCL Accelerator Plugin";
    public string Author => "hexadecimal0x12e";
    public string Description => "ILGPU rendering for the headless client.";
    public Version Version => typeof(ILGPUPlugin).Assembly.GetName().Version ?? new(1, 7);
    public string AuthorUrl => string.Empty;
    public string? PublishingUrl => null;
    public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string> { ["IsInternalPlugin"] = "true" };
    public Dictionary<string, Dictionary<string, string>> LocalizationProvider => [];
    public Dictionary<string, Func<IEffectProvider>> EffectProviderProvider => [];
    public IReadOnlyDictionary<EffectImplementationKey, Func<IEffect>> EffectImplementationProvider => HardwareEffectFactories.Create();
    public Dictionary<string, IVideoSource> VideoSourceProvider => [];
    public Dictionary<string, string> Configuration { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString => [];
    public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider => [];
    public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider => [];
    public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider => [];
    public IMessagingService MessagingQueue { get; set; } = null!;
    public IClip ClipCreator(JsonElement element) => throw new NotSupportedException();
    public ISoundTrack SoundTrackCreator(JsonElement element) => throw new NotSupportedException();

    public bool OnLoaded(out string failedReason)
    {
        AcceleratorsManager.IsRendering = true;
        IVectorContentClip.GlobalDefaultRasterizer =
            SettingsManager.IsBoolSettingTrueOrDefault("render_enableHwAccelRasterizer", true)
                ? new projectFrameCut.Render.HwAccelEngine.VectorRasterizer.VectorToPictureHwAccel()
                : new projectFrameCut.Render.RenderAPIBase.VectorContent.CpuVectorPictureRasterizer();
        failedReason = string.Empty;
        Log("Headless ILGPU effect implementations registered.");
        return true;
    }

    public void OnClosing()
    {
        AcceleratorsManager.ReleaseResources();
        Log("Headless ILGPU resources released.");
    }
}
