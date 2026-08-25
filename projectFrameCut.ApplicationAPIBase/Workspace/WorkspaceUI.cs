using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using System.Text.Json;

namespace projectFrameCut.ApplicationAPIBase.Workspace;

public enum WorkspaceUIRegion { Main, Preview, Timeline, LeftPanel, RightPanel, BottomPanel, Toolbar, StatusBar }

public sealed class WorkspaceViewContext
{
    public WorkspaceViewContext(object? host = null, IServiceProvider? services = null)
        => (Host, Services) = (host, services ?? EmptyServiceProvider.Instance);
    public object? Host { get; }
    public IServiceProvider Services { get; }
    public T GetHost<T>() where T : class => Host as T ?? throw new InvalidOperationException($"The workspace UI host is not a {typeof(T).FullName}.");
}

public interface IWorkspaceModuleViewProvider
{
    string ModuleId { get; }
    WorkspaceUIRegion Region { get; }
    string? Title { get; }
    int Order { get; }
    View CreateView(IWorkspace workspace, WorkspaceViewContext context);
}

public interface IWorkspaceExperienceProvider
{
    string ModuleId { get; }
    IReadOnlyCollection<WorkspaceExperienceDefinition> GetWindows(IWorkspace workspace, WorkspaceViewContext context);
}

public sealed class WorkspaceWindowPlacement
{
    public WindowSnapZone SnapZone { get; init; } = WindowSnapZone.None;
    public double Width { get; init; } = -1;
    public double Height { get; init; } = -1;
    public double X { get; init; }
    public double Y { get; init; }
    public int ZIndex { get; init; }
}

public sealed class WorkspaceExperienceDefinition
{
    public required string WindowKey { get; init; }
    public required string ModuleId { get; init; }
    public string? Title { get; init; }
    public int Order { get; init; }
    public bool IsInitiallyVisible { get; init; } = true;
    public bool IsClosable { get; init; } = true;
    public bool IsResizable { get; init; } = true;
    public bool IsPopOutVisible { get; init; } = true;
    public bool IsNavigationVisible { get; init; } = true;
    public required Func<View> CreateContent { get; init; }
    public WorkspaceWindowPlacement DefaultPlacement { get; init; } = new();
}

public interface IWorkspaceLayoutStore
{
    string? Read(string key);
    void Write(string key, string value);
    void Remove(string key);
}

public sealed class DictionaryWorkspaceLayoutStore(IDictionary<string, string> properties) : IWorkspaceLayoutStore
{
    public string? Read(string key) => properties.TryGetValue(key, out var value) ? value : null;
    public void Write(string key, string value) => properties[key] = value;
    public void Remove(string key) => properties.Remove(key);
}

public sealed class WorkspaceWindowHost : IAsyncDisposable
{
    public const string LayoutStateKey = "__Workspace_WindowLayout_State_v1";
    public const string LegacyLayoutStateKey = "__DraftPage_MainMultiWindowView_State_v1";
    private readonly IWorkspace _workspace;
    private readonly MultiWindowView _view;
    private readonly WorkspaceViewContext _context;
    private readonly IWorkspaceLayoutStore? _layoutStore;
    private readonly Dictionary<string, MultiWindowItem> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkspaceExperienceDefinition> _definitions = new(StringComparer.Ordinal);
    private bool _composed;

    public WorkspaceWindowHost(IWorkspace workspace, MultiWindowView view, WorkspaceViewContext context, IWorkspaceLayoutStore? layoutStore = null)
        => (_workspace, _view, _context, _layoutStore) = (workspace, view, context, layoutStore);

    public IReadOnlyDictionary<string, MultiWindowItem> Windows => _windows;
    public bool WasLayoutRestored { get; private set; }
    public event EventHandler<Exception>? WindowCreationFailed;

    public void Compose(IEnumerable<IWorkspaceExperienceProvider> providers)
    {
        if (_composed) throw new InvalidOperationException("The workspace window host has already been composed.");
        var definitions = providers
            .SelectMany(provider => provider.GetWindows(_workspace, _context))
            .OrderBy(x => x.Order)
            .ThenBy(x => x.WindowKey, StringComparer.Ordinal)
            .ToList();
        foreach (var definition in definitions)
        {
            Validate(definition);
            if (!_definitions.TryAdd(definition.WindowKey, definition))
                throw new InvalidOperationException($"Duplicate Workspace WindowKey '{definition.WindowKey}'.");
            var window = CreateWindow(definition);
            _windows.Add(definition.WindowKey, window);
            if (definition.IsInitiallyVisible) _view.AddWindow(window);
        }
        _composed = true;
        WasLayoutRestored = TryRestoreLayout();
        if (!WasLayoutRestored) ApplyDefaultLayout();
    }

