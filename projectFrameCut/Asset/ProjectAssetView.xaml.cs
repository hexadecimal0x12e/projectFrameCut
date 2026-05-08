using System.Collections.ObjectModel;
using Microsoft.Maui.Storage;
using projectFrameCut.Render;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Shared;
using Path = System.IO.Path;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Plugin;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;

namespace projectFrameCut.Asset;

public partial class ProjectAssetView : ContentView
{
    public DraftPage workingDraft;
    private ProjectAssetViewModel _viewModel;

    public ProjectAssetView(ref DraftPage page)
    {
        workingDraft = page;

        _viewModel = new ProjectAssetViewModel(
            addAssetCommand: new Command(async () => await AddAAsset()),
            removeAssetCommand: new Command<AssetItemViewModel>(async (asset) => await OnRemoveAsset(asset)),
            addToTrackCommand: new Command<AssetItemViewModel>(async (asset) => await OnAddToTrack(asset))
        );

        // 设置本地化字符串
        _viewModel.LocalAssetsTitle = Localized.DraftPage_AssetPanel_LocalAssets;
        _viewModel.SharedAssetsTitle = Localized.DraftPage_AssetPanel_SharedAssets;
        _viewModel.AddButtonText = "Add";

        InitializeComponent();
        BindingContext = _viewModel;
        LoadAssets();
    }


    private void LoadAssets()
    {
        _viewModel.LocalAssets.Clear();
        _viewModel.SharedAssets.Clear();

        // 加载本地素材
        foreach (var kvp in workingDraft.Assets)
        {
            var assetVM = new AssetItemViewModel(kvp.Value, _viewModel, isLocal: true);
            _viewModel.LocalAssets.Add(assetVM);
        }

        // 加载共享素材
        foreach (var kvp in AssetDatabase.Assets.Where(c => c.Value.AssetType is AssetType.Video or AssetType.Audio or AssetType.Image))
        {
            var assetVM = new AssetItemViewModel(kvp.Value, _viewModel, isLocal: false);
            _viewModel.SharedAssets.Add(assetVM);
        }

        // 初始化过滤后的集合
        _viewModel.FilterAssets();

        LogDiagnostic($"[ProjectAssetView] Loaded {_viewModel.LocalAssets.Count} local assets");
        LogDiagnostic($"[ProjectAssetView] Loaded {_viewModel.SharedAssets.Count} shared assets");
    }

    private void OnAssetClipTapped(object sender, TappedEventArgs e)
    {
        if (sender is Border border && border.BindingContext is AssetItemViewModel assetVM)
        {
            // 显示工具提示或其他操作
        }
    }

