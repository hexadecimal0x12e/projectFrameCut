using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.TemplateSystem;
using projectFrameCut.Services;
using projectFrameCut.Template;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Path = System.IO.Path;

namespace projectFrameCut;

public partial class TemplateViewPage : ContentPage
{
    private readonly int _itemSize = 260;
    private readonly int _itemSpacing = 8;
    private readonly TemplatePageViewModel _viewModel;

    private string _lastClickTemplateId = string.Empty;

    public static bool HasNavigatedToTemplate = false;

    public TemplateViewPage()
    {
        InitializeComponent();
        Title = Localized.TemplateViewPage_Title;
        _viewModel = new TemplatePageViewModel();
        BindingContext = _viewModel;
        OrderOptionPicker.SelectedIndex = _viewModel.OrderOption;
        Loaded += (_, _) => ApplyGridSpan();
        SizeChanged += (_, _) => ApplyGridSpan();
    }

    public TemplateViewPage(string Id)
    {
        HasNavigatedToTemplate = false;
        InitializeComponent();
        Title = Localized.TemplateViewPage_Title;
        _viewModel = new TemplatePageViewModel();
        BindingContext = _viewModel;
        OrderOptionPicker.SelectedIndex = _viewModel.OrderOption;
        SizeChanged += (_, _) => ApplyGridSpan();
        Loaded += async (_, _) =>
        {
            ApplyGridSpan();
            if (HasNavigatedToTemplate) return;
            if (_viewModel.FilteredTemplates.FirstOrDefault(t => t.TemplateId == Id) is TemplateItem target)
            {
                HasNavigatedToTemplate = true;
                _viewModel.SelectedTemplate = target;
                TemplatesCollectionView.SelectedItem = target;
                TemplatesCollectionView.ScrollTo(target, position: ScrollToPosition.Center);
                await CreateVideoFromTemplate();
            }
        };
        
    }

    private void ApplyGridSpan()
    {
        if (TemplatesCollectionView.ItemsLayout is not GridItemsLayout gridLayout)
        {
            return;
        }

        var width = TemplatesCollectionView.Width;
        if (width <= 0)
        {
            return;
        }

        var span = Math.Max(1, (int)((width + _itemSpacing) / (_itemSize + _itemSpacing)));
        if (gridLayout.Span != span)
        {
            gridLayout.Span = span;
        }
    }

    private void TemplateSearchBar_TextChanged(object sender, TextChangedEventArgs e)
    {
        _viewModel.SearchText = e.NewTextValue ?? string.Empty;
    }

    private void OrderOptionPicker_SelectedIndexChanged(object sender, EventArgs e)
    {
        _viewModel.OrderOption = OrderOptionPicker.SelectedIndex;
    }

