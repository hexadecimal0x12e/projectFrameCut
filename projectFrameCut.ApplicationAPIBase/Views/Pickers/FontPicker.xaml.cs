using SixLabors.Fonts;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using static projectFrameCut.ApplicationAPIBase.Helpers.TextHelper;

namespace projectFrameCut.ApplicationAPIBase.Views.Pickers;

public class FontItem : INotifyPropertyChanged
{
    public string FontName { get; set; }

    public string[] Tags { get; set; } = [];

    public FontFileInfo? InnerItem { get; set; }

    public FontCollection? InnerFont { get; set; }

    private string? _displayName;

    public string DisplayName
    {
        get => _displayName ?? FontName ?? string.Empty;
        set => _displayName = value;
    }

    public string? PrimaryLanguageTag { get; set; }

    public string Category { get; set; }

    private ImageSource _previewImageSource;
    public ImageSource PreviewImageSource
    {
        get => _previewImageSource;
        set
        {
            _previewImageSource = value;
            OnPropertyChanged();
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public partial class FontPicker : ContentView
{
    public static readonly BindableProperty FontsSourceProperty = BindableProperty.Create(
        nameof(FontsSource), typeof(IEnumerable<FontItem>), typeof(FontPicker), null,
        propertyChanged: OnFontsSourceChanged);

    public static readonly BindableProperty PreviewRendererProperty = BindableProperty.Create(
        nameof(PreviewRenderer), typeof(Func<FontItem, Task<ImageSource>>), typeof(FontPicker), null,
        propertyChanged: OnPreviewRendererChanged);

    public static readonly BindableProperty TitleProperty = BindableProperty.Create(nameof(Title), typeof(string), typeof(FontPicker), "Font Picker", BindingMode.OneWay);

    public IEnumerable<FontItem> FontsSource
    {
        get => (IEnumerable<FontItem>)GetValue(FontsSourceProperty);
        set => SetValue(FontsSourceProperty, value);
    }

    public Func<FontItem, Task<ImageSource>> PreviewRenderer
    {
        get => (Func<FontItem, Task<ImageSource>>)GetValue(PreviewRendererProperty);
        set => SetValue(PreviewRendererProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private ObservableCollection<FontItem> _filteredFonts;
    private CancellationTokenSource _scrollDebounce;
    private int _firstVisibleIndex;
    private int _lastVisibleIndex;
    private double _lastKnownCVHeight = -1;
    private List<string> _categoryList = new();
    private bool _syncingCategory = false;

    public FontPicker()
    {
        InitializeComponent();
        SearchEntry.TextChanged += SearchEntry_TextChanged;
        FontCollectionView.Scrolled += FontCollectionView_Scrolled;
        FontCollectionView.SelectionChanged += FontCollectionView_SelectionChanged;
        FontCollectionView.SizeChanged += OnCollectionViewFirstLayout;
        CategoryPicker.SelectedIndexChanged += CategoryPicker_SelectedIndexChanged;
    }

    public static readonly BindableProperty SelectedFontProperty = BindableProperty.Create(
        nameof(SelectedFont), typeof(FontItem), typeof(FontPicker), null, BindingMode.TwoWay);

    public FontItem SelectedFont
    {
        get => (FontItem)GetValue(SelectedFontProperty);
        set => SetValue(SelectedFontProperty, value);
    }

    public event EventHandler<FontItem> SelectedFontChanged;

    public static readonly BindableProperty SelectedCategoryProperty = BindableProperty.Create(
        nameof(SelectedCategory), typeof(string), typeof(FontPicker), null,
        propertyChanged: OnSelectedCategoryChanged);

    public string SelectedCategory
    {
        get => (string)GetValue(SelectedCategoryProperty);
        set => SetValue(SelectedCategoryProperty, value);
    }

    private static void OnSelectedCategoryChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not FontPicker picker || picker._syncingCategory)
            return;

        picker._syncingCategory = true;
        var category = newValue as string;
        var idx = string.IsNullOrEmpty(category) ? 0 : picker._categoryList.IndexOf(category);
        picker.CategoryPicker.SelectedIndex = Math.Max(0, idx);
        picker._syncingCategory = false;

        picker.FilterFonts(picker.SearchEntry.Text);
    }

    private void CategoryPicker_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_syncingCategory) return;

        _syncingCategory = true;
        var idx = CategoryPicker.SelectedIndex;
        var category = idx > 0 && idx < _categoryList.Count ? _categoryList[idx] : null;
        SetValue(SelectedCategoryProperty, category);
        _syncingCategory = false;

        FilterFonts(SearchEntry.Text);
    }

    private void OnCollectionViewFirstLayout(object sender, EventArgs e)
    {
        var h = FontCollectionView.Height;
        // 仅在出现有效尺寸变化时触发（包括从隐藏状态变为可见的情况）
        if (h <= 0 || h == _lastKnownCVHeight) return;
        _lastKnownCVHeight = h;
        ScheduleRender(debounceMs: 100);
    }

    private static void OnFontsSourceChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is FontPicker picker)
        {
            picker.RefreshCategories();
            picker.FilterFonts(picker.SearchEntry.Text);
        }
    }

    private void RefreshCategories()
    {
        var categories = FontsSource?
            .SelectMany(f => f.Tags.Concat([f.Category]))
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .OrderBy(c => c)
            .ToList() ?? new List<string>();

        _categoryList = ["All", .. categories];

        CategoryPicker.ItemsSource = _categoryList;

        // 恢复已选分类
        _syncingCategory = true;
        var current = SelectedCategory;
        var idx = !string.IsNullOrEmpty(current) ? _categoryList.IndexOf(current) : 0;
        CategoryPicker.SelectedIndex = Math.Max(0, idx);
        _syncingCategory = false;
    }

    private static void OnPreviewRendererChanged(BindableObject bindable, object oldValue, object newValue)
    {
        // PreviewRenderer 设置（或更新）后，重新触发当前可见范围的渲染
        if (bindable is FontPicker picker && newValue != null)
            picker.ScheduleRender(debounceMs: 0);
    }

    private void SearchEntry_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterFonts(e.NewTextValue);
    }

    private void FilterFonts(string searchText)
    {
        if (FontsSource == null)
            return;

        IEnumerable<FontItem> filtered = FontsSource;

        // 按 Category 筛选
        var selectedCategory = SelectedCategory;
        if (!string.IsNullOrEmpty(selectedCategory))
            filtered = filtered.Where(f => f.Category == selectedCategory);

        // 按搜索文本筛选
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var lower = searchText.ToLowerInvariant();
            filtered = filtered.Where(f => f.FontName?.ToLowerInvariant().Contains(lower) == true);
        }

        // 重置所有预览，等待按需渲染
        foreach (var item in filtered)
            item.PreviewImageSource = null;

        _filteredFonts = new ObservableCollection<FontItem>(filtered);
        _firstVisibleIndex = 0;
        _lastVisibleIndex = 0;
        FontCollectionView.ItemsSource = _filteredFonts;

        // 列表刷新后，渲染初始可视区域
        ScheduleRender(debounceMs: 150);
    }

    private void FontCollectionView_Scrolled(object sender, ItemsViewScrolledEventArgs e)
    {
        // Windows MAUI 上 CollectionView.Scrolled 有时会返回 -1，需回退到偏移量估算
        if (e.FirstVisibleItemIndex >= 0 && e.LastVisibleItemIndex >= 0)
        {
            _firstVisibleIndex = e.FirstVisibleItemIndex;
            _lastVisibleIndex = e.LastVisibleItemIndex;
        }
        else
        {
            // 使用垂直偏移量 + CollectionView 高度来估算可见范围
            const double estimatedItemHeight = 72.0;
            var cvHeight = FontCollectionView.Height > 0 ? FontCollectionView.Height : 400;
            _firstVisibleIndex = (int)(e.VerticalOffset / estimatedItemHeight);
            var estimatedVisible = (int)Math.Ceiling(cvHeight / estimatedItemHeight);
            _lastVisibleIndex = _firstVisibleIndex + estimatedVisible;
        }

        // 滚动期间取消上一次待渲染任务，等待滚动完全静止后再渲染
        ScheduleRender(debounceMs: 300);
    }

    private void ScheduleRender(int debounceMs)
    {
        _scrollDebounce?.Cancel();
        _scrollDebounce?.Dispose();
        _scrollDebounce = new CancellationTokenSource();
        var token = _scrollDebounce.Token;

        Task.Delay(debounceMs, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            MainThread.BeginInvokeOnMainThread(RenderVisibleItems);
        }, TaskScheduler.Default);
    }

    private void RenderVisibleItems()
    {
        if (PreviewRenderer == null || _filteredFonts == null || _filteredFonts.Count == 0)
            return;

        var first = Math.Max(0, _firstVisibleIndex);
        // 若还没有发生过任何滚动，_lastVisibleIndex 为 0；
        // 用估算：CollectionView 高度 / 单项平均高度（约 60dp），至少渲染首屏可见条目
        int last;
        if (_lastVisibleIndex > first)
        {
            last = Math.Min(_filteredFonts.Count - 1, _lastVisibleIndex);
        }
        else
        {
            var estimatedVisible = (int)Math.Ceiling(FontCollectionView.Height / 72.0);
            estimatedVisible = Math.Max(estimatedVisible, 8); // 最少保底 8 项
            last = Math.Min(_filteredFonts.Count - 1, first + estimatedVisible);
        }

        for (int i = first; i <= last; i++)
        {
            var item = _filteredFonts[i];
            if (item.PreviewImageSource != null)
                continue;

            var capturedItem = item;
            var renderer = PreviewRenderer; // 捕获此刻的委托，防止替换
            Task.Run(async () =>
            {
                try
                {
                    var source = await renderer(capturedItem);
                    MainThread.BeginInvokeOnMainThread(() => capturedItem.PreviewImageSource = source);
                }
                catch
                {
                    // 渲染失败时保持空白，不影响列表其余项目
                }
            });
        }
    }

    private void FontCollectionView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var previous = e.PreviousSelection?.FirstOrDefault() as FontItem;
        var current = e.CurrentSelection?.FirstOrDefault() as FontItem;

        if (previous != null)
            previous.IsSelected = false;

        if (current != null)
            current.IsSelected = true;

        SelectedFont = current;
        SelectedFontChanged?.Invoke(this, current);
    }
}