using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Maui.Views;

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
			"预览模板",
			"使用该模板创建视频",
			"查看详情"
		};
		var action = await DisplayActionSheetAsync(template.Name, "取消", null, verbs.ToArray());
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
				await DisplayAlertAsync("模板详情", template.Description, "确定");
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

	private async Task PreviewTemplate()
	{
		await Navigation.PushAsync(new Template.TemplateCreatePage());
		return;
		if (_viewModel.SelectedTemplate is null)
		{
			await DisplayAlertAsync("提示", "请先选择一个模板。", "确定");
			return;
		}

		await DisplayAlertAsync(
			"模板预览（Mock）",
			$"模板：{_viewModel.SelectedTemplate.Name}{Environment.NewLine}类型：{_viewModel.SelectedTemplate.Category}{Environment.NewLine}时长：{_viewModel.SelectedTemplate.DurationText}",
			"确定");
	}

	private async Task CreateVideoFromTemplate()
	{
		if (_viewModel.SelectedTemplate is null)
		{
			await DisplayAlertAsync("提示", "请先选择一个模板。", "确定");
			return;
		}

		await DisplayAlertAsync(
			"创建视频（Mock）",
			$"已选择模板“{_viewModel.SelectedTemplate.Name}”。{Environment.NewLine}当前仅为 mock 流程，后续可接入真实模板源与创建逻辑。",
			"确定");
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
			_allTemplates = BuildMockTemplates();
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

		private static List<TemplateItem> BuildMockTemplates()
		{
			return new List<TemplateItem>
			{
				new("temp-001", "旅行开场", "Travel", "00:15", "用于旅行 vlog 的节奏化开场，适合航拍与城市镜头。", new[] { "快节奏", "转场", "vlog" }, 842, DateTime.Now.AddDays(-4), Color.FromArgb("#2D6A4F"), ""),
				new("temp-002", "美食展示", "Food", "00:25", "突出食材细节和烹饪过程，适合短视频平台。", new[] { "特写", "慢动作", "美食" }, 1320, DateTime.Now.AddDays(-12), Color.FromArgb("#BC6C25"), ""),
				new("temp-003", "产品发布", "Commercial", "00:20", "简洁商务风，适合新产品上线和活动预热。", new[] { "商务", "字幕", "品牌" }, 976, DateTime.Now.AddDays(-2), Color.FromArgb("#1D3557"), ""),
				new("temp-004", "生日纪念", "Life", "00:30", "温馨纪念主题，自动留出照片与祝福文案区。", new[] { "情感", "照片", "纪念" }, 688, DateTime.Now.AddDays(-8), Color.FromArgb("#E76F51"), ""),
				new("temp-005", "科技评测", "Tech", "00:40", "适合设备开箱和参数讲解，信息层级清晰。", new[] { "参数", "评测", "科技" }, 1103, DateTime.Now.AddDays(-15), Color.FromArgb("#264653"), ""),
				new("temp-006", "课程宣传", "Education", "00:35", "用于课程卖点介绍，支持章节型结构。", new[] { "教育", "章节", "讲解" }, 529, DateTime.Now.AddDays(-1), Color.FromArgb("#3A86FF"), ""),
				new("temp-007", "婚礼回顾", "Wedding", "00:45", "仪式感镜头专用模板，偏柔和色调。", new[] { "婚礼", "浪漫", "慢节奏" }, 764, DateTime.Now.AddDays(-18), Color.FromArgb("#9D4EDD"), ""),
				new("temp-008", "运动剪辑", "Sports", "00:18", "适合动作高光片段，鼓点驱动型剪辑。", new[] { "动感", "高光", "节拍" }, 1458, DateTime.Now.AddDays(-6), Color.FromArgb("#F77F00"), ""),
				new("temp-009", "很长的名称12345678901234567890", "111111111111111111111111111111111", "99:99", "很长的介绍11111111111111111111111112345678901234567890", new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "cccccccccccccccccccccccccccccccccccccccc" }, 1458, DateTime.Now.AddDays(-6), Color.FromArgb("#FFD272"), "")
			};
		}
	}

	private sealed class TemplateItem
	{
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
		public string UsageText => $"使用 {UsageCount}";

		public TemplateItem(
			string templateId,
			string name,
			string category,
			string durationText,
			string description,
			IReadOnlyList<string> tags,
			int usageCount,
			DateTime createdAt,
			Color accentColor,
			string previewVideoPath)
		{
			TemplateId = templateId;
			Name = name;
			Category = category;
			DurationText = durationText;
			Description = description;
			Tags = tags;
			UsageCount = usageCount;
			CreatedAt = createdAt;
			AccentColor = accentColor;
			PreviewVideoPath = previewVideoPath;
		}
	}
}