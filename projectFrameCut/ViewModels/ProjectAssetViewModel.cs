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

    private ObservableCollection<AssetItemViewModel> _localAssets = new();
    public ObservableCollection<AssetItemViewModel> LocalAssets
    {
        get => _localAssets;
        set
        {
            _localAssets = value;
            OnPropertyChanged();
        }
    }

    private ObservableCollection<AssetItemViewModel> _sharedAssets = new();
    public ObservableCollection<AssetItemViewModel> SharedAssets
    {
        get => _sharedAssets;
        set
        {
            _sharedAssets = value;
            OnPropertyChanged();
        }
    }

    private ObservableCollection<AssetItemViewModel> _filteredLocalAssets = new();
    public ObservableCollection<AssetItemViewModel> FilteredLocalAssets
    {
        get => _filteredLocalAssets;
        set
        {
            _filteredLocalAssets = value;
            OnPropertyChanged();
        }
    }

    private ObservableCollection<AssetItemViewModel> _filteredSharedAssets = new();
    public ObservableCollection<AssetItemViewModel> FilteredSharedAssets
    {
        get => _filteredSharedAssets;
        set
        {
            _filteredSharedAssets = value;
            OnPropertyChanged();
        }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value;
            OnPropertyChanged();
            FilterAssets();
        }
    }

    private string _localAssetsTitle = "本地素材";
    public string LocalAssetsTitle
    {
        get => _localAssetsTitle;
        set
        {
            _localAssetsTitle = value;
            OnPropertyChanged();
        }
    }

    private string _sharedAssetsTitle = "共享素材";
    public string SharedAssetsTitle
    {
        get => _sharedAssetsTitle;
        set
        {
            _sharedAssetsTitle = value;
            OnPropertyChanged();
        }
    }

    private string _addButtonText = "Add";
    public string AddButtonText
    {
        get => _addButtonText;
        set
        {
            _addButtonText = value;
            OnPropertyChanged();
        }
    }

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

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            // 如果搜索文本为空，显示所有素材
            foreach (var asset in LocalAssets)
            {
                FilteredLocalAssets.Add(asset);
            }
            foreach (var asset in SharedAssets)
            {
                FilteredSharedAssets.Add(asset);
            }
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
                    FilteredLocalAssets.Add(asset);
                }
            }
            foreach (var asset in SharedAssets)
            {
                var assetPron = (await TextHelper.GetHowToPronuce(asset.Name, default)).ToLower();
                var assetPronInLocate = (await TextHelper.GetHowToPronuce(asset.Name, TextHelper.FromLanguageCode(Localized._LocaleId_))).ToLower();
                if (asset.Name.ToLower().Contains(searchLower) || assetPron.Contains(SearchText) || assetPron.Contains(inputPron) || assetPron.Contains(inputPronInLocate) || assetPronInLocate.Contains(SearchText) || assetPronInLocate.Contains(inputPron) || assetPronInLocate.Contains(inputPronInLocate))
                {
                    FilteredSharedAssets.Add(asset);
                }
            }
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

    private string _id = "";
    public string Id
    {
        get => _id;
        set
        {
            _id = value;
            OnPropertyChanged();
        }
    }

    private string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            OnPropertyChanged();
        }
    }

    private string _icon = "";
    public string Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            OnPropertyChanged();
        }
    }

    private string _path = "";
    public string Path
    {
        get => _path;
        set
        {
            _path = value;
            OnPropertyChanged();
        }
    }

    private string _thumbnailPath = "";
    public string ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            _thumbnailPath = value;
            OnPropertyChanged();
        }
    }

    private bool _hasThumbnail = false;
    public bool HasThumbnail
    {
        get => _hasThumbnail;
        set
        {
            _hasThumbnail = value;
            OnPropertyChanged();
        }
    }

    private string _displayLabel = "";
    public string DisplayLabel
    {
        get => _displayLabel;
        set
        {
            _displayLabel = value;
            OnPropertyChanged();
        }
    }

    private Brush _backgroundColor = Brush.Gray;
    public Brush BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            _backgroundColor = value;
            OnPropertyChanged();
        }
    }

    private uint _frameCount = 0;
    public uint FrameCount
    {
        get => _frameCount;
        set
        {
            _frameCount = value;
            OnPropertyChanged();
        }
    }

    private bool _isInfiniteLength = false;
    public bool IsInfiniteLength
    {
        get => _isInfiniteLength;
        set
        {
            _isInfiniteLength = value;
            OnPropertyChanged();
        }
    }

    private bool _isLocal = true;
    public bool IsLocal
    {
        get => _isLocal;
        set
        {
            _isLocal = value;
            OnPropertyChanged();
        }
    }

    public AssetItem OriginalAsset { get; set; }

    public AssetItemViewModel(AssetItem asset, bool isLocal = true)
    {
        OriginalAsset = asset;
        Id = asset.AssetId ?? "";
        Name = asset.Name;
        Icon = asset.Icon ?? "";
        Path = asset.Path ?? "";
        ThumbnailPath = asset.ThumbnailPath ?? "";
        HasThumbnail = File.Exists(asset.ThumbnailPath);
        DisplayLabel = $"{asset.Icon} {asset.Name}";
        BackgroundColor = ClipElementUI.DetermineAssetColor(asset.AssetType, asset.GetClipMode());
        FrameCount = (uint)(asset.FrameCount ?? 0L);
        IsInfiniteLength = asset.isInfiniteLength;
        IsLocal = isLocal;
    }
}
