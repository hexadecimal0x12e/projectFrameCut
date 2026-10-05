using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using System.Text.Json;
using static projectFrameCut.Shared.Logger;

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
    private readonly IWorkspace _workspace;
    private readonly MultiWindowView _view;
    private readonly WorkspaceViewContext _context;
    private readonly IWorkspaceLayoutStore? _layoutStore;
    private readonly Dictionary<string, MultiWindowItem> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkspaceExperienceDefinition> _definitions = new(StringComparer.Ordinal);
    private bool _composed;
    private WorkspaceLayoutState? _pendingLayout;
    private bool _layoutFrozen;

    public WorkspaceWindowHost(IWorkspace workspace, MultiWindowView view, WorkspaceViewContext context, IWorkspaceLayoutStore? layoutStore = null)
    {
        (_workspace, _view, _context, _layoutStore) = (workspace, view, context, layoutStore);
        _view.SizeChanged += OnViewSizeChanged;
    }

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

    public void SaveLayout() => SaveLayout(false);

    public void SaveLayout(bool freeze)
    {
        if (_layoutStore is null || !_composed || _layoutFrozen) return;
        if (freeze) _layoutFrozen = true;
        if (_pendingLayout is not null || !_view.IsVisible || _view.Width <= 0 || _view.Height <= 0) return;
        var state = new WorkspaceLayoutState
        {
            Version = 2,
            AreaWidth = _view.GetMdiArea().Width,
            AreaHeight = _view.GetMdiArea().Height,
            ActiveWindowKey = _view.ActiveWindow is { } active ? _windows.FirstOrDefault(x => ReferenceEquals(x.Value, active)).Key : null,
            Windows = _windows.Select(pair =>
            {
                var bounds = pair.Value.GetRestoreBounds();
                return new WorkspaceWindowState
                {
                    WindowKey = pair.Key,
                    IsOpen = _view.Children.Contains(pair.Value) || pair.Value.IsInStandaloneWindowMode,
                    IsVisible = pair.Value.IsVisible || pair.Value.IsMinimized,
                    IsMinimized = pair.Value.IsMinimized,
                    IsMaximized = pair.Value.IsMaximized,
                    TranslationX = bounds.X,
                    TranslationY = bounds.Y,
                    Width = bounds.Width > 0 ? bounds.Width : pair.Value.MinimumWindowWidth,
                    Height = bounds.Height > 0 ? bounds.Height : pair.Value.MinimumWindowHeight,
                    SnapZone = _view.GetSnapZone(pair.Value),
                    RelativeBounds = _view.GetRelativeSnapBounds(pair.Value),
                    PreSnapBounds = pair.Value.PreSnapBounds,
                    ZIndex = pair.Value.ZIndex
                };
            }).ToList()
        };
        _layoutStore.Write(LayoutStateKey, JsonSerializer.Serialize(state));
        LogDiagnostic($"Saved workspace layout: {state.Windows.Count} windows, area {state.AreaWidth:F0} x {state.AreaHeight:F0}.");
    }

    public bool TryRestoreLayout()
    {
        if (_layoutStore is null) return false;
        var raw = _layoutStore.Read(LayoutStateKey);
        WorkspaceLayoutState? state = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try { state = JsonSerializer.Deserialize<WorkspaceLayoutState>(raw); }
            catch (JsonException ex) { LogDiagnostic($"Invalid workspace layout: {ex.Message}"); }
        }
        if (state is null || state.Version is < 1 or > 2 || state.Windows is null
            || !state.Windows.Any(item => item is not null && !string.IsNullOrWhiteSpace(item.WindowKey) && _windows.ContainsKey(item.WindowKey))) return false;
        var restoredKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in state.Windows)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.WindowKey)) continue;
            var windowKey = item.WindowKey;
            if (!_windows.TryGetValue(windowKey, out var window)) continue;
            restoredKeys.Add(windowKey);
            if (!item.IsOpen && _definitions[windowKey].IsClosable)
            {
                if (_view.Children.Contains(window)) _view.CloseWindow(window, force: true);
                continue;
            }
            if (!_view.Children.Contains(window)) _view.AddWindow(window);
            window.ZIndex = item.ZIndex;
            window.IsVisible = item.IsVisible || !_definitions[windowKey].IsClosable;
        }
        foreach (var key in _windows.Keys.Where(key => !restoredKeys.Contains(key)))
            ApplyDefaultPlacement(key);
        _pendingLayout = state;
        RestorePendingLayout();
        return true;
    }

    private void OnViewSizeChanged(object? sender, EventArgs e) => RestorePendingLayout();

    private void RestorePendingLayout()
    {
        if (_pendingLayout is not { } state || !_view.IsVisible || _view.Width <= 0 || _view.Height <= 0) return;
        _pendingLayout = null;
        var area = _view.GetMdiArea();
        foreach (var item in state.Windows)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.WindowKey)
                || !_windows.TryGetValue(item.WindowKey, out var window) || !_view.Children.Contains(window)) continue;
            if (window.IsMinimized) window.Minimize();
            if (window.IsMaximized) window.Maximize();
            var width = double.IsFinite(item.Width) && item.Width > 0 ? item.Width : window.MinimumWindowWidth;
            var height = double.IsFinite(item.Height) && item.Height > 0 ? item.Height : window.MinimumWindowHeight;
            var x = double.IsFinite(item.TranslationX) ? Math.Max(0, item.TranslationX) : 0;
            var y = double.IsFinite(item.TranslationY) ? Math.Max(0, item.TranslationY) : 0;
            if (state.AreaWidth > 0 && state.AreaHeight > 0)
            {
                x *= area.Width / state.AreaWidth;
                y *= area.Height / state.AreaHeight;
                width *= area.Width / state.AreaWidth;
                height *= area.Height / state.AreaHeight;
            }
            _view.RestoreFloatingBounds(window, new Rect(x, y, width, height));
            if (item.RelativeBounds is { } bounds && IsValidRelativeBounds(bounds))
                _view.SnapWindowToRelativeBounds(window, bounds, bringToFront: false);
            else if (Enum.IsDefined(item.SnapZone) && item.SnapZone is not (WindowSnapZone.None or WindowSnapZone.TopCenter))
                _view.SnapWindow(window, item.SnapZone, bringToFront: false);
            if (item.PreSnapBounds is { } preSnap && double.IsFinite(preSnap.X) && double.IsFinite(preSnap.Y)
                && double.IsFinite(preSnap.Width) && double.IsFinite(preSnap.Height) && preSnap.Width > 0 && preSnap.Height > 0)
                window.PreSnapBounds = preSnap;
            if (item.IsMaximized || (state.Version == 1 && item.Width == -1 && item.Height == -1)) window.Maximize();
        }
        // Minimize after restoring all bounds: taskbar visibility changes the available area.
        foreach (var item in state.Windows)
            if (item is not null && item.IsMinimized && _windows.TryGetValue(item.WindowKey, out var window)
                && _view.Children.Contains(window) && !window.IsMinimized) window.Minimize();
        if (state.ActiveWindowKey is { } key && _windows.TryGetValue(key, out var active)
            && _view.Children.Contains(active) && !active.IsMinimized && active.IsVisible) _view.BringToFront(active);
        LogDiagnostic($"Restored workspace layout v{state.Version}: {state.Windows.Count} windows, area {area.Width:F0} x {area.Height:F0}.");
    }

    private static bool IsValidRelativeBounds(Rect bounds)
        => double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height)
            && bounds.X >= 0 && bounds.Y >= 0 && bounds.Width > 0 && bounds.Height > 0 && bounds.Right <= 1 && bounds.Bottom <= 1;

    public void ApplyDefaultLayout()
    {
        _pendingLayout = null;
        foreach (var key in _windows.Keys) ApplyDefaultPlacement(key);
        LogDiagnostic("Applied default workspace layout.");
    }

    public ValueTask DisposeAsync()
    {
        _view.SizeChanged -= OnViewSizeChanged;
        try
        {
            SaveLayout();
        }
        catch { }
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
        if (window.IsMinimized) window.Minimize();
        if (window.IsMaximized) window.Maximize();
        _view.ReleaseSnapState(window);
        window.HorizontalOptions = LayoutOptions.Start;
        window.VerticalOptions = LayoutOptions.Start;
        window.Margin = new Thickness(0);
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

    private sealed class WorkspaceLayoutState
    {
        public int Version { get; set; } = 1;
        public double AreaWidth { get; set; }
        public double AreaHeight { get; set; }
        public string? ActiveWindowKey { get; set; }
        public List<WorkspaceWindowState> Windows { get; set; } = [];
    }
    private sealed class WorkspaceWindowState
    {
        public string WindowKey { get; set; } = string.Empty;
        public bool IsOpen { get; set; } = true;
        public bool IsVisible { get; set; } = true;
        public bool IsMinimized { get; set; }
        public bool IsMaximized { get; set; }
        public WindowSnapZone SnapZone { get; set; }
        public Rect? RelativeBounds { get; set; }
        public Rect? PreSnapBounds { get; set; }
        public double TranslationX { get; set; }
        public double TranslationY { get; set; }
        public double Width { get; set; } = -1;
        public double Height { get; set; } = -1;
        public int ZIndex { get; set; }
    }
}
