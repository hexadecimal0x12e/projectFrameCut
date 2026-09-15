using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Shared;
using System.Text.Json;
using RenderTransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;

namespace projectFrameCut.Services;

internal class IsolatedPluginProxy : IPluginBase
{
    protected readonly IPluginBase Inner;
    private readonly PluginIsolationClient _client;

    public IsolatedPluginProxy(IPluginBase inner, PluginIsolationClient client)
    {
        Inner = inner;
        _client = client;
        EffectProviderProvider = inner.EffectProviderProvider
            .Where(x => !IsPictureEffect(x.Value().TypeOfEffect))
            .ToDictionary();
        foreach (var item in client.CreateEffectProviders()) EffectProviderProvider[item.Key] = item.Value;
        VideoSourceProvider = client.CreateVideoSources();
        AudioSourceProvider = client.CreateAudioSources();
        VideoWriterProvider = client.CreateVideoWriters();
        TransformProvider = client.CreateTransforms();
        ComputerProvider = client.CreateComputers();
        SoundTrackProvider = client.CreateSoundTracks();
    }

    public string PluginID => Inner.PluginID;
    public int PluginAPIVersion => Inner.PluginAPIVersion;
    public int PluginAPIMinorVersion => Inner.PluginAPIMinorVersion;
    public string Name => Inner.Name;
    public string Author => Inner.Author;
    public string Description => Inner.Description;
    public Version Version => Inner.Version;
    public string AuthorUrl => Inner.AuthorUrl;
    public string? PublishingUrl => Inner.PublishingUrl;
    public IReadOnlyDictionary<string, string> Properties => Inner.Properties;
    public Dictionary<string, Dictionary<string, string>> LocalizationProvider => Inner.LocalizationProvider;
    public Dictionary<string, Func<IEffectProvider>> EffectProviderProvider { get; }
    public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider { get; }
    public Dictionary<string, Func<Guid, Guid, RenderTransform>> TransformProvider { get; }
    public Dictionary<string, Func<IComputer>> ComputerProvider { get; }
    public Dictionary<string, IVideoSource> VideoSourceProvider { get; }
    public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider { get; }
    public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider { get; }
    public Dictionary<string, string> Configuration { get => Inner.Configuration; set => Inner.Configuration = value; }
    public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString => Inner.ConfigurationDisplayString;

    public string? ReadLocalizationItem(string key, string locate) => Inner.ReadLocalizationItem(key, locate);
    public IClip ClipCreator(JsonElement element) => _client.CreateClip(element);
    public ISoundTrack SoundTrackCreator(JsonElement element) => _client.RestoreSoundTrack(element);
    public RenderTransform TransformCreator(JsonElement element) => _client.RestoreTransform(element);
    public IVectorComponent VectComponentCreator(JsonElement element) => _client.CreateVectorComponent(element);
    public IEffect EffectCreator(EffectAndMixtureJSONStructure structure, EffectImplementType implementType = EffectImplementType.NotSpecified)
    {
        if (!EffectProviderProvider.TryGetValue(structure.TypeName, out var factory)) throw new KeyNotFoundException($"Remote effect provider '{structure.TypeName}' was not found.");
        var provider = factory();
        if (structure.IsContinuousEffect) provider.MetaData[IEffectProvider.IsContinuousEffectParameterKey] = true;
        var effect = implementType != EffectImplementType.NotSpecified
            ? provider.RestoreInstance(implementType, structure.Parameters)
            : provider.RestoreInstanceWithDefaultType(structure.Parameters);
        effect.Name = structure.Name;
        effect.BindedEffectProvidingSystemID = structure.BindedEffectGroupID;
        effect.RelativeWidth = structure.RelativeWidth;
        effect.RelativeHeight = structure.RelativeHeight;
        effect.Enabled = structure.Enabled;
        effect.Index = structure.Index;
        effect.Id = structure.Id ?? effect.Id;
        return effect;
    }

