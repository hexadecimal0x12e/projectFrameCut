using CommunityToolkit.Maui.Core;
using projectFrameCut.APIClient;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingPages;
using projectFrameCut.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using projectFrameCut.Render.Rendering;
using projectFrameCut.AIAssistance;
using IPicture = projectFrameCut.Shared.IPicture;
using Microsoft.Maui.Storage;
using projectFrameCut.Render.Transform;
using static projectFrameCut.ApplicationAPIBase.Helpers.TextHelper;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;

namespace projectFrameCut.ViewModels;

public partial class ProjectAddClipViewModel : INotifyPropertyChanged
{
    #region menber
    public ProjectAddClipViewModel(ref DraftPage draftPage)
    {
        _draftPage = draftPage;
        RegisterCommands();

        _ = Task.Run(async () => await Refresh());
        _draftPage.SelectedClipChanged += async (s, e) => await Refresh();
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
                _ = Task.Run(async () => await FilterAssets());
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
                _ = Task.Run(async () => await FilterAssets());
            }
        }
    } = 0;

    public string AIPrompt
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
    } = "";

    public string AIContentType
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
    } = "Image"; // 默认为图片

    public int AIVideoDuration
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
    } = 5; // 默认5秒

    public string AIVideoRatio
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
    } = "16:9"; // 默认16:9比例

    public string AIImageStyle
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
    } = Localized.DraftPage_AddClipView_AIGC_ImageStyle_Natural;

    public bool IsGeneratingAIContent
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                _draftPage?.IsPopupClosableByTapBackground = !field;
                OnPropertyChanged();
            }
        }
    } = false;

    // AI转场生成相关属性
    public string AITransitionPrompt
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
    } = "";

    public int AITransitionDuration
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
    } = 2; // 默认2秒

    public bool IsGeneratingAITransition
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                _draftPage?.IsPopupClosableByTapBackground = !field;
                OnPropertyChanged();
            }
        }
    } = false;

    // 基于选中Clip获取相邻片段信息
    public bool CanGenerateAITransition
    {
        get
        {
            if (_draftPage?.SelectedClip == null) return false;
            var (left, right) = _draftPage.FindNeighbors(_draftPage.SelectedClip);
            return left != null || right != null;
        }
    }

    public bool IsDraftSelectedAnyClip
    {
        get => _draftPage.SelectedClip != null;
    }

    public bool IsSideDetermined
    {
        get;
        private set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsApplicableToLeft
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
    public bool IsApplicableToRight
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

    public string TransformAddHint
    {
        get
        {
            var neighbors = _draftPage.FindNeighbors(_draftPage.SelectedClip);

            if (IsSideDetermined)
            {
                return Localized.DraftPage_AddClipView_AddTransform_AddToTargetClipTipSideDetermined_WithNameHint(neighbors.left?.DisplayName ?? "Left", neighbors.right?.DisplayName ?? "Right");
            }
            else
            {
                return Localized.DraftPage_AddClipView_AddTransform_AddToTargetClipTip_WithNameHint(neighbors.left?.DisplayName ?? "Left", _draftPage?.SelectedClip?.DisplayName ?? "Center", neighbors.right?.DisplayName ?? "Right");

            }
        }
    }

    public string TransformAddHintText =>
        IsDraftSelectedAnyClip
            ? TransformAddHint
            : Localized.DraftPage_AddClipView_AddTransform_NoTargetClip;

    // 文本样式支持
    public TextStyleItemViewModel? SelectedTextStyle
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                TextClipInSubTrack = value?.ShouldInSubtrack ?? false;
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

    public bool TextClipInSubTrack
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

    public TransformItemViewModel? SelectedTransformForPreview
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
    public ICommand AddTextClipCommand { get; set; } = null!;
    public ICommand AddSolidColorClipCommand { get; set; } = null!;
    public ICommand AddTextClipWithStyleCommand { get; set; } = null!;
    public ICommand GenerateTextPreviewCommand { get; set; } = null!;
    public ICommand AddSubTitleClipCommand { get; set; } = null!;
    public ICommand AddAlternativeSourceClipCommand { get; set; } = null!;
    public ICommand AddAssetClipCommand { get; set; } = null!;
    public ICommand AddReuseableAssetClipCommand { get; set; } = null!;
    public ICommand AddTransformClipCommand { get; set; } = null!;
    public ICommand AddTransformClipInLeftCommand { get; set; } = null!;
    public ICommand AddTransformClipInRightCommand { get; set; } = null!;
    public ICommand GenerateTransformPreviewCommand { get; set; } = null!;
    public ICommand SelectTransformForPreviewCommand { get; set; } = null!;
    public ICommand GenerateAIContentCommand { get; set; } = null!;

    // AI转场生成相关命令
    public ICommand GenerateAITransitionCommand { get; set; } = null!;

    public ICommand DrawingContentUndoCommand { get; set; } = null!;
    public ICommand DrawingContentRedoCommand { get; set; } = null!;
    public ICommand DrawingSelectPenColorCommand { get; set; } = null!;
    public ICommand AddDrawingContentCommand { get; set; } = null!;

    void RegisterCommands()
    {
        AddSolidColorClipCommand = new Command(async () => await AddSolidColorClip());
        AddTextClipWithStyleCommand = new Command<TextStyleItemViewModel?>(async (style) => await AddTextClipWithStyle(style));
        GenerateTextPreviewCommand = new Command(async () => InitializeTextStyles(string.IsNullOrWhiteSpace(TextToAdd) ? null : TextToAdd));
        AddAlternativeSourceClipCommand = new Command(async () => await AddAlternativeSourceClip());
        AddAssetClipCommand = new Command<AssetItemViewModel>(async (asset) => await AddAssetClip(asset));
        AddReuseableAssetClipCommand = new Command<AssetItemViewModel>(async (asset) => await AddReuseableAssetClip(asset));
        AddTransformClipCommand = new Command<TransformItemViewModel>(async (t) => await AddTransformClip(t, false, false));
        AddTransformClipInLeftCommand = new Command<TransformItemViewModel>(async (t) => await AddTransformClip(t, true, false));
        AddTransformClipInRightCommand = new Command<TransformItemViewModel>(async (t) => await AddTransformClip(t, false, true));
        GenerateTransformPreviewCommand = new Command<TransformItemViewModel?>(async (t) => await GenerateTransformPreviewAsync(t ?? SelectedTransformForPreview));
        SelectTransformForPreviewCommand = new Command<TransformItemViewModel?>(t => SelectedTransformForPreview = t);

        DrawingContentUndoCommand = new Command(DrawingUndo);
        DrawingContentRedoCommand = new Command(DrawingRedo);
        DrawingSelectPenColorCommand = new Command(async () => await SelectDrawingPenColor());
        AddDrawingContentCommand = new Command(async () => await AddDrawingContent());
        GenerateAIContentCommand = new Command(async () => await GenerateAIContent());

        // AI转场生成命令
        GenerateAITransitionCommand = new Command(async (d) => await GenerateAITransition(d));
    }
    #endregion

    #region load

    public event EventHandler? ClipAdded;

    public async Task Refresh()
    {
        RegisterCommands();
        await LoadAssets();
        LoadTransforms();
        InitializeTextStyles();
        var neighbors = _draftPage.FindNeighbors(_draftPage.SelectedClip);
        IsApplicableToLeft = neighbors.left != null;
        IsApplicableToRight = neighbors.right != null;
        OnPropertyChanged(nameof(IsDraftSelectedAnyClip));
        OnPropertyChanged(nameof(IsApplicableToLeft));
        OnPropertyChanged(nameof(IsApplicableToRight));
        OnPropertyChanged(nameof(CanGenerateAITransition));
        OnPropertyChanged(nameof(TransformAddHint));
        OnPropertyChanged(nameof(TransformAddHintText));
        LoadTransforms();
    }

    private async Task LoadAssets()
    {
        LocalAssets.Clear();
        SharedAssets.Clear();
        ReuseableAssets.Clear();

        foreach (var asset in _draftPage.Assets.Values.Where(c => c.AssetType != AssetType.Font).OrderBy(a => a.Name))
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

        foreach (var asset in AssetDatabase.Assets.Values.Where(c => c.AssetType != AssetType.Font).OrderBy(a => a.Name))
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

        // 注释：暂时禁用远程可重用资产加载
        /*
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
        */
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
        if (TextClipInSubTrack)
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
        element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
        element.isInfiniteLength = true;
        element.maxFrameCount = 0;
        element.ExtraData = new();

        var textLang = DetectTextLanguage(text);
        var fontOverride = style.ActualTemplate.fontFamily;
        if (style.ActualTemplate.fontFamily == "Arial")
        {
            if (textLang != TextLanguage.English)
            {
                fontOverride = textLang switch
                {
                    TextLanguage.Chinese => Localized._LocaleId_ == "zh-TW" ? "Noto Sans TC" : "Noto Sans SC",
                    TextLanguage.Japanese => "Noto Sans JP",
                    TextLanguage.Korean => "Noto Sans KR",
                    TextLanguage.Arabic => "HarmonyOS Sans Naskh Arabic",
                    _ => "Noto Sans"
                };
            }
        }

        if (style != null)
        {
            element.ExtraData["TextEntries"] = new List<TextClipEntry>
            {
                style.ActualTemplate with { text = text, fontFamily = fontOverride }
            };

        }

        ClipAdded?.Invoke(this, EventArgs.Empty);
        TextToAdd = "";
    }

    private void InitializeTextStyles(string? previewText = null)
    {

        AvailableTextStyles.Clear();
        try
        {
            Dictionary<string, TextClipEntry> template = new();
            EditSettingPage.LoadTextTemplates(ref template);
            foreach (var item in template)
            {
                AvailableTextStyles.Add(new TextStyleItemViewModel
                {
                    Id = item.Key,
                    Name = item.Key,
                    SampleText = previewText ?? item.Value.SampleText ?? item.Key,
                    ActualTemplate = item.Value
                });
            }
        }
        catch (Exception ex)
        {
            Log(ex, "Load user text templates", this);
            AvailableTextStyles.Add(new TextStyleItemViewModel
            {
                Id = "default",
                Name = "Default",
                SampleText = "Normal",
                ActualTemplate = new TextClipEntry
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
                ActualTemplate = new TextClipEntry
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
                ShouldInSubtrack = true,
                ActualTemplate = new TextClipEntry
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
        }



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
        IsSideDetermined = _draftPage._transformMenuActivatedHandle == "left" || _draftPage._transformMenuActivatedHandle == "right";
        var applicableToLeft = _draftPage?.SelectedClip != null && _draftPage.FindNeighbors(_draftPage.SelectedClip).left is not null;
        var applicableToRight = _draftPage?.SelectedClip != null && _draftPage.FindNeighbors(_draftPage.SelectedClip).right is not null;
        foreach (var kvp in localizedNames)
        {
            AvailableTransforms.Add(new TransformItemViewModel
            {
                TypeKey = kvp.Key,
                DisplayName = kvp.Value,
                IsSideDetermined = IsSideDetermined,
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

    private async Task GenerateTransformPreviewAsync(TransformItemViewModel? transform)
    {
        if (transform is null || transform.IsGeneratingPreview) return;
        transform.IsGeneratingPreview = true;
        try
        {
            var factories = TransformServices.GetAvailableTransforms();
            if (!factories.TryGetValue(transform.TypeKey, out var factory)) return;

            var previewDir = Path.Combine(FileSystem.CacheDirectory, "TransformPreviews");
            Directory.CreateDirectory(previewDir);
            var videoPath = Path.Combine(previewDir, $"{transform.TypeKey}.mp4");

            if (File.Exists(videoPath)) File.Delete(videoPath);

            const int previewW = 320, previewH = 180, fps = 24, frameCount = 48;

            await Task.Run(() =>
            {
                // Detect a suitable encoder
                string codec;
                if (VideoWriter.DetectCodec("libx264")) codec = "libx264";
                else if (VideoWriter.DetectCodec("mpeg4")) codec = "mpeg4";
                else return; // No encoder available

                // Create two solid-color stub clips so ITransform.Previous / Next are never null.
                // Previous: dark blue (left/outgoing clip), Next: dark red (right/incoming clip).
                var prevId = Guid.NewGuid();
                var nextId = Guid.NewGuid();
                var prevClip = new SolidColorClip
                {
                    Id = prevId.ToString(),
                    Name = "_preview_prev",
                    StartFrame = 0,
                    Duration = (uint)frameCount,
                    FrameTime = 1f / fps,
                    SecondPerFrameRatio = 1f,
                    R = 0x2A00,
                    G = 0x5200,
                    B = 0x8C00,
                };
                var nextClip = new SolidColorClip
                {
                    Id = nextId.ToString(),
                    Name = "_preview_next",
                    StartFrame = (uint)frameCount,
                    Duration = (uint)frameCount,
                    FrameTime = 1f / fps,
                    SecondPerFrameRatio = 1f,
                    R = 0x8C00,
                    G = 0x2A00,
                    B = 0x2A00,
                };

                var t = factory(prevId, nextId);
                try
                {
                    t.Init();

                    using var writer = new VideoWriter
                    {
                        Width = previewW,
                        Height = previewH,
                        FramePerSecond = fps,
                        OutputPath = videoPath,
                        CodecName = codec,
                        PixelFormat = "AV_PIX_FMT_YUV420P"
                    };
                    writer.Initialize();

                    for (uint i = 0; i < frameCount; i++)
                    {
                        double progress = (double)i / Math.Max(1, frameCount - 1);
                        using var frame = TransformProcessing.ProcessTransform(prevClip, nextClip, t, previewW, previewH, i);
                        writer.Append(frame);
                    }

                    writer.Finish();
                }
                finally
                {
                    if (t is IDisposable disposable)
                        disposable.Dispose();
                    prevClip.Dispose();
                    nextClip.Dispose();
                }
            });


            if (File.Exists(videoPath))
            {
                transform.PreviewVideoPath = videoPath;

            }
        }
        catch (Exception ex)
        {
            Log(ex, $"GenerateTransformPreview for {transform.TypeKey}", this);
        }
        finally
        {
            transform.IsGeneratingPreview = false;
        }
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
            var inputPron = (await TextServices.GetHowToPronuce(SearchText, default)).ToLower();
            var inputPronInLocate = ((await TextServices.GetHowToPronuce(SearchText, TextHelper.FromLanguageCode(Localized._LocaleId_)))).ToLower();
            // 过滤素材
            var searchLower = SearchText.ToLower();
            foreach (var asset in LocalAssets)
            {
                var assetPron = (await TextServices.GetHowToPronuce(asset.Name, default)).ToLower();
                var assetPronInLocate = (await TextServices.GetHowToPronuce(asset.Name, TextHelper.FromLanguageCode(Localized._LocaleId_))).ToLower();
                if (asset.Name.ToLower().Contains(searchLower) || assetPron.Contains(SearchText) || assetPron.Contains(inputPron) || assetPron.Contains(inputPronInLocate) || assetPronInLocate.Contains(SearchText) || assetPronInLocate.Contains(inputPron) || assetPronInLocate.Contains(inputPronInLocate))
                {
                    localFiltered.Add(asset);
                }
            }
            foreach (var asset in SharedAssets)
            {
                var assetPron = (await TextServices.GetHowToPronuce(asset.Name, default)).ToLower();
                var assetPronInLocate = (await TextServices.GetHowToPronuce(asset.Name, TextHelper.FromLanguageCode(Localized._LocaleId_))).ToLower();
                if (asset.Name.ToLower().Contains(searchLower) || assetPron.Contains(SearchText) || assetPron.Contains(inputPron) || assetPron.Contains(inputPronInLocate) || assetPronInLocate.Contains(SearchText) || assetPronInLocate.Contains(inputPron) || assetPronInLocate.Contains(inputPronInLocate))
                {
                    sharedFiltered.Add(asset);
                }
            }
            foreach (var asset in ReuseableAssets)
            {
                var assetPron = (await TextServices.GetHowToPronuce(asset.Name, default)).ToLower();
                var assetPronInLocate = (await TextServices.GetHowToPronuce(asset.Name, TextHelper.FromLanguageCode(Localized._LocaleId_))).ToLower();
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

        ushort R = 65535, G = 65535, B = 65535;
        float A = 1f;

        var picker = new ColorPicker();

        var tcs = new TaskCompletionSource();

        var view = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Children =
                {
                    picker,
                    new Button
                    {
                        Text = Localized._Confirm,
                        Command = new Command(() => tcs.SetResult())
                    }
                }
            }
        };

        await _draftPage.ShowAPopup(view, null, null, "dialog");
        await tcs.Task;
        R = (ushort)(picker.SelectedColor.Red * ushort.MaxValue);
        G = (ushort)(picker.SelectedColor.Green * ushort.MaxValue);
        B = (ushort)(picker.SelectedColor.Blue * ushort.MaxValue);
        A = picker.SelectedColor.Alpha;

        var element = _draftPage.CreateAndAddClip(
            startX: 0,
            width: _draftPage.FrameToPixel(90),
            trackIndex: trackIndex,
            id: null,
            labelText: $"#{R / 257:X2}{G / 257:X2}{B / 257:X2}{(int)(A * 255):X2}",
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
                    await _draftPage.DisplayAlertAsync("错误", "无法获取文件访问令牌", "确定");
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
            await _draftPage.DisplayAlertAsync("Error", $"Failed to add asset: {ex.Message}", "OK");
        }
    }
    #endregion

    #region AI Content Generation

    private async Task GenerateAIContent()
    {
        if (string.IsNullOrWhiteSpace(AIPrompt))
        {
            await _draftPage.DisplayAlertAsync(
                Localized._Error,
                "Please input prompt.",
                Localized._OK);
            return;
        }

        if (IsGeneratingAIContent)
        {
            return; // 防止重复触发
        }

        IsGeneratingAIContent = true;

        try
        {
            if (AIContentType == "Image")
            {
                await GenerateAIImage();
            }
            else if (AIContentType == "Video")
            {
                await GenerateAIVideo();
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Generate AI content", this);
            await _draftPage.DisplayAlertAsync(
                Localized._Error,
                $"{SettingsManager.SettingLocalizedResources.AISetting_Test_ErrorResponse}{Environment.NewLine}({Localized._ExceptionTemplate(ex)})",
                Localized._OK);
        }
        finally
        {
            IsGeneratingAIContent = false;
        }
    }

    private async Task GenerateAIImage()
    {
        try
        {
            // 根据比例设定解析宽高（复用视频比例解析逻辑）
            var (width, height) = ParseVideoRatio(AIVideoRatio);

            // 将 UI 风格字符串映射到 ImageStyle 枚举
            var imageStyle = AIImageStyle switch
            {
                var t when t == Localized.DraftPage_AddClipView_AIGC_ImageStyle_Natural => ImageStyle.Natural,
                var t when t == Localized.DraftPage_AddClipView_AIGC_ImageStyle_Vivid => ImageStyle.Vivid,
                var t when t == Localized.DraftPage_AddClipView_AIGC_ImageStyle_Anime => ImageStyle.Anime,
                var t when t == Localized.DraftPage_AddClipView_AIGC_ImageStyle_Photography => ImageStyle.Photography,
                var t when t == Localized.DraftPage_AddClipView_AIGC_ImageStyle_TradidtionalPainting => ImageStyle.TradidtionalPainting,
                _ => ImageStyle.Natural
            };

            // 创建图片生成选项
            var options = new ImageGenerationOptions
            {
                Width = width,
                Height = height,
                Style = imageStyle,
                Quality = ImageQuality.Standard
            };

            // 调用AI生成图片
            var result = await AIHelper.GenerateImageAsync(AIPrompt, options);

            if (!result.Success)
            {
                await _draftPage.DisplayAlertAsync(
                    Localized._Error,
                    result.ErrorMessage ?? SettingsManager.SettingLocalizedResources.AISetting_Test_ErrorResponse,
                    Localized._OK);
                return;
            }

            if (string.IsNullOrWhiteSpace(result.ImageUrl))
            {
                Log("No image URL returned from AI generation.", "error");
                await _draftPage.DisplayAlertAsync(
                    Localized._Error,
                    SettingsManager.SettingLocalizedResources.AISetting_Test_ErrorResponse,
                    Localized._OK);
                return;
            }

            // 下载图片到本地并添加到素材库
            var asset = await DownloadRemoteResourcesToLocal(_draftPage, result.ImageUrl, "png", "AIGenerated-{0}");
            if (asset == null)
            {
                await _draftPage.DisplayAlertAsync(
                    Localized._Error,
                    $"{SettingsManager.SettingLocalizedResources.AISetting_Test_ErrorResponse}{Environment.NewLine}(Cannot download result.)",
                    Localized._OK);
                return;
            }

            // 添加图片到时间轴
            await AddAIGeneratedImageToTimeline(asset.Path, AIPrompt);

            // 清空输入
            AIPrompt = "";

            // 通知添加成功
            ClipAdded?.Invoke(this, EventArgs.Empty);
            await _draftPage.HidePopup(true);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Generate AI image", this);
            throw;
        }
    }


    private async Task AddAIGeneratedImageToTimeline(string imagePath, string prompt)
    {
        try
        {
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
                labelText: $"AI: {string.Join("", prompt.Take(20).ToArray())}",
                background: new SolidColorBrush(Colors.Orange),
                resolveOverlap: true,
                relativeStart: 0,
                maxFrames: defaultFrames
            );

            element.ClipType = ClipMode.PhotoClip;
            element.SourcePath = imagePath;
            element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
            element.isInfiniteLength = false;
            element.maxFrameCount = defaultFrames;
            element.ExtraData = new Dictionary<string, object>
            {
                ["IsAI"] = true,
                ["AIPrompt"] = prompt,
                ["GeneratedAt"] = DateTime.Now,
                ["ImageRatio"] = AIVideoRatio,
                ["ImageStyle"] = AIImageStyle
            };
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Add AI generated image to timeline", this);
            throw;
        }
    }

    private async Task GenerateAIVideo()
    {
        try
        {
            var (width, height) = ParseVideoRatio(AIVideoRatio);

            var options = new VideoGenerationOptions
            {
                Width = width,
                Height = height,
                Duration = AIVideoDuration,
                PromptExtend = true,
                Watermark = false
            };

            var result = await AIHelper.GenerateVideoAsync(AIPrompt, options);

            if (!result.Success)
            {
                await _draftPage.DisplayAlertAsync(
                    Localized._Error,
                    result.ErrorMessage ?? SettingsManager.SettingLocalizedResources.AISetting_Test_ErrorResponse,
                    Localized._OK);
                return;
            }

            if (string.IsNullOrWhiteSpace(result.VideoUrl))
            {
                Log("No VideoUri provided.", "error");
                await _draftPage.DisplayAlertAsync(
                    Localized._Error,
                    SettingsManager.SettingLocalizedResources.AISetting_Test_ErrorResponse,
                    Localized._OK);
                return;
            }

            var asset = await DownloadRemoteResourcesToLocal(_draftPage, result.VideoUrl, "mp4", "AIGenerated-{0}");
            if (asset == null)
            {
                await _draftPage.DisplayAlertAsync(
                    Localized._Error,
                    $"{SettingsManager.SettingLocalizedResources.AISetting_Test_ErrorResponse}{Environment.NewLine}(Cannot download result.)",
                    Localized._OK);
                return;
            }

            await AddAIGeneratedVideoToTimeline(asset.Path, AIPrompt);

            AIPrompt = "";

            ClipAdded?.Invoke(this, EventArgs.Empty);
            await _draftPage.HidePopup(true);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Generate AI video", this);
            throw;
        }
    }

    private (int width, int height) ParseVideoRatio(string ratio)
    {
        return ratio switch
        {
            "16:9" => (1280, 720),
            "9:16" => (720, 1280),
            "1:1" => (1024, 1024),
            "4:3" => (1024, 768),
            "3:4" => (768, 1024),
            _ => (1280, 720) // 默认16:9
        };
    }


    private async Task AddAIGeneratedVideoToTimeline(string videoPath, string prompt)
    {
        try
        {
            var frameRate = 30;
            var totalFrames = (uint)(AIVideoDuration * frameRate);
            try
            {
                var src = PluginManager.CreateVideoSource(videoPath);
                frameRate = (int)src.Fps;
                totalFrames = (uint)(src.TotalFrames);
            }
            catch { }

            var trackIndex = _draftPage.Tracks.Keys
                .Where(k => k < DraftPage.SubTrackOffset)
                .DefaultIfEmpty(0)
                .Max();

            if (!_draftPage.Tracks.ContainsKey(trackIndex))
                _draftPage.AddATrack(trackIndex);

            var element = _draftPage.CreateAndAddClip(
                startX: 0,
                width: _draftPage.FrameToPixel(totalFrames),
                trackIndex: trackIndex,
                id: null,
                labelText: $"AI Video: {string.Join("", prompt.Take(20).ToArray())}",
                background: new SolidColorBrush(Colors.Purple),
                resolveOverlap: true,
                relativeStart: 0,
                maxFrames: totalFrames
            );

            element.ClipType = ClipMode.VideoClip;
            element.SourcePath = videoPath;
            element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
            element.isInfiniteLength = false;
            element.maxFrameCount = totalFrames;
            element.ExtraData = new Dictionary<string, object>
            {
                ["IsAI"] = true,
                ["AIPrompt"] = prompt,
                ["GeneratedAt"] = DateTime.Now,
                ["VideoDuration"] = AIVideoDuration,
                ["VideoRatio"] = AIVideoRatio
            };
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Add AI generated video to timeline", this);
            throw;
        }
    }

    #endregion

    #region AI Transition Generation

    private async Task GenerateAITransition(object direction)
    {
        if (direction is not string directionStr) directionStr = "";

        bool left = directionStr == "left";
        bool right = directionStr == "right";
        if (!left && !right && !string.IsNullOrWhiteSpace(_draftPage._transformMenuActivatedHandle))
        {
            left = _draftPage._transformMenuActivatedHandle == "left";
            right = _draftPage._transformMenuActivatedHandle == "right";
        }
        if (!left && !right)
        {
            await _draftPage.DisplayAlertAsync(
                Localized._Error,
                "Invalid transition direction.",
                Localized._OK);
            return;
        }

        // 验证是否有选中的Clip和相邻Clip
        var selectedClip = _draftPage?.SelectedClip;
        var (leftClip, rightClip) = _draftPage!.FindNeighbors(selectedClip);

        if (selectedClip == null)
        {
            await _draftPage!.DisplayAlertAsync(
                Localized._Error,
                Localized.DraftPage_PropertyPanel_SelectToContinue,
                Localized._OK);
            return;
        }

        if (string.IsNullOrWhiteSpace(AITransitionPrompt))
        {
            await _draftPage.DisplayAlertAsync(
                Localized._Error,
                "Please input prompt",
                Localized._OK);
            return;
        }

        if (IsGeneratingAITransition)
        {
            return;
        }

        IsGeneratingAITransition = true;

        try
        {
            var durationInSeconds = AITransitionDuration switch
            {
                0 => 1,  // 1秒
                1 => 2,  // 2秒
                2 => 3,  // 3秒
                3 => 5,  // 5秒
                _ => 2   // 默认2秒
            };
            IPicture? firstFrame = null!, lastFrame = null!;

            if (left && !right)
            {
                if (leftClip is null || selectedClip is null) return;
                firstFrame = await GetClipLastFrame(leftClip);
                lastFrame = await GetClipFirstFrame(selectedClip);
            }
            else if (!left && right)
            {
                if (rightClip is null || selectedClip is null) return;
                firstFrame = await GetClipLastFrame(selectedClip);
                lastFrame = await GetClipFirstFrame(rightClip);

            }
            else
            {
                await _draftPage.DisplayAlertAsync(
                Localized._Error,
                "Invalid transition direction.",
                Localized._OK);
                return;
            }

            if (firstFrame == null || lastFrame == null)
            {
                await _draftPage.DisplayAlertAsync(
                    Localized._Error,
                    "Cannot read frames.",
                    Localized._OK);
                return;
            }

            var options = new VideoGenerationOptions
            {
                Width = 1280,
                Height = 720,
                Duration = durationInSeconds,
                PromptExtend = true,
                Watermark = false
            };

            var result = await AIHelper.GenerateVideoFromFramesAsync(
                firstFrame,
                lastFrame,
                AITransitionPrompt,
                options);

            if (!result.Success)
            {
                await _draftPage.DisplayAlertAsync(
                    Localized._Error,
                    result.ErrorMessage ?? SettingsManager.SettingLocalizedResources.AISetting_Test_ErrorResponse,
                    Localized._OK);
                return;
            }

            if (string.IsNullOrEmpty(result.VideoUrl))
            {
                Log("No video provided.", "Error");
                await _draftPage.DisplayAlertAsync(
                    Localized._Error,
                   SettingsManager.SettingLocalizedResources.AISetting_Test_ErrorResponse,
                    Localized._OK);
                return;
            }

            var asset = await DownloadRemoteResourcesToLocal(_draftPage, result.VideoUrl, "mp4", "AIGeneratedTransform-{0}");
            if (asset == null)
            {
                await _draftPage.DisplayAlertAsync(
                    Localized._Error,
                    $"{SettingsManager.SettingLocalizedResources.AISetting_Test_ErrorResponse}{Environment.NewLine}(Cannot download result)",
                    Localized._OK);
                return;
            }

            if (string.IsNullOrWhiteSpace(asset.Path))
            {
                await _draftPage.DisplayAlertAsync(
                    Localized._Error,
                    $"Asset downloaded but Path is empty! AssetId={asset.AssetId}",
                    Localized._OK);
                return;
            }

            var sourcePath = asset.Path; // 捕获到局部变量中

            _draftPage.AddTransformBetweenSelected((a, b) => new ExternalSourceTransform
            {
                Name = "AITransform",
                BindedLeftClip = a,
                BindedRightClip = b,
                SourcePath = sourcePath
            }, selectedClip, left, right, (c) => c.ExtraData["IsAI"] = true);


            AITransitionPrompt = "";

            ClipAdded?.Invoke(this, EventArgs.Empty);
            await _draftPage.HidePopup(true);
            LoadTransforms();
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Generate AI transition", this);
            await _draftPage.DisplayAlertAsync(
                Localized._Error,
                $"{SettingsManager.SettingLocalizedResources.AISetting_Test_ErrorResponse}{Environment.NewLine}({Localized._ExceptionTemplate(ex)})",
                Localized._OK);
        }
        finally
        {
            IsGeneratingAITransition = false;
        }
    }

    private async Task<IPicture?> GetClipLastFrame(ClipElementUI clipElement)
    {
        try
        {
            // 将ClipElementUI转换为IClip以获取帧数据
            var clipData = ConvertClipElementToIClip(clipElement);
            if (clipData == null) return null;

            // 获取片段的最后一帧
            var lastFrameIndex = clipData.Duration > 0 ? clipData.Duration - 1 : 0;
            var frame = clipData.GetFrameRelativeToStartPointOfSource(lastFrameIndex, 1280, 720, false);

            return frame;
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Get clip last frame", this);
            return null;
        }
    }

    private async Task<IPicture?> GetClipFirstFrame(ClipElementUI clipElement)
    {
        try
        {
            var clipData = ConvertClipElementToIClip(clipElement);
            if (clipData == null) return null;

            var frame = clipData.GetFrameRelativeToStartPointOfSource(0, 1280, 720, false);

            return frame;
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Get clip first frame", this);
            return null;
        }
    }

    private Render.RenderAPIBase.ClipAndTrack.IClip? ConvertClipElementToIClip(ClipElementUI clipElement)
    {
        try
        {
            // 导出单个Clip的数据
            var draftData = DraftImportAndExportHelper.ExportFromDraftPage(_draftPage, true);
            var clips = DraftImportAndExportHelper.JSONToIClips(draftData);

            // 根据ID查找对应的IClip
            return clips.FirstOrDefault(c => c.Id == clipElement.Id);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Convert ClipElementUI to IClip", this);
            return null;
        }
    }

    public static async Task<AssetItem?> DownloadRemoteResourcesToLocal(DraftPage workingPage, string videoUrl, string extension, string prompt)
    {
        try
        {
            var aiVideosDir = Path.Combine(workingPage.WorkingPath, "assets");
            Directory.CreateDirectory(aiVideosDir);
            var id = Guid.NewGuid();
            var safeFileName = Path.ChangeExtension($"AIGenerated-{id}.mp4", extension);
            var localPath = Path.Combine(aiVideosDir, safeFileName);

            using var client = new HttpClient();
            var videoBytes = await client.GetByteArrayAsync(videoUrl);
            await File.WriteAllBytesAsync(localPath, videoBytes);

            var assetType = AssetItem.GetAssetType(localPath);
            var item = new AssetItem
            {
                AssetId = id.ToString(),
                Name = Path.GetFileNameWithoutExtension(safeFileName),
                AssetType = assetType,
                Path = localPath,
                CreatedAt = DateTime.Now,
                SecondPerFrame = -1,
                FrameCount = 0,
                IsAIGenerated = true
            };

            // 生成缩略图
            var thumbnailsDir = Path.Combine(workingPage.WorkingPath, "assets", ".thumbnails");
            Directory.CreateDirectory(thumbnailsDir);
            var thumbnailPath = Path.Combine(thumbnailsDir, id.ToString() + ".png");

            try
            {
                switch (assetType)
                {
                    case AssetType.Video:
                        {
                            var vid = PluginManager.CreateVideoSource(localPath);
                            item.FrameCount = vid.TotalFrames;
                            item.SecondPerFrame = (float)(1f / vid.Fps);
                            vid.GetFrame(0U, false).SaveAsPng16bpp(thumbnailPath, null);
                            item.ThumbnailPath = thumbnailPath;
                            break;
                        }
                    case AssetType.Image:
                        {
                            // 图片直接使用原路径作为缩略图
                            item.ThumbnailPath = localPath;
                            break;
                        }

                    default:
                        item.ThumbnailPath = null;
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"Generate thumbnail for {safeFileName}");
                item.ThumbnailPath = null;
            }

            workingPage.Assets.AddOrUpdate(id.ToString(), item, (key, existing) => item);

            return item;
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Download remote stuff");
            return null;
        }
    }


    #endregion

}

#region child vm

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

public class TransformItemViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public string TypeKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsApplicableToLeft { get; set; } = false;
    public bool IsApplicableToRight { get; set; } = false;
    public bool IsSideDetermined { get; set; } = false;
    public bool IsDraftSelectedAnyClip { get; set; } = false;

    private string? _previewVideoPath;
    /// <summary>Path to the locally-cached preview MP4 for this transform.</summary>
    public string? PreviewVideoPath
    {
        get => _previewVideoPath;
        set
        {
            if (_previewVideoPath != value)
            {
                _previewVideoPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPreviewReady));
            }
        }
    }

    /// <summary>True once a preview video has been cached for this transform.</summary>
    public bool IsPreviewReady => !string.IsNullOrEmpty(_previewVideoPath);

    private bool _isGeneratingPreview;
    /// <summary>True while the preview is being rendered in the background.</summary>
    public bool IsGeneratingPreview
    {
        get => _isGeneratingPreview;
        set
        {
            if (_isGeneratingPreview != value)
            {
                _isGeneratingPreview = value;
                OnPropertyChanged();
            }
        }
    }
}

public class TextStyleItemViewModel
{
    public required string Id { get; set; } = string.Empty;
    public required string Name { get; set; } = string.Empty;
    public required string SampleText { get; set; } = string.Empty;
    public required TextClipEntry ActualTemplate { get; set; } = default!;
    public ImageSource PreviewSource
    {
        get
        {
            var sample = SampleText ?? "AaBbYyZz";
            TextClip t = new TextClip
            {
                Id = Id,
                Name = Id,
                TextEntries = new List<TextClipEntry>
                {
                    ActualTemplate with { text = sample }
                }
            };

            var fs = ActualTemplate.fontSize > 0 ? ActualTemplate.fontSize : 36;
            var imgHeight = Math.Clamp((int)(fs * 1.2) + 4, 24, 200);
            var imgWidth = Math.Clamp((int)(sample.Length * fs * 0.6) + 20, 100, 1200);

            var img = t.GetFrameRelativeToStartPointOfSource(0, imgWidth, imgHeight, true);
            return img.ToImageSource();
        }
    }
    public bool ShouldInSubtrack { get; set; } = false;
}

#endregion
