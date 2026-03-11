using projectFrameCut.ViewModels;

namespace projectFrameCut.DraftStuff;

public partial class ProjectAddClipView : ContentView
{
    private readonly ProjectAddClipViewModel _viewModel = null!;
    private readonly DraftPage _page = null!;
    
    private readonly int ItemSize = 180;
    private readonly int ItemSpacing = 8;

    public ProjectAddClipView(ref DraftPage draftPage)
    {
        InitializeComponent();
        _page = draftPage;
        _viewModel = new ProjectAddClipViewModel(ref draftPage);
        BindingContext = _viewModel;
        _viewModel.SetDrawingView(DrawingCanvas);
        ParentChanged += (s,e) => (BindingContext as ProjectAddClipViewModel)?.LoadTransforms();
        MainTabView.OnTabSwitched += MainTabView_OnTabSwitched;
        var orderOpt = SettingsManager.GetSetting("Edit_AddView_DefaultOrderOption", "date");
        OrderOptionPicker.SelectedIndex = orderOpt switch
        {
            "date" => 0,
            "name" => 1,
            _ => 0
        };
        if (_page?.UseCompactLayout ?? false || DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            SearchInputEntry.IsVisible = false;
            OrderOptionPicker.IsVisible = false;
        }
    }

    private void MainTabView_OnTabSwitched(object? sender, ApplicationAPIBase.Views.TabbedView.TabbedViewItem e)
    {
        switch (e.Tag)
        {
            case "LocalAssets":
            case "AIGC":
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
            case "AIGC":
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
        if (_page?.UseCompactLayout ?? false || DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            SearchInputEntry.IsVisible = false;
            OrderOptionPicker.IsVisible = false;
        }
    }

    public event EventHandler? ClipAdded
    {
        add => _viewModel.ClipAdded += value;
        remove => _viewModel.ClipAdded -= value;
    }

    private void OnAIContentTypeChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (e.Value && sender is RadioButton radioButton)
        {
            _viewModel.AIContentType = radioButton.Value?.ToString() ?? "Image";
        }
    }

    private void OnCollectionViewSizeChanged(object sender, EventArgs e)
    {
        if (sender is CollectionView collectionView && collectionView.ItemsLayout is GridItemsLayout gridLayout)
        {
            var width = collectionView.Width;
            if (width > 0)
            {
                var span = Math.Max(1, (int)((width + ItemSpacing) / (ItemSize + ItemSpacing)));
                if (gridLayout.Span != span)
                {
                    gridLayout.Span = span;
                }
            }
        }
    }

}