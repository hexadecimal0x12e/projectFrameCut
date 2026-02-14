namespace projectFrameCut.AIAssistance;

using Microsoft.Extensions.AI;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

public partial class AssistanceChatSessionsView : ContentView
{
    private readonly ObservableCollection<SessionListItem> _sessions = [];

    public AssistanceChatView? Current = null;

    public AssistanceChatSessionsView()
    {
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
        var all = AssistanceChatSessionStore.GetSessions();
        _sessions.Clear();
        foreach (AssistanceChatSession session in all)
        {
            _sessions.Add(new SessionListItem
            {
                SessionId = session.SessionId,
                Title = session.Title,
                Preview = session.LastPreview,
                UpdatedAtText = $"{session.UpdatedAt:yyyy-MM-dd HH:mm}",
            });
        }
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
        AssistanceChatSession session = AssistanceChatSessionStore.CreateSession();
        NavigateToSession(session.SessionId);
    }

    private void NavigateToSession(Guid sessionId)
    {
        var s = new AssistanceChatView(sessionId);
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
                AssistanceChatSessionStore.RenameSession(item.SessionId, newTitle);
            }
        }
        else if (action == delete)
        {
            bool confirm = await page.DisplayAlertAsync(delete, $"{Localized.HomePage_ProjectContextMenu_Delete_Confirm0(item.Title)}?", Localized._Confirm, Localized._Cancel);
            if (confirm)
            {
                AssistanceChatSessionStore.DeleteSession(item.SessionId);
            }
        }
    }

    private partial class SessionListItem : INotifyPropertyChanged
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


}
