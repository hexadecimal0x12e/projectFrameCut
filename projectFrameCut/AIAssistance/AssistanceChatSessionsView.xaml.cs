namespace projectFrameCut.AIAssistance;

using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

public partial class AssistanceChatSessionsView : ContentView
{
    private readonly ObservableCollection<SessionListItem> _sessions = [];

    public AssistanceChatSessionsView()
    {
        InitializeComponent();
        SessionListView.ItemsSource = _sessions;
        AssistanceChatSessionStore.SessionsChanged += AssistanceChatSessionStore_SessionsChanged;
        RefreshSessions();
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
        if (GetHostWindow() is MultiWindowItem host)
        {
            host.NavigateTo(new AssistanceChatView(sessionId));
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

    private sealed class SessionListItem : INotifyPropertyChanged
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
