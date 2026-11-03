using projectFrameCut.Render.Text;
using projectFrameCut.Render.RenderAPIBase.Text;
using ITransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using CommunityToolkit.Maui.Core;
using Microsoft.Maui.Storage;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Sources;
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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using static projectFrameCut.ApplicationAPIBase.Helpers.TextHelper;


namespace projectFrameCut.ViewModels;

public partial class ProjectAddClipViewModel : INotifyPropertyChanged
{
    #region menber
    public ProjectAddClipViewModel(ref DraftPage draftPage)
    {
        _draftPage = draftPage;
        RegisterCommands();

        _ = Task.Run(RefreshSafelyAsync);
        _draftPage.SelectedClipChanged += async (s, e) => await RefreshSafelyAsync();
    }

    public readonly DraftPage _draftPage;
    private int _filterVersion;
    private int _rpcLoadVersion;


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

    public bool IsDraftSelectedAnyClip
    {
        get => _draftPage.SelectedClip != null;
    }

    // 文本样式支持
    public string TextToAdd
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasTextToAdd));
                UpdateTextStyleCommands();
            }
        }
    } = string.Empty;

    public bool HasTextToAdd => !string.IsNullOrWhiteSpace(TextToAdd);

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
    public ObservableCollection<RpcVideoSourceItemViewModel> RpcVideoSources { get; } = new();
    public ObservableCollection<TemplateItemViewModel> AvailableTemplates { get; } = new();
    public ObservableCollection<TextStyleProviderItemViewModel> AvailableTextStyleProviders { get; } = new();

    public ObservableCollection<object> FilteredLocalAssets { get; } = new();
    public ObservableCollection<object> FilteredSharedAssets { get; } = new();
    public ObservableCollection<AssetItemViewModel> FilteredReuseableAssets { get; } = new();
    public ObservableCollection<object> FilteredRpcVideoSources { get; } = new();
    public bool HasNoExternalSources => FilteredRpcVideoSources.All(x => x is AddSourceCardViewModel);
    public ObservableCollection<TemplateItemViewModel> FilteredAvailableTemplates { get; } = new();
    public ObservableCollection<TextStyleProviderItemViewModel> FilteredAvailableTextStyleProviders { get; } = new();
    #endregion

    #region command
    public ICommand AddTextClipCommand { get; set; } = null!;
    public ICommand AddSolidColorClipCommand { get; set; } = null!;
    public ICommand AddEmptyVideoClipCommand { get; set; } = null!;
    public ICommand AddPassthroughMaskClipCommand { get; set; } = null!;
    public ICommand GenerateTextPreviewCommand { get; set; } = null!;
    public ICommand AddSubTitleClipCommand { get; set; } = null!;
    public ICommand AddAlternativeSourceClipCommand { get; set; } = null!;
    public ICommand ImportProjectReferenceCommand => _draftPage.ImportProjectReferenceCommand;
    public ICommand AddAssetClipCommand { get; set; } = null!;
    public ICommand AddTemplateCommand { get; set; } = null!;
    public ICommand AddReuseableAssetClipCommand { get; set; } = null!;
    public ICommand RefreshRpcVideoSourcesCommand { get; set; } = null!;
    public ICommand DrawingContentUndoCommand { get; set; } = null!;
    public ICommand DrawingContentRedoCommand { get; set; } = null!;
    public ICommand DrawingSelectPenColorCommand { get; set; } = null!;
    public ICommand AddDrawingContentCommand { get; set; } = null!;

    void RegisterCommands()
    {
        InitializeSubtitleCommands();
        AddSolidColorClipCommand = new Command(async () => await AddSolidColorClip());
        AddEmptyVideoClipCommand = new Command(AddEmptyVideoClip);
        AddPassthroughMaskClipCommand = new Command(() => AddMaskSource("Passthrough", Localized.DraftPage_AddClipView_PassthroughMaskSource));
        GenerateTextPreviewCommand = new Command(async () => await InitializeTextStyles());
        AddAlternativeSourceClipCommand = new Command(async () => await AddAlternativeSourceClip());
        AddAssetClipCommand = new Command<AssetItemViewModel>(async (asset) => await AddAssetClip(asset));
        RefreshRpcVideoSourcesCommand = new Command(async () => await LoadRpcVideoSources());
        AddTemplateCommand = new Command<TemplateItemViewModel>(async (template) => await AddTemplate(template));

        DrawingContentUndoCommand = new Command(DrawingUndo);
        DrawingContentRedoCommand = new Command(DrawingRedo);
        DrawingSelectPenColorCommand = new Command(async () => await SelectDrawingPenColor());
        AddDrawingContentCommand = new Command(async () => await AddDrawingContent());
    }
    #endregion

    #region load

    public event EventHandler? ClipAdded;

    private async Task RefreshSafelyAsync()
    {
        try { await Refresh(); }
        catch (Exception ex) { Log(ex, "Refresh clip source panel", this); }
    }

    public async Task Refresh()
    {
        if (!MainThread.IsMainThread)
        {
            await MainThread.InvokeOnMainThreadAsync(Refresh);
            return;
        }
        RegisterCommands();
        await LoadAssets();
        await LoadRpcVideoSources();
        LoadTemplates();
        await InitializeTextStyles();
        await FilterAssets();
        OnPropertyChanged(nameof(IsDraftSelectedAnyClip));
    }

    public void LoadTemplates()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(LoadTemplates);
            return;
        }
        _filterVersion++;
        AvailableTemplates.Clear();

        foreach (var kv in TemplateStore.Templates)
        {
            var template = kv.Value;
            var item = new TemplateItemViewModel(this)
            {
                TemplateId = kv.Key,
                Name = string.IsNullOrWhiteSpace(template.TemplateName) ? kv.Key.ToString() : template.TemplateName,
                TemplateType = template is ScriptBasedTemplateStructure ? "Script (unsupported)" : template.TemplateType.ToString(),
                Scope = template.Scope.ToString(),
                Template = template
            };

            if (template is JSONBasedTemplateStructure jsonTemplate)
            {
                // 从 Draft 取实际片段数（完整模板）；列表模板用元数据中预存的值
                item.ClipCount = jsonTemplate.Draft.Clips?.Length > 0
                    ? jsonTemplate.Draft.Clips.Length
                    : jsonTemplate.ClipCount;
                item.TrackCount = jsonTemplate.Draft.SoundTracks?.Length > 0
                    ? jsonTemplate.Draft.SoundTracks.Length
                    : jsonTemplate.TrackCount;
            }
            else if (template is ScriptBasedTemplateStructure scriptTemplate)
            {
                item.ClipCount = scriptTemplate.Draft.Clips?.Length > 0
                    ? scriptTemplate.Draft.Clips.Length
                    : scriptTemplate.ClipCount;
                item.TrackCount = scriptTemplate.Draft.SoundTracks?.Length > 0
                    ? scriptTemplate.Draft.SoundTracks.Length
                    : scriptTemplate.TrackCount;
            }

            AvailableTemplates.Add(item);
        }
    }

    private static bool ShouldShowTemplateByScope(TemplateItemViewModel template, bool showAllTemplates)
    {
        if (showAllTemplates)
            return true;

        return string.Equals(template.Scope, TemplateScope.Any.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(template.Scope, TemplateScope.Clips.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public async Task LoadAssets()
    {
        if (!MainThread.IsMainThread)
        {
            await MainThread.InvokeOnMainThreadAsync(LoadAssets);
            return;
        }
        _filterVersion++;
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

    public async Task LoadRpcVideoSources()
    {
        if (!MainThread.IsMainThread)
        {
            await MainThread.InvokeOnMainThreadAsync(LoadRpcVideoSources);
            return;
        }
        int version = ++_rpcLoadVersion;
        List<RpcVideoSourceItemViewModel> sources = new();
        if (ProjectExternalSourceService.AllowAssemblySources)
        {
            try
            {
                var catalog = await projectFrameCut.Render.RPCProtocol.ProjectExternalSourceRuntime.ListAsync(_draftPage.WorkingPath);
                foreach (var asset in projectFrameCut.Render.PluginIsolation.ProjectExternalSourceDatabase.Read(_draftPage.WorkingPath).Assets)
                {
                    var status = catalog.Sources.FirstOrDefault(x => x.ImportId == asset.ImportId);
                    if (asset.Sources.Count == 0 && asset.AudioSources.Count == 0 && asset.MaskSources.Count == 0)
                        sources.Add(new(this, new ExternalVideoSourceDescriptor { Name = asset.Manifest.Name }, asset, status));
                    else
                    {
                        foreach (var source in status?.Sources ?? asset.Sources) sources.Add(new(this, source, asset, status));
                        foreach (var source in status?.AudioSources ?? asset.AudioSources) sources.Add(new(this, source, asset, status));
                        foreach (var source in status?.MaskSources ?? asset.MaskSources) sources.Add(new(this, source, asset, status));
                    }
                }
            }
            catch (Exception ex) { Log(ex, "Load project external sources", this); }
        }
        if (ProjectExternalSourceService.AllowRpcSources && RenderRpcBootstrap.TryGetClient(out var client) && client is not null)
        {
            try
            {
                foreach (var source in (await client.ListExternalVideoSourcesAsync()).Sources.OrderBy(x => x.ClientName).ThenBy(x => x.Name))
                    sources.Add(new(this, source));
                foreach (var source in (await client.ListExternalAudioSourcesAsync()).Sources.OrderBy(x => x.ClientName).ThenBy(x => x.Name))
                    sources.Add(new(this, source));
                foreach (var source in (await client.ListExternalMaskSourcesAsync()).Sources.OrderBy(x => x.ClientName).ThenBy(x => x.Name))
                    sources.Add(new(this, source));
            }
            catch (Exception ex)
            {
                Log(ex, "Load external RPC sources", this);
            }
        }
        bool updated = await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (version != _rpcLoadVersion) return false;
            _filterVersion++;
            RpcVideoSources.Clear();
            foreach (var source in sources) RpcVideoSources.Add(source);
            return true;
        });
        if (updated) await FilterAssets();
    }

    #endregion

    #region asset
    public async Task AddAssetClip(AssetItemViewModel? assetViewModel)
    {
        if (assetViewModel?.OriginalAsset == null) return;

        if (assetViewModel.OriginalAsset.Path is string subtitlePath && projectFrameCut.Render.Subtitles.SubtitleImporter.Supports(subtitlePath))
        {
            if (_draftPage.IsReadonly) return;
            try
            {
                var imported = await SubtitleServices.ReadAsync(subtitlePath, _draftPage);
                if (imported is not null) AddSubtitle(imported.Data, assetViewModel.Name);
            }
            catch (Exception ex)
            {
                Log(ex, "Add subtitle asset", _draftPage);
                await _draftPage.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
            }
            return;
        }

        if (assetViewModel.OriginalAsset.GetClipMode() == ClipMode.VectorComponentClip)
        {
            AddVectorComponent(VectorClipServices.Import(assetViewModel.OriginalAsset.Path!));
            return;
        }

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

    public async Task AddRpcVideoSource(RpcVideoSourceItemViewModel? item)
    {
        if (item is null || !item.CanAdd) return;
        try
        {
            if (item.MaskSource is { } mask)
            {
                AddMaskSource(item.ProjectAsset is null ? RemoteRpcMaskSource.CreatePath(mask)
                    : ProjectExternalMaskSource.CreatePath(item.ProjectAsset.ImportId, mask), mask.Name);
                return;
            }
            var source = item.Source;
            var audio = item.AudioSource;
            var path = audio is not null
                ? item.ProjectAsset is null ? RemoteRpcAudioSource.CreatePath(audio) : ProjectExternalAudioSource.CreatePath(item.ProjectAsset.ImportId, audio)
                : item.ProjectAsset is null ? RemoteRpcVideoSource.CreatePath(source) : ProjectExternalVideoSource.CreatePath(item.ProjectAsset.ImportId, source);
            var asset = new AssetItem
            {
                AssetId = $"rpc:{(audio is not null ? "audio:" : "")}{source.ClientId:N}:{source.SourceId}",
                Name = source.Name,
                Path = path,
                AssetType = audio is not null ? AssetType.Audio : AssetType.Video,
                ClipType = audio is not null ? ClipMode.AudioClip : ClipMode.VideoClip,
                Duration = audio is not null ? audio.Duration : source.TotalFrames,
                SecondPerFrame = audio is not null ? 1 : source.Fps > 0 ? (float)(1d / source.Fps) : -1,
                Width = source.Width,
                Height = source.Height,
                BitPerPixel = source.HasKnownResultBitsPerPixel ? source.ResultBitsPerPixel : 0,
                CreatedAt = DateTime.UtcNow,
            };
            BeginTimelineClipPlacement((trackIndex, startX) =>
            {
                var clip = _draftPage.CreateFromAsset(asset, trackIndex, startX, InternalPluginBase.InternalPluginBaseID, path);
                _draftPage.RegisterClip(clip, true);
                _draftPage.AddAClip(clip);
                return clip;
            }, name: source.Name, useSubTrack: audio is not null);
            ClipAdded?.Invoke(this, EventArgs.Empty);
            Log($"Added external source {source.ClientId}/{source.SourceId} to the timeline.");
        }
        catch (Exception ex)
        {
            Log(ex, "Add external source", this);
            await _draftPage.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

    public async Task AddTemplate(TemplateItemViewModel? templateViewModel)
    {
        if (templateViewModel?.Template is null)
        {
            return;
        }

        if (templateViewModel.Template is ScriptBasedTemplateStructure)
        {
            await _draftPage.DisplayAlertAsync(Localized._Error, "Script templates are no longer supported.", Localized._OK);
            return;
        }

        if (templateViewModel.Template is not JSONBasedTemplateStructure jsonTemplate)
        {
            await _draftPage.DisplayAlertAsync(Localized._Info, "This template does not support timeline insertion.", Localized._OK);
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
            if (!await EnsurePackagedTemplateAssetsInProjectAsync(draftNode, jsonTemplate)) return;

            var remappedDraft = draftNode.Deserialize<DraftStructureJSON>(DraftPage.DraftJSONOption);
            if (remappedDraft is null)
            {
                await _draftPage.DisplayAlertAsync(Localized._Error, "Template draft deserialization failed.", Localized._OK);
                return;
            }

            var clipDtos = remappedDraft.Clips?.ToList() ?? [];

            var soundtrackDtos = remappedDraft.SoundTracks?.ToList() ?? [];

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
                Clips = clipDtos.ToArray(),
                SoundTracks = soundtrackDtos.ToArray(),
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
        ITemplateStructure template,
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

    private async Task<bool> EnsurePackagedTemplateAssetsInProjectAsync(JsonNode draftNode, JSONBasedTemplateStructure template)
    {
        if (template.AssetHashTable is not { Count: > 0 })
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_draftPage.WorkingPath))
        {
            return true;
        }

        var referencedAssetIds = CollectTemplateReferencedAssetIds(draftNode);
        if (referencedAssetIds.Count == 0)
        {
            return true;
        }

        var projectAssetsDir = Path.Combine(_draftPage.WorkingPath, "assets");
        bool? copyToProject = null;

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

            copyToProject ??= await AssetDatabase.PickImportModeAsync(_draftPage);
            if (copyToProject is null) return false;
            var assetPath = Path.GetFullPath(packagedSourcePath);
            if (copyToProject.Value)
            {
                Directory.CreateDirectory(projectAssetsDir);
                assetPath = Path.Combine(projectAssetsDir, assetId + extension);
                await Task.Run(() => File.Copy(packagedSourcePath, assetPath, overwrite: true));
            }

            var projectAsset = AssetDatabase.Assets.TryGetValue(assetId, out var sharedAsset)
                ? JsonSerializer.Deserialize<AssetItem>(
                    JsonSerializer.Serialize(sharedAsset, DraftPage.DraftJSONOption),
                    DraftPage.DraftJSONOption)
                : null;

            projectAsset ??= new AssetItem();
            projectAsset.AssetId = assetId;
            projectAsset.Path = assetPath;
            projectAsset.IsReference = !copyToProject.Value;
            if (projectAsset.AssetType == AssetType.Other)
            {
                projectAsset.AssetType = AssetItem.GetAssetType(assetPath);
            }
            if (projectAsset.AssetType == AssetType.Image) projectAsset.ThumbnailPath = assetPath;

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
            resolvedAssetPaths[assetId] = assetPath;
        }

        ReplaceTemplateAssetReferencesWithPaths(draftNode, resolvedAssetPaths);
        return true;
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

    #endregion

    #region transform

    public async Task GenerateTransformPreviewAsync(TransformItemViewModel? transform, TransformInputMode inputMode = TransformInputMode.TwoInput, TransformSide side = TransformSide.Right, IEffectProvider? configured = null)
    {
        if (transform is null || transform.IsGeneratingPreview) return;
        transform.IsGeneratingPreview = true;
        try
        {
            var factories = TransformServices.GetAvailableTransforms();
            if (!factories.TryGetValue(transform.TypeKey, out var factory) && configured is null) return;

            var previewDir = Path.Combine(MauiProgram.CachePath, "TransformPreviews");
            Directory.CreateDirectory(previewDir);
            var videoPath = Path.Combine(previewDir, $"{Guid.NewGuid():N}.mp4");

            if (File.Exists(videoPath)) File.Delete(videoPath);

            const int previewW = 320, previewH = 180, fps = 24, frameCount = 48;

            await Task.Run(() =>
            {
                // Detect a suitable encoder
                string codec;
                if (VideoWriterFP16.DetectCodec("libx264")) codec = "libx264";
                else if (VideoWriterFP16.DetectCodec("mpeg4")) codec = "mpeg4";
                else return; // No encoder available

                var prevId = Guid.NewGuid();
                var nextId = Guid.NewGuid();
                var prevClip = new SolidColorClip
                {
                    Id = prevId,
                    Name = "_preview_prev",
                    UseFixedOutputSize = false,
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
                    Id = nextId,
                    Name = "_preview_next",
                    UseFixedOutputSize = false,
                    StartFrame = (uint)frameCount,
                    Duration = (uint)frameCount,
                    FrameTime = 1f / fps,
                    SecondPerFrameRatio = 1f,
                    R = 0x8C00,
                    G = 0x2A00,
                    B = 0x2A00,
                };

                projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform? t = null;
                try
                {
                    t = TransformServices.Create(configured ?? factory!());
                    using var writer = new VideoWriterFP16
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
                        float progress = (float)i / Math.Max(1, frameCount - 1);
                        using var context = ValueProviderFrameContext.PushFrame(i, progress);
                        using var leftFrame = ((IClip)prevClip).GetFrame(0, previewW, previewH, IPicture.PicturePixelMode.Float16Picture);
                        using var rightFrame = inputMode == TransformInputMode.TwoInput ? ((IClip)nextClip).GetFrame(nextClip.StartFrame, previewW, previewH, IPicture.PicturePixelMode.Float16Picture) : null;
                        leftFrame.CanBeDisposed = true;
                        if (rightFrame is not null) rightFrame.CanBeDisposed = true;
                        var result = TransformProcessing.ProcessFrames(leftFrame, rightFrame, t, inputMode, progress, side, previewW, previewH);
                        using var frame = ReferenceEquals(result, leftFrame) || ReferenceEquals(result, rightFrame) ? result.Clone() : result;
                        if (frame.Width != previewW || frame.Height != previewH)
                            throw new InvalidOperationException($"Transform preview {t.TypeName} returned {frame.Width}x{frame.Height}; expected {previewW}x{previewH}.");
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
        var state = await MainThread.InvokeOnMainThreadAsync(() => (
            Version: ++_filterVersion,
            Search: SearchText ?? "",
            Order: OrderOption,
            ShowAll: ShowAllTemplates,
            Local: LocalAssets.ToArray(),
            Shared: SharedAssets.ToArray(),
            Reuseable: ReuseableAssets.ToArray(),
            Rpc: RpcVideoSources.ToArray(),
            Templates: AvailableTemplates.ToArray(),
            Providers: AvailableTextStyleProviders.ToArray()));

        List<AssetItemViewModel> localFiltered = new();
        List<AssetItemViewModel> sharedFiltered = new();
        List<AssetItemViewModel> reuseableFiltered = new();
        List<RpcVideoSourceItemViewModel> rpcFiltered = new();
        List<TemplateItemViewModel> templateFiltered = new();

        if (string.IsNullOrWhiteSpace(state.Search))
        {
            // ????????????????
            localFiltered.AddRange(state.Local);
            sharedFiltered.AddRange(state.Shared);
            reuseableFiltered.AddRange(state.Reuseable);
            rpcFiltered.AddRange(state.Rpc);
            templateFiltered.AddRange(state.Templates.Where(x => ShouldShowTemplateByScope(x, state.ShowAll)));
        }
        else
        {
            var inputPron = (await TextServices.GetHowToPronuce(state.Search, default)).ToLower();
            var inputPronInLocate = ((await TextServices.GetHowToPronuce(state.Search, TextHelper.FromLanguageCode(Localized._LocaleId_)))).ToLower();
            // ????
            var searchLower = state.Search.ToLower();
            foreach (var asset in state.Local)
            {
                var assetPron = (await TextServices.GetHowToPronuce(asset.Name, default)).ToLower();
                var assetPronInLocate = (await TextServices.GetHowToPronuce(asset.Name, TextHelper.FromLanguageCode(Localized._LocaleId_))).ToLower();
                if (asset.Name.ToLower().Contains(searchLower) || assetPron.Contains(state.Search) || assetPron.Contains(inputPron) || assetPron.Contains(inputPronInLocate) || assetPronInLocate.Contains(state.Search) || assetPronInLocate.Contains(inputPron) || assetPronInLocate.Contains(inputPronInLocate))
                {
                    localFiltered.Add(asset);
                }
            }
            foreach (var asset in state.Shared)
            {
                var assetPron = (await TextServices.GetHowToPronuce(asset.Name, default)).ToLower();
                var assetPronInLocate = (await TextServices.GetHowToPronuce(asset.Name, TextHelper.FromLanguageCode(Localized._LocaleId_))).ToLower();
                if (asset.Name.ToLower().Contains(searchLower) || assetPron.Contains(state.Search) || assetPron.Contains(inputPron) || assetPron.Contains(inputPronInLocate) || assetPronInLocate.Contains(state.Search) || assetPronInLocate.Contains(inputPron) || assetPronInLocate.Contains(inputPronInLocate))
                {
                    sharedFiltered.Add(asset);
                }
            }
            foreach (var asset in state.Reuseable)
            {
                var assetPron = (await TextServices.GetHowToPronuce(asset.Name, default)).ToLower();
                var assetPronInLocate = (await TextServices.GetHowToPronuce(asset.Name, TextHelper.FromLanguageCode(Localized._LocaleId_))).ToLower();
                if (asset.Name.ToLower().Contains(searchLower) || assetPron.Contains(state.Search) || assetPron.Contains(inputPron) || assetPron.Contains(inputPronInLocate) || assetPronInLocate.Contains(state.Search) || assetPronInLocate.Contains(inputPron) || assetPronInLocate.Contains(inputPronInLocate))
                {
                    reuseableFiltered.Add(asset);
                }
            }
            rpcFiltered.AddRange(state.Rpc.Where(x => x.Name.Contains(searchLower, StringComparison.OrdinalIgnoreCase)
                || x.ClientName.Contains(searchLower, StringComparison.OrdinalIgnoreCase)
                || x.Status.Contains(searchLower, StringComparison.OrdinalIgnoreCase)
                || x.DecoderName.Contains(searchLower, StringComparison.OrdinalIgnoreCase)));

            foreach (var template in state.Templates)
            {
                if (!ShouldShowTemplateByScope(template, state.ShowAll))
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
        if (state.Order == 0)
        {
            // By add date - ???????????
            localFiltered = localFiltered.OrderByDescending(a => a.OriginalAsset?.CreatedAt ?? DateTime.MinValue).ToList();
            sharedFiltered = sharedFiltered.OrderByDescending(a => a.OriginalAsset?.CreatedAt ?? DateTime.MinValue).ToList();
            reuseableFiltered = reuseableFiltered.OrderByDescending(a => a.OriginalAsset?.CreatedAt ?? DateTime.MinValue).ToList();
            rpcFiltered = rpcFiltered.OrderByDescending(x => x.ProjectAsset?.CreatedAt ?? DateTime.MinValue).ToList();
        }
        else if (state.Order == 1)
        {
            // By name - ??????
            localFiltered = (await localFiltered.OrderByPronounceAsync(a => a.Name)).ToList();
            sharedFiltered = (await sharedFiltered.OrderByPronounceAsync(a => a.Name)).ToList();
            reuseableFiltered = (await reuseableFiltered.OrderByPronounceAsync(a => a.Name)).ToList();
            rpcFiltered = rpcFiltered.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            templateFiltered = (await templateFiltered.OrderByPronounceAsync(a => a.Name)).ToList();
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (state.Version != _filterVersion || state.Search != (SearchText ?? "")
                || state.Order != OrderOption || state.ShowAll != ShowAllTemplates) return;
            FilterVectorComponents();
            _ = LoadMaskSources();
            FilteredLocalAssets.Clear();
            FilteredSharedAssets.Clear();
            FilteredRpcVideoSources.Clear();
            FilteredReuseableAssets.Clear();
            FilteredAvailableTemplates.Clear();
            FilteredAvailableTextStyleProviders.Clear();
            foreach (var asset in localFiltered) FilteredLocalAssets.Add(asset);
            foreach (var asset in sharedFiltered) FilteredSharedAssets.Add(asset);
            var externalSources = ExternalSourceSettings.Load(MauiProgram.BasicDataPath);
            foreach (var source in rpcFiltered.Where(x => x.ProjectAsset is null ? externalSources.AllowRpcSources : externalSources.AllowAssemblySources))
                FilteredRpcVideoSources.Add(source);
            FilteredLocalAssets.Add(new AddSourceCardViewModel("LocalAssets", Localized.AssetPage_AddAAsset));
            FilteredSharedAssets.Add(new AddSourceCardViewModel("SharedAssets", Localized.AssetPage_AddAAsset));
            if (externalSources.AllowAssemblySources)
                FilteredRpcVideoSources.Add(new AddSourceCardViewModel("RpcSources", Localized.ProjectExternalSource_Add));
            OnPropertyChanged(nameof(HasNoExternalSources));
            foreach (var asset in reuseableFiltered) FilteredReuseableAssets.Add(asset);
            foreach (var template in templateFiltered) FilteredAvailableTemplates.Add(template);

            var searchLower = state.Search.ToLower();
            foreach (var p in state.Providers)
            {
                if (!MatchesTextStyleFilter(p)) continue;
                if (string.IsNullOrWhiteSpace(searchLower) ||
                    p.Name.ToLower().Contains(searchLower) ||
                    p.BasicText.ToLower().Contains(searchLower))
                    FilteredAvailableTextStyleProviders.Add(p);
            }
        });
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
            var sketchDir = Path.Combine(MauiProgram.CachePath, "Sketches");
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


    public void AddEmptyVideoClip()
    {
        BeginTimelineClipPlacement((track, start) =>
        {
            var clip = _draftPage.CreateAndAddClip(start,
                _draftPage.FrameToPixel(Math.Max(1u, _draftPage.ProjectInfo.TargetFrameRate) * 3),
                track, null, Localized.DraftPage_AddClipView_EmptyVideoSource,
                new SolidColorBrush(Colors.CornflowerBlue), resolveOverlap: true);
            clip.ClipType = ClipMode.VideoClip;
            clip.TypeName = nameof(VideoClip);
            clip.FromPlugin = InternalPluginBase.InternalPluginBaseID;
            clip.isInfiniteLength = true;
            clip.maxFrameCount = 0;
            clip.sourceSecondPerFrame = 1f / Math.Max(1u, _draftPage.ProjectInfo.TargetFrameRate);
            clip.TargetWidth = Math.Max(1, _draftPage.ProjectInfo.RelativeWidth);
            clip.TargetHeight = Math.Max(1, _draftPage.ProjectInfo.RelativeHeight);
            clip.SourcePath = EmptyVideoSource.CreatePath(clip.TargetWidth, clip.TargetHeight);
            Log($"Created empty video clip {clip.Id}, source {clip.SourcePath}.");
            return clip;
        }, name: Localized.DraftPage_AddClipView_EmptyVideoSource);
        ClipAdded?.Invoke(this, EventArgs.Empty);
    }

    public async Task AddAlternativeSourceClip()
    {
        var path = await _draftPage.DisplayPromptAsync("Add", "Input source path", placeholder: "#<provider>,<stream id>");
        if (string.IsNullOrWhiteSpace(path)) return;

        var vidSrc = PluginManager.CreateVideoSource(path);

        BeginTimelineClipPlacement((trackIndex, startX) =>
        {
            var targetFps = Math.Max(1u, _draftPage.ProjectInfo.TargetFrameRate);
            var sourceSpf = vidSrc.Fps > 0 ? (1d / vidSrc.Fps) : 0d;
            var timelineLength = ResolveTimelineFrameCount((uint)Math.Max(0, vidSrc.TotalFrames), sourceSpf, targetFps);

            var element = _draftPage.CreateAndAddClip(
                startX: startX,
                width: _draftPage.FrameToPixel(timelineLength),
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
            element.maxFrameCount = timelineLength;
            element.sourceSecondPerFrame = (float)(vidSrc.Fps > 0 ? (1d / vidSrc.Fps) : (1d / Math.Max(1u, _draftPage.ProjectInfo.TargetFrameRate)));
            element.ExtraData = new();
            return element;
        }, name: path);

        ClipAdded?.Invoke(this, EventArgs.Empty);
    }

    private static uint ResolveTimelineFrameCount(uint sourceFrames, double sourceSecondPerFrame, uint targetFrameRate)
    {
        if (sourceFrames == 0)
        {
            return 1;
        }

        if (sourceSecondPerFrame <= 0 || targetFrameRate == 0)
        {
            return sourceFrames;
        }

        double seconds = sourceFrames * sourceSecondPerFrame;
        double timelineFrames = seconds * targetFrameRate;
        if (timelineFrames < 1d)
        {
            return 1;
        }

        if (timelineFrames >= uint.MaxValue)
        {
            return uint.MaxValue;
        }

        return (uint)Math.Round(timelineFrames, MidpointRounding.AwayFromZero);
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

public sealed class RpcVideoSourceItemViewModel
{
    public RpcVideoSourceItemViewModel(ProjectAddClipViewModel parent, ExternalMaskSourceDescriptor source,
        ProjectExternalSourceAsset? projectAsset = null, ProjectExternalSourceStatus? status = null)
        : this(parent, new ExternalVideoSourceDescriptor { ClientId = source.ClientId, ClientName = source.ClientName,
            SourceId = source.SourceId, Name = source.Name }, projectAsset, status)
    {
        MaskSource = source;
    }

    public RpcVideoSourceItemViewModel(ProjectAddClipViewModel parent, ExternalAudioSourceDescriptor source,
        ProjectExternalSourceAsset? projectAsset = null, ProjectExternalSourceStatus? status = null)
        : this(parent, new ExternalVideoSourceDescriptor { ClientId = source.ClientId, ClientName = source.ClientName,
            SourceId = source.SourceId, Name = source.Name, DecoderName = source.DecoderName }, projectAsset, status)
    {
        AudioSource = source;
    }

    public RpcVideoSourceItemViewModel(ProjectAddClipViewModel parent, ExternalVideoSourceDescriptor source,
        ProjectExternalSourceAsset? projectAsset = null, ProjectExternalSourceStatus? status = null)
    {
        Source = source;
        ProjectAsset = projectAsset;
        LoadStatus = status;
        AddCommand = new Command(async () => await parent.AddRpcVideoSource(this));
    }

    public ExternalVideoSourceDescriptor Source { get; }
    public ExternalAudioSourceDescriptor? AudioSource { get; }
    public ExternalMaskSourceDescriptor? MaskSource { get; }
    public bool IsMediaSource => MaskSource is null;
    public ProjectExternalSourceAsset? ProjectAsset { get; }
    private ProjectExternalSourceStatus? LoadStatus { get; }
    public bool CanAdd => !string.IsNullOrWhiteSpace(Source.SourceId)
        && (ProjectAsset is null ? ProjectExternalSourceService.AllowRpcSources : ProjectExternalSourceService.AllowAssemblySources);
    public string Status => ProjectAsset is null ? string.Empty : !string.IsNullOrWhiteSpace(LoadStatus?.Error)
        ? $"{Localized.ProjectExternalSource_Failed}: {LoadStatus.Error}"
        : LoadStatus?.Loaded == true ? Localized.ProjectExternalSource_Loaded : Localized.ProjectExternalSource_NotLoaded;
    public string Name => Source.Name;
    public string ClientName => ProjectAsset is null ? (string.IsNullOrWhiteSpace(Source.ClientName) ? Source.ClientId.ToString("D") : Source.ClientName)
        : $"{ProjectAsset.Manifest.Name} · {ProjectAsset.Manifest.Author} · {ProjectAsset.Manifest.Version}";
    public string DecoderName => Source.DecoderName;
    public string Resolution => Source.Width > 0 && Source.Height > 0 ? $"{Source.Width}×{Source.Height}" : "?";
    public string FrameRate => Source.Fps > 0 ? $"{Source.Fps:0.###} FPS" : "? FPS";
    public string Duration => AudioSource is not null ? TimeSpan.FromSeconds(AudioSource.Duration).ToString("hh\\:mm\\:ss")
        : Source.TotalFrames > 0 && Source.Fps > 0 ? TimeSpan.FromSeconds(Source.TotalFrames / Source.Fps).ToString("hh\\:mm\\:ss") : "--:--:--";

    public string Capabilities => AudioSource is not null ? $"{AudioSource.ChannelCount} ch · {AudioSource.SamplePerSecond} Hz"
        : $"{(Source.HasKnownResultBitsPerPixel ? $"{Source.ResultBitsPerPixel}-bit" : "Variable bit depth")} RGB{(Source.SupportsAlpha ? "A" : "")}{(Source.SupportsHdr ? " (HDR)" : "")}";
    public string SourceInfoDisplay => MaskSource is not null ? Localized.Mask_Title
        : AudioSource is not null ? $"{Capabilities} ({Duration})" : $"{Resolution} {FrameRate} ({Duration})";
    public Command AddCommand { get; }
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


    public TransformItemViewModel() { }
}

#endregion
