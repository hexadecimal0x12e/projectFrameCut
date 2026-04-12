#nullable enable
using projectFrameCut.Controls;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace projectFrameCut.Asset;

public partial class AssetPicker : ContentView
{
	public static readonly BindableProperty SelectedAssetProperty = BindableProperty.Create(
		nameof(SelectedAsset),
		typeof(AssetItem),
		typeof(AssetPicker),
		default(AssetItem),
		BindingMode.TwoWay,
		propertyChanged: OnSelectedAssetChanged);

	public static readonly BindableProperty IsDoubleTapPreviewEnabledProperty = BindableProperty.Create(
		nameof(IsDoubleTapPreviewEnabled),
		typeof(bool),
		typeof(AssetPicker),
		true);

	private readonly AssetViewModel _viewModel = new();
	private readonly int _itemSize = 200;
	private readonly int _itemSpacing = 5;

	public event EventHandler<AssetItem?>? SelectedAssetChanged;
	public event EventHandler<AssetItem>? AssetDoubleTapped;

	public AssetItem? SelectedAsset
	{
		get => (AssetItem?)GetValue(SelectedAssetProperty);
		set => SetValue(SelectedAssetProperty, value);
	}

	public bool IsDoubleTapPreviewEnabled
	{
		get => (bool)GetValue(IsDoubleTapPreviewEnabledProperty);
		set => SetValue(IsDoubleTapPreviewEnabledProperty, value);
	}

	public AssetPicker()
	{
		InitializeComponent();
		BindingContext = _viewModel;
		_viewModel.PropertyChanged += OnViewModelPropertyChanged;

		SourcePicker.ItemsSource = new string[]
		{
			OperatingSystem.IsWindows() ? Environment.MachineName : "Your devices"
		};
		SourcePicker.SelectedIndex = 0;

		var orderOpt = SettingsManager.GetSetting("Edit_AddView_DefaultOrderOption", "date");
		OrderOptionPicker.SelectedIndex = orderOpt switch
		{
			"date" => 0,
			"name" => 1,
			_ => 0
		};
	}

	private static void OnSelectedAssetChanged(BindableObject bindable, object oldValue, object newValue)
	{
		if (bindable is not AssetPicker picker)
		{
			return;
		}

		if (!ReferenceEquals(picker._viewModel.SelectedAsset, newValue))
		{
			picker._viewModel.SelectedAsset = newValue as AssetItem;
		}
	}

	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(AssetViewModel.SelectedAsset))
		{
			return;
		}

		if (!ReferenceEquals(SelectedAsset, _viewModel.SelectedAsset))
		{
			SelectedAsset = _viewModel.SelectedAsset;
		}

		SelectedAssetChanged?.Invoke(this, _viewModel.SelectedAsset);
	}

	private void AssetSearchBar_TextChanged(object sender, TextChangedEventArgs e)
	{
		_viewModel.SearchText = e.NewTextValue ?? string.Empty;
	}

	private void SourcePicker_SelectedIndexChanged(object sender, EventArgs e)
	{
		// Reserved for source filtering; currently only local assets are shown.
	}

	private void OnCollectionViewSizeChanged(object sender, EventArgs e)
	{
		if (sender is CollectionView collectionView && collectionView.ItemsLayout is GridItemsLayout gridLayout)
		{
			var width = collectionView.Width;
			if (width > 0)
			{
				var span = Math.Max(1, (int)((width + _itemSpacing) / (_itemSize + _itemSpacing)));
				if (gridLayout.Span != span)
				{
					gridLayout.Span = span;
				}
			}
		}
	}

	private async void OnAssetItemDoubleTapped(object sender, TappedEventArgs e)
	{
		if (sender is Border border && border.BindingContext is AssetItem asset)
		{
			AssetDoubleTapped?.Invoke(this, asset);
			if (IsDoubleTapPreviewEnabled)
			{
				await ShowPreviewAsync(asset);
			}
		}
	}

	private async void OnAssetPreviewDoubleClicked(object sender, TappedEventArgs e)
	{
		if (sender is Image image && image.BindingContext is AssetItem asset)
		{
			AssetDoubleTapped?.Invoke(this, asset);
			if (IsDoubleTapPreviewEnabled)
			{
				await ShowPreviewAsync(asset);
			}
		}
	}

	private async Task ShowPreviewAsync(AssetItem currentAsset)
	{
		try
		{
			if (currentAsset.AssetType is AssetType.Font or AssetType.Other)
			{
				if (!string.IsNullOrWhiteSpace(currentAsset.Path) && File.Exists(currentAsset.Path))
				{
					await FileSystemService.OpenFileAsync(currentAsset.Path);
				}
			}
			else if (GetHostPage() is Page page)
			{
				var playbackPage = new AssetPlaybackPage(currentAsset);
				try
				{
					await page.Navigation.PushAsync(playbackPage);
				}
				catch
				{
					await page.Navigation.PushModalAsync(playbackPage);
				}
			}
		}
		catch (Exception ex)
		{
			Log(ex, "Showing asset playback in picker", this);
			if (GetHostPage() is Page page)
			{
				await page.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
			}
		}
	}

	private Page? GetHostPage()
	{
		Element? parent = this;
		while (parent is not null)
		{
			if (parent is Page page)
			{
				return page;
			}

			parent = parent.Parent;
		}

		return Application.Current?.Windows.FirstOrDefault()?.Page;
	}
}