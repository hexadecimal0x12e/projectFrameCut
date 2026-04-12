using CommunityToolkit.Maui.Core;
using Microsoft.Maui.Storage;
using projectFrameCut.AIAssistance;
using projectFrameCut.APIClient;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Transform;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Setting.SettingPages;
using projectFrameCut.Shared;
using projectFrameCut.Template;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using static projectFrameCut.ApplicationAPIBase.Helpers.TextHelper;
using IPicture = projectFrameCut.Shared.IPicture;

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

    public readonly DraftPage _draftPage;

    public bool TransformMenuActivatedViaHandleClick = false;

    private void EnsurePlacementTrackExists(bool useSubTrack)
    {
        if (useSubTrack)
        {
            var trackIndex = _draftPage.Tracks.Keys.Where(k => k >= DraftPage.SubTrackOffset).DefaultIfEmpty(DraftPage.SubTrackOffset).Max();
            if (!_draftPage.Tracks.ContainsKey(trackIndex))
            {
                _draftPage.AddASubTrack(trackIndex);
            }
            return;
        }

        var mainTrackIndex = _draftPage.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(0).Max();
        if (!_draftPage.Tracks.ContainsKey(mainTrackIndex))
        {
            _draftPage.AddATrack(mainTrackIndex);
        }
    }

    private void BeginTimelineClipPlacement(Func<int, double, ClipElementUI> clipFactory, bool useSubTrack = false, string? name = null)
    {
        EnsurePlacementTrackExists(useSubTrack);
        _draftPage.BeginClipPlacement(
            clipFactory,
            useSubTrack ? trackId => trackId >= DraftPage.SubTrackOffset : trackId => trackId < DraftPage.SubTrackOffset,
            name);
    }

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

    public bool ShowAllTemplates
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
    } = false;

    public bool NotAddTemplateAsGroup
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
    } = false;

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
    } = "Image"; // ??????

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
    } = 5; // ??5?

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
    } = "16:9"; // ??16:9??

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
    } = 2; // ??2?

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

    // ???? Clip ????????
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
        set
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
                OnPropertyChanged(nameof(TextClipInSubTrack));
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

    // ?????????
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
    public ObservableCollection<TemplateItemViewModel> AvailableTemplates { get; } = new();
    public ObservableCollection<TransformItemViewModel> AvailableTransforms { get; } = new();
    public ObservableCollection<TextStyleItemViewModel> AvailableTextStyles { get; } = new();

    public ObservableCollection<AssetItemViewModel> FilteredLocalAssets { get; } = new();
    public ObservableCollection<AssetItemViewModel> FilteredSharedAssets { get; } = new();
    public ObservableCollection<AssetItemViewModel> FilteredReuseableAssets { get; } = new();
    public ObservableCollection<TemplateItemViewModel> FilteredAvailableTemplates { get; } = new();
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
    public ICommand AddTemplateCommand { get; set; } = null!;
    public ICommand AddReuseableAssetClipCommand { get; set; } = null!;
    public ICommand AddTransformClipCommand { get; set; } = null!;
    public ICommand AddTransformClipInLeftCommand { get; set; } = null!;
    public ICommand AddTransformClipInRightCommand { get; set; } = null!;
    public ICommand GenerateTransformPreviewCommand { get; set; } = null!;
    public ICommand SelectTransformForPreviewCommand { get; set; } = null!;
    public ICommand GenerateAIContentCommand { get; set; } = null!;

    // AI ????????
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
        AddTemplateCommand = new Command<TemplateItemViewModel>(async (template) => await AddTemplate(template));
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

        // AI ??????
        GenerateAITransitionCommand = new Command(async (d) => await GenerateAITransition(d));
    }
    #endregion

    #region load

    public event EventHandler? ClipAdded;

    public async Task Refresh()
    {
        RegisterCommands();
        await LoadAssets();
        LoadTemplates();
        LoadTransforms();
        InitializeTextStyles();
        await FilterAssets();
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

    public void LoadTemplates()
    {
        AvailableTemplates.Clear();

        foreach (var kv in TemplateStore.Templates)
        {
            var template = kv.Value;
            var item = new TemplateItemViewModel(this)
            {
                TemplateId = kv.Key,
                Name = string.IsNullOrWhiteSpace(template.TemplateName) ? kv.Key.ToString() : template.TemplateName,
                TemplateType = template.TemplateType.ToString(),
                Scope = template.Scope.ToString(),
                Template = template
            };

            if (template is JSONBasedTemplateStructure jsonTemplate)
            {
                item.ClipCount = (jsonTemplate.Draft.Clips ?? Array.Empty<object>()).Length;
                item.TrackCount = (jsonTemplate.Draft.SoundTracks ?? Array.Empty<object>()).Length;
            }

            AvailableTemplates.Add(item);
        }
    }

    private bool ShouldShowTemplateByScope(TemplateItemViewModel template)
    {
        if (ShowAllTemplates)
            return true;

        return string.Equals(template.Scope, TemplateScope.Any.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(template.Scope, TemplateScope.Clips.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public async Task LoadAssets()
    {
        LocalAssets.Clear();
        SharedAssets.Clear();
        ReuseableAssets.Clear();

        foreach (var asset in _draftPage.Assets.Values.Where(c => c.AssetType != AssetType.Font).OrderBy(a => a.Name))
        {
            LocalAssets.Add(new AssetItemViewModel(this)
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
            SharedAssets.Add(new AssetItemViewModel(this)
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

        // ???????????????
        /*
        // ?????????????????
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

        // ???????
        await FilterAssets();
        */
    }

    #endregion

    #region asset
    public async Task AddAssetClip(AssetItemViewModel? assetViewModel)
    {
        if (assetViewModel?.OriginalAsset == null) return;

        var sourcePath = _draftPage.Assets.ContainsKey(assetViewModel.Id) ? assetViewModel.SourcePath : null;
        BeginTimelineClipPlacement((trackIndex, startX) =>
        {
            var clip = _draftPage.CreateFromAsset(
                assetViewModel.OriginalAsset,
                trackIndex,
                startX,
                InternalPluginBase.InternalPluginBaseID,
                sourcePath);

            _draftPage.RegisterClip(clip, true);
            _draftPage.AddAClip(clip);
            return clip;
        }, name: assetViewModel.Name, useSubTrack: assetViewModel.OriginalAsset.AssetType == AssetType.Audio);

        ClipAdded?.Invoke(this, EventArgs.Empty);
    }

    public async Task AddTemplate(TemplateItemViewModel? templateViewModel)
    {
        if (templateViewModel?.Template is not JSONBasedTemplateStructure jsonTemplate)
        {
            await _draftPage.DisplayAlertAsync(Localized._Info, "Only JSON templates are supported for timeline insertion.", Localized._OK);
            return;
        }

        try
        {
            var clonedDraft = JsonSerializer.Deserialize<DraftStructureJSON>(
                JsonSerializer.Serialize(jsonTemplate.Draft, DraftPage.DraftJSONOption),
                DraftPage.DraftJSONOption) ?? new DraftStructureJSON();

            var draftNode = JsonSerializer.SerializeToNode(clonedDraft, DraftPage.DraftJSONOption) as JsonObject;
            if (draftNode is null)
            {
                await _draftPage.DisplayAlertAsync(Localized._Error, "Template draft is invalid.", Localized._OK);
                return;
            }

            var templateDefaults = jsonTemplate.Variables ?? new Dictionary<string, string?>();
            var templateDefinitions = jsonTemplate.VariableDefinitions ?? new Dictionary<string, TemplateVariableDefinition>();

            var inputValues = await PromptTemplateValuesWithViewAsync(jsonTemplate, templateViewModel.Name);
            if (inputValues is null)
            {
                return;
            }

            ReplaceTemplatePlaceholders(draftNode, inputValues, templateDefaults, templateDefinitions);
            RemapAllTemplateIds(draftNode);
            EnsurePackagedTemplateAssetsInProject(draftNode, jsonTemplate);

            var remappedDraft = draftNode.Deserialize<DraftStructureJSON>(DraftPage.DraftJSONOption);
            if (remappedDraft is null)
            {
                await _draftPage.DisplayAlertAsync(Localized._Error, "Template draft deserialization failed.", Localized._OK);
                return;
            }

            var clipDtos = new List<ClipDraftDTO>();
            foreach (var obj in remappedDraft.Clips ?? Array.Empty<object>())
            {
                var dto = obj switch
                {
                    ClipDraftDTO c => c,
                    JsonElement je => je.Deserialize<ClipDraftDTO>(DraftPage.DraftJSONOption),
                    _ => null
                };

                if (dto is not null)
                {
                    clipDtos.Add(dto);
                }
            }

            var soundtrackDtos = new List<SoundtrackDTO>();
            foreach (var obj in remappedDraft.SoundTracks ?? Array.Empty<object>())
            {
                var dto = obj switch
                {
                    SoundtrackDTO s => s,
                    JsonElement je => je.Deserialize<SoundtrackDTO>(DraftPage.DraftJSONOption),
                    _ => null
                };

                if (dto is not null)
                {
                    soundtrackDtos.Add(dto);
                }
            }

            if (clipDtos.Count == 0 && soundtrackDtos.Count == 0)
            {
                await _draftPage.DisplayAlertAsync(Localized._Info, "This template has no clip/track data.", Localized._OK);
                return;
            }

            var allStarts = clipDtos.Select(c => c.StartFrame).Concat(soundtrackDtos.Select(s => s.StartFrame)).ToArray();
            var minStart = allStarts.Length > 0 ? allStarts.Min() : 0u;

            static uint NormalizeStart(uint value, uint minValue)
            {
                if (value <= minValue)
                {
                    return 0u;
                }

                return value - minValue;
            }

            foreach (var c in clipDtos)
            {
                c.StartFrame = NormalizeStart(c.StartFrame, minStart);
            }

            foreach (var s in soundtrackDtos)
            {
                s.StartFrame = NormalizeStart(s.StartFrame, minStart);
            }

            var importDraft = new DraftStructureJSON
            {
                Clips = clipDtos.Cast<object>().ToArray(),
                SoundTracks = soundtrackDtos.Cast<object>().ToArray(),
                TargetFrameRate = _draftPage.ProjectInfo.TargetFrameRate,
                SavedAt = DateTime.Now
            };

            var (importedClips, _) = DraftImportAndExportHelper.ImportFromJSON(importDraft, _draftPage.ProjectInfo);

            var templateClips = importedClips.Values
                .Where(c => c.origTrack is not null)
                .OrderBy(c => c.origTrack ?? 0)
                .ThenBy(c => c.origX)
                .ToList();

            if (templateClips.Count == 0)
            {
                await _draftPage.DisplayAlertAsync(Localized._Info, "This template has no valid timeline clips.", Localized._OK);
                return;
            }

            var minTemplateX = templateClips.Min(c => c.origX);
            var mainTemplateTracks = templateClips
                .Select(c => c.origTrack ?? 0)
                .Where(track => track < DraftPage.SubTrackOffset)
                .Distinct()
                .OrderBy(track => track)
                .ToArray();
            var subTemplateTracks = templateClips
                .Select(c => c.origTrack ?? 0)
                .Where(track => track >= DraftPage.SubTrackOffset)
                .Distinct()
                .OrderBy(track => track)
                .ToArray();

            var hasMainTrackClip = mainTemplateTracks.Length > 0;
            var hasSubTrackClip = subTemplateTracks.Length > 0;

            if (hasMainTrackClip && !hasSubTrackClip)
            {
                EnsurePlacementTrackExists(false);
            }
            else if (!hasMainTrackClip && hasSubTrackClip)
            {
                EnsurePlacementTrackExists(true);
            }
            else
            {
                EnsurePlacementTrackExists(false);
                EnsurePlacementTrackExists(true);
            }

            Predicate<int>? trackFilter = null;
            if (hasMainTrackClip && !hasSubTrackClip)
            {
                trackFilter = trackId => trackId < DraftPage.SubTrackOffset;
            }
            else if (!hasMainTrackClip && hasSubTrackClip)
            {
                trackFilter = trackId => trackId >= DraftPage.SubTrackOffset;
            }

            bool hasPlacedTemplate = false;
            _draftPage.BeginClipPlacement((targetTrack, targetStartX) =>
            {
                if (hasPlacedTemplate)
                {
                    throw new InvalidOperationException("Template placement has already been committed.");
                }

                hasPlacedTemplate = true;
                var xOffset = targetStartX - minTemplateX;

                var mainTrackMap = new Dictionary<int, int>();
                if (hasMainTrackClip)
                {
                    var mainAnchorTrack = targetTrack < DraftPage.SubTrackOffset
                        ? targetTrack
                        : _draftPage.Tracks.Keys.Where(k => k < DraftPage.SubTrackOffset).DefaultIfEmpty(-1).Max() + 1;
                    if (mainAnchorTrack < 0)
                    {
                        mainAnchorTrack = 0;
                    }

                    for (int i = 0; i < mainTemplateTracks.Length; i++)
                    {
                        mainTrackMap[mainTemplateTracks[i]] = mainAnchorTrack + i;
                    }
                }

                var subTrackMap = new Dictionary<int, int>();
                if (hasSubTrackClip)
                {
                    var subAnchorTrack = targetTrack >= DraftPage.SubTrackOffset
                        ? targetTrack
                        : Math.Max(
                            DraftPage.SubTrackOffset,
                            _draftPage.Tracks.Keys.Where(k => k >= DraftPage.SubTrackOffset).DefaultIfEmpty(DraftPage.SubTrackOffset - 1).Max() + 1);

                    for (int i = 0; i < subTemplateTracks.Length; i++)
                    {
                        subTrackMap[subTemplateTracks[i]] = subAnchorTrack + i;
                    }
                }

                foreach (var trackId in mainTrackMap.Values.Concat(subTrackMap.Values).Distinct().OrderBy(v => v))
                {
                    if (_draftPage.Tracks.ContainsKey(trackId))
                    {
                        continue;
                    }

                    if (trackId >= DraftPage.SubTrackOffset)
                    {
                        _draftPage.AddASubTrack(trackId);
                    }
                    else
                    {
                        _draftPage.AddATrack(trackId);
                    }
                }

                ClipElementUI? firstClip = null;
                var placedTemplateClips = new List<ClipElementUI>();
                foreach (var sourceClip in templateClips)
                {
                    if (sourceClip.origTrack is not int sourceTrack)
                    {
                        continue;
                    }

                    var mappedTrack = sourceTrack >= DraftPage.SubTrackOffset
                        ? subTrackMap[sourceTrack]
                        : mainTrackMap[sourceTrack];
                    var mappedX = Math.Max(0, sourceClip.origX + xOffset);

                    sourceClip.origTrack = mappedTrack;
                    sourceClip.SubLayerIndex = mappedTrack;
                    sourceClip.origX = mappedX;
                    sourceClip.layoutX = mappedX;
                    sourceClip.layoutY = 0;
                    sourceClip.defaultY = 0;
                    sourceClip.Clip.TranslationX = mappedX;
                    sourceClip.Clip.TranslationY = 0;

                    _draftPage.RegisterClip(sourceClip, true);
                    _draftPage.AddAClip(sourceClip);

                    placedTemplateClips.Add(sourceClip);
                    firstClip ??= sourceClip;
                }

                if (!NotAddTemplateAsGroup && placedTemplateClips.Count > 1)
                {
                    _ = _draftPage.CombineClipsAsGroupAsync(placedTemplateClips);
                }

                _ = _draftPage.UpdateAdjacencyForTrack();
                return firstClip ?? throw new InvalidOperationException("No clip could be placed from template.");
            }, trackFilter, templateViewModel.Name);

            ClipAdded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log(ex, "add template", this);
            await _draftPage.DisplayAlertAsync(Localized._Error, $"Failed to add template: {ex.Message}", Localized._OK);
        }
    }

    private async Task<Dictionary<string, string?>?> PromptTemplateValuesWithViewAsync(
        JSONBasedTemplateStructure template,
        string? templateName)
    {
        var inputView = new TemplateCreatePage();
        var originPopupClosable = _draftPage.IsPopupClosableByTapBackground;
        EventHandler closeRequestedHandler = async (_, _) => await _draftPage.HidePopup(true);
        inputView.CloseRequested += closeRequestedHandler;

        try
        {
            _draftPage.IsPopupClosableByTapBackground = false;
            var inputTask = inputView.PromptTemplateValuesAsync(template, $"???{templateName ?? "???"}");
            await _draftPage.ShowAPopup(inputView, mode: "dialog");
            return await inputTask;
        }
        finally
        {
            _draftPage.IsPopupClosableByTapBackground = originPopupClosable;
            inputView.CloseRequested -= closeRequestedHandler;
        }
    }

    private static void ReplaceTemplatePlaceholders(
        JsonNode? node,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string?> defaults,
        IReadOnlyDictionary<string, TemplateVariableDefinition> definitions)
    {
        if (node is JsonObject obj)
        {
            var keys = obj.Select(kv => kv.Key).ToArray();
            foreach (var key in keys)
            {
                var current = obj[key];
                if (current is JsonValue val && TryGetTemplatePlaceholderKey(val, out var placeholderKey))
                {
                    if (!TryResolveTemplateVariable(placeholderKey, values, defaults, definitions, out var resolved, out var variableType))
                    {
                        throw new KeyNotFoundException($"Missing template variable: {placeholderKey}");
                    }

                    obj[key] = ConvertTemplateResolvedValue(resolved, variableType);
                }
                else
                {
                    ReplaceTemplatePlaceholders(current, values, defaults, definitions);
                }
            }
            return;
        }

        if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var current = arr[i];
                if (current is JsonValue val && TryGetTemplatePlaceholderKey(val, out var placeholderKey))
                {
                    if (!TryResolveTemplateVariable(placeholderKey, values, defaults, definitions, out var resolved, out var variableType))
                    {
                        throw new KeyNotFoundException($"Missing template variable: {placeholderKey}");
                    }

                    arr[i] = ConvertTemplateResolvedValue(resolved, variableType);
                }
                else
                {
                    ReplaceTemplatePlaceholders(current, values, defaults, definitions);
                }
            }
        }
    }

    private static bool TryResolveTemplateVariable(
        string key,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string?> defaults,
        IReadOnlyDictionary<string, TemplateVariableDefinition> definitions,
        out string? resolved,
        out TemplateVariableType variableType)
    {
        variableType = TemplateVariableType.Auto;
        if (definitions.TryGetValue(key, out var def) && def is not null)
        {
            variableType = def.Type;
        }

        if (values.TryGetValue(key, out resolved))
        {
            return true;
        }

        if (values.TryGetValue($"{{{{{key}}}}}", out resolved))
        {
            return true;
        }

        if (def?.DefaultValue is not null)
        {
            resolved = def.DefaultValue;
            return true;
        }

        if (defaults.TryGetValue(key, out resolved))
        {
            return true;
        }

        if (defaults.TryGetValue($"{{{{{key}}}}}", out resolved))
        {
            return true;
        }

        return false;
    }

    private static JsonNode? ConvertTemplateResolvedValue(string? value, TemplateVariableType type)
    {
        if (value is null)
        {
            return null;
        }

        switch (type)
        {
            case TemplateVariableType.String:
            case TemplateVariableType.File:
                return JsonValue.Create(value);

            case TemplateVariableType.Boolean:
                if (!bool.TryParse(value, out var boolValue))
                {
                    throw new FormatException($"Value '{value}' is not a valid boolean.");
                }
                return JsonValue.Create(boolValue);

            case TemplateVariableType.Integer:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    throw new FormatException($"Value '{value}' is not a valid integer.");
                }
                return JsonValue.Create(longValue);

            case TemplateVariableType.Number:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                {
                    throw new FormatException($"Value '{value}' is not a valid number.");
                }
                return JsonValue.Create(doubleValue);

            case TemplateVariableType.Json:
                return JsonNode.Parse(value);
        }

        if (value.StartsWith("json:", StringComparison.OrdinalIgnoreCase))
        {
            return JsonNode.Parse(value.Substring(5));
        }

        if (bool.TryParse(value, out var b))
        {
            return JsonValue.Create(b);
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return JsonValue.Create(d);
        }

        return JsonValue.Create(value);
    }

    private static bool TryGetTemplatePlaceholderKey(JsonValue value, out string key)
    {
        key = string.Empty;
        if (!value.TryGetValue<string>(out var str) || string.IsNullOrWhiteSpace(str))
        {
            return false;
        }

        var trimmed = str.Trim();
        if (!trimmed.StartsWith("{{", StringComparison.Ordinal) || !trimmed.EndsWith("}}", StringComparison.Ordinal) || trimmed.Length <= 4)
        {
            return false;
        }

        key = trimmed.Substring(2, trimmed.Length - 4).Trim();
        return !string.IsNullOrWhiteSpace(key);
    }

    private void EnsurePackagedTemplateAssetsInProject(JsonNode draftNode, JSONBasedTemplateStructure template)
    {
        if (template.AssetHashTable is not { Count: > 0 })
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_draftPage.WorkingPath))
        {
            return;
        }

        var referencedAssetIds = CollectTemplateReferencedAssetIds(draftNode);
        if (referencedAssetIds.Count == 0)
        {
            return;
        }

        var projectAssetsDir = Path.Combine(_draftPage.WorkingPath, "assets");
        Directory.CreateDirectory(projectAssetsDir);

        var resolvedAssetPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assetId in referencedAssetIds)
        {
            if (!template.AssetHashTable.TryGetValue(assetId, out var packagedSourcePath)
                || string.IsNullOrWhiteSpace(packagedSourcePath)
                || !File.Exists(packagedSourcePath))
            {
                continue;
            }

            if (_draftPage.Assets.TryGetValue(assetId, out var existingProjectAsset)
                && !string.IsNullOrWhiteSpace(existingProjectAsset.Path)
                && File.Exists(existingProjectAsset.Path))
            {
                resolvedAssetPaths[assetId] = existingProjectAsset.Path;
                continue;
            }

            var extension = Path.GetExtension(packagedSourcePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".bin";
            }

            var copiedPath = Path.Combine(projectAssetsDir, assetId + extension);
            File.Copy(packagedSourcePath, copiedPath, overwrite: true);

            var projectAsset = AssetDatabase.Assets.TryGetValue(assetId, out var sharedAsset)
                ? JsonSerializer.Deserialize<AssetItem>(
                    JsonSerializer.Serialize(sharedAsset, DraftPage.DraftJSONOption),
                    DraftPage.DraftJSONOption)
                : null;

            projectAsset ??= new AssetItem();
            projectAsset.AssetId = assetId;
            projectAsset.Path = copiedPath;
            if (projectAsset.AssetType == AssetType.Other)
            {
                projectAsset.AssetType = AssetItem.GetAssetType(copiedPath);
            }

            if (string.IsNullOrWhiteSpace(projectAsset.Name))
            {
                projectAsset.Name = Path.GetFileNameWithoutExtension(packagedSourcePath);
                if (string.IsNullOrWhiteSpace(projectAsset.Name))
                {
                    projectAsset.Name = $"Asset@{assetId[..Math.Min(assetId.Length, 8)]}";
                }
            }

            if (projectAsset.CreatedAt == default)
            {
                projectAsset.CreatedAt = DateTime.Now;
            }

            _draftPage.Assets[assetId] = projectAsset;
            resolvedAssetPaths[assetId] = copiedPath;
        }

        ReplaceTemplateAssetReferencesWithPaths(draftNode, resolvedAssetPaths);
    }

    private static HashSet<string> CollectTemplateReferencedAssetIds(JsonNode? node)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectTemplateReferencedAssetIds(node, refs);
        return refs;
    }

    private static void CollectTemplateReferencedAssetIds(JsonNode? node, ISet<string> refs)
    {
        if (node is null)
        {
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                CollectTemplateReferencedAssetIds(kv.Value, refs);
            }
            return;
        }

        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                CollectTemplateReferencedAssetIds(item, refs);
            }
            return;
        }

        if (node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && TryParseTemplateAssetReference(text, out var assetId))
        {
            refs.Add(assetId);
        }
    }

    private static bool TryParseTemplateAssetReference(string? value, out string assetId)
    {
        assetId = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith('$') || trimmed.Length <= 1)
        {
            return false;
        }

        assetId = trimmed[1..].Trim();
        return !string.IsNullOrWhiteSpace(assetId);
    }

    private static void ReplaceTemplateAssetReferencesWithPaths(JsonNode? node, IReadOnlyDictionary<string, string> resolvedAssetPaths)
    {
        if (node is null || resolvedAssetPaths.Count == 0)
        {
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToArray())
            {
                var current = obj[key];
                if (current is JsonValue jsonValue
                    && jsonValue.TryGetValue<string>(out var text)
                    && TryParseTemplateAssetReference(text, out var assetId)
                    && resolvedAssetPaths.TryGetValue(assetId, out var resolvedPath))
                {
                    obj[key] = resolvedPath;
                }
                else
                {
                    ReplaceTemplateAssetReferencesWithPaths(current, resolvedAssetPaths);
                }
            }
            return;
        }

        if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var current = arr[i];
                if (current is JsonValue jsonValue
                    && jsonValue.TryGetValue<string>(out var text)
                    && TryParseTemplateAssetReference(text, out var assetId)
                    && resolvedAssetPaths.TryGetValue(assetId, out var resolvedPath))
                {
                    arr[i] = resolvedPath;
                }
                else
                {
                    ReplaceTemplateAssetReferencesWithPaths(current, resolvedAssetPaths);
                }
            }
        }
    }

    private void RemapAllTemplateIds(JsonNode root)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);

        if (root["Clips"] is JsonArray clips)
        {
            foreach (var item in clips.OfType<JsonObject>())
            {
                var id = item["Id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                idMap[id] = Guid.NewGuid().ToString();
            }
        }

        if (root["SoundTracks"] is JsonArray tracks)
        {
            foreach (var item in tracks.OfType<JsonObject>())
            {
                var id = item["Id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                idMap[id] = Guid.NewGuid().ToString();
            }
        }

        if (idMap.Count == 0)
        {
            return;
        }

        ReplaceMappedStringValues(root, idMap);
    }

    private static void ReplaceMappedStringValues(JsonNode? node, IReadOnlyDictionary<string, string> idMap)
    {
        if (node is null)
        {
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToArray())
            {
                var value = obj[key];
                if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var str) && idMap.TryGetValue(str, out var newValue))
                {
                    obj[key] = newValue;
                }
                else
                {
                    ReplaceMappedStringValues(value, idMap);
                }
            }
            return;
        }

        if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var value = arr[i];
                if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var str) && idMap.TryGetValue(str, out var newValue))
                {
                    arr[i] = newValue;
                }
                else
                {
                    ReplaceMappedStringValues(value, idMap);
                }
            }
        }
    }

    #endregion

    #region text

    public async Task AddTextClipWithStyle(TextStyleItemViewModel? styleOverride = null)
    {
        var style = styleOverride ?? SelectedTextStyle;
        if (style is null) return;
        var text = TextToAdd;
        if (string.IsNullOrWhiteSpace(text)) return;

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

        BeginTimelineClipPlacement((trackIndex, startX) =>
        {
            var element = _draftPage.CreateAndAddClip(
                startX: startX,
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
            element.ExtraData["TextEntries"] = new List<TextClipEntry>
            {
                style.ActualTemplate with { text = text, fontFamily = fontOverride }
            };

            return element;
        }, TextClipInSubTrack, name: "Text");

        ClipAdded?.Invoke(this, EventArgs.Empty);
        TextToAdd = "";
    }

    public void InitializeTextStyles(string? previewText = null)
    {

        AvailableTextStyles.Clear();
        try
        {
            Dictionary<string, TextClipEntry> template = new();
            EditSettingPage.LoadTextTemplates(ref template);
            foreach (var item in template)
            {
                AvailableTextStyles.Add(new TextStyleItemViewModel(this)
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
            AvailableTextStyles.Add(new TextStyleItemViewModel(this)
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
            AvailableTextStyles.Add(new TextStyleItemViewModel(this)
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
            AvailableTextStyles.Add(new TextStyleItemViewModel(this)
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

        // ????
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
            AvailableTransforms.Add(new TransformItemViewModel(this)
            {
                TypeKey = kvp.Key,
                DisplayName = kvp.Value,
                IsSideDetermined = IsSideDetermined,
                IsApplicableToLeft = applicableToLeft,
                IsApplicableToRight = applicableToRight,
                IsDraftSelectedAnyClip = IsDraftSelectedAnyClip
            });
        }

        // ????
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

    public async Task AddTransformClip(TransformItemViewModel? transform, bool left, bool right)
    {
        if (transform is null) return;
        if (transform.IsSideDetermined) _draftPage.AddTransformToNeighbors(transform.TypeKey);
        else
        {
            _draftPage.AddTransformBetweenSelected(transform.TypeKey, _draftPage.SelectedClip, left, right);
        }
        ClipAdded?.Invoke(this, EventArgs.Empty);
    }

    public async Task GenerateTransformPreviewAsync(TransformItemViewModel? transform)
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
                        using var frame = TransformProcessing.ProcessTransform(prevClip, nextClip, t, previewW, previewH, i, 8);
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

    public async Task FilterAssets()
    {
        FilteredLocalAssets.Clear();
        FilteredSharedAssets.Clear();
        FilteredReuseableAssets.Clear();
        FilteredAvailableTemplates.Clear();

        List<AssetItemViewModel> localFiltered = new();
        List<AssetItemViewModel> sharedFiltered = new();
        List<AssetItemViewModel> reuseableFiltered = new();
        List<TemplateItemViewModel> templateFiltered = new();

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            // ????????????????
            localFiltered.AddRange(LocalAssets);
            sharedFiltered.AddRange(SharedAssets);
            reuseableFiltered.AddRange(ReuseableAssets);
            templateFiltered.AddRange(AvailableTemplates.Where(ShouldShowTemplateByScope));
        }
        else
        {
            var inputPron = (await TextServices.GetHowToPronuce(SearchText, default)).ToLower();
            var inputPronInLocate = ((await TextServices.GetHowToPronuce(SearchText, TextHelper.FromLanguageCode(Localized._LocaleId_)))).ToLower();
            // ????
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

            foreach (var template in AvailableTemplates)
            {
                if (!ShouldShowTemplateByScope(template))
                {
                    continue;
                }

                if (template.Name.ToLower().Contains(searchLower)
                    || template.TemplateType.ToLower().Contains(searchLower)
                    || template.Scope.ToLower().Contains(searchLower))
                {
                    templateFiltered.Add(template);
                }
            }
        }

        // ????
        if (OrderOption == 0)
        {
            // By add date - ???????????
            localFiltered = localFiltered.OrderByDescending(a => a.OriginalAsset?.CreatedAt ?? DateTime.MinValue).ToList();
            sharedFiltered = sharedFiltered.OrderByDescending(a => a.OriginalAsset?.CreatedAt ?? DateTime.MinValue).ToList();
            reuseableFiltered = reuseableFiltered.OrderByDescending(a => a.OriginalAsset?.CreatedAt ?? DateTime.MinValue).ToList();
        }
        else if (OrderOption == 1)
        {
            // By name - ??????
            localFiltered = (await localFiltered.OrderByPronounceAsync(a => a.Name)).ToList();
            sharedFiltered = (await sharedFiltered.OrderByPronounceAsync(a => a.Name)).ToList();
            reuseableFiltered = (await reuseableFiltered.OrderByPronounceAsync(a => a.Name)).ToList();
            templateFiltered = (await templateFiltered.OrderByPronounceAsync(a => a.Name)).ToList();
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

        foreach (var template in templateFiltered)
        {
            FilteredAvailableTemplates.Add(template);
        }

        // ????
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

        // ??????
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

    public CommunityToolkit.Maui.Views.DrawingView? _drawingView;
    public readonly Stack<CommunityToolkit.Maui.Core.IDrawingLine> _redoStack = new();

    public void SetDrawingView(CommunityToolkit.Maui.Views.DrawingView drawingView)
    {
        _drawingView = drawingView;
        _drawingView.LineColor = DrawingPenColor;
        _drawingView.LineWidth = DrawingLineWidth;
        _drawingView.DrawingLineCompleted += (_, _) => _redoStack.Clear();
    }

    public void DrawingUndo()
    {
        if (_drawingView?.Lines is { Count: > 0 } lines)
        {
            var last = lines[^1];
            _redoStack.Push(last);
            lines.RemoveAt(lines.Count - 1);
        }
    }

    public void DrawingRedo()
    {
        if (_redoStack.Count > 0 && _drawingView != null)
        {
            _drawingView.Lines.Add(_redoStack.Pop());
        }
    }

    public async Task SelectDrawingPenColor()
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
        // ? Windows ???????????
        Color[] palette = [Colors.Black, Colors.Red, Colors.Blue, Colors.Green, Colors.Orange, Colors.Purple, Colors.White];
        var idx = Array.FindIndex(palette, c => c == DrawingPenColor);
        DrawingPenColor = palette[(idx + 1) % palette.Length];
#endif
    }

    public async Task AddDrawingContent()
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
                await _draftPage.DisplayAlertAsync(Localized._Error, "Failed to capture drawing.", Localized._OK);
                return;
            }

            // ???????
            var sketchDir = Path.Combine(FileSystem.CacheDirectory, "Sketches");
            Directory.CreateDirectory(sketchDir);
            var tempPath = Path.Combine(sketchDir, $"sketch_{Guid.NewGuid():N}.png");

            using (var fs = File.Create(tempPath))
            {
                await imageStream.CopyToAsync(fs);
            }

            const uint defaultFrames = 90u;
            BeginTimelineClipPlacement((trackIndex, startX) =>
            {
                var element = _draftPage.CreateAndAddClip(
                    startX: startX,
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

                _drawingView.Lines.Clear();
                _redoStack.Clear();
                return element;
            }, name: "Drawing");

            ClipAdded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log(ex, "AddDrawingContent", this);
            await _draftPage.DisplayAlertAsync(Localized._Error, $"Failed to add sketch: {ex.Message}", Localized._OK);
        }
    }

    #endregion

    #region misc
    public async Task AddSolidColorClip()
    {
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
        var colorStr = $"#{R / 257:X2}{G / 257:X2}{B / 257:X2}{(int)(A * 255):X2}";
        BeginTimelineClipPlacement((trackIndex, startX) =>
        {
            var element = _draftPage.CreateAndAddClip(
                startX: startX,
                width: _draftPage.FrameToPixel(90),
                trackIndex: trackIndex,
                id: null,
                labelText: colorStr,
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
            return element;
        }, name: colorStr);

        ClipAdded?.Invoke(this, EventArgs.Empty);
    }


    public async Task AddAlternativeSourceClip()
    {
        var path = await _draftPage.DisplayPromptAsync("Add", "Input source path", placeholder: "#<provider>,<stream id>");
        if (string.IsNullOrWhiteSpace(path)) return;

        var vidSrc = PluginManager.CreateVideoSource(path);

        BeginTimelineClipPlacement((trackIndex, startX) =>
        {
            var element = _draftPage.CreateAndAddClip(
                startX: startX,
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
            return element;
        }, name: path);

        ClipAdded?.Invoke(this, EventArgs.Empty);
    }


    public async Task AddReuseableAssetClip(AssetItemViewModel? assetViewModel)
    {
        if (assetViewModel?.OriginalAsset == null) return;

        try
        {
            // ??????????????? token ???
            if (assetViewModel.IsRemote)
            {
                // ??????
                // TODO: ???????

                // ??????????? token
                var multiServerService = MultiServerRemoteAssetService.Instance;
                var tokenResponse = await multiServerService.GetFileTokenAsync(assetViewModel.ServerId ?? "", assetViewModel.Id);

                if (tokenResponse == null)
                {
                    await _draftPage.DisplayAlertAsync(Localized._Error, "Cannot get file access token.", Localized._OK);
                    return;
                }

                // ?????? URL?????????? URL?
                var serverBaseUrl = assetViewModel.ServerUrl?.TrimEnd('/') ?? "";
                var fileServerUri = new Uri($"{serverBaseUrl}/api/file/download?token={tokenResponse.token}");

                Log($"Downloading asset from {fileServerUri}...");

                // ?????????
                var cacheDir = Path.Combine(FileSystem.CacheDirectory, "RemoteAssets", assetViewModel.ServerId ?? "default");
                if (!Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }

                var fileName = Path.GetFileName(assetViewModel.OriginalAsset.Path) ?? $"{assetViewModel.Id}{Path.GetExtension(assetViewModel.OriginalAsset.Path)}";
                var localPath = Path.Combine(cacheDir, fileName);

                // ????????????
                if (!File.Exists(localPath))
                {
#if DEBUG
                    // ??????? SSL ????
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

                // ?????????????
                assetViewModel.OriginalAsset.Path = localPath;
            }

            BeginTimelineClipPlacement((trackIndex, startX) =>
            {
                var clip = _draftPage.CreateFromAsset(
                    assetViewModel.OriginalAsset,
                    trackIndex,
                    startX,
                    InternalPluginBase.InternalPluginBaseID,
                    assetViewModel.SourcePath
                );

                _draftPage.RegisterClip(clip, true);
                _draftPage.AddAClip(clip);
                return clip;
            }, name: assetViewModel.Name);

            ClipAdded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log(ex, "load reuseabel asset", this);
            await _draftPage.DisplayAlertAsync(Localized._Error, $"Failed to add asset: {ex.Message}", Localized._OK);
        }
    }
    #endregion

    #region AI Content Generation

    public async Task GenerateAIContent()
    {
        if (string.IsNullOrWhiteSpace(AIPrompt))
        {
            await _draftPage.DisplayAlertAsync(
                Localized._Error,
                Localized.DraftPage_AddClipView_AIGC_InputPlaceholder,
                Localized._OK);
            return;
        }

        if (IsGeneratingAIContent)
        {
            return; // ??????
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

    public async Task GenerateAIImage()
    {
        try
        {
            // ??????????
            var (width, height) = ParseVideoRatio(AIVideoRatio);

            // ? UI ???????? ImageStyle ??
            var imageStyle = AIImageStyle switch
            {
                var t when t == Localized.DraftPage_AddClipView_AIGC_ImageStyle_Natural => ImageStyle.Natural,
                var t when t == Localized.DraftPage_AddClipView_AIGC_ImageStyle_Vivid => ImageStyle.Vivid,
                var t when t == Localized.DraftPage_AddClipView_AIGC_ImageStyle_Anime => ImageStyle.Anime,
                var t when t == Localized.DraftPage_AddClipView_AIGC_ImageStyle_Photography => ImageStyle.Photography,
                var t when t == Localized.DraftPage_AddClipView_AIGC_ImageStyle_TradidtionalPainting => ImageStyle.TradidtionalPainting,
                _ => ImageStyle.Natural
            };

            // ????????
            var options = new ImageGenerationOptions
            {
                Width = width,
                Height = height,
                Style = imageStyle,
                Quality = ImageQuality.Standard
            };

            // ?? AI ????
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

            // ??????????????
            var asset = await DownloadRemoteResourcesToLocal(_draftPage, result.ImageUrl, "png", "AIGenerated-{0}");
            if (asset == null)
            {
                await _draftPage.DisplayAlertAsync(
                    Localized._Error,
                    $"{SettingsManager.SettingLocalizedResources.AISetting_Test_ErrorResponse}{Environment.NewLine}(Cannot download result.)",
                    Localized._OK);
                return;
            }

            // ????????
            await AddAIGeneratedImageToTimeline(asset.Path, AIPrompt);

            // ????
            AIPrompt = "";

            // ??????
            ClipAdded?.Invoke(this, EventArgs.Empty);
            await _draftPage.HidePopup(true);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Generate AI image", this);
            throw;
        }
    }


    public async Task AddAIGeneratedImageToTimeline(string imagePath, string prompt)
    {
        try
        {
            const uint defaultFrames = 90u;
            BeginTimelineClipPlacement((trackIndex, startX) =>
            {
                var element = _draftPage.CreateAndAddClip(
                    startX: startX,
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
                return element;
            }, name: $"AI: {string.Join("", prompt.Take(20).ToArray())}");
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Add AI generated image to timeline", this);
            throw;
        }
    }

    public async Task GenerateAIVideo()
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

    public (int width, int height) ParseVideoRatio(string ratio)
    {
        return ratio switch
        {
            "16:9" => (1280, 720),
            "9:16" => (720, 1280),
            "1:1" => (1024, 1024),
            "4:3" => (1024, 768),
            "3:4" => (768, 1024),
            _ => (1280, 720) // ??16:9
        };
    }


    public async Task AddAIGeneratedVideoToTimeline(string videoPath, string prompt)
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

            BeginTimelineClipPlacement((trackIndex, startX) =>
            {
                var element = _draftPage.CreateAndAddClip(
                    startX: startX,
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
                return element;
            }, name: $"AI: {string.Join("", prompt.Take(20).ToArray())}");
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Add AI generated video to timeline", this);
            throw;
        }
    }

    #endregion

    #region AI Transition Generation

    public async Task GenerateAITransition(object direction)
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
                Localized.DraftPage_AddClipView_AddTransform_AddInSelectedDirection,
                Localized._OK);
            return;
        }

        // ???????? Clip ??? Clip
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
                Localized.DraftPage_AddClipView_AIGC_InputPlaceholder,
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
                0 => 1,  // 1?
                1 => 2,  // 2?
                2 => 3,  // 3?
                3 => 5,  // 5?
                _ => 2   // ??2?
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
                Localized.DraftPage_AddClipView_AddTransform_AddInSelectedDirection,
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

            var sourcePath = asset.Path; // ????????

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

    public async Task<IPicture?> GetClipLastFrame(ClipElementUI clipElement)
    {
        try
        {
            // ? ClipElementUI ??? IClip ??????
            var clipData = ConvertClipElementToIClip(clipElement);
            if (clipData == null) return null;

            // ?????????
            var lastFrameIndex = clipData.Duration > 0 ? clipData.Duration - 1 : 0;
            var frame = clipData.GetFrameRelativeToStartPointOfSource(lastFrameIndex, 1280, 720, 8);

            return frame;
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Get clip last frame", this);
            return null;
        }
    }

    public async Task<IPicture?> GetClipFirstFrame(ClipElementUI clipElement)
    {
        try
        {
            var clipData = ConvertClipElementToIClip(clipElement);
            if (clipData == null) return null;

            var frame = clipData.GetFrameRelativeToStartPointOfSource(0, 1280, 720, 8);

            return frame;
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Get clip first frame", this);
            return null;
        }
    }

    public Render.RenderAPIBase.ClipAndTrack.IClip? ConvertClipElementToIClip(ClipElementUI clipElement)
    {
        try
        {
            // ???? Clip ???
            var draftData = DraftImportAndExportHelper.ExportFromDraftPage(_draftPage, true, false);
            var clips = DraftImportAndExportHelper.JSONToIClips(draftData, true, 8);

            // ?? ID ????? IClip
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

            // ?????
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
                            // ??????????????
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
    /// ????? ID??????????
    /// </summary>
    public string? ServerId { get; set; }

    /// <summary>
    /// ???????
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// ????? URL
    /// </summary>
    public string? ServerUrl { get; set; }

    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbPath);

    public Brush BackgroundBrush { get; set; } = new SolidColorBrush(Colors.CornflowerBlue);

    public Command AddAssetClipCommand { get; set; }

    public AssetItemViewModel(ProjectAddClipViewModel parent)
    {
        AddAssetClipCommand = new Command(async () => await parent.AddAssetClip(this));
    }
}

public class TemplateItemViewModel
{
    public Guid TemplateId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TemplateType { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public int ClipCount { get; set; }
    public int TrackCount { get; set; }
    public ITemplateStructure Template { get; set; } = default!;

    public string Summary => Localized.DraftPage_AssetPanel_Templates_Summary(ClipCount, TrackCount);

    public Command AddTemplateCommand { get; set; }

    public TemplateItemViewModel(ProjectAddClipViewModel parent)
    {
        AddTemplateCommand = new Command(async () => await parent.AddTemplate(this));
    }
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

    public string? _previewVideoPath;
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

    public bool _isGeneratingPreview;
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

    public Command AddTransformClipCommand { get; set; }
    public Command AddTransformClipInLeftCommand { get; set; }
    public Command AddTransformClipInRightCommand { get; set; }

    public TransformItemViewModel(ProjectAddClipViewModel parent)
    {
        AddTransformClipCommand = new Command(async () => await parent.AddTransformClip(this, false, false));
        AddTransformClipInLeftCommand = new Command(async () => await parent.AddTransformClip(this, true, false));
        AddTransformClipInRightCommand = new Command(async () => await parent.AddTransformClip(this, false, true));
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

    public Command AddTextClipWithStyleCommand { get; set; }
    public TextStyleItemViewModel(ProjectAddClipViewModel parent)
    {
        AddTextClipWithStyleCommand = new Command(async () => parent.AddTextClipWithStyle(this));
    }
}

#endregion


