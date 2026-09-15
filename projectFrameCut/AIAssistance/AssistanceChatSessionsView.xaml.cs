namespace projectFrameCut.AIAssistance;

using Microsoft.Extensions.AI;
using projectFrameCut.ApplicationAPIBase.Workspace;
using projectFrameCut.ApplicationAPIBase.Workspace.Modules;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

public partial class AssistanceChatSessionsView : ContentView
{
    private readonly List<SessionListItem> _allSessions = [];
    private readonly ObservableCollection<SessionListItem> _sessions = [];
    private readonly string? _projectPath;
    private readonly string? _projectName;
    private readonly IWorkspace? _workspace;
    private MultiWindowItem? _hostWindow;
    public AssistanceChatView? Current { get; private set; }

    public Func<IEnumerable<AIFunction>>? GlobalToolCallFactories;


    public AssistanceChatSessionsView() : this(null, null, null)
    {
    }

    public AssistanceChatSessionsView(string? projectPath, string? projectName, IWorkspace? workspace = null)
    {
        _projectPath = projectPath;
        _projectName = projectName;
        _workspace = workspace;
        InitializeComponent();
        SessionListView.ItemsSource = _sessions;
        AssistanceChatSessionStore.SessionsChanged += AssistanceChatSessionStore_SessionsChanged;
        RefreshSessions();
        if (_workspace is not null
            && _workspace.TryGetModule<AssistanceModule>(out var assistanceModule)
            && assistanceModule is not null)
        {
            assistanceModule.BindMessageHandler(HandleWorkspaceMessageAsync);
        }
    }

    protected override void OnParentSet()
    {
        base.OnParentSet();
        var host = GetHostWindow();
        if (host is null || ReferenceEquals(host, _hostWindow)) return;

        if (_hostWindow is not null) _hostWindow.OnNavigate -= HostWindow_OnNavigate;
        _hostWindow = host;
        _hostWindow.OnNavigate += HostWindow_OnNavigate;
    }

    private void HostWindow_OnNavigate(object? sender, (View Next, View Current) navigation)
    {
        if (navigation.Next is AssistanceChatView view)
        {
            view.ToolCallFactories = GlobalToolCallFactories;
            SetCurrent(view);
        }
        else if (navigation.Next is AssistanceChatSessionsView)
        {
            SetCurrent(null);
        }
    }

