using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using Microsoft.Maui.Controls.Shapes;
using System.Threading.Tasks;
using System.Diagnostics;

namespace projectFrameCut.ApplicationAPIBase.Views.TabbedView
{
    [ContentProperty(nameof(TabItems))]
    public partial class TabbedView : ContentView
    {
        private readonly Dictionary<TabbedViewItem, View?> _pendingTabContents = new();

        public HorizontalStackLayout HeadersPanel { get; private set; }
        public ContentView ContentPresenter { get; private set; }
        public ContentView HeaderRightContentContainer { get; private set; }

        public event EventHandler<TabbedViewItem>? OnTabSwitched;

        public TabbedView()
        {
            InitializeComponent();
            TabItems = new ObservableCollection<TabbedViewItem>();
        }

        private void InitializeComponent()
        {
            var grid = new Grid
            {
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto },
                    new RowDefinition { Height = GridLength.Star },
                }
            };

            HeadersPanel = new HorizontalStackLayout
            {
                Spacing = 2,
                Padding = new Thickness(5, 5, 5, 0)
            };

            var headersScroll = new ScrollView
            {
                Orientation = ScrollOrientation.Horizontal,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
                Content = HeadersPanel
            };

            // 创建右侧内容容器
            HeaderRightContentContainer = new ContentView
            {
                Padding = new Thickness(5, 5, 5, 0),
                VerticalOptions = LayoutOptions.Center
            };

