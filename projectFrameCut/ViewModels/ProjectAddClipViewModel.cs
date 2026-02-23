using CommunityToolkit.Maui.Core;
using projectFrameCut.APIClient;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
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
    #region menber
    public ProjectAddClipViewModel(ref DraftPage draftPage)
    {
        _draftPage = draftPage;


        Refresh();
        _draftPage.SelectedClipChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(IsDraftSelectedAnyClip));
            LoadTransforms();
        };
    }

    private readonly DraftPage _draftPage;

    public bool TransformMenuActivatedViaHandleClick = false;

    public string SearchText
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                FilterAssets();
            }
        }
    } = "";

    public int OrderOption
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                FilterAssets();
            }
        }
    } = 0;

    public bool IsDraftSelectedAnyClip
    {
        get => _draftPage.SelectedClip != null;
    }


    // 文本样式支持
    public TextStyleItemViewModel? SelectedTextStyle
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    }

    public string TextToAdd
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    } = string.Empty;

    // 绘图颜色与线宽
    public Color DrawingPenColor
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (_drawingView != null)
                    _drawingView.LineColor = value;
            }
        }
    } = Colors.Black;

    public float DrawingLineWidth
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (_drawingView != null)
                    _drawingView.LineWidth = value;
            }
        }
    } = 5f;


    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    #endregion

    #region data
    public ObservableCollection<AssetItemViewModel> LocalAssets { get; } = new();
    public ObservableCollection<AssetItemViewModel> SharedAssets { get; } = new();
    public ObservableCollection<AssetItemViewModel> ReuseableAssets { get; } = new();
    public ObservableCollection<TransformItemViewModel> AvailableTransforms { get; } = new();
    public ObservableCollection<TextStyleItemViewModel> AvailableTextStyles { get; } = new();

    public ObservableCollection<AssetItemViewModel> FilteredLocalAssets { get; } = new();
    public ObservableCollection<AssetItemViewModel> FilteredSharedAssets { get; } = new();
    public ObservableCollection<AssetItemViewModel> FilteredReuseableAssets { get; } = new();
    public ObservableCollection<TransformItemViewModel> FilteredAvailableTransforms { get; } = new();
    public ObservableCollection<TextStyleItemViewModel> FilteredAvailableTextStyles { get; } = new();
    #endregion

    #region command
    public ICommand AddTextClipCommand { get; set; }
    public ICommand AddSolidColorClipCommand { get; set; }
    public ICommand AddTextClipWithStyleCommand { get; set; }
    public ICommand AddSubTitleClipCommand { get; set; }
    public ICommand AddAlternativeSourceClipCommand { get; set; }
    public ICommand AddAssetClipCommand { get; set; }
    public ICommand AddReuseableAssetClipCommand { get; set; }
    public ICommand AddTransformClipCommand { get; set; }
    public ICommand AddTransformClipInLeftCommand { get; set; }
    public ICommand AddTransformClipInRightCommand { get; set; }

    // 绘图命令
    public ICommand DrawingContentUndoCommand { get; set; }
    public ICommand DrawingContentRedoCommand { get; set; }
    public ICommand DrawingSelectPenColorCommand { get; set; }
    public ICommand AddDrawingContentCommand { get; set; }

    void RegisterCommands()
    {
        AddSolidColorClipCommand = new Command(async () => await AddSolidColorClip());
        AddTextClipWithStyleCommand = new Command<TextStyleItemViewModel?>(async (style) => await AddTextClipWithStyle(style));
        AddAlternativeSourceClipCommand = new Command(async () => await AddAlternativeSourceClip());
        AddAssetClipCommand = new Command<AssetItemViewModel>(async (asset) => await AddAssetClip(asset));
        AddReuseableAssetClipCommand = new Command<AssetItemViewModel>(async (asset) => await AddReuseableAssetClip(asset));
        AddTransformClipCommand = new Command<TransformItemViewModel>(async (t) => await AddTransformClip(t, false, false));
        AddTransformClipInLeftCommand = new Command<TransformItemViewModel>(async (t) => await AddTransformClip(t, true, false));
        AddTransformClipInRightCommand = new Command<TransformItemViewModel>(async (t) => await AddTransformClip(t, false, true));

        DrawingContentUndoCommand = new Command(DrawingUndo);
        DrawingContentRedoCommand = new Command(DrawingRedo);
        DrawingSelectPenColorCommand = new Command(async () => await SelectDrawingPenColor());
        AddDrawingContentCommand = new Command(async () => await AddDrawingContent());
    }
    #endregion

    #region load

    public event EventHandler? ClipAdded;

    public async Task Refresh()
    {
        RegisterCommands();
        await LoadAssets();
        LoadTransforms();
        InitializeDefaultTextStyles();
    }

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
                OriginalAsset = asset,
                BackgroundBrush = ClipElementUI.DetermineAssetColor(asset.AssetType)
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
                OriginalAsset = asset,
                BackgroundBrush = ClipElementUI.DetermineAssetColor(asset.AssetType)

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

    #endregion

    #region asset
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

    #endregion

    #region text

    private async Task AddTextClipWithStyle(TextStyleItemViewModel? styleOverride = null)
    {
        var style = styleOverride ?? SelectedTextStyle;
        if (style is null) return;
        var text = TextToAdd;
        if (string.IsNullOrWhiteSpace(text)) return;


        int trackIndex = 0;
        if (style.ShouldInSubtrack)
        {
            trackIndex = _draftPage.Tracks.Keys.Where(k => k >= DraftPage.SubTrackOffset).DefaultIfEmpty(DraftPage.SubTrackOffset).Max();
            if (!_draftPage.Tracks.ContainsKey(trackIndex))
            {
                _draftPage.AddASubTrack(trackIndex);
            }
        }
        else
        {
            trackIndex = _draftPage.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(0).Max();
            if (!_draftPage.Tracks.ContainsKey(trackIndex))
            {
                _draftPage.AddATrack(trackIndex);
            }
        }


        var element = _draftPage.CreateAndAddClip(
            startX: 0,
            width: _draftPage.FrameToPixel(300),
            trackIndex: trackIndex,
            id: null,
            labelText: text,
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
        element.ExtraData = new();

        if (style != null)
        {
            element.ExtraData["TextEntries"] = new List<TextClip.TextClipEntry>
            {
                style.ActualTemplate with { text = text }
            };

        }

        ClipAdded?.Invoke(this, EventArgs.Empty);
        TextToAdd = "";
    }

    private void InitializeDefaultTextStyles()
    {
        AvailableTextStyles.Clear();
        AvailableTextStyles.Add(new TextStyleItemViewModel
        {
            Id = "default",
            Name = "Default",
            SampleText = "Normal",
            FontSize = 36,
            FontColor = Colors.White.ToHex(),
            ActualTemplate = new TextClip.TextClipEntry
            {
                r = 65535,
                g = 65535,
                b = 65535,
                a = null,
                fontFamily = "Arial",
                fontSize = 36
            }
        });
        AvailableTextStyles.Add(new TextStyleItemViewModel
        {
            Id = "title",
            Name = "Title",
            SampleText = "Title",
            FontSize = 64,
            FontColor = Colors.White.ToHex(),
            ActualTemplate = new TextClip.TextClipEntry
            {
                r = 65535,
                g = 65535,
                b = 65535,
                a = null,
                fontFamily = "Arial",
                fontSize = 64
            }
        });
        AvailableTextStyles.Add(new TextStyleItemViewModel
        {
            Id = "subtitle",
            Name = "Subtitle",
            SampleText = "Subtitle",
            FontSize = 32,
            FontColor = Colors.White.ToHex(),
            ShouldInSubtrack = true,
            ActualTemplate = new TextClip.TextClipEntry
            {
                r = 65535,
                g = 65535,
                b = 65535,
                a = null,
                fontFamily = "Arial",
                fontSize = 32,
                ShouldInSubtrack = true,
            }
        });

        SelectedTextStyle = AvailableTextStyles.FirstOrDefault();

        // 同步过滤
        FilteredAvailableTextStyles.Clear();
        var searchLower = SearchText?.ToLower() ?? "";
        foreach (var s in AvailableTextStyles)
        {
            if (string.IsNullOrWhiteSpace(searchLower) ||
                s.Name.ToLower().Contains(searchLower) ||
                s.SampleText.ToLower().Contains(searchLower))
            {
                FilteredAvailableTextStyles.Add(s);
            }
        }
    }

    #endregion

    #region transform    

    public void LoadTransforms()
    {
        AvailableTransforms.Clear();
        var localizedNames = TransformServices.GetLocalizedTransformNames();
        var sideDetermined = _draftPage._transformMenuActivatedHandle == "left" || _draftPage._transformMenuActivatedHandle == "right";
        var applicableToLeft = _draftPage.FindNeighbors(_draftPage?.SelectedClip).left is not null;
        var applicableToRight = _draftPage.FindNeighbors(_draftPage?.SelectedClip).right is not null;
        foreach (var kvp in localizedNames)
        {
            AvailableTransforms.Add(new TransformItemViewModel
            {
                TypeKey = kvp.Key,
                DisplayName = kvp.Value,
                IsSideDetermined = sideDetermined,
                IsApplicableToLeft = applicableToLeft,
                IsApplicableToRight = applicableToRight,
                IsDraftSelectedAnyClip = IsDraftSelectedAnyClip
            });
        }

        // 同步过滤
        FilteredAvailableTransforms.Clear();
        var searchLower = SearchText?.ToLower() ?? "";
        foreach (var t in AvailableTransforms)
        {
            if (string.IsNullOrWhiteSpace(searchLower) ||
                t.DisplayName.ToLower().Contains(searchLower) ||
                t.TypeKey.ToLower().Contains(searchLower))
            {
                FilteredAvailableTransforms.Add(t);
            }
        }
    }

    private async Task AddTransformClip(TransformItemViewModel? transform, bool left, bool right)
    {
        if (transform is null) return;
        if (transform.IsSideDetermined) _draftPage.AddTransformToNeighbors(transform.TypeKey);
        else
        {
            _draftPage.AddTransformBetweenSelected(transform.TypeKey, _draftPage.SelectedClip, left, right);
        }
        ClipAdded?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region search

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

        // 过滤转场
        FilteredAvailableTransforms.Clear();
        var transformSearch = SearchText?.ToLower() ?? "";
        foreach (var t in AvailableTransforms)
        {
            if (string.IsNullOrWhiteSpace(transformSearch) ||
                t.DisplayName.ToLower().Contains(transformSearch) ||
                t.TypeKey.ToLower().Contains(transformSearch))
            {
                FilteredAvailableTransforms.Add(t);
            }
        }

        // 过滤文字样式
        FilteredAvailableTextStyles.Clear();
        var styleSearch = SearchText?.ToLower() ?? "";
        foreach (var s in AvailableTextStyles)
        {
            if (string.IsNullOrWhiteSpace(styleSearch) ||
                s.Name.ToLower().Contains(styleSearch) ||
                s.SampleText.ToLower().Contains(styleSearch))
            {
                FilteredAvailableTextStyles.Add(s);
            }
        }
    }


    #endregion

    #region sketch

    private CommunityToolkit.Maui.Views.DrawingView? _drawingView;
    private readonly Stack<CommunityToolkit.Maui.Core.IDrawingLine> _redoStack = new();

    public void SetDrawingView(CommunityToolkit.Maui.Views.DrawingView drawingView)
    {
        _drawingView = drawingView;
        _drawingView.LineColor = DrawingPenColor;
        _drawingView.LineWidth = DrawingLineWidth;
        _drawingView.DrawingLineCompleted += (_, _) => _redoStack.Clear();
    }

    private void DrawingUndo()
    {
        if (_drawingView?.Lines is { Count: > 0 } lines)
        {
            var last = lines[^1];
            _redoStack.Push(last);
            lines.RemoveAt(lines.Count - 1);
        }
    }

    private void DrawingRedo()
    {
        if (_redoStack.Count > 0 && _drawingView != null)
        {
            _drawingView.Lines.Add(_redoStack.Pop());
        }
    }

    private async Task SelectDrawingPenColor()
    {
#if WINDOWS
        var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
        {
            ColorSpectrumShape = Microsoft.UI.Xaml.Controls.ColorSpectrumShape.Ring,
            IsMoreButtonVisible = true,
            IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false,
            Color = new Windows.UI.Color
            {
                A = 255,
                R = (byte)(DrawingPenColor.Red * 255),
                G = (byte)(DrawingPenColor.Green * 255),
                B = (byte)(DrawingPenColor.Blue * 255)
            }
        };
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = Localized.DraftPage_AddClipView_AddSketch_SelectColor,
            Content = picker,
            CloseButtonText = Localized._Cancel,
            PrimaryButtonText = Localized._OK,
        };
        var services = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services;
        var dialogueHelper = services?.GetService(typeof(projectFrameCut.Platforms.Windows.IDialogueHelper)) as projectFrameCut.Platforms.Windows.IDialogueHelper;
        if (dialogueHelper != null)
        {
            await dialogueHelper.ShowContentDialogue(dialog);
            var c = picker.Color;
            DrawingPenColor = Color.FromRgb(c.R, c.G, c.B);
        }
#else
        // 非 Windows 平台：循环切换常用颜色
        Color[] palette = [Colors.Black, Colors.Red, Colors.Blue, Colors.Green, Colors.Orange, Colors.Purple, Colors.White];
        var idx = Array.FindIndex(palette, c => c == DrawingPenColor);
        DrawingPenColor = palette[(idx + 1) % palette.Length];
#endif
    }

    private async Task AddDrawingContent()
    {
        if (_drawingView == null || _drawingView.Lines.Count == 0)
        {
            await _draftPage.DisplayAlertAsync(
                Localized._Error,
                Localized.DraftPage_AddClipView_AddSketch_NoContent,
                Localized._OK);
            return;
        }

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var imageStream = await _drawingView.GetImageStream(
                1920, 1080,
                CommunityToolkit.Maui.Core.DrawingViewOutputOption.FullCanvas,
                cts.Token);

            if (imageStream == Stream.Null || imageStream.Length == 0)
            {
                await _draftPage.DisplayAlertAsync("Error", "Failed to capture drawing.", Localized._OK);
                return;
            }

            // 保存到临时文件
            var sketchDir = Path.Combine(FileSystem.CacheDirectory, "Sketches");
            Directory.CreateDirectory(sketchDir);
            var tempPath = Path.Combine(sketchDir, $"sketch_{Guid.NewGuid():N}.png");

            using (var fs = File.Create(tempPath))
            {
                await imageStream.CopyToAsync(fs);
            }

            // 添加到时间轴（Photo Clip，默认 90 帧 / 3 秒）
            const uint defaultFrames = 90u;
            var trackIndex = _draftPage.Tracks.Keys
                .Where(k => k < DraftPage.SubTrackOffset)
                .DefaultIfEmpty(0)
                .Max();
            if (!_draftPage.Tracks.ContainsKey(trackIndex))
                _draftPage.AddATrack(trackIndex);

            var element = _draftPage.CreateAndAddClip(
                startX: 0,
                width: _draftPage.FrameToPixel(defaultFrames),
                trackIndex: trackIndex,
                id: null,
                labelText: "Sketch 1",
                background: new SolidColorBrush(Colors.MediumSeaGreen),
                resolveOverlap: true,
                relativeStart: 0,
                maxFrames: defaultFrames
            );

            element.ClipType = ClipMode.PhotoClip;
            element.SourcePath = tempPath;
            element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
            element.isInfiniteLength = false;
            element.maxFrameCount = defaultFrames;
            element.ExtraData = new();

            // 清空画布
            _drawingView.Lines.Clear();
            _redoStack.Clear();

            ClipAdded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log(ex, "AddDrawingContent", this);
            await _draftPage.DisplayAlertAsync("Error", $"Failed to add sketch: {ex.Message}", Localized._OK);
        }
    }

    #endregion

    #region misc
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
    #endregion

}

#region vm

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

    public Brush BackgroundBrush { get; set; } = new SolidColorBrush(Colors.CornflowerBlue);
}

public class TransformItemViewModel
{
    public string TypeKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsApplicableToLeft { get; set; } = false;
    public bool IsApplicableToRight { get; set; } = false;
    public bool IsSideDetermined { get; set; } = false;
    public bool IsDraftSelectedAnyClip { get; set; } = false;
}

public class TextStyleItemViewModel
{
    public required string Id { get; set; } = string.Empty;
    public required string Name { get; set; } = string.Empty;
    public required string SampleText { get; set; } = string.Empty;
    public required TextClip.TextClipEntry ActualTemplate { get; set; } = null;
    public int FontSize { get; set; } = 36;
    public string? FontColor { get; set; }
    public FontAttributes FontAttribute { get; set; } = FontAttributes.None;
    public bool ShouldInSubtrack { get; set; } = false;
}

#endregion
