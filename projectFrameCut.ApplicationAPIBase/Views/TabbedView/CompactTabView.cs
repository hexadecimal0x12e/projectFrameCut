using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Threading.Tasks;

namespace projectFrameCut.ApplicationAPIBase.Views.TabbedView
{
    [ContentProperty(nameof(TabItems))]
    public class CompactTabView : ContentView
    {
        private readonly Grid _switchRow;
        private readonly Grid _contentHost;
        private readonly Dictionary<TabbedViewItem, View?> _pendingTabContents = new();

        public event EventHandler<TabbedViewItem>? OnTabSwitched;

        public CompactTabView()
        {
            _switchRow = new Grid
            {
                ColumnSpacing = 8,
                Padding = new Thickness(10, 10, 10, 0)
            };

            _contentHost = new Grid();

            Content = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star)
                },
                RowSpacing = 8,
                Children =
                {
                    _switchRow,
                    _contentHost
                }
            };

            Grid.SetRow(_switchRow, 0);
            Grid.SetRow(_contentHost, 1);

            TabItems = new ObservableCollection<TabbedViewItem>();
        }

        public static readonly BindableProperty TabItemsProperty =
            BindableProperty.Create(
                nameof(TabItems),
                typeof(ObservableCollection<TabbedViewItem>),
                typeof(CompactTabView),
                null,
                defaultValueCreator: _ => new ObservableCollection<TabbedViewItem>(),
                propertyChanged: (bindable, oldValue, newValue) =>
                {
                    var control = (CompactTabView)bindable;
                    if (oldValue is INotifyCollectionChanged oldCollection)
                    {
                        oldCollection.CollectionChanged -= control.TabItems_CollectionChanged;
                    }

                    if (newValue is INotifyCollectionChanged newCollection)
                    {
                        newCollection.CollectionChanged += control.TabItems_CollectionChanged;
                    }

                    control.RebuildTabs();
                });

        public ObservableCollection<TabbedViewItem> TabItems
        {
            get => (ObservableCollection<TabbedViewItem>)GetValue(TabItemsProperty);
            set => SetValue(TabItemsProperty, value);
        }

        public static readonly BindableProperty SelectedIndexProperty =
            BindableProperty.Create(
                nameof(SelectedIndex),
                typeof(int),
                typeof(CompactTabView),
                0,
                BindingMode.TwoWay,
                propertyChanged: OnSelectedIndexChanged);

        public int SelectedIndex
        {
            get => (int)GetValue(SelectedIndexProperty);
            set => SetValue(SelectedIndexProperty, value);
        }

        public static readonly BindableProperty SelectedItemProperty =
            BindableProperty.Create(
                nameof(SelectedItem),
                typeof(TabbedViewItem),
                typeof(CompactTabView),
                null,
                BindingMode.TwoWay,
                propertyChanged: OnSelectedItemChanged);

        public TabbedViewItem? SelectedItem
        {
            get => (TabbedViewItem?)GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        private static void OnSelectedIndexChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (CompactTabView)bindable;
            control.UpdateSelection(raiseEvent: true);
        }

        private static void OnSelectedItemChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (CompactTabView)bindable;
            if (newValue is TabbedViewItem item && control.TabItems.Contains(item))
            {
                int index = control.TabItems.IndexOf(item);
                if (index != control.SelectedIndex)
                {
                    control.SelectedIndex = index;
                }
            }
        }

        public void SelectByTag(string tag)
        {
            if (TabItems == null)
            {
                return;
            }

            for (int i = 0; i < TabItems.Count; i++)
            {
                if (TabItems[i]?.Tag == tag)
                {
                    SelectedIndex = i;
                    return;
                }
            }
        }

        private void TabItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RebuildTabs();
        }

        private void RebuildTabs()
        {
            _switchRow.ColumnDefinitions.Clear();
            _switchRow.Children.Clear();
            _contentHost.Children.Clear();
            var previousPendingContents = new Dictionary<TabbedViewItem, View?>(_pendingTabContents);
            _pendingTabContents.Clear();

            if (TabItems == null || TabItems.Count == 0)
            {
                SelectedItem = null;
                return;
            }

            for (int i = 0; i < TabItems.Count; i++)
            {
                int index = i;
                var item = TabItems[i];
                var content = item.Content;
                if (previousPendingContents.TryGetValue(item, out var previousPendingContent) && previousPendingContent != null)
                {
                    content = previousPendingContent;
                }

                _pendingTabContents[item] = content;
                item.Content = null;

                _switchRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

                var button = CreateHeaderButton(item);
                button.Clicked += (_, _) => SelectedIndex = index;
                _switchRow.Add(button, index, 0);

                item.IsVisible = false;
                _contentHost.Children.Add(item);
            }

            if (SelectedIndex < 0 || SelectedIndex >= TabItems.Count)
            {
                SelectedIndex = 0;
            }
            else
            {
                UpdateSelection(raiseEvent: false);
            }
        }

        private static Button CreateHeaderButton(TabbedViewItem item)
        {
            string text = item.Header?.ToString() ?? "Tab";
            if (item.Header is Label labelHeader)
            {
                text = labelHeader.Text ?? text;
            }

            return new Button
            {
                Text = text,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Center,
                CornerRadius = 8,
                FontSize = 13,
                Padding = new Thickness(10, 6),
                BackgroundColor = Color.FromArgb("#1F1F22"),
                TextColor = Color.FromArgb("#C8C8CC")
            };
        }

        private async void UpdateSelection(bool raiseEvent)
        {
            if (TabItems == null || TabItems.Count == 0)
            {
                SelectedItem = null;
                return;
            }

            if (SelectedIndex < 0 || SelectedIndex >= TabItems.Count)
            {
                throw new IndexOutOfRangeException($"Tab index {SelectedIndex} is out of range, currently there is {TabItems.Count} tabs.");
            }

            var selected = TabItems[SelectedIndex];

            for (int i = 0; i < TabItems.Count; i++)
            {
                bool isActive = i == SelectedIndex;

                var tabItem = TabItems[i];
                tabItem.IsVisible = isActive;
                tabItem.IsSelected = isActive;

                if (_switchRow.Children.Count > i && _switchRow.Children[i] is Button button)
                {
                    button.BackgroundColor = isActive ? Color.FromArgb("#3A3A40") : Color.FromArgb("#1F1F22");
                    button.TextColor = isActive ? Colors.White : Color.FromArgb("#C8C8CC");
                }
            }

            if (SelectedItem != selected)
            {
                SelectedItem = selected;
            }

            if (selected.LazyContentFactory != null && selected.Content == null &&
                (!_pendingTabContents.TryGetValue(selected, out var cachedContent) || cachedContent == null))
            {
                var lazyContent = selected.LazyContentFactory();
                if (lazyContent != null)
                {
                    selected.Content = lazyContent;
                    _pendingTabContents[selected] = null;
                }
            }

            if (selected.LazyAsyncContentFactory != null && selected.Content == null &&
                (!_pendingTabContents.TryGetValue(selected, out var cachedAsyncContent) || cachedAsyncContent == null))
            {
                var indicator = new ActivityIndicator
                {
                    IsRunning = true,
                    IsVisible = true,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                };

                selected.Content = indicator;

                View? lazyContent = null;
                try
                {
                    lazyContent = await selected.LazyAsyncContentFactory();
                }
                catch (Exception ex)
                {
                    Log(ex, $"Show Tab {selected.Tag}/{selected.Header} in the compact tabview of {Parent}({Parent?.GetType().Name})", this);
                    lazyContent = new VerticalStackLayout
                    {
                        Children =
                        {
                            new Label
                            {
                                Text = ex.Message,
                                FontSize = 12,
                                TextColor = Colors.Gray,
                                HorizontalOptions = LayoutOptions.Center,
                                VerticalOptions = LayoutOptions.Center
                            }
                        },
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
                        Margin = new Thickness(8)
                    };
                }

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (lazyContent != null)
                    {
                        selected.Content = lazyContent;
                        _pendingTabContents[selected] = null;
                    }
                    else if (selected.Content == indicator)
                    {
                        selected.Content = null;
                        _pendingTabContents[selected] = null;
                    }
                });
            }

            if (selected.Content == null && _pendingTabContents.TryGetValue(selected, out var pendingContent) && pendingContent != null)
            {
                selected.Content = pendingContent;
                _pendingTabContents[selected] = null;
            }

            if (raiseEvent)
            {
                OnTabSwitched?.Invoke(this, selected);
            }
        }
    }
}