    public IVideoSource VideoSourceCreator(string filePath) => CreateVideoSource(filePath, null);
    public IVideoSource VideoSourceCreator(string filePath, string decoderName) => CreateVideoSource(filePath, decoderName);
    public IAudioSource AudioSourceCreator(string filePath) => Inner.AudioSourceCreator(filePath);
    public IAudioSource AudioSourceCreator(string filePath, string decoderName) => Inner.AudioSourceCreator(filePath, decoderName);
    public bool OnLoaded(out string failedReason) { failedReason = string.Empty; return true; }
    public ProjectJSONStructure? OnProjectLoad(ProjectJSONStructure project) => Inner.OnProjectLoad(project);
    public ProjectJSONStructure? OnProjectSave(ProjectJSONStructure project) => Inner.OnProjectSave(project);
    public ProjectJSONStructure? OnProjectClose(ProjectJSONStructure project) => Inner.OnProjectClose(project);

    public virtual void OnClosing()
    {
        try { _client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        finally { Inner.OnClosing(); }
    }

    private static bool IsPictureEffect(EffectType type) => type is EffectType.NormalEffect or EffectType.ContinuousEffect or EffectType.MixtureProvider or EffectType.SourceReplacement;

    private IVideoSource CreateVideoSource(string path, string? decoder)
    {
        IEnumerable<IVideoSource> candidates = decoder is null
            ? VideoSourceProvider.Values.OrderByDescending(x => x.PreferredExtension.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            : VideoSourceProvider.TryGetValue(decoder, out var selected) ? [selected] : [];
        foreach (var candidate in candidates)
        {
            IVideoSource? source = null;
            try
            {
                source = candidate.CreateNew(path);
                if (source.TryInitialize()) return source;
            }
            catch { }
            source?.Dispose();
        }
        throw new NotSupportedException(decoder is null ? $"No isolated video source supports '{path}'." : $"Isolated video source '{decoder}' was not found or failed to initialize.");
    }
}

internal sealed class ExternalPluginProxy : IPluginBase
{
    private readonly PluginMetadata _metadata;
    private readonly PluginIsolationClient _client;
    private Dictionary<string, string> _configuration;

    public ExternalPluginProxy(PluginMetadata metadata, PluginIsolationClient client)
    {
        _metadata = metadata;
        _client = client;
        var descriptor = client.Descriptor;
        if (!string.Equals(descriptor.PluginId, metadata.PluginID, StringComparison.Ordinal) ||
            descriptor.PluginApiVersion != metadata.PluginAPIVersion ||
            descriptor.PluginApiMinorVersion != metadata.PluginAPIMinorVersion ||
            !Version.TryParse(descriptor.Version, out var version) || version != metadata.Version ||
            !string.Equals(descriptor.Name, metadata.Name, StringComparison.Ordinal) ||
            !string.Equals(descriptor.Author, metadata.Author, StringComparison.Ordinal) ||
            !string.Equals(descriptor.Description, metadata.Description, StringComparison.Ordinal) ||
            !string.Equals(descriptor.AuthorUrl, metadata.AuthorUrl, StringComparison.Ordinal) ||
            !string.Equals(string.IsNullOrEmpty(descriptor.PublishingUrl) ? null : descriptor.PublishingUrl, metadata.PublishingUrl, StringComparison.Ordinal))
            throw new InvalidDataException("The external backend descriptor does not match the signed plugin metadata.");
        var capabilities = ExternalPluginCapabilities.None;
        if (descriptor.Providers.Count > 0) capabilities |= ExternalPluginCapabilities.Effects;
        if (descriptor.VideoSources.Count > 0) capabilities |= ExternalPluginCapabilities.VideoSources;
        if (descriptor.AudioSources.Count > 0) capabilities |= ExternalPluginCapabilities.AudioSources;
        if (descriptor.SoundTracks.Count > 0) capabilities |= ExternalPluginCapabilities.SoundTracks;
        if (descriptor.Transforms.Count > 0) capabilities |= ExternalPluginCapabilities.Transforms;
        if (descriptor.Computers.Count > 0) capabilities |= ExternalPluginCapabilities.Computers;
        if (descriptor.VideoWriters.Count > 0) capabilities |= ExternalPluginCapabilities.VideoWriters;
        if (descriptor.ProvidesClips) capabilities |= ExternalPluginCapabilities.Clips;
        if (descriptor.ProvidesVectorComponents) capabilities |= ExternalPluginCapabilities.VectorComponents;
        if (capabilities != metadata.ExternalBackend!.Capabilities)
            throw new InvalidDataException($"The external backend capabilities '{capabilities}' do not match the signed declaration '{metadata.ExternalBackend.Capabilities}'.");
        Properties = descriptor.Properties;
        LocalizationProvider = descriptor.Localization.ToDictionary(x => x.Key, x => x.Value.Values);
        ConfigurationDisplayString = descriptor.ConfigurationDisplayStrings.ToDictionary(x => x.Key, x => x.Value.Values);
        _configuration = descriptor.Configuration;
        EffectProviderProvider = client.CreateEffectProviders();
        VideoSourceProvider = client.CreateVideoSources();
        AudioSourceProvider = client.CreateAudioSources();
        VideoWriterProvider = client.CreateVideoWriters();
        TransformProvider = client.CreateTransforms();
        ComputerProvider = client.CreateComputers();
        SoundTrackProvider = client.CreateSoundTracks();
    }

    public string PluginID => _metadata.PluginID;
    public int PluginAPIVersion => _metadata.PluginAPIVersion;
    public int PluginAPIMinorVersion => _metadata.PluginAPIMinorVersion;
    public string Name => _metadata.Name;
    public string Author => _metadata.Author;
    public string Description => _metadata.Description;
    public Version Version => _metadata.Version;
    public string AuthorUrl => _metadata.AuthorUrl;
    public string? PublishingUrl => _metadata.PublishingUrl;
    public IReadOnlyDictionary<string, string> Properties { get; }
    public Dictionary<string, Dictionary<string, string>> LocalizationProvider { get; }
    public Dictionary<string, Func<IEffectProvider>> EffectProviderProvider { get; }
    public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider { get; }
    public Dictionary<string, Func<Guid, Guid, RenderTransform>> TransformProvider { get; }
    public Dictionary<string, Func<IComputer>> ComputerProvider { get; }
    public Dictionary<string, IVideoSource> VideoSourceProvider { get; }
    public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider { get; }
    public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider { get; }
    public Dictionary<string, string> Configuration
    {
        get => _configuration;
        set
        {
            _configuration = value ?? [];
            _client.UpdateConfigurationAsync(_configuration).AsTask().GetAwaiter().GetResult();
        }
    }
    public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString { get; }

    public ProjectJSONStructure? OnProjectLoad(ProjectJSONStructure project) => InvokeProject(RenderOperation.IsolationPluginProjectLoad, project);
    public ProjectJSONStructure? OnProjectSave(ProjectJSONStructure project) => InvokeProject(RenderOperation.IsolationPluginProjectSave, project);
    public ProjectJSONStructure? OnProjectClose(ProjectJSONStructure project) => InvokeProject(RenderOperation.IsolationPluginProjectClose, project);
    public void OnClosing() => _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    public IClip ClipCreator(JsonElement element) => _client.CreateClip(element);
    public ISoundTrack SoundTrackCreator(JsonElement element) => _client.RestoreSoundTrack(element);
    public RenderTransform TransformCreator(JsonElement element) => _client.RestoreTransform(element);
    public IVectorComponent VectComponentCreator(JsonElement element) => _client.CreateVectorComponent(element);

    private ProjectJSONStructure? InvokeProject(RenderOperation operation, ProjectJSONStructure project)
    {
        var json = _client.InvokeProjectLifecycleAsync(operation, JsonSerializer.Serialize(project)).AsTask().GetAwaiter().GetResult();
        return json is null ? null : JsonSerializer.Deserialize<ProjectJSONStructure>(json);
    }
}

internal sealed class IsolatedApplicationPluginProxy : IsolatedPluginProxy, IApplicationPluginBase
{
    private readonly IApplicationPluginBase _app;

    public IsolatedApplicationPluginProxy(IApplicationPluginBase inner, PluginIsolationClient client) : base(inner, client) => _app = inner;

    public int AppLevelPluginAPIVersion => _app.AppLevelPluginAPIVersion;
    public Dictionary<string, Func<IEffectProvider, IEffectProviderUIProvider>> EffectProviderUIProvider => _app.EffectProviderUIProvider;
    public Dictionary<string, Func<ITextClipStyleProvider>> TextClipStyleProvider => _app.TextClipStyleProvider;
    public Dictionary<string, Func<IVectorComponentHandler>> VectorComponentHandlerProvider => _app.VectorComponentHandlerProvider;
    public IEffectProviderUIProvider? GetDefaultEffectProviderUIProvider(IEffectProvider source) => _app.GetDefaultEffectProviderUIProvider(source);
    public View? SettingPageProvider(ref IApplicationPluginBase instance)
    {
        IApplicationPluginBase inner = _app;
        return _app.SettingPageProvider(ref inner);
    }
    public void OnApplicationPluginLoaded() => _app.OnApplicationPluginLoaded();
    public void InjectUI(IDraftPage draftPage) => _app.InjectUI(draftPage);
    public List<MenuFlyoutItem> GetMenuItems(IDraftPage page) => _app.GetMenuItems(page);
}

internal sealed class RemoteProjectPluginProxy : IApplicationPluginBase, IRemoteProjectPluginTools
{
    private readonly PluginMetadata _metadata;
    private readonly PluginIsolationClient _client;

    private readonly ProjectPluginDeclaration _declaration;

    public RemoteProjectPluginProxy(PluginMetadata metadata, PluginIsolationClient client, ProjectPluginDeclaration declaration, Dictionary<string, string> configuration)
    {
        _metadata = metadata;
        _client = client;
        _declaration = declaration;
        Configuration = configuration;
        var declaredTools = declaration.Tools.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var undeclared = client.ProjectTools.Except(declaredTools, StringComparer.Ordinal).ToArray();
        if (undeclared.Length > 0)
            throw new InvalidDataException($"The plugin exposes tools that are absent from its signed declaration: {string.Join(", ", undeclared)}.");
        var missing = declaredTools.Except(client.ProjectTools, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"The signed declaration contains tools that the plugin does not expose: {string.Join(", ", missing)}.");
        if (client.ProjectTools.Count > 0 && !declaration.Capabilities.HasFlag(ProjectPluginCapability.Tools))
            throw new InvalidDataException("The plugin exposes tools without declaring the Tools capability.");
        EffectProviderProvider = client.CreateEffectProviders();
        VideoSourceProvider = client.CreateVideoSources();
        if (EffectProviderProvider.Count > 0 && !declaration.Capabilities.HasFlag(ProjectPluginCapability.Effects))
            throw new InvalidDataException("The plugin exposes effects without declaring the Effects capability.");
        if (VideoSourceProvider.Count > 0 && !declaration.Capabilities.HasFlag(ProjectPluginCapability.VideoSources))
            throw new InvalidDataException("The plugin exposes video sources without declaring the VideoSources capability.");
    }

    public string PluginID => _metadata.PluginID;
    public int PluginAPIVersion => _metadata.PluginAPIVersion;
    public int PluginAPIMinorVersion => _metadata.PluginAPIMinorVersion;
    public string Name => _metadata.Name;
    public string Author => _metadata.Author;
    public string Description => _metadata.Description;
    public Version Version => _metadata.Version;
    public string AuthorUrl => _metadata.AuthorUrl;
    public string? PublishingUrl => _metadata.PublishingUrl;
    public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string>
    {
        ["IsProjectPlugin"] = bool.TrueString,
        ["IsIsolated"] = bool.TrueString,
    };
    public Dictionary<string, Dictionary<string, string>> LocalizationProvider { get; } = [];
    public Dictionary<string, Func<IEffectProvider>> EffectProviderProvider { get; }
    public Dictionary<string, Func<string, string, ISoundTrack>> SoundTrackProvider { get; } = [];
    public Dictionary<string, Func<Guid, Guid, RenderTransform>> TransformProvider { get; } = [];
    public Dictionary<string, Func<IComputer>> ComputerProvider { get; } = [];
    public Dictionary<string, IVideoSource> VideoSourceProvider { get; }
    public Dictionary<string, Func<string, IAudioSource>> AudioSourceProvider { get; } = [];
    public Dictionary<string, Func<string, IVideoWriter>> VideoWriterProvider { get; } = [];
    public Dictionary<string, string> Configuration { get; set; }
    public Dictionary<string, Dictionary<string, string>> ConfigurationDisplayString { get; } = [];
    public int AppLevelPluginAPIVersion => IApplicationPluginBase.CurrentAppLevelPluginAPIVersion;
    public Dictionary<string, Func<IEffectProvider, IEffectProviderUIProvider>> EffectProviderUIProvider { get; } = [];
    public Dictionary<string, Func<ITextClipStyleProvider>> TextClipStyleProvider { get; } = [];
    public Dictionary<string, Func<IVectorComponentHandler>> VectorComponentHandlerProvider { get; } = [];
    public IReadOnlyList<ProjectPluginToolDeclaration> ToolDeclarations => _declaration.Tools;

    public IEffectProviderUIProvider? GetDefaultEffectProviderUIProvider(IEffectProvider source) => new EffectProviderUI(source);
    public View? SettingPageProvider(ref IApplicationPluginBase instance)
    {
        if (!_declaration.Capabilities.HasFlag(ProjectPluginCapability.Settings) || _declaration.Settings.Count == 0) return null;
        return new ScrollView { Content = BuildFields(_declaration.Settings, string.Empty, true) };
    }

    private VerticalStackLayout BuildFields(IEnumerable<ProjectPluginSettingDeclaration> fields, string keyPrefix, bool updateWorker)
    {
        var layout = new VerticalStackLayout { Spacing = 12, Padding = 16 };
        foreach (var setting in fields)
        {
            var key = keyPrefix + setting.Id;
            layout.Add(new Label { Text = setting.Title, FontAttributes = FontAttributes.Bold });
            if (!string.IsNullOrWhiteSpace(setting.Description)) layout.Add(new Label { Text = setting.Description, Opacity = .7 });
            if (!Configuration.TryGetValue(key, out var value)) Configuration[key] = value = setting.DefaultValue;
            switch (setting.Kind)
            {
                case ProjectPluginSettingKind.Boolean:
                    var toggle = new Switch { IsToggled = bool.TryParse(value, out var enabled) && enabled };
                    toggle.Toggled += async (_, e) =>
                    {
                        Configuration[key] = e.Value.ToString();
                        if (updateWorker) await UpdateConfigurationAsync();
                    };
                    layout.Add(toggle);
                    break;
                case ProjectPluginSettingKind.Choice:
                    var picker = new Picker { ItemsSource = setting.Options };
                    picker.SelectedIndex = Math.Max(0, setting.Options.IndexOf(value ?? setting.DefaultValue));
                    picker.SelectedIndexChanged += async (_, _) =>
                    {
                        if (picker.SelectedItem is string selected)
                        {
                            Configuration[key] = selected;
                            if (updateWorker) await UpdateConfigurationAsync();
                        }
                    };
                    layout.Add(picker);
                    break;
                default:
                    var entry = new Entry { Text = value ?? setting.DefaultValue, Keyboard = setting.Kind == ProjectPluginSettingKind.Number ? Keyboard.Numeric : Keyboard.Default };
                    entry.Unfocused += async (_, _) =>
                    {
                        if (setting.Kind != ProjectPluginSettingKind.Number)
                        {
                            Configuration[key] = entry.Text ?? string.Empty;
                            if (updateWorker) await UpdateConfigurationAsync();
                            return;
                        }
                        if (!double.TryParse(entry.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var number) &&
                            !double.TryParse(entry.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number))
                        {
                            entry.Text = Configuration[key];
                            return;
                        }
                        Configuration[key] = Math.Clamp(number, setting.Minimum ?? double.MinValue, setting.Maximum ?? double.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        entry.Text = Configuration[key];
                        if (updateWorker) await UpdateConfigurationAsync();
                    };
                    layout.Add(entry);
                    break;
            }
        }
        return layout;
    }

    private async Task UpdateConfigurationAsync()
    {
        try { await _client.UpdateConfigurationAsync(Configuration); }
        catch (Exception ex) { Logger.Log(ex, $"update project plugin '{PluginID}' configuration", this); }
    }
    public List<MenuFlyoutItem> GetMenuItems(IDraftPage page)
    {
        var result = new List<MenuFlyoutItem>();
        if (_declaration.Capabilities.HasFlag(ProjectPluginCapability.Menus))
        {
            foreach (var declaration in _declaration.Menus)
            {
                var menu = new MenuFlyoutItem { Text = declaration.Title };
                menu.Clicked += async (_, _) =>
                {
                    try
                    {
                        var output = await InvokeProjectToolAsync(declaration.ToolId, "{}");
                        if (Application.Current?.Windows.FirstOrDefault()?.Page is Page p && !string.IsNullOrWhiteSpace(output))
                            await p.DisplayAlertAsync(declaration.Title, output, "OK");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(ex, $"invoke project plugin menu '{declaration.Id}'", this);
                    }
                };
                result.Add(menu);
            }
        }
        if (_declaration.Capabilities.HasFlag(ProjectPluginCapability.PropertyPanels))
        {
            foreach (var panel in _declaration.PropertyPanels)
            {
                var menu = new MenuFlyoutItem { Text = panel.Title };
                menu.Clicked += async (_, _) =>
                {
                    if (Application.Current?.Windows.FirstOrDefault()?.Page is Page p)
                        await p.Navigation.PushAsync(CreatePropertyPanelPage(panel));
                };
                result.Add(menu);
            }
        }
        return result;
    }

    private Page CreatePropertyPanelPage(ProjectPluginPanelDeclaration panel)
    {
        var prefix = $"$panel:{panel.Id}:";
        var layout = BuildFields(panel.Fields, prefix, false);
        if (!string.IsNullOrWhiteSpace(panel.Description))
            layout.Children.Insert(0, new Label { Text = panel.Description, Opacity = .7 });
        var run = new Button { Text = panel.Title };
        run.Clicked += async (_, _) =>
        {
            try
            {
                var input = panel.Fields.ToDictionary(x => x.Id, x => ReadPanelValue(x, Configuration[prefix + x.Id]), StringComparer.Ordinal);
                var output = await InvokeProjectToolAsync(panel.ToolId, JsonSerializer.Serialize(input));
                if (!string.IsNullOrWhiteSpace(output) && Application.Current?.Windows.FirstOrDefault()?.Page is Page p)
                    await p.DisplayAlertAsync(panel.Title, output, "OK");
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"invoke project plugin property panel '{panel.Id}'", this);
            }
        };
        layout.Add(run);
        return new ContentPage { Title = panel.Title, Content = new ScrollView { Content = layout } };
    }

    private static object? ReadPanelValue(ProjectPluginSettingDeclaration field, string value) => field.Kind switch
    {
        ProjectPluginSettingKind.Boolean => bool.TryParse(value, out var boolValue) && boolValue,
        ProjectPluginSettingKind.Number => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var numberValue) ? numberValue : null,
        _ => value,
    };
    public ValueTask<string> InvokeProjectToolAsync(string toolId, string inputJson, CancellationToken cancellationToken = default)
    {
        if (!_declaration.Tools.Any(x => string.Equals(x.Id, toolId, StringComparison.Ordinal)))
            throw new KeyNotFoundException($"Project tool '{toolId}' is absent from the signed declaration.");
        return _client.InvokeProjectToolAsync(toolId, inputJson, cancellationToken);
    }
    public IClip ClipCreator(JsonElement element) => throw new NotSupportedException("Project plugins cannot create clips in the host process.");
    public ISoundTrack SoundTrackCreator(JsonElement element) => throw new NotSupportedException("Project plugins cannot create sound tracks in the host process.");
    public RenderTransform TransformCreator(JsonElement element) => throw new NotSupportedException("Project plugins cannot create transforms in the host process.");
    public IVectorComponent VectComponentCreator(JsonElement element) => throw new NotSupportedException("Project plugins cannot create vector components in the host process.");
    public IAudioSource AudioSourceCreator(string filePath) => throw new NotSupportedException("Project plugins cannot create audio sources in the host process.");
    public IAudioSource AudioSourceCreator(string filePath, string decoderName) => throw new NotSupportedException("Project plugins cannot create audio sources in the host process.");
    public ProjectJSONStructure? OnProjectLoad(ProjectJSONStructure project) => null;
    public ProjectJSONStructure? OnProjectSave(ProjectJSONStructure project) => null;
    public ProjectJSONStructure? OnProjectClose(ProjectJSONStructure project) => null;
    public bool OnLoaded(out string failedReason) { failedReason = string.Empty; return true; }
    public void OnClosing() => _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
