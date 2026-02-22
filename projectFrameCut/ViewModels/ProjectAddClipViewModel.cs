using projectFrameCut.APIClient;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace projectFrameCut.ViewModels;

public partial class ProjectAddClipViewModel : INotifyPropertyChanged
{
    private readonly DraftPage _draftPage;

    public bool TransformMenuActivatedViaHandleClick = false;

    public ProjectAddClipViewModel(ref DraftPage draftPage)
    {
        _draftPage = draftPage;

        // 初始化命令
        AddTextClipCommand = new Command(async () => await AddTextClip());
        AddSolidColorClipCommand = new Command(async () => await AddSolidColorClip());
        AddSubTitleClipCommand = new Command(async () => await AddSubTitleClip());
        AddAlternativeSourceClipCommand = new Command(async () => await AddAlternativeSourceClip());
        AddAssetClipCommand = new Command<AssetItemViewModel>(async (asset) => await AddAssetClip(asset));
        AddReuseableAssetClipCommand = new Command<AssetItemViewModel>(async (asset) => await AddReuseableAssetClip(asset));
        AddTransformClipCommand = new Command<TransformItemViewModel>(async (t) => await AddTransformClip(t));

        // 加载资源
        LoadAssets();
        LoadTransforms();
    }

    // 资源列表
    public ObservableCollection<AssetItemViewModel> LocalAssets { get; } = new();
    public ObservableCollection<AssetItemViewModel> SharedAssets { get; } = new();
    public ObservableCollection<AssetItemViewModel> ReuseableAssets { get; } = new();

