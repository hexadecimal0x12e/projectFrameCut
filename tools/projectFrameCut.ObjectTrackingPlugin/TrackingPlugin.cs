using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;

namespace projectFrameCut.ObjectTrackingPlugin;

public sealed class TrackingPlugin : IPluginBase
{
    public const string PluginId = "projectFrameCut.ObjectTrackingPlugin.TrackingPlugin";
    public string PluginID => PluginId;
    public int PluginAPIVersion => IPluginBase.CurrentPluginAPIVersion;
    public string Name => "Object tracking";
    public string Author => "projectFrameCut";
    public string Description => "Locate a selected reference object in each frame.";
    public Version Version => new(1, 0, 0, 0);
    public string AuthorUrl => "https://github.com/projectFrameCut";
    public string? PublishingUrl => null;
    public Dictionary<string, Func<IEffectProvider>> EffectProviderProvider => new()
    {
        [TrackingProvider.ProviderType] = () => new TrackingProvider(),
    };
    public IReadOnlyDictionary<EffectImplementationKey, Func<IEffect>> EffectImplementationProvider =>
        new Dictionary<EffectImplementationKey, Func<IEffect>>
        {
            [new(TrackingProvider.ProviderType, EffectImplementType.IPicture)] = () => new TrackingEffect(),
        };
    public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider => new();
    public Dictionary<string, IVideoSource> VideoSourceProvider => new();
    public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider => new();
    public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider => new();
    public Dictionary<string, string> Configuration { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString => new();
    public Dictionary<string, Dictionary<string, string>> LocalizationProvider => new()
    {
        ["en-US"] = Labels("Object tracking", "Tracked object", "Canvas coordinates", "Image placement", "Position", "Found"),
        ["zh-CN"] = Labels("对象跟踪", "被跟踪的对象", "输出画布坐标", "图片在画布上的位置", "对象位置", "有没有这个对象"),
    };

    private static Dictionary<string, string> Labels(string name, string target, string canvas, string placement, string position, string found) => new()
    {
        ["_PluginBase_Name_"] = name,
        ["DisplayName_Effect_ObjectTracking"] = name,
        ["Effect_ObjectTracking_Target"] = target,
        ["Effect_ObjectTracking_CanvasCoordinates"] = canvas,
        ["Effect_ObjectTracking_ImagePlacement"] = placement,
        ["Effect_ObjectTracking_Position"] = position,
        ["Effect_ObjectTracking_Found"] = found,
    };
}
