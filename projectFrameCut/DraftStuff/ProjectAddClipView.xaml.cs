using projectFrameCut.ViewModels;

namespace projectFrameCut.DraftStuff;

public partial class ProjectAddClipView : ContentView
{
    private readonly ProjectAddClipViewModel _viewModel;

    public ProjectAddClipView(ref DraftPage draftPage)
    {
        InitializeComponent();
        
        _viewModel = new ProjectAddClipViewModel(ref draftPage);
        BindingContext = _viewModel;
    }

    public event EventHandler? ClipAdded
    {
        add => _viewModel.ClipAdded += value;
        remove => _viewModel.ClipAdded -= value;
    }
}