    public async Task AddAAsset(string? assetSource = null)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            workingDraft.SetStateBusy(Localized.DraftPage_WaitForUser);
            try
            {
                var result = assetSource ?? (await FilePicker.PickAsync(new PickOptions
                {
                    PickerTitle = "Select a asset",
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        { DevicePlatform.WinUI, ["*", ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff", ".pjfc"] },
                        { DevicePlatform.Android, ["image/*", "video/*"] },
#if iDevices
                        {DevicePlatform.iOS , ["public.image", "public.movie", "public.video", "public.mpeg-4", "com.apple.protected-mpeg-4-video", "com.apple.quicktime-movie", "public.avi", "org.matroska.mkv"]},
                        {DevicePlatform.MacCatalyst , ["public.image", "public.movie", "public.video", "public.mpeg-4", "com.apple.protected-mpeg-4-video", "com.apple.quicktime-movie", "public.avi", "org.matroska.mkv"]}
#endif
                    })
                })).FullPath;

                if (result is not null)
                {
                    PropertyPanelBuilder optionPPB = new();
                    optionPPB.AddText(Localized.DraftPage_AssetPanel_Add_SelectMode)
                        .AddIconTitleDescriptionCard("Copy", null, Localized.DraftPage_AssetPanel_AsLocalAssets, Localized.DraftPage_AssetPanel_AsLocalAssets_Desc)
                        .AddIconTitleDescriptionCard("CopyToShared", null, Localized.DraftPage_AssetPanel_AsSharedAssets, Localized.DraftPage_AssetPanel_AsSharedAssets_Desc)
                        .AddIconTitleDescriptionCard("Reference", null, Localized.DraftPage_AssetPanel_Reference, Localized.DraftPage_AssetPanel_Reference_Desc)
                        .AddButton("Cancel", Localized._Cancel);

                    TaskCompletionSource tcs = new();

                    optionPPB.ListenToChanges(async (e) =>
                    {
                        string resultPath = "";
                        switch (e.Id)
                        {
                            case "Reference":
                                {
                                    resultPath = result;
                                    await AddAssetToProject(resultPath);
                                    break;
                                }
                            case "Copy":
                                {
                                    resultPath = Path.Combine(workingDraft.WorkingPath, "assets", Guid.NewGuid().ToString() + Path.GetExtension(resultPath));
                                    if (!string.IsNullOrWhiteSpace(workingDraft.WorkingPath))
                                    {
#if WINDOWS
                                        File.Copy(result, resultPath, true);
#else
                                        File.Move(result, resultPath, true);
#endif
                                        await AddAssetToProject(resultPath);
                                    }
                                    break;
                                }
                            case "CopyToShared":
                                {
                                    await AssetDatabase.Add(result, workingDraft);
                                    LoadAssets(); // 重新加载共享素材
                                    break;
                                }
                        }
                        tcs.SetResult();
                    });

                    await workingDraft.ShowACenteredPopup(600, 400, optionPPB.Build());
                    try
                    {
                        var cts = new CancellationTokenSource();
                        cts.CancelAfter(60 * 1000);
                        await tcs.Task.WaitAsync(cts.Token);
                    }
                    catch (TaskCanceledException) { }

                    await workingDraft.HidePopup();
                    workingDraft.SetStateOK();
                }
            }
            catch (Exception ex)
            {
                Log(ex, "Add asset", workingDraft);
            }
        });
    }

    private async Task AddAssetToProject(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var nameInput = await workingDraft.DisplayPromptAsync(Localized.AssetPage_AddAAsset_InputName, Path.GetFileName(path), Localized._OK, Localized._Cancel, name, 0, null, name);
        if (!string.IsNullOrEmpty(nameInput)) name = nameInput;
        var type = AssetItem.GetAssetType(path);
        if (type == AssetType.Other)
        {
            var map = new Dictionary<string, AssetType>
                {
                    {Localized.AssetPage_AssetType_Video, AssetType.Video },
                    {Localized.AssetPage_AssetType_Audio, AssetType.Audio },
                    {Localized.AssetPage_AssetType_Image, AssetType.Image },
                    {Localized.AssetPage_AssetType_Font, AssetType.Font },
                };
            var selection = await workingDraft.DisplayActionSheetAsync(Localized.AssetPage_AssetType_Unknown(name), null, null, map.Keys.ToArray());
            if (!map.TryGetValue(selection, out type)) return;
        }
        var asset = AssetDatabase.Create(path, nameInput, default);
        if (asset is not null)
        {
            workingDraft.Assets[asset.AssetId] = asset;
            _viewModel.LocalAssets.Add(new AssetItemViewModel(asset, _viewModel, isLocal: true));
            _viewModel.FilterAssets(); // 刷新过滤列表
        }
    }

    private async Task OnRemoveAsset(AssetItemViewModel assetVM)
    {
        if (assetVM.IsLocal)
        {
            workingDraft.Assets.Remove(assetVM.Id, out var _);
            _viewModel.LocalAssets.Remove(assetVM);
            _viewModel.FilterAssets(); // 刷新过滤列表
        }
    }

    private async Task OnAddToTrack(AssetItemViewModel assetVM)
    {
        var asset = assetVM.OriginalAsset;
        var mode = ClipElementUI.DetermineClipMode(asset.Path);
        int trackIndex = 0;

        if (mode == ClipMode.AudioClip || mode == ClipMode.SubtitleClip)
        {
            int maxSub = workingDraft.Tracks.Keys.Where(k => k >= DraftPage.SubTrackOffset).DefaultIfEmpty(DraftPage.SubTrackOffset - 1).Max();
            if (maxSub < DraftPage.SubTrackOffset) maxSub = DraftPage.SubTrackOffset;
            if (!workingDraft.Tracks.ContainsKey(maxSub)) workingDraft.AddASubTrack(maxSub);
            trackIndex = maxSub;
        }
        else
        {
            int maxMain = workingDraft.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(0).Max();
            trackIndex = maxMain;
        }

        string path;
        if (assetVM.IsLocal)
        {
            path = Path.GetRelativePath(workingDraft.WorkingPath, asset.Path);
            if (path.Contains("..")) path = asset.Path;
        }
        else
        {
            path = asset.Path;
        }

        var elem = workingDraft.CreateFromAsset(asset, trackIndex, InternalPluginBase.InternalPluginBaseID, path);
        workingDraft.RegisterClip(elem, true);
        workingDraft.AddAClip(elem);

        await workingDraft.UpdateAdjacencyForTrack();
        workingDraft.SetStatusText(Localized.DraftPage_AssetAdded(asset.Name));
        await workingDraft.HidePopup();
    }
}
