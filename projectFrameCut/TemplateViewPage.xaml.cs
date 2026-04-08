using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CommunityToolkit.Maui.Views;
using projectFrameCut.Controls;
using projectFrameCut.Render.TemplateSystem;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Template;
using System.Diagnostics.CodeAnalysis;

namespace projectFrameCut;

public partial class TemplateViewPage : ContentPage
{
    private readonly int _itemSize = 260;
    private readonly int _itemSpacing = 8;
    private readonly TemplatePageViewModel _viewModel;

    private string _lastClickTemplateId = string.Empty;

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
        var previewPlayer = host?.Children.OfType<CompatMediaElement>().FirstOrDefault();
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

    private static CompatMediaElement? GetOrCreatePreviewPlayer(Border border)
    {
        var host = border.FindByName<Grid>("TemplatePreviewHost");
        var existing = host?.Children.OfType<CompatMediaElement>().FirstOrDefault();
        if (existing is not null)
        {
            return existing;
        }

        if (host is null)
        {
            return null;
        }

        var previewPlayer = new CompatMediaElement
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
            Localized.TemplateViewPage_TemplateInfo
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
                await Task.Run(() =>
                {
                    // Read the template file
                    var templateJson = File.ReadAllText(filePath);

                    // Deserialize the template
                    var template = JSONBasedTemplateHelper.DeserializeTemplate(templateJson);

                    // Add to TemplateStore
                    TemplateStore.Templates[template.TemplateID] = template;

                    File.Copy(filePath, Path.Combine(MauiProgram.DataPath, "My Templates", $"{template.TemplateID}.json"));
                });

                // Reload the templates in the view
                _viewModel.ReloadTemplates();

                await DisplayAlertAsync(Localized._Info, SettingsManager.SettingLocalizedResources.Advanced_Success, Localized._OK);
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

        //await DisplayAlertAsync(
        //    "模板预览（Mock）",
        //    $"模板：{_viewModel.SelectedTemplate.Name}{Environment.NewLine}类型：{_viewModel.SelectedTemplate.Category}{Environment.NewLine}时长：{_viewModel.SelectedTemplate.DurationText}",
        //    Localized._OK);
    }

    private async Task CreateVideoFromTemplate()
    {
        if (_viewModel.SelectedTemplate?.Structure is not JSONBasedTemplateStructure stru)
        {
            await DisplayAlertAsync(Localized._Info, Localized.TemplateViewPage_SelectToContinue, Localized._OK);
            return;
        }
        var view = new Template.TemplateCreatePage(stru);
        view.ConfigureForProjectCreationMode();
        view.CloseRequested += async (_, _) => await Navigation.PopAsync();
        await Navigation.PushAsync(new ContentPage
        {
            Title = Localized.TemplateViewPage_CreateFrom(stru.TemplateName),
            Content = view
        });
        return;
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

        private static List<TemplateItem> BuildTemplates() => Template.TemplateStore.Templates.Values.OfType<JSONBasedTemplateStructure>().Select(c => new TemplateItem(c)).ToList();

    }

    private sealed class TemplateItem
    {
        public required JSONBasedTemplateStructure Structure { get; init; }

        public string TemplateId { get; }
        public string Name { get; }
        public string Category { get; }
        public string DurationText { get; }
        public string Description { get; }
        public IReadOnlyList<string> Tags { get; }
        public int UsageCount { get; }
        public DateTime CreatedAt { get; }
        public Color AccentColor { get; }
        public string PreviewVideoPath { get; }

        public string TagsText => string.Join("  ", Tags.Select(t => $"#{t}"));
        public string UsageText => Localized.TemplateViewPage_UsageDisplay(UsageCount);
        public bool IsSupportProject => Structure.Scope == TemplateScope.Project || Structure.Scope == TemplateScope.Any;

        [SetsRequiredMembers]
        public TemplateItem(JSONBasedTemplateStructure structure)
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
            CreatedAt = DateTime.Now; // Could be parsed from variables if available
            AccentColor = Color.FromArgb("#3A86FF"); // Default color, could be customized via variables
            PreviewVideoPath = structure.Variables?.ContainsKey("previewPath") == true
                ? structure.Variables["previewPath"] ?? ""
                : "";
        }

        //public TemplateItem(string templateId, string name, string category, string durationText, string description, IReadOnlyList<string> tags, int usageCount, DateTime createdAt, Color accentColor, string previewVideoPath)
        //{
        //    TemplateId = templateId ?? throw new ArgumentNullException(nameof(templateId));
        //    Name = name ?? throw new ArgumentNullException(nameof(name));
        //    Category = category ?? throw new ArgumentNullException(nameof(category));
        //    DurationText = durationText ?? throw new ArgumentNullException(nameof(durationText));
        //    Description = description ?? throw new ArgumentNullException(nameof(description));
        //    Tags = tags ?? throw new ArgumentNullException(nameof(tags));
        //    UsageCount = usageCount;
        //    CreatedAt = createdAt;
        //    AccentColor = accentColor ?? throw new ArgumentNullException(nameof(accentColor));
        //    PreviewVideoPath = previewVideoPath ?? throw new ArgumentNullException(nameof(previewVideoPath));
        //}
    }
}