    private void TemplatesCollectionView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection?.FirstOrDefault() is TemplateItem item)
        {
            _viewModel.SelectedTemplate = item;
        }
    }

    private void TemplateCard_Loaded(object sender, EventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

#if WINDOWS || MACCATALYST
        var rightClick = new TapGestureRecognizer { NumberOfTapsRequired = 1, Buttons = ButtonsMask.Secondary };
        rightClick.Tapped += async (_, _) =>
        {
            if (border.BindingContext is TemplateItem template)
            {
                await ShowContextMenu(template);
            }
        };

        var doubleClick = new TapGestureRecognizer { NumberOfTapsRequired = 2, Buttons = ButtonsMask.Primary };
        doubleClick.Tapped += async (_, _) =>
        {
            if (border.BindingContext is TemplateItem template)
            {
                _viewModel.SelectedTemplate = template;
                await PreviewTemplate();
            }
        };

        var existingTaps = border.GestureRecognizers.OfType<TapGestureRecognizer>().ToList();
        foreach (var gesture in existingTaps)
        {
            border.GestureRecognizers.Remove(gesture);
        }

        var existingPointers = border.GestureRecognizers.OfType<PointerGestureRecognizer>().ToList();
        foreach (var gesture in existingPointers)
        {
            border.GestureRecognizers.Remove(gesture);
        }

        var hoverPointer = new PointerGestureRecognizer();
        hoverPointer.PointerEntered += (_, _) => StartTemplatePreview(border);
        hoverPointer.PointerExited += (_, _) => StopTemplatePreview(border);

        border.GestureRecognizers.Add(rightClick);
        border.GestureRecognizers.Add(doubleClick);
        border.GestureRecognizers.Add(hoverPointer);
#elif ANDROID || IOS
		var pointerGesture = new PointerGestureRecognizer();
		DateTime pointerDownTime = DateTime.MinValue;

		pointerGesture.PointerPressed += (_, _) =>
		{
			pointerDownTime = DateTime.Now;
		};

		pointerGesture.PointerReleased += async (_, _) =>
		{
			var duration = (DateTime.Now - pointerDownTime).TotalMilliseconds;
			if (border.BindingContext is not TemplateItem template)
			{
				return;
			}

			if (duration >= 500)
			{
				await ShowContextMenu(template);
				return;
			}

			if (_lastClickTemplateId == template.TemplateId)
			{
				_viewModel.SelectedTemplate = template;
				await PreviewTemplate();
			}
			else
			{
				_lastClickTemplateId = template.TemplateId;
				_viewModel.SelectedTemplate = template;
			}
		};

		var existingPointer = border.GestureRecognizers.OfType<PointerGestureRecognizer>().FirstOrDefault();
		if (existingPointer is not null)
		{
			border.GestureRecognizers.Remove(existingPointer);
		}
		border.GestureRecognizers.Add(pointerGesture);
#endif
    }

    private static void StartTemplatePreview(Border border)
    {
        if (border.BindingContext is not TemplateItem template)
        {
            return;
        }

        try
        {
            var previewPlayer = GetOrCreatePreviewPlayer(border);
            if (previewPlayer is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(template.PreviewVideoPath)
                && Uri.TryCreate(template.PreviewVideoPath, UriKind.Absolute, out var uri))
            {
                previewPlayer.Source = MediaSource.FromUri(uri);
            }

            previewPlayer.Play();
        }
        catch
        {
            // Ignore playback errors to avoid breaking card interactions.
        }
    }

    private static void StopTemplatePreview(Border border)
    {
        var host = border.FindByName<Grid>("TemplatePreviewHost");
        var previewPlayer = host?.Children.OfType<MediaElement>().FirstOrDefault();
        if (previewPlayer is null)
        {
            return;
        }

        try
        {
            previewPlayer.Pause();
            previewPlayer.Source = null;
        }
        catch
        {
            // Ignore playback errors to avoid breaking card interactions.
        }
    }

    private static MediaElement? GetOrCreatePreviewPlayer(Border border)
    {
        var host = border.FindByName<Grid>("TemplatePreviewHost");
        var existing = host?.Children.OfType<MediaElement>().FirstOrDefault();
        if (existing is not null)
        {
            return existing;
        }

        if (host is null)
        {
            return null;
        }

        var previewPlayer = new MediaElement
        {
            Aspect = Aspect.AspectFill,
            ShouldAutoPlay = false,
            ShouldLoopPlayback = true,
            ShouldMute = true,
            ShouldShowPlaybackControls = false
        };

        host.Children.Add(previewPlayer);
        return previewPlayer;
    }

    private async Task ShowContextMenu(TemplateItem template)
    {
        var verbs = new List<string>
        {
            Localized.AssetPage_ShowPreview,
            Localized.TemplateViewPage_CreateVideo,
            Localized.TemplateViewPage_TemplateInfo,
            Localized.HomePage_ProjectContextMenu_Delete
        };
        var action = await DisplayActionSheetAsync(template.Name, Localized._Cancel, null, verbs.ToArray());
        switch (verbs.IndexOf(action))
        {
            case 0:
                _viewModel.SelectedTemplate = template;
                await PreviewTemplate();
                break;
            case 1:
                _viewModel.SelectedTemplate = template;
                await CreateVideoFromTemplate();
                break;
            case 2:
                _viewModel.SelectedTemplate = template;
                await DisplayAlertAsync(Localized._Info, template.Description, Localized._OK);
                break;
            case 3:
                _viewModel.SelectedTemplate = template;
                await DeleteTemplate(template);
                break;
            default:
                break;
        }
    }

    private async void PreviewTemplate_Clicked(object sender, EventArgs e)
    {
        await PreviewTemplate();
    }

    private async void CreateVideo_Clicked(object sender, EventArgs e)
    {
        await CreateVideoFromTemplate();
    }

    private async void DeleteTemplate_Clicked(object sender, EventArgs e)
    {
        await DeleteSelectedTemplate();
    }

    private async void ImportTemplateButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            var path = await FileSystemService.PickFileAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await ImportTemplate(path);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Localized._Error, $"{Localized.TemplateCreatePage_InvalidTemplate}{Environment.NewLine}{Environment.NewLine}{Localized._ExceptionTemplate(ex)})", Localized._OK);
        }
    }
    private async void CreateScriptTemplateButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            await Navigation.PushAsync(new ScriptTemplateCreatePage());
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Localized._Error, $"{Localized.TemplateCreatePage_InvalidTemplate}{Environment.NewLine}{Environment.NewLine}{Localized._ExceptionTemplate(ex)})", Localized._OK);
        }
    }

    private async Task ImportTemplate(string filePath)
    {
        try
        {
            // Show loading indicator
            var origContent = Content;
            Content = new ActivityIndicator
            {
                IsRunning = true,
                WidthRequest = 200,
                HeightRequest = 200
            };

            try
            {
                // 判断文件类型
                var ext = Path.GetExtension(filePath);
                var isPjfcPackage = string.Equals(ext, ".pjfcTemplate", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase);

                ITemplateStructure template;

                if (isPjfcPackage)
                {
                    // 新流程：保存 .pjfcTemplate + 轻量元数据
                    template = await TemplatePackageIO.ImportPjfcTemplateAsync(
                        filePath, DraftPage.DraftJSONOption);
                }
                else
                {
                    // 纯 JSON 模板直接导入（旧格式）
                    var text = await File.ReadAllTextAsync(filePath);
                    template = JsonSerializer.Deserialize<JSONBasedTemplateStructure>(text, DraftPage.DraftJSONOption)
                        ?? throw new InvalidOperationException("Invalid template file.");
                }

                TemplateStore.Templates[template.TemplateID] = template;

                // Reload the templates in the view
                _viewModel.ReloadTemplates();

                await DisplayAlertAsync(Localized._Info,
                    SettingsManager.SettingLocalizedResources.Advanced_Success,
                    Localized._OK);
            }
            finally
            {
                Content = origContent;
            }
        }
        catch (Exception ex)
        {
            Log(ex, "Import template", this);
            throw;
        }
    }

    private async Task PreviewTemplate()
    {
        if (_viewModel.SelectedTemplate is null)
        {
            await DisplayAlertAsync(Localized._Info, Localized.TemplateViewPage_SelectToContinue, Localized._OK);
            return;
        }

        var markdownView = new ScrollView
        {
            Content = Markdown2XAML.Convert(string.IsNullOrWhiteSpace(_viewModel.SelectedTemplate.Readme) ? $"**{Localized.TemplateViewPage_NoReadme}**" : _viewModel.SelectedTemplate.Readme),
            MinimumHeightRequest = 250,
            MinimumWidthRequest = 200,
            Background = Color.FromArgb("#ff262D3D")
        };

        await Navigation.ShowPopupAsync(markdownView, new PopupOptions { Shape = new RoundRectangle { CornerRadius = new CornerRadius(UIServices.GetWindowCornerRadius()), Fill = Colors.Transparent, Stroke = Colors.Transparent } });

    }

    private async Task CreateVideoFromTemplate()
    {
        if (_viewModel.SelectedTemplate is not TemplateItem selected)
        {
            await DisplayAlertAsync(Localized._Info, Localized.TemplateViewPage_SelectToContinue, Localized._OK);
            return;
        }

        // 尝试按需解压 .pjfcTemplate 获取完整结构（含 Project/Draft/素材）
        string? tempExtractDir = null;
        ITemplateStructure fullTemplate;
        try
        {
            if (Guid.TryParse(selected.TemplateId, out var templateGuid))
            {
                (fullTemplate, tempExtractDir) = await TemplatePackageIO.ExtractStoredTemplateAsync(
                    templateGuid, DraftPage.DraftJSONOption);
            }
            else
            {
                // Guid 解析失败，回退到内存中的结构
                fullTemplate = selected.Structure;
            }
        }
        catch (FileNotFoundException)
        {
            // 没有对应的 .pjfcTemplate（纯 JSON 导入的旧模板），使用内存中的结构
            fullTemplate = selected.Structure;
        }

        var view = new Template.TemplateCreatePage(fullTemplate);
        view.ConfigureForProjectCreationMode();
        view.CloseRequested += async (_, _) =>
        {
            // 清理临时解压目录
            TemplatePackageIO.TryCleanupExtractDir(tempExtractDir);
            await Navigation.PopAsync();
        };
        await Navigation.PushAsync(new ContentPage
        {
            Title = Localized.TemplateViewPage_CreateFrom(fullTemplate.TemplateName),
            Content = view
        });
    }

    private async Task DeleteSelectedTemplate()
    {
        if (_viewModel.SelectedTemplate is null)
        {
            await DisplayAlertAsync(Localized._Info, Localized.TemplateViewPage_SelectToContinue, Localized._OK);
            return;
        }

        await DeleteTemplate(_viewModel.SelectedTemplate);
    }

    private async Task DeleteTemplate(TemplateItem template)
    {
        try
        {
            var confirm0 = await DisplayAlertAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm0(template.Name), Localized._Confirm, Localized._Cancel);
            if (!confirm0)
            {
                return;
            }

            var confirm1 = await DisplayAlertAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm1(template.Name), Localized._Confirm, Localized._Cancel);
            if (!confirm1)
            {
                return;
            }

            var confirm2 = await DisplayPromptAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm2Input(template.Name), Localized._Confirm, Localized._Cancel, "no");
            if (confirm2 != "yes")
            {
                return;
            }

            if (!Guid.TryParse(template.TemplateId, out var templateGuid))
            {
                throw new InvalidOperationException($"Template id is invalid: {template.TemplateId}");
            }

            DeleteTemplateStorage(templateGuid);
            TemplateStore.Templates.Remove(templateGuid);

            _viewModel.SelectedTemplate = null;
            _viewModel.ReloadTemplates();
            TemplatesCollectionView.SelectedItem = null;

            await DisplayAlertAsync(Localized._Info, Localized.HomePage_ProjectContextMenu_Delete_Deleted(template.Name), Localized._OK);
        }
        catch (Exception ex)
        {
            Log(ex, "delete template", this);
            await DisplayAlertAsync(Localized._Error, Localized.HomePage_ProjectContextMenu_Delete_Fail(template.Name, ex), Localized._OK);
        }
    }

    private static void DeleteTemplateStorage(Guid templateId)
    {
        var templateRootPath = Path.Combine(MauiProgram.DataPath, "My Templates");
        if (!Directory.Exists(templateRootPath))
        {
            return;
        }

        var idStr = templateId.ToString("N");

        // 删除轻量元数据 .json
        foreach (var filePath in Directory.EnumerateFiles(templateRootPath, "*.json", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (string.Equals(fileName, idStr, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(filePath);
            }
        }

        // 删除原始 .pjfcTemplate 包
        var pjfcPath = Path.Combine(templateRootPath, $"{idStr}.pjfcTemplate");
        if (File.Exists(pjfcPath))
        {
            File.Delete(pjfcPath);
        }

        // 清理旧的 .packaged-assets 目录（兼容旧导入）
        var packagedAssetsRootPath = Path.Combine(templateRootPath, ".packaged-assets");
        if (Directory.Exists(packagedAssetsRootPath))
        {
            foreach (var directoryPath in Directory.EnumerateDirectories(packagedAssetsRootPath, "*", SearchOption.TopDirectoryOnly))
            {
                var directoryName = Path.GetFileName(directoryPath);
                if (Guid.TryParse(directoryName, out var dirGuid) && dirGuid == templateId)
                {
                    Directory.Delete(directoryPath, true);
                }
            }
        }
    }

    private sealed class TemplatePageViewModel : INotifyPropertyChanged
    {
        private readonly List<TemplateItem> _allTemplates;
        private bool _isApplyingFilter;

        public ObservableCollection<TemplateItem> FilteredTemplates { get; } = new();
        public ObservableCollection<string> Tags { get; } = new();

        public string SearchText
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    ApplyFilter();
                }
            }
        } = string.Empty;

        public int OrderOption
        {
            get;
            set
            {
                var normalized = value < 0 ? 0 : value;
                if (SetProperty(ref field, normalized))
                {
                    ApplyFilter();
                }
            }
        }

        public int SelectedTag
        {
            get;
            set
            {
                var normalized = value < 0 ? 0 : value;
                if (SetProperty(ref field, normalized) && !_isApplyingFilter)
                {
                    ApplyFilter();
                }
            }
        } = 0;

        public TemplateItem? SelectedTemplate
        {
            get;
            set => SetProperty(ref field, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public TemplatePageViewModel()
        {
            _allTemplates = BuildTemplates();
            ApplyFilter();
        }

        public void ReloadTemplates()
        {
            try
            {
                TemplateStore.Templates.Clear();
                var templateDir = Path.Combine(MauiProgram.DataPath, "My Templates");
                if (Directory.Exists(templateDir))
                {
                    // 只加载轻量元数据 .json 文件（不含 Project/Draft，仅供列表展示）
                    foreach (var item in Directory.GetFiles(templateDir, "*.json", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var templateJson = File.ReadAllText(item);

                            // 检测是否为新的轻量元数据格式（含 $schema 标记）
                            if (templateJson.Contains("\"$schema\"") && templateJson.Contains("\"template-meta-v2\""))
                            {
                                var listingTemplate = TemplatePackageIO.LoadListingTemplate(templateJson);
                                if (listingTemplate.TemplateID != Guid.Empty)
                                    TemplateStore.Templates[listingTemplate.TemplateID] = listingTemplate;
                            }
                            else
                            {
                                // 兼容旧格式：通过 JSON 中的 ScriptContent 字段检测旧版脚本模板
                                if (templateJson.Contains("\"ScriptContent\""))
                                {
                                    var scriptCandidate = JsonSerializer.Deserialize<ScriptBasedTemplateStructure>(templateJson);
                                    if (scriptCandidate?.TemplateID != Guid.Empty)
                                    {
                                        TemplateStore.Templates[scriptCandidate.TemplateID] = scriptCandidate;
                                        continue;
                                    }
                                }

                                var template = JSONBasedTemplateHelper.DeserializeTemplate(templateJson);
                                TemplateStore.Templates[template.TemplateID] = template;
                            }
                        }
                        catch (Exception exInner)
                        {
                            Log(exInner, $"load template meta: {item}", this);
                        }
                    }

                    // 不再在重载时解压 .pjfcTemplate，改为使用时按需解压
                }
            }
            catch (Exception ex)
            {
                Log(ex, "load templates", this);
            }
            _allTemplates.Clear();
            _allTemplates.AddRange(BuildTemplates());
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (_isApplyingFilter)
            {
                return;
            }

            _isApplyingFilter = true;
            try
            {
                var query = (SearchText ?? string.Empty).Trim();
                var tags = _allTemplates
                    .SelectMany(c => c.Tags)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                SyncTags(tags);

                var normalizedSelectedTag = Math.Clamp(SelectedTag, 0, tags.Count);
                if (SelectedTag != normalizedSelectedTag)
                {
                    SelectedTag = normalizedSelectedTag;
                }

                var selectedTagText = normalizedSelectedTag > 0 ? tags[normalizedSelectedTag - 1] : string.Empty;
                IEnumerable<TemplateItem> items = _allTemplates;

                if (!string.IsNullOrWhiteSpace(query))
                {
                    items = items.Where(t =>
                        t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || t.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || t.Tags.Any(currentTag => currentTag.Contains(query, StringComparison.OrdinalIgnoreCase)));
                }

                if (!string.IsNullOrWhiteSpace(selectedTagText))
                {
                    items = items.Where(c => c.Tags.Any(currentTag => currentTag.Equals(selectedTagText, StringComparison.OrdinalIgnoreCase)));
                }

                items = OrderOption switch
                {
                    1 => items.OrderByDescending(t => t.UsageCount),
                    2 => items.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase),
                    _ => items.OrderByDescending(t => t.CreatedAt)
                };

                FilteredTemplates.Clear();
                foreach (var item in items)
                {
                    FilteredTemplates.Add(item);
                }

                if (SelectedTemplate is not null && !FilteredTemplates.Any(t => t.TemplateId == SelectedTemplate.TemplateId))
                {
                    SelectedTemplate = FilteredTemplates.FirstOrDefault();
                }
            }
            finally
            {
                _isApplyingFilter = false;
            }
        }

        private void SyncTags(IReadOnlyList<string> latestTags)
        {
            if (Tags.Count == latestTags.Count + 1
                && Tags.FirstOrDefault() == Localized.TemplateViewPage_TemplateTag_None
                && Tags.Skip(1).SequenceEqual(latestTags, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            Tags.Clear();
            Tags.Add(Localized.TemplateViewPage_TemplateTag_None);
            foreach (var item in latestTags)
            {
                Tags.Add(item);
            }
        }

        private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        private static List<TemplateItem> BuildTemplates()
        {
            var items = new List<TemplateItem>();
            foreach (var template in TemplateStore.Templates.Values)
            {
                if (template is JSONBasedTemplateStructure jsonTemplate)
                {
                    items.Add(new TemplateItem(jsonTemplate, jsonTemplate.Readme ?? "")
                    {
                        Tags = [Localized.TemplateViewPage_TemplateTag_Local]
                    });
                }
                else if (template is ScriptBasedTemplateStructure scriptTemplate)
                {
                    items.Add(new TemplateItem(scriptTemplate, scriptTemplate.Readme ?? "")
                    {
                        Tags = [Localized.TemplateViewPage_TemplateTag_Local]
                    });
                }
            }
            return items;
        }

    }

    private sealed class TemplateItem
    {
        public required ITemplateStructure Structure { get; init; }

        public string TemplateId { get; }
        public string Name { get; }
        public string Category { get; }
        public string DurationText { get; }
        public string Description { get; }
        public IReadOnlyList<string> Tags { get; set; }
        public int UsageCount { get; }
        public DateTime CreatedAt { get; }
        public Color AccentColor { get; }
        public string PreviewVideoPath { get; }
        public string Readme { get; set; }

        public string TagsText => string.Join("  ", Tags.Select(t => $"#{t}"));
        public string UsageText => Localized.TemplateViewPage_UsageDisplay(UsageCount);
        public bool IsSupportProject => Structure.Scope == TemplateScope.Project || Structure.Scope == TemplateScope.Any;

        [SetsRequiredMembers]
        public TemplateItem(ITemplateStructure structure, string readme = "")
        {
            Structure = structure;
            TemplateId = structure.TemplateID.ToString();
            Name = structure.TemplateName ?? "Unnamed Template";
            Category = structure.Variables?.ContainsKey("category") == true
                ? structure.Variables["category"] ?? "Other"
                : "Other";
            Description = structure.Variables?.ContainsKey("description") == true
                    ? structure.Variables["description"] ?? Name
                    : Name;
            DurationText = structure.Variables?.ContainsKey("duration") == true
                ? structure.Variables["duration"] ?? "00:00"
                : "00:00";
            var tagsStr = structure.Variables?.ContainsKey("tags") == true
                                ? structure.Variables["tags"] ?? ""
                                : "";
            Tags = string.IsNullOrWhiteSpace(tagsStr)
                ? Array.Empty<string>()
                : tagsStr.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToArray();
            UsageCount = structure.Variables?.ContainsKey("usageCount") == true
                && int.TryParse(structure.Variables["usageCount"], out var count)
                ? count
                : 0;
            CreatedAt = DateTime.Now;
            AccentColor = Color.FromArgb("#3A86FF");
            PreviewVideoPath = structure.Variables?.ContainsKey("previewPath") == true
                ? structure.Variables["previewPath"] ?? ""
                : "";

            Readme = readme;
        }
    }
}