    private void AssistanceChatSessionStore_SessionsChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(RefreshSessions);
    }

    private void RefreshSessions()
    {
        var all = AssistanceChatSessionStore.GetSessions(_projectPath);
        _allSessions.Clear();
        foreach (AssistanceChatSession session in all.Where(c => !c.IsSubAgent))
        {
            _allSessions.Add(new SessionListItem
            {
                SessionId = session.SessionId,
                Title = session.Title,
                Preview = session.LastPreview,
                UpdatedAtText = $"{session.UpdatedAt:yyyy-MM-dd HH:mm}",
            });
        }

        ApplyFilter();
    }

    private void SessionListView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.Count == 0 || e.CurrentSelection[0] is not SessionListItem item)
        {
            return;
        }

        SessionListView.SelectedItem = null;
        NavigateToSession(item.SessionId);
    }

    private void NewSessionButton_Clicked(object? sender, EventArgs e)
    {
        AssistanceChatSession session = AssistanceChatSessionStore.CreateSession(_projectPath);
        NavigateToSession(session.SessionId);
    }

    private void SessionSearchBar_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var keyword = (SessionSearchBar.Text ?? string.Empty).Trim();
        IEnumerable<SessionListItem> src = _allSessions;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            src = src.Where(s =>
                s.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || s.Preview.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        _sessions.Clear();
        foreach (var item in src)
        {
            _sessions.Add(item);
        }
    }

    private void NavigateToSession(Guid sessionId)
    {
        var s = new AssistanceChatView(sessionId, GlobalToolCallFactories, _projectPath, _projectName, workspace: _workspace);
        SetCurrent(s);
        if (GetHostWindow() is MultiWindowItem host)
        {
            host.NavigateTo(s);
        }
        else if (Window?.Page?.Navigation is INavigation nav)
        {
            // Fallback: push via NavigationPage (covers standalone Assistant P and pushed pages in pop-out)
            var content = new ContentPage { Content = s, Title = "" };
            NavigationPage.SetHasNavigationBar(content, false);
            nav.PushAsync(content);
        }
        else
        {
            Log($"Failed to navigate to session {sessionId} because host window is not found. Parent is a {Parent?.GetType().Name}\r\n{Environment.StackTrace}", "error");
            _ = Application.Current?.Windows?[0]?.Page?.DisplayAlertAsync(Localized._Error, $"Parent is not a valid window. Please feedback this bug.", Localized._OK);
        }
    }

    private void SetCurrent(AssistanceChatView? view)
    {
        Current = view;
        if (_workspace is not null
            && _workspace.TryGetModule<AssistanceModule>(out var module)
            && module is not null)
            module.SetActiveSession(view?.SessionId);
    }

    private async Task<WorkspaceAssistanceMessageResponse> HandleWorkspaceMessageAsync(
        WorkspaceAssistanceMessageRequest request,
        CancellationToken cancellationToken)
    {
        AssistanceChatView target = await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (Current is { } current) return current;

            var session = AssistanceChatSessionStore.CreateSession(_projectPath);
            var view = new AssistanceChatView(
                session.SessionId,
                GlobalToolCallFactories,
                _projectPath,
                _projectName,
                workspace: _workspace);
            SetCurrent(view);
            GetHostWindow()?.NavigateTo(view);
            return view;
        });

        var text = await target.ReceiveWorkspaceMessageAsync(request, cancellationToken);
        return new WorkspaceAssistanceMessageResponse
        {
            SessionId = target.SessionId,
            SourceModuleId = request.SourceModuleId,
            Text = text,
        };
    }

    private MultiWindowItem? GetHostWindow()
    {
        Element? current = this;
        while (current is not null)
        {
            if (current is MultiWindowItem window)
            {
                return window;
            }

            current = current.Parent;
        }

        return null;
    }

    private void Border_Loaded(object sender, EventArgs e)
    {
        if (sender is Border b)
        {
            UIServices.RegisterSelectOrContextMenu
                (b,
                OnContextMenuClick: async () =>
                {
                    if (b.BindingContext is SessionListItem item)
                    {
                        await ShowContextMenu(item);
                    }
                }
                );

        }
    }

    private async Task ShowContextMenu(SessionListItem item)
    {
        string rename = Localized.HomePage_ProjectContextMenu_Rename;
        string delete = Localized.HomePage_ProjectContextMenu_Delete;
        string branch = Localized.AIAssistant_ChatView_BranchSession;

        string action = await DisplayActionSheetAsync(item.Title, Localized._Cancel, null, rename, delete, branch);

        if (action == rename)
        {
            string newTitle = await DisplayPromptAsync(rename, "", Localized._Confirm, Localized._Cancel, initialValue: item.Title);
            if (!string.IsNullOrWhiteSpace(newTitle))
            {
                AssistanceChatSessionStore.RenameSession(_projectPath, item.SessionId, newTitle);
            }
        }
        else if (action == delete)
        {
            bool confirm = await DisplayAlertAsync(delete, $"{Localized.HomePage_ProjectContextMenu_Delete_Confirm0(item.Title)}?", Localized._Confirm, Localized._Cancel);
            if (confirm)
            {
                AssistanceChatSessionStore.DeleteSession(_projectPath, item.SessionId);
            }
        }
        else if (action == branch)
        {
            AssistanceChatSession? source = AssistanceChatSessionStore.GetSession(_projectPath, item.SessionId);
            if (source is not null)
            {
                var newSession = AssistanceChatSessionStore.ForkSession(
                    _projectPath, item.SessionId,
                    source.Messages, source.History);
                NavigateToSession(newSession.SessionId);
            }
        }
    }

    /// <summary>
    /// 显示一个确认对话框（接受/取消），返回用户选择。
    /// 优先查找父 MultiWindowItem，回退到根窗口 Page。
    /// </summary>
    private async Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel)
    {
        if (GetHostWindow() is MultiWindowItem host)
            return await host.DisplayAlertAsync(title, message, accept, cancel);

        if (Window.Page is Page page1)
            return await page1.DisplayAlertAsync(title, message, accept, cancel);

        if (Application.Current?.Windows?[0]?.Page is Page page)
            return await page.DisplayAlertAsync(title, message, accept, cancel);

        LogDiagnostic($"Unable to display confirm '{title}': no dialog host available.");
        return false;
    }

    /// <summary>
    /// 显示一个输入对话框，返回用户输入的文本。
    /// 优先查找父 MultiWindowItem，回退到根窗口 Page。
    /// </summary>
    private async Task<string?> DisplayPromptAsync(
        string title, string message,
        string accept = "OK", string cancel = "Cancel",
        string? placeholder = null, int maxLength = -1,
        Keyboard? keyboard = null, string? initialValue = "")
    {
        if (GetHostWindow() is MultiWindowItem host)
            return await host.DisplayPromptAsync(title, message, accept, cancel, placeholder!, maxLength, keyboard!, initialValue!);

        if (Window.Page is Page page1)
            return await page1.DisplayPromptAsync(title, message, accept, cancel, placeholder!, maxLength, keyboard!, initialValue!);


        if (Application.Current?.Windows?[0]?.Page is Page page)
            return await page.DisplayPromptAsync(title, message, accept, cancel, placeholder!, maxLength, keyboard!, initialValue!);

        LogDiagnostic($"Unable to display prompt '{title}': no dialog host available.");
        return null;
    }

    /// <summary>
    /// 显示一个操作列表，返回用户选择的按钮文本。
    /// 优先查找父 MultiWindowItem，回退到根窗口 Page。
    /// </summary>
    private async Task<string?> DisplayActionSheetAsync(string title, string cancel, string? destruction, params string[] buttons)
    {
        if (GetHostWindow() is MultiWindowItem host)
            return await host.DisplayActionSheetAsync(title, cancel, destruction, buttons);

        if (Window.Page is Page page1)
            return await page1.DisplayActionSheetAsync(title, cancel, destruction, buttons);

        if (Application.Current?.Windows?[0]?.Page is Page page2)
            return await page2.DisplayActionSheetAsync(title, cancel, destruction, buttons);

        LogDiagnostic($"Unable to display action sheet '{title}': no dialog host available.");
        return null;
    }



}


public partial class SessionListItem : INotifyPropertyChanged
{
    public Guid SessionId { get; init; }

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value;
            OnPropertyChanged();
        }
    }

    private string _preview = string.Empty;
    public string Preview
    {
        get => _preview;
        set
        {
            if (_preview == value)
            {
                return;
            }

            _preview = value;
            OnPropertyChanged();
        }
    }

    private string _updatedAtText = string.Empty;
    public string UpdatedAtText
    {
        get => _updatedAtText;
        set
        {
            if (_updatedAtText == value)
            {
                return;
            }

            _updatedAtText = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
