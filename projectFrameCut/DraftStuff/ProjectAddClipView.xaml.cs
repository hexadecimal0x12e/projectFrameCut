using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ViewModels;

namespace projectFrameCut.DraftStuff;

public partial class ProjectAddClipView : ContentView
{
    private readonly ProjectAddClipViewModel _viewModel = null!;
    private readonly DraftPage _page = null!;

    private readonly int ItemSize = 180;
    private readonly int ItemSpacing = 8;
    private bool _isAddingAsset;

    public ProjectAddClipView(ref DraftPage draftPage)
    {
        InitializeComponent();
        _page = draftPage;
        _viewModel = new ProjectAddClipViewModel(ref draftPage);
        _viewModel.LoadVectorComponents();
        BindingContext = _viewModel;
        _viewModel.SetDrawingView(DrawingCanvas);
        MainTabView.OnTabSwitched += MainTabView_OnTabSwitched;
        var orderOpt = SettingsManager.GetSetting("Edit_AddView_DefaultOrderOption", "date");
        OrderOptionPicker.SelectedIndex = orderOpt switch
        {
            "date" => 0,
            "name" => 1,
            _ => 0
        };
        CollapseHeaderControls();
    }

    internal Task RefreshExternalSourcesAsync() => _viewModel.LoadRpcVideoSources();
    internal Task RefreshAssetsAsync() => MainThread.InvokeOnMainThreadAsync(_viewModel.LoadAssets);

    private void MainTabView_OnTabSwitched(object? sender, ApplicationAPIBase.Views.TabbedView.TabbedViewItem e)
    {
        CollapseHeaderControls();

        OrderOptionContainer.IsVisible = e.Tag is "LocalAssets" or "SharedAssets" or "RpcSources" or "Templates";
        SearchContainer.IsVisible = e.Tag is not ("Sketch" or "AIGeneratedContent" or "More");
        if (e.Tag == "RpcSources") _ = _viewModel.LoadRpcVideoSources();
        if (e.Tag == "Graphics") _viewModel.LoadVectorComponents();
    }

    private async void OnAddAssetClicked(object? sender, EventArgs e)
    {
        var sourceType = sender switch
        {
            Button b => b.CommandParameter as string,
            Border { BindingContext: AddSourceCardViewModel source } => source.SourceType,
            _ => null
        };
        if (sourceType is null || !CanAddAsset(sourceType)) return;
        _isAddingAsset = true;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            if (sourceType == "RpcSources")
            {
                await Services.ProjectExternalSourceService.AddAsync(_page);
                return;
            }
            var file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = Localized.AssetPage_AddAAsset
            });
            if (file is null) return;

            await AddAssetPathAsync(file.FullPath, sourceType);
        }
        catch (Exception ex)
        {
            Log(ex, "Add asset from clip panel", _page);
            await _page.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
        finally
        {
            await RefreshSourceAssetsAsync(sourceType);
            _page.SetStateOK();
            if (button is not null) button.IsEnabled = true;
            _isAddingAsset = false;
        }
    }

    private bool CanAddAsset(string sourceType) => !_isAddingAsset && (!_page.IsReadonly || sourceType == "SharedAssets");

    private async Task AddAssetPathAsync(string path, string sourceType)
    {
        Log($"Importing '{path}' into {sourceType} from the clip panel.");
        if (sourceType == "RpcSources")
            await Services.ProjectExternalSourceService.AddAsync(_page, path);
        else if (sourceType == "SharedAssets")
            await Asset.AssetDatabase.Add(path, _page);
        else
            await _page.AddAsset(path, false);
    }

    private async Task RefreshSourceAssetsAsync(string sourceType)
    {
        try
        {
            if (sourceType == "RpcSources")
                await _viewModel.LoadRpcVideoSources();
            else
                await RefreshAssetsAsync();
        }
        catch (Exception ex)
        {
            Log(ex, $"Refresh {sourceType} after importing assets", _page);
        }
    }

    private void OnAddAssetDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not DropGestureRecognizer { Parent: Border { BindingContext: AddSourceCardViewModel source } card }) return;
        var allowed = CanAddAsset(source.SourceType);
#if WINDOWS
        if (e.PlatformArgs?.DragEventArgs is { } args)
        {
            allowed &= args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems);
            args.AcceptedOperation = allowed ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            args.Handled = true;
            if (allowed)
            {
                args.DragUIOverride.Caption = source.Name;
                args.DragUIOverride.IsCaptionVisible = true;
            }
        }
#endif
        e.AcceptedOperation = allowed ? DataPackageOperation.Copy : DataPackageOperation.None;
        card.Stroke = new SolidColorBrush(allowed ? Colors.CornflowerBlue : Colors.Gray);
        card.StrokeThickness = allowed ? 2 : 1;
    }

    private void OnAddAssetDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is not DropGestureRecognizer { Parent: Border card }) return;
        card.Stroke = new SolidColorBrush(Colors.Gray);
        card.StrokeThickness = 1;
#if WINDOWS
        if (e.PlatformArgs?.DragEventArgs is { } args) args.Handled = true;
#endif
    }

    private async void OnAddAssetDrop(object? sender, DropEventArgs e)
    {
        if (sender is not DropGestureRecognizer { Parent: Border { BindingContext: AddSourceCardViewModel source } card }) return;
#if WINDOWS
        if (e.PlatformArgs?.DragEventArgs is { } args) args.Handled = true;
#endif
        card.Stroke = new SolidColorBrush(Colors.Gray);
        card.StrokeThickness = 1;
        if (!CanAddAsset(source.SourceType)) return;

        _isAddingAsset = true;
#if WINDOWS
        var deferral = e.PlatformArgs?.DragEventArgs?.GetDeferral();
#endif
        try
        {
            var paths = await FileDropHelper.GetFilePathsFromDrop(e, source.SourceType == "RpcSources");
            foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            {
                try
                {
                    await AddAssetPathAsync(path, source.SourceType);
                }
                catch (Exception ex)
                {
                    Log(ex, $"Drop asset '{path}' into {source.SourceType}", _page);
                    await _page.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
                }
            }
        }
        catch (Exception ex)
        {
            Log(ex, "Read dropped assets from clip panel", _page);
            await _page.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
        finally
        {
            await RefreshSourceAssetsAsync(source.SourceType);
            _page.SetStateOK();
            _isAddingAsset = false;
#if WINDOWS
            deferral?.Complete();
#endif
        }
    }

    private void OnOrderOptionExpandButtonClicked(object? sender, EventArgs e)
    {
        CollapseSearch();
        OrderOptionExpandButton.IsVisible = false;
        OrderOptionPicker.IsVisible = true;
        OrderOptionPicker.Focus();
    }

    private void OnOrderOptionPickerSelectedIndexChanged(object? sender, EventArgs e)
    {
        CollapseOrderOption();
    }

    private void OnSearchExpandButtonClicked(object? sender, EventArgs e)
    {
        CollapseOrderOption();
        SearchExpandButton.IsVisible = false;
        SearchInputEntry.IsVisible = true;
        SearchInputEntry.Focus();
    }

    private void OnSearchInputSearchButtonPressed(object? sender, EventArgs e)
    {
        SearchInputEntry.Unfocus();
        CollapseSearch();
    }

    private void CollapseHeaderControls()
    {
        CollapseOrderOption();
        CollapseSearch();
    }

    private void CollapseOrderOption()
    {
        OrderOptionPicker.IsVisible = false;
        OrderOptionExpandButton.IsVisible = true;
    }

    private void CollapseSearch()
    {
        SearchInputEntry.IsVisible = false;
        SearchExpandButton.IsVisible = true;
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