    public MultiWindowItem GetWindow(string windowKey) => _windows.TryGetValue(windowKey, out var window)
        ? window : throw new KeyNotFoundException($"Workspace window '{windowKey}' does not exist.");

    public void OpenWindow(string windowKey)
    {
        var window = GetWindow(windowKey);
        if (!_view.Children.Contains(window)) _view.AddWindow(window);
        window.IsVisible = true;
        _view.BringToFront(window);
    }

    public void CloseModuleWindows(string moduleId)
    {
        foreach (var pair in _definitions.Where(x => string.Equals(x.Value.ModuleId, moduleId, StringComparison.Ordinal)).ToList())
        {
            var window = _windows[pair.Key];
            if (_view.Children.Contains(window)) _view.CloseWindow(window, force: true);
            if (window.Content is IDisposable disposable) disposable.Dispose();
            _windows.Remove(pair.Key);
            _definitions.Remove(pair.Key);
        }
    }

    public void SaveLayout()
    {
        if (_layoutStore is null || !_composed) return;
        var state = new WorkspaceLayoutState
        {
            Version = 1,
            ActiveWindowKey = _view.ActiveWindow is { } active ? _windows.FirstOrDefault(x => ReferenceEquals(x.Value, active)).Key : null,
            Windows = _windows.Select(pair => new WorkspaceWindowState
            {
                WindowKey = pair.Key,
                IsOpen = _view.Children.Contains(pair.Value),
                IsVisible = pair.Value.IsVisible,
                IsMinimized = pair.Value.IsMinimized,
                TranslationX = pair.Value.TranslationX,
                TranslationY = pair.Value.TranslationY,
                Width = pair.Value.WidthRequest,
                Height = pair.Value.HeightRequest,
                ZIndex = pair.Value.ZIndex
            }).ToList()
        };
        _layoutStore.Write(LayoutStateKey, JsonSerializer.Serialize(state));
    }

