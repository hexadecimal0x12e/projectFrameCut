using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.DraftStuff;
using projectFrameCut.Services;

namespace projectFrameCut.Asset;

public class ProjectAssetViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public ObservableCollection<AssetItemViewModel> LocalAssets
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = new();

    public ObservableCollection<AssetItemViewModel> SharedAssets
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = new();

    public ObservableCollection<AssetItemViewModel> FilteredLocalAssets
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = new();

    public ObservableCollection<AssetItemViewModel> FilteredSharedAssets
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = new();

    public string SearchText
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            FilterAssets();
        }
    } = "";

    public int OrderOption
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
            FilterAssets();
        }
    } = 0;

    public string LocalAssetsTitle
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "本地素材";

    public string SharedAssetsTitle
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "共享素材";

    public string AddButtonText
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "Add";

    public ICommand AddAssetCommand { get; set; }
    public ICommand RemoveAssetCommand { get; set; }
    public ICommand AddToTrackCommand { get; set; }

    public ProjectAssetViewModel()
    {
        AddAssetCommand = new Command(async () => await OnAddAsset());
        RemoveAssetCommand = new Command<AssetItemViewModel>(async (asset) => await OnRemoveAsset(asset));
        AddToTrackCommand = new Command<AssetItemViewModel>(async (asset) => await OnAddToTrack(asset));
    }

    private async Task OnAddAsset()
    {
        // 这个方法将在 ProjectAssetView.xaml.cs 中被重载
    }

    private async Task OnRemoveAsset(AssetItemViewModel asset)
    {
        // 这个方法将在 ProjectAssetView.xaml.cs 中被重载
    }

    private async Task OnAddToTrack(AssetItemViewModel asset)
    {
        // 这个方法将在 ProjectAssetView.xaml.cs 中被重载
    }

    public async Task FilterAssets()
    {
        FilteredLocalAssets.Clear();
        FilteredSharedAssets.Clear();

        List<AssetItemViewModel> localFiltered = new();
        List<AssetItemViewModel> sharedFiltered = new();

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            // 如果搜索文本为空，显示所有素材
            localFiltered.AddRange(LocalAssets);
            sharedFiltered.AddRange(SharedAssets);
        }
        else
        {
            var inputPron = (await TextHelper.GetHowToPronuce(SearchText, default)).ToLower();
            var inputPronInLocate = ((await TextHelper.GetHowToPronuce(SearchText, TextHelper.FromLanguageCode(Localized._LocaleId_)))).ToLower();
            // 过滤素材
            var searchLower = SearchText.ToLower();
            foreach (var asset in LocalAssets)
            {
                var assetPron = (await TextHelper.GetHowToPronuce(asset.Name, default)).ToLower();
                var assetPronInLocate = (await TextHelper.GetHowToPronuce(asset.Name, TextHelper.FromLanguageCode(Localized._LocaleId_))).ToLower();
                if (asset.Name.ToLower().Contains(searchLower) || assetPron.Contains(SearchText) || assetPron.Contains(inputPron) || assetPron.Contains(inputPronInLocate)|| assetPronInLocate.Contains(SearchText) || assetPronInLocate.Contains(inputPron) || assetPronInLocate.Contains(inputPronInLocate))
                {
                    localFiltered.Add(asset);
                }
            }
            foreach (var asset in SharedAssets)
            {
                var assetPron = (await TextHelper.GetHowToPronuce(asset.Name, default)).ToLower();
                var assetPronInLocate = (await TextHelper.GetHowToPronuce(asset.Name, TextHelper.FromLanguageCode(Localized._LocaleId_))).ToLower();
                if (asset.Name.ToLower().Contains(searchLower) || assetPron.Contains(SearchText) || assetPron.Contains(inputPron) || assetPron.Contains(inputPronInLocate) || assetPronInLocate.Contains(SearchText) || assetPronInLocate.Contains(inputPron) || assetPronInLocate.Contains(inputPronInLocate))
                {
                    sharedFiltered.Add(asset);
                }
            }
        }

        // 应用排序
        if (OrderOption == 0)
        {
            // By add date - 使用原始资产的创建时间
            localFiltered = localFiltered.OrderByDescending(a => a.OriginalAsset?.CreatedAt ?? DateTime.MinValue).ToList();
            sharedFiltered = sharedFiltered.OrderByDescending(a => a.OriginalAsset?.CreatedAt ?? DateTime.MinValue).ToList();
        }
        else if (OrderOption == 1)
        {
            // By name - 使用发音排序
            localFiltered = localFiltered.OrderBy(a => TextHelper.GetPronounceForOrdering(a.Name).Result).ToList();
            sharedFiltered = sharedFiltered.OrderBy(a => TextHelper.GetPronounceForOrdering(a.Name).Result).ToList();
        }

        foreach (var asset in localFiltered)
        {
            FilteredLocalAssets.Add(asset);
        }
        foreach (var asset in sharedFiltered)
        {
            FilteredSharedAssets.Add(asset);
        }
    }
}

public class AssetItemViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public string Id
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "";

    public string Name
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "";

    public string Icon
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "";

    public string Path
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "";

    public string DurationDisplay
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "";

    public string ThumbnailPath
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "";

    public bool HasThumbnail
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = false;

    public string DisplayLabel
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = "";

    public Brush BackgroundColor
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = Brush.Gray;

    public uint FrameCount
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = 0;

    public bool IsInfiniteLength
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = false;

    public bool IsLocal
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged();
        }
    } = true;

    public AssetItem OriginalAsset { get; set; }

    public AssetItemViewModel(AssetItem asset, bool isLocal = true)
    {
        OriginalAsset = asset;
        Id = asset.AssetId ?? "";
        Name = asset.Name;
        Icon = asset.Icon ?? "";
        Path = asset.Path ?? "";
        DurationDisplay = asset.DurationDisplay;
        ThumbnailPath = asset.ThumbnailPath ?? "";
        HasThumbnail = File.Exists(asset.ThumbnailPath);
        DisplayLabel = $"{asset.Icon} {asset.Name}";
        BackgroundColor = ClipElementUI.DetermineAssetColor(asset.AssetType, asset.GetClipMode());
        FrameCount = (uint)(asset.FrameCount ?? 0L);
        IsInfiniteLength = asset.isInfiniteLength;
        IsLocal = isLocal;
    }
}
