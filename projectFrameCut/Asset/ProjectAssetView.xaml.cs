using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.ApplicationAPIBase.Workspace.Modules;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using Path = System.IO.Path;

namespace projectFrameCut.Asset;

public partial class ProjectAssetView : ContentView
{
    public DraftPage workingDraft;
    private readonly ProjectAssetViewModel _viewModel;
    private int _loadVersion;

    public ProjectAssetView(ref DraftPage page)
    {
        workingDraft = page;
        _viewModel = new ProjectAssetViewModel(!page.IsReadonly, !page.IsReadonly && !page.IsRemoteProject);
        InitializeComponent();
        BindingContext = _viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        workingDraft.Workspace.GetModule<AssetModule>().Changed += OnAssetsChanged;
        await LoadAssetsAsync();
    }

    private void OnUnloaded(object? sender, EventArgs e)
        => workingDraft.Workspace.GetModule<AssetModule>().Changed -= OnAssetsChanged;

    private void OnAssetsChanged(object? sender, EventArgs e)
        => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_viewModel.IsIdle) _ = LoadAssetsAsync();
        });

    private async Task LoadAssetsAsync()
    {
        int version = ++_loadVersion;
        try
        {
            var assets = workingDraft.Assets.ToArray();
            var draft = DraftImportAndExportHelper.ExportFromDraftPage(workingDraft, rebuildEffects: false);
            var files = await Task.Run(() => ProjectAssetFile.Collect(draft, assets.Select(a => a.Value), workingDraft.WorkingPath));
            if (version != _loadVersion) return;
            _viewModel.LocalAssets.Clear();
            foreach (var asset in assets)
                _viewModel.LocalAssets.Add(new AssetItemViewModel(asset.Key, asset.Value,
                    files.FirstOrDefault(f => f.AssetIds.Contains(asset.Value.AssetId ?? asset.Key)), workingDraft.WorkingPath, _viewModel));
            _viewModel.Summary = Localized.DraftSettingPage_AssetFiles_Summary(assets.Length, _viewModel.LocalAssets.Count(a => a.IsMissing));
            await _viewModel.FilterAssets();
        }
        catch (Exception ex)
        {
            Log(ex, "Load project assets", workingDraft);
            _viewModel.Summary = Localized._ExceptionTemplate(ex);
        }
    }

    private async Task EditAssetAsync(Func<Task> action)
    {
        if (!_viewModel.IsIdle || workingDraft.IsReadonly) return;
        _viewModel.IsIdle = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log(ex, "Edit project asset", workingDraft);
            workingDraft.SetStateFail(Localized._ExceptionTemplate(ex));
            await workingDraft.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
        finally
        {
            await LoadAssetsAsync();
            _viewModel.IsIdle = true;
        }
    }

    private async Task SaveAssetsAsync(string detail)
    {
        await workingDraft.Save(args: new ClipUpdateEventArgs { Reason = ClipUpdateReason.PropertyChanged, DetailInfo = detail }, throwOnFailure: true);
        await workingDraft.AddClipView.RefreshAssetsAsync();
    }

    private async void OnRefreshClicked(object sender, EventArgs e) => await LoadAssetsAsync();
    private async void OnAddAssetClicked(object sender, EventArgs e) => await AddAAsset();

    public Task AddAAsset(string? assetSource = null) => EditAssetAsync(async () =>
    {
        var path = assetSource ?? (await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Localized.AssetPage_AddAAsset }))?.FullPath;
        if (path is null) return;
        if (await workingDraft.AddAsset(path) is not null) await SaveAssetsAsync(Localized.AssetPage_AddAAsset);
    });

    private async void OnRenameAssetClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: AssetItemViewModel asset }) return;
        await EditAssetAsync(async () =>
        {
            var name = await workingDraft.DisplayPromptAsync(Localized.HomePage_ProjectContextMenu_Rename,
                Localized.AssetPage_InputNewName, Localized._Confirm, Localized._Cancel, initialValue: asset.Name);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == asset.Name) return;
            asset.OriginalAsset.Name = name.Trim();
            await SaveAssetsAsync(Localized.HomePage_ProjectContextMenu_Rename);
        });
    }

    private async void OnRelinkAssetClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: AssetItemViewModel { File: { } file } }) return;
        await EditAssetAsync(async () =>
        {
            var selected = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Localized.DraftSettingPage_AssetFiles_Relink });
            if (selected is null) return;
            workingDraft.SetStateBusy(Localized.DraftPage_ApplyingChanges);
            var replacement = await Task.Run(() => file.ReadReplacement(selected.FullPath));
            await workingDraft.RelinkAssetFileAsync(file, replacement);
            Log($"Relinked project asset file '{file.Path}' to '{replacement.Path}' ({file.ReferenceCount} references).");
        });
    }

    private async void OnCreateProxyClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: AssetItemViewModel { CanCreateProxy: true } asset }) return;
        await EditAssetAsync(() => workingDraft.UpdateAssetProxyAsync(asset.OriginalAsset, remove: false));
    }

    private async void OnRemoveProxyClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: AssetItemViewModel { CanRemoveProxy: true } asset }) return;
        await EditAssetAsync(() => workingDraft.UpdateAssetProxyAsync(asset.OriginalAsset, remove: true));
    }

    private async void OnRemoveAssetClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: AssetItemViewModel asset }) return;
        await EditAssetAsync(async () =>
        {
            if (!await workingDraft.DisplayAlertAsync(Localized._Warn, Localized.DraftPage_AssetPanel_Remove_Confirm0(asset.Name), Localized._Confirm, Localized._Cancel)) return;
            if (!await workingDraft.DisplayAlertAsync(Localized._Warn, Localized.DraftPage_AssetPanel_Remove_Confirm1(asset.Name), Localized._Confirm, Localized._Cancel)) return;
            if (await workingDraft.DisplayPromptAsync(Localized._Warn, Localized.DraftPage_AssetPanel_Remove_Confirm2Input(asset.Name), Localized._Remove, Localized._Cancel, "no") != "yes") return;
            workingDraft.Assets.TryRemove(asset.Id, out _);
            await SaveAssetsAsync(Localized._Remove);
        });
    }

    private async void OnAddToTrackClicked(object sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: AssetItemViewModel { IsAddable: true } assetVM }) return;
        await EditAssetAsync(async () =>
        {
            var asset = assetVM.OriginalAsset;
            if (asset.GetClipMode() == ClipMode.VectorComponentClip)
            {
                if (!workingDraft.Tracks.ContainsKey(0)) workingDraft.AddATrack(0);
                VectorClipServices.AddClip(workingDraft, VectorClipServices.Import(assetVM.Path), 0, 0,
                    Math.Max(1u, SettingsManager.GetSettingAs<uint>("Edit_DefaultInfLengthClipLength", 300, 300)));
                workingDraft.NotifyVectorAssetChanged();
                return;
            }
            int trackIndex;
            if (asset.GetClipMode() is ClipMode.AudioClip or ClipMode.SubtitleClip)
            {
                trackIndex = workingDraft.Tracks.Keys.Where(k => k >= DraftPage.SubTrackOffset).DefaultIfEmpty(DraftPage.SubTrackOffset).Max();
                if (!workingDraft.Tracks.ContainsKey(trackIndex)) workingDraft.AddASubTrack(trackIndex);
            }
            else
            {
                trackIndex = workingDraft.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(0).Max();
                if (!workingDraft.Tracks.ContainsKey(trackIndex)) workingDraft.AddATrack(trackIndex);
            }
            var path = Path.GetRelativePath(workingDraft.WorkingPath, assetVM.Path);
            if (asset.GetClipMode() == ClipMode.SubtitleClip || path.StartsWith("..") || Path.IsPathRooted(path)) path = assetVM.Path;
            var clip = workingDraft.CreateFromAsset(asset, trackIndex, InternalPluginBase.InternalPluginBaseID, path);
            workingDraft.RegisterClip(clip, true);
            workingDraft.AddAClip(clip);
            await workingDraft.UpdateAdjacencyForTrack();
            workingDraft.SetStatusText(Localized.DraftPage_AssetAdded(asset.Name));
            await workingDraft.HidePopup();
        });
    }
}