    public bool TryRestoreLayout()
    {
        if (_layoutStore is null) return false;
        var raw = _layoutStore.Read(LayoutStateKey);
        WorkspaceLayoutState? state = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try { state = JsonSerializer.Deserialize<WorkspaceLayoutState>(raw); } catch { }
        }
        if (state is null)
        {
            state = TryMigrateLegacy(_layoutStore.Read(LegacyLayoutStateKey));
            if (state is not null) _layoutStore.Write(LayoutStateKey, JsonSerializer.Serialize(state));
        }
        if (state is null) return false;
        var restoredKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in state.Windows)
        {
            if (!_windows.TryGetValue(item.WindowKey, out var window)) continue;
            restoredKeys.Add(item.WindowKey);
            if (!item.IsOpen)
            {
                if (_view.Children.Contains(window)) _view.CloseWindow(window, force: true);
                continue;
            }
            if (!_view.Children.Contains(window)) _view.AddWindow(window);
            window.HorizontalOptions = LayoutOptions.Start;
            window.VerticalOptions = LayoutOptions.Start;
            window.TranslationX = Math.Max(0, item.TranslationX);
            window.TranslationY = Math.Max(0, item.TranslationY);
            window.WidthRequest = item.Width;
            window.HeightRequest = item.Height;
            window.ZIndex = item.ZIndex;
            window.IsVisible = item.IsVisible;
            if (item.IsMinimized && !window.IsMinimized) window.Minimize();
        }
        if (state.ActiveWindowKey is not null && _windows.TryGetValue(state.ActiveWindowKey, out var active) && _view.Children.Contains(active))
            _view.BringToFront(active);
        foreach (var key in _windows.Keys.Where(key => !restoredKeys.Contains(key)))
            ApplyDefaultPlacement(key);
        return true;
    }

    public void ApplyDefaultLayout()
    {
        foreach (var key in _windows.Keys) ApplyDefaultPlacement(key);
    }

    public ValueTask DisposeAsync()
    {
        SaveLayout();
        foreach (var window in _windows.Values.ToList())
            if (_view.Children.Contains(window)) _view.CloseWindow(window, force: true);
        _windows.Clear();
        _definitions.Clear();
        return ValueTask.CompletedTask;
    }

    private MultiWindowItem CreateWindow(WorkspaceExperienceDefinition definition)
    {
        View content;
        try { content = definition.CreateContent(); }
        catch (Exception ex)
        {
            WindowCreationFailed?.Invoke(this, ex);
            content = CreateErrorContent(definition, ex);
        }
        return new MultiWindowItem
        {
            Title = definition.Title ?? definition.WindowKey,
            Content = content,
            IsClosable = definition.IsClosable,
            IsResizable = definition.IsResizable,
            IsPopOutVisible = definition.IsPopOutVisible,
            IsNavigationVisible = definition.IsNavigationVisible
        };
    }

    private void ApplyDefaultPlacement(string key)
    {
        var placement = _definitions[key].DefaultPlacement;
        var window = _windows[key];
        if (placement.Width > 0) window.WidthRequest = placement.Width;
        if (placement.Height > 0) window.HeightRequest = placement.Height;
        window.TranslationX = placement.X;
        window.TranslationY = placement.Y;
        window.ZIndex = placement.ZIndex;
        if (placement.SnapZone != WindowSnapZone.None && _view.Children.Contains(window))
            _view.SnapWindow(window, placement.SnapZone, bringToFront: false);
    }

    private View CreateErrorContent(WorkspaceExperienceDefinition definition, Exception error)
    {
        var message = new Label { Text = $"Unable to create '{definition.WindowKey}'.\n{error.Message}", Margin = 16 };
        var retry = new Button { Text = "Retry", Margin = 16 };
        var panel = new VerticalStackLayout { Children = { message, retry } };
        retry.Clicked += (_, _) =>
        {
            try
            {
                var window = GetWindow(definition.WindowKey);
                window.Content = definition.CreateContent();
            }
            catch (Exception ex) { message.Text = $"Unable to create '{definition.WindowKey}'.\n{ex.Message}"; WindowCreationFailed?.Invoke(this, ex); }
        };
        return panel;
    }

    private static void Validate(WorkspaceExperienceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.WindowKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ModuleId);
        ArgumentNullException.ThrowIfNull(definition.CreateContent);
    }

    private static WorkspaceLayoutState? TryMigrateLegacy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!root.TryGetProperty("Windows", out var windows)) return null;
            var keyMap = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PreviewSubwindow"] = "preview.main",
                ["PropertiesSubwindow"] = "properties.main",
                ["AssisstantSubWindow"] = "legacy.assistant",
                ["HistorySubWindow"] = "history.main"
            };
            var result = new WorkspaceLayoutState();
            foreach (var item in windows.EnumerateArray())
            {
                if (!item.TryGetProperty("WindowKey", out var keyValue) || keyValue.GetString() is not { } oldKey || !keyMap.TryGetValue(oldKey, out var key)) continue;
                result.Windows.Add(new WorkspaceWindowState
                {
                    WindowKey = key,
                    IsOpen = !item.TryGetProperty("IsOpen", out var open) || open.GetBoolean(),
                    IsVisible = !item.TryGetProperty("IsVisible", out var visible) || visible.GetBoolean(),
                    IsMinimized = item.TryGetProperty("IsMinimized", out var minimized) && minimized.GetBoolean(),
                    TranslationX = item.TryGetProperty("TranslationX", out var x) ? x.GetDouble() : 0,
                    TranslationY = item.TryGetProperty("TranslationY", out var y) ? y.GetDouble() : 0,
                    Width = item.TryGetProperty("WidthRequest", out var width) ? width.GetDouble() : -1,
                    Height = item.TryGetProperty("HeightRequest", out var height) ? height.GetDouble() : -1,
                    ZIndex = item.TryGetProperty("ZIndex", out var z) ? z.GetInt32() : 0
                });
            }
            if (root.TryGetProperty("ActiveWindowKey", out var active) && active.GetString() is { } activeKey)
                result.ActiveWindowKey = keyMap.GetValueOrDefault(activeKey);
            return result;
        }
        catch { return null; }
    }

    private sealed class WorkspaceLayoutState
    {
        public int Version { get; set; } = 1;
        public string? ActiveWindowKey { get; set; }
        public List<WorkspaceWindowState> Windows { get; set; } = [];
    }
    private sealed class WorkspaceWindowState
    {
        public string WindowKey { get; set; } = string.Empty;
        public bool IsOpen { get; set; } = true;
        public bool IsVisible { get; set; } = true;
        public bool IsMinimized { get; set; }
        public double TranslationX { get; set; }
        public double TranslationY { get; set; }
        public double Width { get; set; } = -1;
        public double Height { get; set; } = -1;
        public int ZIndex { get; set; }
    }
}
