using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using System.Text.Json;

namespace SomePublisher;

public static class ExamplePluginConstants
{
    public const string PluginId = "SomePublisher.MyPlugin";
}

public class MyPlugin : IPluginBase
{
    public string PluginID => ExamplePluginConstants.PluginId;
    public int PluginAPIVersion => IPluginBase.CurrentPluginAPIVersion;
    public string Name => "A usable example plugin";
    public string Author => "SomePublisher";
    public string Description => "A small plugin with one working example of every render provider.";
    public Version Version => new(5, 6, 7, 8);
    public string AuthorUrl => "https://example.com";
    public string? PublishingUrl => "https://example.com";

    public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string>
    {
        ["Example"] = "true",
        ["ProviderCount"] = "7"
    };

    public Dictionary<string, Dictionary<string, string>> LocalizationProvider => new()
    {
        ["en-US"] = new()
        {
            ["_PluginBase_Name_"] = Name,
            ["_PluginBase_Description_"] = Description,
            ["ExampleEnabled"] = "Enable example providers"
        },
        ["zh-CN"] = new()
        {
            ["_PluginBase_Name_"] = "可用示例插件",
            ["_PluginBase_Description_"] = "为每一种渲染 provider 提供一个简单实现。",
            ["ExampleEnabled"] = "启用示例 provider"
        }
    };

    public Dictionary<string, Func<IEffectProvider>> EffectProviderProvider => new()
    {
        ["ExampleInvert"] = () => new ExampleInvertEffectProvider()
    };

    public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider => new()
    {
        ["ExampleToneTrack"] = (id, name) => new ExampleToneTrack(id, name)
    };

    public Dictionary<string, Func<Guid, Guid, ITransform>> TransformProvider => new()
    {
        ["ExampleCrossfade"] = (left, right) => new ExampleCrossfadeTransform(left, right)
    };

    public Dictionary<string, Func<IComputer>> ComputerProvider => new()
    {
        ["ExampleAddComputer"] = () => new ExampleAddComputer()
    };

    public Dictionary<string, IVideoSource> VideoSourceProvider => new()
    {
        ["ExampleVideoSource"] = new ExampleVideoSource()
    };

    public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider => new()
    {
        ["ExampleAudioSource"] = path => new ExampleAudioSource(path)
    };

    public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider => new()
    {
        ["ExampleFrameStream"] = path => new ExampleFrameWriter(path)
    };

    public Dictionary<string, string> Configuration { get; set; } = new()
    {
        ["ExampleEnabled"] = "true"
    };

    public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString => new()
    {
        ["en-US"] = new() { ["ExampleEnabled"] = "Enable example providers" },
        ["zh-CN"] = new() { ["ExampleEnabled"] = "启用示例 provider" }
    };

    public ISoundTrack SoundTrackCreator(JsonElement element) => new ExampleToneTrack(
        element.TryGetProperty("Id", out var id) ? id.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString(),
        element.TryGetProperty("Name", out var name) ? name.GetString() ?? "Example tone" : "Example tone");

    public ITransform TransformCreator(JsonElement element) => new ExampleCrossfadeTransform(
        element.TryGetProperty("BindedLeftClip", out var left) ? left.GetGuid() : Guid.Empty,
        element.TryGetProperty("BindedRightClip", out var right) ? right.GetGuid() : Guid.Empty)
    {
        Duration = element.TryGetProperty("Duration", out var duration) ? duration.GetUInt32() : 30
    };
}