    // 过滤后的资源列表
    public ObservableCollection<AssetItemViewModel> FilteredLocalAssets { get; } = new();
    public ObservableCollection<AssetItemViewModel> FilteredSharedAssets { get; } = new();
    public ObservableCollection<AssetItemViewModel> FilteredReuseableAssets { get; } = new();

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                OnPropertyChanged();
                FilterAssets();
            }
        }
    }

    private int _orderOption = 0; // 0: By add date, 1: By name
    public int OrderOption
    {
        get => _orderOption;
        set
        {
            if (_orderOption != value)
            {
                _orderOption = value;
                OnPropertyChanged();
                FilterAssets();
            }
        }
    }

    // Transform 列表
    public ObservableCollection<TransformItemViewModel> AvailableTransforms { get; } = new();

    // 命令
    public ICommand AddTextClipCommand { get; }
    public ICommand AddSolidColorClipCommand { get; }
    public ICommand AddSubTitleClipCommand { get; }
    public ICommand AddAlternativeSourceClipCommand { get; }
    public ICommand AddAssetClipCommand { get; }
    public ICommand AddReuseableAssetClipCommand { get; }
    public ICommand AddTransformClipCommand { get; }

    // 事件：当需要关闭弹窗时触发
    public event EventHandler? ClipAdded;

    private async Task LoadAssets()
    {
        LocalAssets.Clear();
        SharedAssets.Clear();
        ReuseableAssets.Clear();

        foreach (var asset in _draftPage.Assets.Values.OrderBy(a => a.Name))
        {
            LocalAssets.Add(new AssetItemViewModel
            {
                Id = asset.AssetId,
                Name = asset.Name,
                Type = asset.AssetType.ToString(),
                DurationDisplay = asset.DurationDisplay,
                SourcePath = asset.Path,
                ThumbPath = asset.ThumbnailPath,
                OriginalAsset = asset
            });
        }

        foreach (var asset in AssetDatabase.Assets.Values.OrderBy(a => a.Name))
        {
            SharedAssets.Add(new AssetItemViewModel
            {
                Id = asset.AssetId,
                Name = asset.Name,
                Type = asset.AssetType.ToString(),
                DurationDisplay = asset.DurationDisplay,
                SourcePath = asset.Path,
                ThumbPath = asset.ThumbnailPath,
                OriginalAsset = asset
            });
        }
        await FilterAssets();

        return;
        // 加载远程可重用资产（从多个服务器）
        try
        {
            var multiServerService = MultiServerRemoteAssetService.Instance;
            var assetsWithServerInfo = await multiServerService.GetAllAssetsFromAllServersAsync();

            foreach (var assetInfo in assetsWithServerInfo.OrderBy(a => a.Asset.Name))
            {
                ReuseableAssets.Add(new AssetItemViewModel
                {
                    Id = assetInfo.Asset.AssetId,
                    Name = assetInfo.Asset.Name,
                    Type = assetInfo.Asset.AssetType.ToString(),
                    SourcePath = assetInfo.Asset.Path,
                    DurationDisplay = "?",
                    ThumbPath = assetInfo.Asset.ThumbnailPath,
                    OriginalAsset = assetInfo.Asset,
                    IsRemote = true,
                    ServerId = assetInfo.ServerId,
                    ServerName = assetInfo.ServerName,
                    ServerUrl = assetInfo.ServerUrl
                });
            }
        }
        catch (Exception ex)
        {
            Log(ex, "load reuseable asset", this);
        }

        // 初始化过滤列表
        await FilterAssets();
    }

    private async Task AddTextClip()
    {
        var text = await _draftPage.DisplayPromptAsync("Add", "Input text");
        if (!string.IsNullOrWhiteSpace(text))
        {
            var trackIndex = _draftPage.Tracks.Keys.Where(k => k >= DraftPage.SubTrackOffset).DefaultIfEmpty(DraftPage.SubTrackOffset).Max();
            if (!_draftPage.Tracks.ContainsKey(trackIndex))
            {
                _draftPage.AddASubTrack(trackIndex);
            }

            var element = _draftPage.CreateAndAddClip(
                startX: 0,
                width: _draftPage.FrameToPixel(300),
                trackIndex: trackIndex,
                id: null,
                labelText: "Text 1",
                background: new SolidColorBrush(Colors.MediumPurple),
                resolveOverlap: true,
                relativeStart: 0,
                maxFrames: 0
            );

            element.ClipType = ClipMode.TextClip;
            element.SourcePath = text;
            element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
            element.isInfiniteLength = true;
            element.maxFrameCount = 0;
            element.ExtraData = new()
            {
                { "fontSize", 48 },
                { "fontColor", Colors.White.ToHex() }
            };

            ClipAdded?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task AddSolidColorClip()
    {
        var trackIndex = _draftPage.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(0).Max();
        if (!_draftPage.Tracks.ContainsKey(trackIndex))
        {
            _draftPage.AddATrack(trackIndex);
        }

        ushort R = 65535, G = 65535, B = 65535, A = 65535;
#if WINDOWS
        var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
        {
            ColorSpectrumShape = Microsoft.UI.Xaml.Controls.ColorSpectrumShape.Ring,
            IsMoreButtonVisible = true,
            IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
            IsAlphaEnabled = true,
            IsAlphaSliderVisible = true,
            IsAlphaTextInputVisible = true,
        };
        Microsoft.UI.Xaml.Controls.ContentDialog diag = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "Pick a color",
            Content = picker,
            CloseButtonText = Localized._Cancel,
            PrimaryButtonText = Localized._OK,
        };

        var services = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services;
        var dialogueHelper = services?.GetService(typeof(projectFrameCut.Platforms.Windows.IDialogueHelper)) as projectFrameCut.Platforms.Windows.IDialogueHelper;
        if (dialogueHelper != null)
        {
            var r = await dialogueHelper.ShowContentDialogue(diag);
            var color = picker.Color;
            R = (ushort)(color.R * 257);
            G = (ushort)(color.G * 257);
            B = (ushort)(color.B * 257);
            A = (ushort)(color.A * 257);
        }
#endif



        var element = _draftPage.CreateAndAddClip(
            startX: 0,
            width: _draftPage.FrameToPixel(90),
            trackIndex: trackIndex,
            id: null,
            labelText: $"#{R / 257:X2}{G / 257:X2}{B / 257:X2}{A / 257:X2}",
            background: new SolidColorBrush(Colors.MediumPurple),
            resolveOverlap: true,
            relativeStart: 0,
            maxFrames: 0
        );

        element.ClipType = ClipMode.SolidColorClip;
        element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
        element.isInfiniteLength = true;
        element.maxFrameCount = 0;
        element.ExtraData["R"] = R;
        element.ExtraData["G"] = G;
        element.ExtraData["B"] = B;
        element.ExtraData["A"] = A;
        element.isInfiniteLength = true;

        ClipAdded?.Invoke(this, EventArgs.Empty);
    }

    private async Task AddSubTitleClip()
    {
        var text = await _draftPage.DisplayPromptAsync("Add", "Input subtitle text");
        if (!string.IsNullOrWhiteSpace(text))
        {
            var trackIndex = _draftPage.Tracks.Keys.Where(k => k >= DraftPage.SubTrackOffset).DefaultIfEmpty(DraftPage.SubTrackOffset).Max();
            if (!_draftPage.Tracks.ContainsKey(trackIndex))
            {
                _draftPage.AddASubTrack(trackIndex);
            }

            var element = _draftPage.CreateAndAddClip(
                startX: 0,
                width: _draftPage.FrameToPixel(300),
                trackIndex: trackIndex,
                id: null,
                labelText: "Subtitle 1",
                background: new SolidColorBrush(Colors.Yellow),
                resolveOverlap: true,
                relativeStart: 0,
                maxFrames: 0
            );

            element.ClipType = ClipMode.SubtitleClip;
            element.SourcePath = text;
            element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
            element.isInfiniteLength = true;
            element.maxFrameCount = 0;
            element.ExtraData = new()
            {
                { "fontSize", 32 },
                { "fontColor", Colors.White.ToHex() }
            };

            ClipAdded?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task AddAlternativeSourceClip()
    {
        var path = await _draftPage.DisplayPromptAsync("Add", "Input source path", placeholder: "#<provider>,<stream id>");
        if (string.IsNullOrWhiteSpace(path)) return;

        var trackIndex = _draftPage.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(0).Max();
        if (!_draftPage.Tracks.ContainsKey(trackIndex))
        {
            _draftPage.AddATrack(trackIndex);
        }

        var vidSrc = PluginManager.CreateVideoSource(path);

        var element = _draftPage.CreateAndAddClip(
            startX: 0,
            width: _draftPage.FrameToPixel((uint)vidSrc.TotalFrames),
            trackIndex: trackIndex,
            id: null,
            labelText: "Alternative source 1",
            background: new SolidColorBrush(Colors.MediumPurple),
            resolveOverlap: true,
            relativeStart: 0,
            maxFrames: 0
        );

        element.ClipType = ClipMode.VideoClip;
        element.SourcePath = path;
        element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
        element.isInfiniteLength = false;
        element.maxFrameCount = (uint)vidSrc.TotalFrames;
        element.sourceSecondPerFrame = (float)(1 / vidSrc.Fps);
        element.ExtraData = new();

        ClipAdded?.Invoke(this, EventArgs.Empty);
    }

    private void LoadTransforms()
    {
        AvailableTransforms.Clear();
        var localizedNames = TransformServices.GetLocalizedTransformNames();
        foreach (var kvp in localizedNames)
        {
            AvailableTransforms.Add(new TransformItemViewModel
            {
                TypeKey = kvp.Key,
                DisplayName = kvp.Value
            });
        }
    }

    private async Task AddTransformClip(TransformItemViewModel? transform)
    {
        if (transform is null) return;
        _draftPage.AddTransformToNeighbors(transform.TypeKey);
        ClipAdded?.Invoke(this, EventArgs.Empty);
    }

    private async Task AddAssetClip(AssetItemViewModel? assetViewModel)
    {
        if (assetViewModel?.OriginalAsset == null) return;

        var trackIndex = _draftPage.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(0).Max();
        if (!_draftPage.Tracks.ContainsKey(trackIndex))
        {
            _draftPage.AddATrack(trackIndex);
        }

        var clip = _draftPage.CreateFromAsset(
            assetViewModel.OriginalAsset,
            trackIndex,
            InternalPluginBase.InternalPluginBaseID,
            _draftPage.Assets.ContainsKey(assetViewModel.Id) ? assetViewModel.SourcePath : null
        );

        _draftPage.RegisterClip(clip, true);
        _draftPage.AddAClip(clip);
        ClipAdded?.Invoke(this, EventArgs.Empty);
    }

    private async Task AddReuseableAssetClip(AssetItemViewModel? assetViewModel)
    {
        if (assetViewModel?.OriginalAsset == null) return;

        try
        {
            // 如果是远程资产，需要先获取访问 token 并下载
            if (assetViewModel.IsRemote)
            {
                // 显示加载提示
                // TODO: 添加加载指示器

                // 从多服务器系统获取文件 token
                var multiServerService = MultiServerRemoteAssetService.Instance;
                var tokenResponse = await multiServerService.GetFileTokenAsync(assetViewModel.ServerId ?? "", assetViewModel.Id);

                if (tokenResponse == null)
                {
                    await _draftPage.DisplayAlert("错误", "无法获取文件访问令牌", "确定");
                    return;
                }

                // 构建文件下载 URL（使用资产所属服务器的 URL）
                var serverBaseUrl = assetViewModel.ServerUrl?.TrimEnd('/') ?? "";
                var fileServerUri = new Uri($"{serverBaseUrl}/api/file/download?token={tokenResponse.token}");

                Log($"Downloading asset from {fileServerUri}...");

                // 下载文件到缓存目录
                var cacheDir = Path.Combine(FileSystem.CacheDirectory, "RemoteAssets", assetViewModel.ServerId ?? "default");
                if (!Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }

                var fileName = Path.GetFileName(assetViewModel.OriginalAsset.Path) ?? $"{assetViewModel.Id}{Path.GetExtension(assetViewModel.OriginalAsset.Path)}";
                var localPath = Path.Combine(cacheDir, fileName);

                // 如果文件已经存在，直接使用
                if (!File.Exists(localPath))
                {
#if DEBUG
                    // 开发环境：忽略 SSL 证书验证
                    var handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
                    };
                    using var client = new HttpClient(handler);
#else
                    using var client = new HttpClient();
#endif
                    var response = await client.GetAsync(fileServerUri);
                    response.EnsureSuccessStatusCode();

                    using var fileStream = File.Create(localPath);
                    await response.Content.CopyToAsync(fileStream);
                }

                // 更新资产的路径为本地缓存路径
                assetViewModel.OriginalAsset.Path = localPath;
            }

            var trackIndex = _draftPage.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(0).Max();
            if (!_draftPage.Tracks.ContainsKey(trackIndex))
            {
                _draftPage.AddATrack(trackIndex);
            }

            var clip = _draftPage.CreateFromAsset(
                assetViewModel.OriginalAsset,
                trackIndex,
                InternalPluginBase.InternalPluginBaseID,
                assetViewModel.SourcePath
            );

            _draftPage.RegisterClip(clip, true);

            _draftPage.AddAClip(clip);

            ClipAdded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log(ex, "load reuseabel asset", this);
            await _draftPage.DisplayAlert("Error", $"Failed to add asset: {ex.Message}", "OK");
        }
    }

    private async Task FilterAssets()
    {
        FilteredLocalAssets.Clear();
        FilteredSharedAssets.Clear();
        FilteredReuseableAssets.Clear();

        List<AssetItemViewModel> localFiltered = new();
        List<AssetItemViewModel> sharedFiltered = new();
        List<AssetItemViewModel> reuseableFiltered = new();

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            // 如果搜索文本为空，显示所有素材
            localFiltered.AddRange(LocalAssets);
            sharedFiltered.AddRange(SharedAssets);
            reuseableFiltered.AddRange(ReuseableAssets);
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
                if (asset.Name.ToLower().Contains(searchLower) || assetPron.Contains(SearchText) || assetPron.Contains(inputPron) || assetPron.Contains(inputPronInLocate) || assetPronInLocate.Contains(SearchText) || assetPronInLocate.Contains(inputPron) || assetPronInLocate.Contains(inputPronInLocate))
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
            foreach (var asset in ReuseableAssets)
            {
                var assetPron = (await TextHelper.GetHowToPronuce(asset.Name, default)).ToLower();
                var assetPronInLocate = (await TextHelper.GetHowToPronuce(asset.Name, TextHelper.FromLanguageCode(Localized._LocaleId_))).ToLower();
                if (asset.Name.ToLower().Contains(searchLower) || assetPron.Contains(SearchText) || assetPron.Contains(inputPron) || assetPron.Contains(inputPronInLocate) || assetPronInLocate.Contains(SearchText) || assetPronInLocate.Contains(inputPron) || assetPronInLocate.Contains(inputPronInLocate))
                {
                    reuseableFiltered.Add(asset);
                }
            }
        }

        // 应用排序
        if (OrderOption == 0)
        {
            // By add date - 使用原始资产的创建时间
            localFiltered = localFiltered.OrderByDescending(a => a.OriginalAsset?.CreatedAt ?? DateTime.MinValue).ToList();
            sharedFiltered = sharedFiltered.OrderByDescending(a => a.OriginalAsset?.CreatedAt ?? DateTime.MinValue).ToList();
            reuseableFiltered = reuseableFiltered.OrderByDescending(a => a.OriginalAsset?.CreatedAt ?? DateTime.MinValue).ToList();
        }
        else if (OrderOption == 1)
        {
            // By name - 使用发音排序
            localFiltered = (await localFiltered.OrderByPronounceAsync(a => a.Name)).ToList();
            sharedFiltered = (await sharedFiltered.OrderByPronounceAsync(a => a.Name)).ToList();
            reuseableFiltered = (await reuseableFiltered.OrderByPronounceAsync(a => a.Name)).ToList();
        }

        foreach (var asset in localFiltered)
        {
            FilteredLocalAssets.Add(asset);
        }
        foreach (var asset in sharedFiltered)
        {
            FilteredSharedAssets.Add(asset);
        }
        foreach (var asset in reuseableFiltered)
        {
            FilteredReuseableAssets.Add(asset);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class TransformItemViewModel
{
    public string TypeKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public class AssetItemViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string DurationDisplay { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string? ThumbPath { get; set; }
    public AssetItem? OriginalAsset { get; set; }
    public bool IsRemote { get; set; } = false;

    /// <summary>
    /// 远程服务器ID（用于多服务器支持）
    /// </summary>
    public string? ServerId { get; set; }

    /// <summary>
    /// 远程服务器名称
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// 远程服务器URL
    /// </summary>
    public string? ServerUrl { get; set; }

    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbPath);

    public Brush BackgroundBrush
    {
        get => ClipElementUI.DetermineAssetColor(OriginalAsset?.Type ?? ClipMode.Special);
    }
}
