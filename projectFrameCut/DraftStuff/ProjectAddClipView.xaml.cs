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
        _viewModel.SetDrawingView(DrawingCanvas);
        ParentChanged += (s,e) => (BindingContext as ProjectAddClipViewModel)?.LoadTransforms();
        MainTabView.OnTabSwitched += MainTabView_OnTabSwitched;
    }

    private void MainTabView_OnTabSwitched(object? sender, ApplicationAPIBase.Views.TabbedView.TabbedViewItem e)
    {
        switch (e.Tag)
        {
            case "LocalAssets":
            case "SharedAssets":
                {
                    OrderOptionPicker.IsVisible = true;
                    break;
                }
            default:
                {
                    OrderOptionPicker.IsVisible = false;    
                    break;
                }
        }
        switch (e.Tag)
        {
            case "Sketch":
            case "More":
                {
                    SearchInputEntry.IsVisible = false;
                    break;
                }
            default:
                {
                    SearchInputEntry.IsVisible = true;    
                    break;
                }
        }
    }

    public event EventHandler? ClipAdded
    {
        add => _viewModel.ClipAdded += value;
        remove => _viewModel.ClipAdded -= value;
    }
}