            // 创建包含标题和右侧内容的网格
            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };
            Grid.SetColumn(headersScroll, 0);
            Grid.SetColumn(HeaderRightContentContainer, 1);
            headerGrid.Children.Add(headersScroll);
            headerGrid.Children.Add(HeaderRightContentContainer);

            var headersBorder = new Border
            {
                StrokeThickness = 0,
                BackgroundColor = Color.FromArgb("#80404040"),
                Content = headerGrid
            };
            Grid.SetRow(headersBorder, 0);

            ContentPresenter = new ContentView();

            var contentBorder = new Border
            {
                StrokeThickness = 0,
                BackgroundColor = Colors.Transparent,
                Content = ContentPresenter
            };
            Grid.SetRow(contentBorder, 1);

            grid.Children.Add(headersBorder);
            grid.Children.Add(contentBorder);

            Content = grid;
        }

        public static readonly BindableProperty TabItemsProperty =
            BindableProperty.Create(nameof(TabItems), typeof(ObservableCollection<TabbedViewItem>), typeof(TabbedView), null,
                defaultValueCreator: bindable => new ObservableCollection<TabbedViewItem>(),
                propertyChanged: (bindable, oldValue, newValue) =>
                {
                    var control = (TabbedView)bindable;
                    if (oldValue is INotifyCollectionChanged oldCollection)
                        oldCollection.CollectionChanged -= control.TabItems_CollectionChanged;
                    if (newValue is INotifyCollectionChanged newCollection)
                        newCollection.CollectionChanged += control.TabItems_CollectionChanged;
                    control.RebuildHeaders();
                });

        public ObservableCollection<TabbedViewItem> TabItems
        {
            get => (ObservableCollection<TabbedViewItem>)GetValue(TabItemsProperty);
            set => SetValue(TabItemsProperty, value);
        }

        public static readonly BindableProperty SelectedIndexProperty = BindableProperty.Create(
            nameof(SelectedIndex), typeof(int), typeof(TabbedView), 0, BindingMode.TwoWay, propertyChanged: OnSelectedIndexChanged);

        public int SelectedIndex
        {
            get => (int)GetValue(SelectedIndexProperty);
            set
            {
                SetValue(SelectedIndexProperty, value);
                OnTabSwitched?.Invoke(this, TabItems[value]);
            }
        }

        public static readonly BindableProperty SelectedItemProperty = BindableProperty.Create(
            nameof(SelectedItem), typeof(TabbedViewItem), typeof(TabbedView), null, BindingMode.TwoWay,
            propertyChanged: OnSelectedItemChanged);

        public TabbedViewItem SelectedItem
        {
            get => (TabbedViewItem)GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        public static readonly BindableProperty HeaderRightContentProperty = BindableProperty.Create(
            nameof(HeaderRightContent), typeof(View), typeof(TabbedView), null, BindingMode.OneWay,
            propertyChanged: OnHeaderRightContentChanged);

        public View HeaderRightContent
        {
            get => (View)GetValue(HeaderRightContentProperty);
            set => SetValue(HeaderRightContentProperty, value);
        }

        private static void OnHeaderRightContentChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (TabbedView)bindable;
            if (control.HeaderRightContentContainer != null)
            {
                control.HeaderRightContentContainer.Content = newValue as View;
            }
        }

        private static void OnSelectedIndexChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (TabbedView)bindable;
            control.UpdateSelection();
        }

        private static void OnSelectedItemChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (TabbedView)bindable;
            if (newValue is TabbedViewItem item && control.TabItems != null && control.TabItems.Contains(item))
            {
                control.SelectedIndex = control.TabItems.IndexOf(item);
            }
        }

        // Select a tab by its Tag value. Uses equality comparison (Equals).
        public void SelectByTag(string tag)
        {
            if (TabItems == null) return;
            for (int i = 0; i < TabItems.Count; i++)
            {
                if (TabItems[i]?.Tag == tag)
                {
                    SelectedIndex = i;
                    return;
                }
            }
        }

        private void TabItems_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            RebuildHeaders();
        }

        private void RebuildHeaders()
        {
            HeadersPanel.Children.Clear();
            var previousPendingContents = new Dictionary<TabbedViewItem, View?>(_pendingTabContents);
            _pendingTabContents.Clear();
            if (TabItems == null) return;

            for (int i = 0; i < TabItems.Count; i++)
            {
                var item = TabItems[i];
                var content = item.Content;
                if (previousPendingContents.TryGetValue(item, out var previousPendingContent) && previousPendingContent != null)
                {
                    content = previousPendingContent;
                }

                _pendingTabContents[item] = content;
                item.Content = null;
                var headerView = CreateHeaderView(item, i);
                HeadersPanel.Children.Add(headerView);
            }
            UpdateSelection();
        }

        private View CreateHeaderView(TabbedViewItem item, int index)
        {
            View content;
            if (item.Header is View viewHeader)
            {
                content = viewHeader;
            }
            else
            {
                content = new Label
                {
                    Text = item.Header?.ToString() ?? "Tab",
                    VerticalOptions = LayoutOptions.Center,
                    HorizontalOptions = LayoutOptions.Center,
                    Margin = new Thickness(8, 4),
                    TextColor = Colors.Black
                };
            }

            var border = new Border
            {
                Content = content,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(6, 6, 0, 0) },
                StrokeThickness = 0,
                BackgroundColor = Colors.Gray,
                Margin = new Thickness(0, 2, 2, 0),
                Padding = new Thickness(10, 6)
            };

            var tapGesture = new TapGestureRecognizer();
            tapGesture.Tapped += (s, e) =>
            {
                SelectedIndex = index;
            };
            border.GestureRecognizers.Add(tapGesture);

            return border;
        }

        private async void UpdateSelection()
        {
            if (TabItems == null || TabItems.Count == 0)
            {
                ContentPresenter.Content = null;
                SelectedItem = null;
                return;
            }

            if (SelectedIndex < 0) throw new IndexOutOfRangeException($"Tab index ({SelectedIndex}) must greater or equals than 0.");
            if (SelectedIndex >= TabItems.Count) throw new IndexOutOfRangeException($"Tab index {SelectedIndex} is out of range, currently there is {TabItems.Count} tabs.");

            var selectedItem = TabItems[SelectedIndex];

            if (SelectedItem != selectedItem)
            {
                SelectedItem = selectedItem;
            }

            for (int i = 0; i < HeadersPanel.Children.Count; i++)
            {
                if (HeadersPanel.Children[i] is Border border)
                {
                    if (i == SelectedIndex)
                    {
                        border.BackgroundColor = Colors.LightGray;
                        if (border.Content is Label l) l.FontAttributes = FontAttributes.Bold;
                    }
                    else
                    {
                        border.BackgroundColor = Colors.Gray;
                        if (border.Content is Label l) l.FontAttributes = FontAttributes.None;
                    }
                }
            }

            foreach (var item in TabItems)
            {
                item.IsSelected = (item == selectedItem);
            }


            if (selectedItem.LazyContentFactory != null && selectedItem.Content == null &&
                (!_pendingTabContents.TryGetValue(selectedItem, out var cachedContent) || cachedContent == null))
            {
                var lazyContent = selectedItem.LazyContentFactory();
                if (lazyContent != null)
                {
                    _pendingTabContents[selectedItem] = lazyContent;
                }
            }

            if (selectedItem.LazyAsyncContentFactory != null && selectedItem.Content == null &&
                (!_pendingTabContents.TryGetValue(selectedItem, out var cachedAsyncContent) || cachedAsyncContent == null))
            {
                // Show a loading indicator immediately to keep the UI responsive while content is created in background.
                var indicator = new ActivityIndicator
                {
                    IsRunning = true,
                    IsVisible = true,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                };
                ContentPresenter.Content = indicator;

                View? lazyContent = null;
                try
                {
                    lazyContent = await selectedItem.LazyAsyncContentFactory();
                }
                catch (Exception ex)
                {
                    Log(ex, $"Show Tab {SelectedItem.Tag}/{SelectedItem.Header} in the tabview of {Parent}({Parent.GetType().Name})", this);
                    lazyContent = new VerticalStackLayout
                    {
                        Children =
                            {
                                new Label
                                {
                                    Text = LocalizedResources.APIBaseLocalizedResources.Localized.TabView_Error(SelectedItem?.Header?.ToString() ?? "Unknown tab"),
                                    FontSize = 20,
                                    TextColor = Colors.Yellow,
                                    HorizontalOptions = LayoutOptions.Center,
                                    VerticalOptions = LayoutOptions.Center
                                },
                                new Label
                                {
                                    Text = ex.ToString(),
                                    FontSize = 12,
                                    TextColor = Colors.Gray,
                                    HorizontalOptions = LayoutOptions.Center,
                                    VerticalOptions = LayoutOptions.Center
                                }
                            },
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
                        Margin = new(8)
                    };

                    if (Debugger.IsAttached)
                    {
                        if (await Dispatcher.DispatchAsync(async () => await Shell.Current.CurrentPage.DisplayAlertAsync("Error", $"Failed to load content for tab '{SelectedItem?.Header ?? "Unknown"}'.{Environment.NewLine}Error: {ex.Message}{Environment.NewLine}{Environment.NewLine}Throw it?", "Yes", "No")))
                        {
                            throw;
                        }
                    }

                }

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (lazyContent != null)
                    {
                        _pendingTabContents[selectedItem] = lazyContent;
                        UpdateSelection();
                    }
                    else
                    {
                        // Remove the indicator if loading failed or returned null and clear pending entry.
                        if (ContentPresenter.Content == indicator)
                            ContentPresenter.Content = null;
                        _pendingTabContents[selectedItem] = null;
                    }
                });

                return;
            }

            if (selectedItem.Content == null && _pendingTabContents.TryGetValue(selectedItem, out var pendingContent) && pendingContent != null)
            {
                selectedItem.Content = pendingContent;
                _pendingTabContents[selectedItem] = null;
            }

            var selectedContent = selectedItem.Content;
            if (ContentPresenter.Content != selectedContent)
            {
                ContentPresenter.Content = selectedContent;
            }


        }
    }
}
