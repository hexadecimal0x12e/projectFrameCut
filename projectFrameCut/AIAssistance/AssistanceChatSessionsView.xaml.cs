namespace projectFrameCut.AIAssistance;

using Microsoft.Extensions.AI;
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

    public AssistanceChatView? Current = null;

    public Func<IEnumerable<AIFunction>>? GlobalToolCallFactories;


    public AssistanceChatSessionsView() : this(null)
    {
    }

    public AssistanceChatSessionsView(string? projectPath)
    {
        _projectPath = projectPath;
        InitializeComponent();
        SessionListView.ItemsSource = _sessions;
        AssistanceChatSessionStore.SessionsChanged += AssistanceChatSessionStore_SessionsChanged;
        RefreshSessions();
        if (Parent is MultiWindowItem host)
        {
            host.OnNavigate += (s, view) =>
            {
                if (view.Next is AssistanceChatView v)
                {
                    Current = v;
                    v.ToolCallFactories = GlobalToolCallFactories;
                }
            };
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
        foreach (AssistanceChatSession session in all)
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
        var s = new AssistanceChatView(sessionId, GlobalToolCallFactories, _projectPath);
        Current = s;
        if (GetHostWindow() is MultiWindowItem host)
        {
            host.NavigateTo(s);
        }
        else
        {
            Log($"Failed to navigate to session {sessionId} because host window is not found. Parent is a {Parent?.GetType().Name}\r\n{Environment.StackTrace}", "error");
            _ = Application.Current?.Windows?[0]?.Page?.DisplayAlertAsync(Localized._Error, $"Parent is not a MultiWindowItem. Please feedback this bug.", Localized._OK);
        }
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
        if (GetHostWindow() is not MultiWindowItem page)
        {
            return;
        }

        string rename = Localized.HomePage_ProjectContextMenu_Rename;
        string delete = Localized.HomePage_ProjectContextMenu_Delete;

        string action = await page.DisplayActionSheetAsync(item.Title, Localized._Cancel, null, rename, delete);

        if (action == rename)
        {
            string newTitle = await page.DisplayPromptAsync(rename, "", Localized._Confirm, Localized._Cancel, initialValue: item.Title);
            if (!string.IsNullOrWhiteSpace(newTitle))
            {
                AssistanceChatSessionStore.RenameSession(_projectPath, item.SessionId, newTitle);
            }
        }
        else if (action == delete)
        {
            bool confirm = await page.DisplayAlertAsync(delete, $"{Localized.HomePage_ProjectContextMenu_Delete_Confirm0(item.Title)}?", Localized._Confirm, Localized._Cancel);
            if (confirm)
            {
                AssistanceChatSessionStore.DeleteSession(_projectPath, item.SessionId);
            }
        }
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
