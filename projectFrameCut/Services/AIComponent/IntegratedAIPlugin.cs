using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Sources;
using ITransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;
namespace projectFrameCut.Services.AIComponent;

/// <summary>
/// Built-in plugin that bridges platform-provided AI components into the
/// render and application APIs.
/// </summary>
internal sealed class IntegratedAIPlugin : IPluginBase
{
    public const string IntegratedAIPluginId = "projectFrameCut.Services.AIComponent.IntegratedAIPlugin";

    private readonly IReadOnlyList<IIntegratedAIComponent> _components;
    private readonly List<IIntegratedAIComponent> _registeredComponents = [];

    public IntegratedAIPlugin(IEnumerable<IIntegratedAIComponent> components)
    {
        _components = components.ToArray();
    }

    public string PluginID => IntegratedAIPluginId;
    public int PluginAPIVersion => IPluginBase.CurrentPluginAPIVersion;
    public string Name => "Integrated System AI";
    public string Author => "projectFrameCut";
    public string Description => "Provides operating-system AI capabilities to projectFrameCut.";
    public Version Version => new(1, 0, 0, 0);
    public string AuthorUrl => "https://github.com/hexadecimal0x12e/projectFrameCut";
    public string? PublishingUrl => null;

    public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string>
    {
        ["IsInternalPlugin"] = bool.TrueString,
        ["AIComponents"] = string.Join(", ", _components.Select(component => component.Id))
    };

    public Dictionary<string, Dictionary<string, string>> LocalizationProvider { get; } = new()
    {
        ["en-US"] = new()
        {
            ["_PluginBase_Name_"] = "Integrated System AI",
            ["_PluginBase_Author_"] = "projectFrameCut",
            ["_PluginBase_Description_"] = "Provides operating-system AI capabilities to projectFrameCut."
        },
        ["zh-CN"] = new()
        {
            ["_PluginBase_Name_"] = "集成系统 AI",
            ["_PluginBase_Author_"] = "projectFrameCut",
            ["_PluginBase_Description_"] = "将操作系统提供的 AI 能力接入 projectFrameCut。"
        }
    };

    public Dictionary<string, Func<IEffectProvider>> EffectProviderProvider { get; } = [];
    public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider { get; } = [];
    public Dictionary<string, Func<Guid, Guid, ITransform>> TransformProvider { get; } = [];
    public Dictionary<string, Func<IComputer>> ComputerProvider { get; } = [];
    public Dictionary<string, IVideoSource> VideoSourceProvider { get; } = [];
    public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider { get; } = [];
    public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider { get; } = [];
    public Dictionary<string, string> Configuration { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString { get; } = [];

    public bool OnLoaded(out string failedReason)
    {
        failedReason = string.Empty;
        _registeredComponents.Clear();

        foreach (var component in _components.Where(component => component.IsSupported))
        {
            try
            {
                component.Register();
                _registeredComponents.Add(component);
                Log($"[SystemAI] Registered integrated component: {component.Id}.");
            }
            catch (Exception ex)
            {
                Log(ex, $"register integrated AI component {component.Id}", this);
            }
        }

        return true;
    }

    public void OnClosing()
    {
        for (int index = _registeredComponents.Count - 1; index >= 0; index--)
        {
            var component = _registeredComponents[index];
            try
            {
                component.Unregister();
            }
            catch (Exception ex)
            {
                Log(ex, $"unregister integrated AI component {component.Id}", this);
            }
        }

        _registeredComponents.Clear();
    }
}
