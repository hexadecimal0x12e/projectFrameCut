using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using projectFrameCut.Render.RenderAPIBase.Project;

namespace projectFrameCut.Asset;

public class ProjectAssetViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private int _filterVersion;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ObservableCollection<AssetItemViewModel> LocalAssets { get; } = new();
    public ObservableCollection<AssetItemViewModel> FilteredLocalAssets { get; } = new();
    public bool CanEdit { get; }
    public bool CanManageProxy { get; }

    public bool IsIdle
    {
        get;
        set { field = value; OnPropertyChanged(); }
    } = true;

    public string Summary
    {
        get;
        set { field = value; OnPropertyChanged(); }
    } = "";

    public string SearchText
    {
        get;
        set { field = value ?? ""; OnPropertyChanged(); _ = FilterAssets(); }
    } = "";

    public int OrderOption
    {
        get;
        set { field = value; OnPropertyChanged(); _ = FilterAssets(); }
    }

    public bool MissingOnly
    {
        get;
        set { field = value; OnPropertyChanged(); _ = FilterAssets(); }
    }

    public ProjectAssetViewModel(bool canEdit, bool canManageProxy)
    {
        CanEdit = canEdit;
        CanManageProxy = canManageProxy;
    }

    public async Task FilterAssets()
    {
        int version = ++_filterVersion;
        string search = SearchText.Trim().ToLowerInvariant();
        var assets = LocalAssets.Where(a => !MissingOnly || a.IsMissing).ToList();
        try
        {
            if (search.Length > 0)
            {
                var inputPron = (await TextServices.GetHowToPronuce(search, default)).ToLowerInvariant();
                var language = TextHelper.FromLanguageCode(Localized._LocaleId_);
                var inputLocal = (await TextServices.GetHowToPronuce(search, language)).ToLowerInvariant();
                var filtered = new List<AssetItemViewModel>();
                foreach (var asset in assets)
                {
                    if (version != _filterVersion) return;
                    if (asset.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || asset.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
                    {
                        filtered.Add(asset);
                        continue;
                    }
                    var pronunciation = (await TextServices.GetHowToPronuce(asset.Name, default)).ToLowerInvariant();
                    var local = (await TextServices.GetHowToPronuce(asset.Name, language)).ToLowerInvariant();
                    if (pronunciation.Contains(search) || pronunciation.Contains(inputPron) || pronunciation.Contains(inputLocal)
                        || local.Contains(search) || local.Contains(inputPron) || local.Contains(inputLocal))
                        filtered.Add(asset);
                }
                assets = filtered;
            }
            assets = OrderOption == 1
                ? (await assets.OrderByPronounceAsync(a => a.Name)).ToList()
                : assets.OrderByDescending(a => a.OriginalAsset.CreatedAt).ToList();
            if (version != _filterVersion) return;
            FilteredLocalAssets.Clear();
            foreach (var asset in assets) FilteredLocalAssets.Add(asset);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Filter project assets", this);
        }
    }
}

public class AssetItemViewModel
{
    public AssetItem OriginalAsset { get; }
    internal ProjectAssetFile? File { get; }
    public string Id { get; }
    public string Name => OriginalAsset.Name;
    public string Icon => OriginalAsset.Icon ?? "";
    public string Path { get; }
    public string ThumbnailPath { get; }
    public bool HasThumbnail => System.IO.File.Exists(ThumbnailPath);
    public string DurationDisplay => OriginalAsset.DurationTimeDisplay;
    public bool IsMissing { get; }
    public string FileStatus => IsMissing ? Localized.DraftSettingPage_AssetFiles_Missing : Localized.DraftSettingPage_AssetFiles_Available;
    public Color StatusColor => IsMissing ? Colors.OrangeRed : Colors.Green;
    public string References { get; }
    public bool CanEdit { get; }
    public bool CanRelink => CanEdit && File is not null;
    public bool IsVideo => OriginalAsset.GetClipMode() == ClipMode.VideoClip;
    public bool IsAddable => CanEdit && !IsMissing && OriginalAsset.AssetType is AssetType.Video or AssetType.Image or AssetType.Audio or AssetType.VectorComposition or AssetType.Subtitle;
    public string ProxyPath { get; }
    public bool HasProxy => System.IO.File.Exists(ProxyPath) && System.IO.File.Exists(ProxyPath + ".ready");
    public string ProxyStatus => HasProxy ? Localized.AssetPage_ProxyReady : Localized.AssetPage_ProxyNotCreated;
    public string CreateProxyText => HasProxy ? Localized.AssetPage_RebuildProxy : Localized.AssetPage_CreateProxy;
    public bool CanCreateProxy { get; }
    public bool CanRemoveProxy { get; }

    internal AssetItemViewModel(string id, AssetItem asset, ProjectAssetFile? file, string projectRoot, ProjectAssetViewModel parent)
    {
        Id = id;
        OriginalAsset = asset;
        File = file;
        Path = ProjectAssetFile.ResolvePath(asset.Path ?? "", projectRoot);
        ThumbnailPath = ProjectAssetFile.ResolvePath(asset.ThumbnailPath ?? "", projectRoot);
        IsMissing = file?.IsMissing ?? !System.IO.File.Exists(Path);
        References = Localized.DraftSettingPage_AssetFiles_References(file?.ReferenceCount ?? 0);
        CanEdit = parent.CanEdit;
        ProxyPath = System.IO.Path.Combine(ProjectCachePaths.GetRoot(projectRoot), "proxy", $"{System.IO.Path.GetFileNameWithoutExtension(Path)}.proxy.mp4");
        CanCreateProxy = parent.CanManageProxy && IsVideo && !IsMissing;
        CanRemoveProxy = parent.CanManageProxy && IsVideo && (System.IO.File.Exists(ProxyPath) || System.IO.File.Exists(ProxyPath + ".ready"));
    }
}
