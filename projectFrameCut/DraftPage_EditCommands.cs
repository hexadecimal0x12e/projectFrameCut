namespace projectFrameCut;

internal enum DraftEditCommand { Delete, Copy, Cut, Paste, Undo, Redo }

internal sealed class DraftEditCommandEventArgs(DraftEditCommand command) : EventArgs
{
    public DraftEditCommand Command { get; } = command;
    public bool Handled { get; set; }
}

public partial class DraftPage
{
    private EventHandler<DraftEditCommandEventArgs>? _editCommandRequested;

    internal event EventHandler<DraftEditCommandEventArgs> EditCommandRequested
    {
        add
        {
            _editCommandRequested += value;
            OnPropertyChanged(nameof(CanEditSelection));
        }
        remove
        {
            _editCommandRequested -= value;
            OnPropertyChanged(nameof(CanEditSelection));
        }
    }

    public bool CanEditSelection => SelectedAnyClip || _editCommandRequested is not null;

    private bool RouteEditCommand(DraftEditCommand command)
    {
        var e = new DraftEditCommandEventArgs(command);
        _editCommandRequested?.Invoke(this, e);
        return e.Handled;
    }
}
