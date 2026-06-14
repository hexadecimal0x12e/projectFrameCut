using Microsoft.Maui;
using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
#pragma warning disable CS8981 // 该类型名称仅包含小写 ascii 字符。此类名称可能会成为该语言的保留值。
using pppcea = projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders.PropertyPanelPropertyChangedEventArgs;
using Switch = Microsoft.Maui.Controls.Switch; //make code shorter
#pragma warning restore CS8981 // 该类型名称仅包含小写 ascii 字符。此类名称可能会成为该语言的保留值。

namespace projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders
{
    /// <summary>
    /// Provides a builder pattern for creating a property panel layout with various UI elements such as labels, entries, checkboxes, switches, sliders, and any custom views.
    /// </summary>
    /// <remarks>
    /// This builder supports a uniform event-based property change notifications.
    /// </remarks>
    [System.Diagnostics.DebuggerNonUserCode()]
    public class PropertyPanelBuilder
    {
        /// <summary>
        /// Set the default width of the <see cref="WidthOfContent"/>.
        /// </summary>
        public static double DefaultWidthOfContent = 5;

        private List<View> children = new();

        /// <summary>
        /// Represents a collection of components added to the property panel, identified by their unique string IDs.
        /// </summary>
        public Dictionary<string, View> Components { get; private init; } = new();

        /// <summary>
        /// Gets a collection of custom properties associated with the object.
        /// </summary>
        public Dictionary<string, object> Properties { get; } = new Dictionary<string, object>();

        /// <summary>
        /// Get or set the default ratio of length of the content area (the second column) by their labels (the first column).
        /// </summary>
        /// <remarks>
        /// Use null for default value, which is equals to <see cref="DefaultWidthOfContent"/>.
        /// </remarks>
        public double? WidthOfContent { get; set; } = null;

        /// <summary>
        /// Gets or sets the default padding applied to the control's outer grid,
        /// </summary>
        /// <remarks>
        /// Except <see cref="AddSeparator(Action{BoxView}?, string)"/>, <see cref="AddCustomChild(View)"/>, and <see cref="AddCustomChild(Func{Action{object}, View}, string, object)"/>.
        /// </remarks>
        public Thickness DefaultPadding { get; set; } = new Thickness(0, 8, 0, 0);

        /// <summary>
        /// Get a builder for creating child items in a fluent way.
        /// </summary>
        public PropertyPanelChildrenBuilder childBuilder;


        /// <summary>
        /// Triggered when any property of the child items created by the preset creator changes, 
        /// or when they are added via <see cref="AddCustomChild(Func{Action{object}, View}, string, object)"/>, 
        /// provided you have correctly set up the target view's event invoker.        
        /// </summary>
        public event EventHandler<pppcea>? PropertyChanged;

        public PropertyPanelBuilder()
        {
            instanceID = Guid.NewGuid();
            childBuilder = new PropertyPanelChildrenBuilder(this);
        }

        private Guid instanceID;
        private bool valid = true;

        private ColumnDefinitionCollection CreateTwoColumnDefinitions()
        {
            double effective = WidthOfContent ?? DefaultWidthOfContent;
            var cols = new ColumnDefinitionCollection();
            if (effective >= 0)
            {
                cols.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cols.Add(new ColumnDefinition { Width = new GridLength(effective, GridUnitType.Star) });
            }
            else
            {
                cols.Add(new ColumnDefinition { Width = new GridLength(Math.Abs(effective), GridUnitType.Star) });
                cols.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            return cols;
        }

        /// <summary>
        /// Adds a <seealso cref="Label"/> to the property panel.
        /// </summary>
        public PropertyPanelBuilder AddText(string content, string Id = "", double fontSize = 14, FontAttributes fontAttributes = FontAttributes.None)
        {
            var label = new Label
            {
                Text = content,
                FontSize = fontSize,
                FontAttributes = fontAttributes
            };
            if (!string.IsNullOrWhiteSpace(Id)) Components.Add(Id, label);
            children.Add(label);
            return this;
        }
        /// <summary>
        /// Add a single line of text with a label to the property panel. 
        /// </summary>
        /// <param name="label"></param>
        /// <param name="Id"></param>
        /// <returns></returns>
        public PropertyPanelBuilder AddText(PropertyPanelItemLabel label, string Id = "")
        {
            var l = label.LabelConfigure();
            if (!string.IsNullOrWhiteSpace(Id)) Components.Add(Id, l);
            children.Add(l);
            return this;
        }
        /// <summary>
        /// Add a <see cref="PropertyPanelItemLabel"/> with a header <see cref="PropertyPanelItemLabel"/>
        /// </summary>
        public PropertyPanelBuilder AddText(PropertyPanelItemLabel header, PropertyPanelItemLabel label, string Id = "")
        {
            var left = header.LabelConfigure();
            var right = label.LabelConfigure();
            var grid = new Grid
            {
                ColumnDefinitions = CreateTwoColumnDefinitions(),
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto }
                },
                Padding = DefaultPadding
            };
            grid.Children.Add(left);
            grid.Children.Add(right);
            Grid.SetColumn(right, 1);
            children.Add(grid);
            if(!string.IsNullOrWhiteSpace(Id)) Components.Add(Id, right);
            return this;
        }
        /// <summary>
        /// Add a <see cref="Label"/> with a header <see cref="PropertyPanelItemLabel"/>.
        /// </summary>
        public PropertyPanelBuilder AddText(PropertyPanelItemLabel header, Label right, string Id = "")
        {
            var left = header.LabelConfigure();
            var grid = new Grid
            {
                ColumnDefinitions = CreateTwoColumnDefinitions(),
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto }
                },
                Padding = DefaultPadding
            };
            grid.Children.Add(left);
            grid.Children.Add(right);
            Grid.SetColumn(right, 1);
            children.Add(grid);
            if(!string.IsNullOrWhiteSpace(Id)) Components.Add(Id, right);
            return this;
        }

        public PropertyPanelBuilder AddText(Label label, string Id = "", Action<Label>? LabelSetter = null)
        {
            if (!string.IsNullOrWhiteSpace(Id)) Components.Add(Id, label);
            LabelSetter?.Invoke(label);
            children.Add(label);
            return this;
        }

        /// <summary>
        /// Adds a text input box (<seealso cref="Entry"/>) with an associated label to the property panel.
        /// </summary>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        /// <param name="defaultValue">The default value to assign to the property identified by <paramref name="Id"/>.</param>
        public PropertyPanelBuilder AddEntry(string Id, PropertyPanelItemLabel title, string defaultValue, string placeholder, Action<Entry>? EntrySeter = null, EntryUpdateEventCallMode? mode = EntryUpdateEventCallMode.OnUnfocusedAndValueChanged)
        {
            var entry = new Entry
            {
                Placeholder = placeholder,
                Text = defaultValue,
                HorizontalOptions = LayoutOptions.Fill,
                BindingContext = this
            };
            var label = title.LabelConfigure();

            Properties[Id] = defaultValue;
            switch (mode ?? EntryUpdateEventCallMode.OnUnfocusedAndValueChanged)
            {
                case EntryUpdateEventCallMode.OnAnyTextChange:
                    entry.TextChanged += (s, e) => pppcea.CreateAndInvoke(this, Id, e.NewTextValue);
                    break;
                case EntryUpdateEventCallMode.OnUnfocused:
                    entry.Unfocused += (s, e) => pppcea.CreateAndInvoke(this, Id, entry.Text);
                    break;
                case EntryUpdateEventCallMode.OnUnfocusedAndValueChanged:
                    entry.Unfocused += (s, e) =>
                    {
                        if (entry.Text != Properties[Id] as string)
                        {
                            pppcea.CreateAndInvoke(this, Id, entry.Text);
                        }
                    };
                    break;
            }
            EntrySeter?.Invoke(entry);

            var grid = new Grid
            {
                ColumnDefinitions = CreateTwoColumnDefinitions(),
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto }
                },
                Padding = DefaultPadding
            };
            grid.Children.Add(label);
            grid.Children.Add(entry);
            Grid.SetColumn(entry, 1);

            children.Add(grid);
            Components.Add(Id, entry);
            return this;
        }

        /// <summary>
        /// Adds a 2-state <seealso cref="CheckBox"/> with an associated label to the property panel.
        /// </summary>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        /// <param name="defaultValue">The default value to assign to the property identified by <paramref name="Id"/>.</param>
        public PropertyPanelBuilder AddCheckbox(string Id, PropertyPanelItemLabel title, bool defaultValue, Action<CheckBox>? CheckboxSetter = null, Action<Label>? LabelSetter = null)
        {
            var checkbox = new CheckBox
            {
                IsChecked = defaultValue,
                HorizontalOptions = LayoutOptions.End,
                BindingContext = this
            };
            var label = title.LabelConfigure();
            Properties[Id] = defaultValue;
            checkbox.CheckedChanged += async (s, e) =>
            {
                await Task.Delay(350); //let animation go
                pppcea.CreateAndInvoke(this, Id, e.Value);
            };
            CheckboxSetter?.Invoke(checkbox);
            var grid = new Grid
            {
                ColumnDefinitions = CreateTwoColumnDefinitions(),
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto }
                },
                Padding = DefaultPadding
            };
            grid.Children.Add(label);
            grid.Children.Add(checkbox);
            Grid.SetColumn(checkbox, 1);
            children.Add(grid);
            Components.Add(Id, checkbox);
            return this;
        }
        /// <summary>
        /// Adds a <seealso cref="Switch"/> with an associated label to the property panel.
        /// </summary>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        /// <param name="defaultValue">The default value to assign to the property identified by <paramref name="Id"/>.</param>
        public PropertyPanelBuilder AddSwitch(string Id, PropertyPanelItemLabel title, bool defaultValue, Action<Switch>? SwitchSetter = null)
        {
            var swtch = new Switch
            {
                IsToggled = defaultValue,
                HorizontalOptions = LayoutOptions.End,
                BindingContext = this,

            };
            var label = title.LabelConfigure();
            Properties[Id] = defaultValue;
            swtch.Toggled += async (s, e) =>
            {
                await Task.Delay(350); //let animation go
                pppcea.CreateAndInvoke(this, Id, e.Value);
            };
            SwitchSetter?.Invoke(swtch);
            var grid = new Grid
            {
                ColumnDefinitions = CreateTwoColumnDefinitions(),
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto }
                },
                Padding = DefaultPadding
            };
            grid.Children.Add(label);
            grid.Children.Add(swtch);
            Grid.SetColumn(swtch, 1);
            children.Add(grid);
            Components.Add(Id, swtch);
            return this;
        }


        /// <summary>
        /// Adds a <seealso cref="Picker"/> with an associated label to the property panel.
        /// </summary>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        /// <param name="defaultOne">The default value to assign to the property identified by <paramref name="Id"/>.</param>
        public PropertyPanelBuilder AddPicker(string Id, PropertyPanelItemLabel title, string[] values, string? defaultOne = null, Action<Picker>? PickerSetter = null)
        {
            var picker = new Picker
            {
            };
            picker.ItemsSource = values;
            picker.SelectedIndex = Array.IndexOf(values, defaultOne);

            var label = title.LabelConfigure();
            Properties[Id] = defaultOne!;
#if !iDevices
            picker.SelectedIndexChanged += (s, e) =>
            {
                var selected = picker.SelectedItem as string;
                if (selected is null) return;
                pppcea.CreateAndInvoke(this, Id, selected);
            };
#else //avoid picker disappears before selection done
            picker.Closed += (s, e) =>
            {
                var selected = picker.SelectedItem as string;
                if (selected is null) return;
                pppcea.CreateAndInvoke(this, Id, selected);
            };
#endif
            PickerSetter?.Invoke(picker);
            var grid = new Grid
            {
                ColumnDefinitions = CreateTwoColumnDefinitions(),
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto }
                },
                Padding = DefaultPadding
            };
            grid.Children.Add(label);
            grid.Children.Add(picker);
            Grid.SetColumn(picker, 1);
            children.Add(grid);
            Components.Add(Id, picker);
            return this;
        }

        /// <summary>
        /// Adds a <seealso cref="Slider"/> with an associated label to the property panel.
        /// </summary>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        /// <param name="defaultValue">The default value to assign to the property identified by <paramref name="Id"/>.</param>
        public PropertyPanelBuilder AddSlider(string Id, PropertyPanelItemLabel title, double min, double max, double defaultValue, Action<Slider>? SliderSetter = null, SliderUpdateEventCallMode? eventCallMode = SliderUpdateEventCallMode.OnMouseUp)
        {
            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = defaultValue,
                HorizontalOptions = LayoutOptions.Fill,
                BindingContext = this
            };
            var label = title.LabelConfigure();

            Properties[Id] = defaultValue;
            switch (eventCallMode ?? SliderUpdateEventCallMode.OnMouseUp)
            {
                case SliderUpdateEventCallMode.OnValueChanged:
                    {
                        slider.ValueChanged += (s, e) => pppcea.CreateAndInvoke(this, Id, e.NewValue);
                        break;
                    }

                default:
                    {
                        slider.DragCompleted += (s, e) => pppcea.CreateAndInvoke(this, Id, slider.Value);
                        break;
                    }
            }

            SliderSetter?.Invoke(slider);

            var grid = new Grid
            {
                ColumnDefinitions = CreateTwoColumnDefinitions(),
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto }
                },
                Padding = DefaultPadding
            };
            grid.Children.Add(label);
            grid.Children.Add(slider);
            Grid.SetColumn(slider, 1);
            children.Add(grid);
            Components.Add(Id, slider);
            return this;
        }

        /// <summary>
        /// 添加一个位置元组输入框（例如 XYWH 坐标，或 XW/WH 尺寸），支持 2 个或 4 个数值输入框并排排列。
        /// 每个子输入框触发 <see cref="PropertyChanged"/> 时，<see cref="pppcea.Id"/> 为 "{Id}_X"、"{Id}_Y"、"{Id}_W" 或 "{Id}_H"，
        /// <see cref="pppcea.Value"/> 为 <see langword="double"/> 类型。
        /// </summary>
        /// <param name="Id">基础标识符，子项将以 "{Id}_{field}" 命名。</param>
        /// <param name="title">左侧标签。</param>
        /// <param name="mode">输入模式：XYWH(4值)、XW(2值)、WH(2值)。</param>
        /// <param name="defaultValue">默认值元组 (X, Y, W, H)，未使用的字段将被忽略。</param>
        /// <param name="EntrySetter">用于自定义所有 Entry 的委托。</param>
        /// <param name="entryWidth">每个数字输入框的宽度。</param>
        /// <param name="eventCallMode">事件触发模式，默认在失去焦点时触发。</param>
        public PropertyPanelBuilder AddPositionTupleInputBox(
            string Id,
            PropertyPanelItemLabel title,
            PositionTupleMode mode,
            (double X, double Y, double W, double H) defaultValue,
            Action<Entry>? EntrySetter = null,
            double entryWidth = 60,
            EntryUpdateEventCallMode? eventCallMode = EntryUpdateEventCallMode.OnUnfocused)
        {
            var label = title.LabelConfigure();
            var entryContainer = new HorizontalStackLayout
            {
                Spacing = 6,
                VerticalOptions = LayoutOptions.Center
            };

            Entry CreateField(string fieldName, string shortLabel, double defaultVal)
            {
                var entry = new Entry
                {
                    Text = defaultVal.ToString(),
                    Placeholder = "0",
                    Keyboard = Keyboard.Numeric,
                    WidthRequest = entryWidth,
                    VerticalOptions = LayoutOptions.Center,
                    BindingContext = this
                };

                var fullId = $"{Id}_{fieldName}";
                Properties[fullId] = defaultVal;

                switch (eventCallMode ?? EntryUpdateEventCallMode.OnUnfocused)
                {
                    case EntryUpdateEventCallMode.OnAnyTextChange:
                        entry.TextChanged += (s, e) =>
                        {
                            if (double.TryParse(e.NewTextValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val))
                                pppcea.CreateAndInvoke(this, fullId, val);
                        };
                        break;
                    default:
                        entry.Unfocused += (s, e) =>
                        {
                            if (double.TryParse(entry.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val))
                                pppcea.CreateAndInvoke(this, fullId, val);
                        };
                        break;
                }

                EntrySetter?.Invoke(entry);
                Components.Add(fullId, entry);
                return entry;
            }

            void AddFieldToContainer(string fieldName, string shortLabel, double defaultVal)
            {
                var fieldLabel = new Label
                {
                    Text = shortLabel,
                    FontSize = 11,
                    VerticalOptions = LayoutOptions.Center
                };
                var entry = CreateField(fieldName, shortLabel, defaultVal);
                entryContainer.Children.Add(fieldLabel);
                entryContainer.Children.Add(entry);
            }

            switch (mode)
            {
                case PositionTupleMode.XY:
                    AddFieldToContainer("X", "X", defaultValue.X);
                    AddFieldToContainer("Y", "Y", defaultValue.Y);
                    break;
                case PositionTupleMode.XYWH:
                    AddFieldToContainer("X", "X", defaultValue.X);
                    AddFieldToContainer("Y", "Y", defaultValue.Y);
                    AddFieldToContainer("W", "W", defaultValue.W);
                    AddFieldToContainer("H", "H", defaultValue.H);
                    break;
                case PositionTupleMode.XW:
                    AddFieldToContainer("X", "X", defaultValue.X);
                    AddFieldToContainer("W", "W", defaultValue.W);
                    break;
                case PositionTupleMode.WH:
                    AddFieldToContainer("W", "W", defaultValue.W);
                    AddFieldToContainer("H", "H", defaultValue.H);
                    break;
            }

            var grid = new Grid
            {
                ColumnDefinitions = CreateTwoColumnDefinitions(),
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto }
                },
                Padding = DefaultPadding
            };
            grid.Children.Add(label);
            grid.Children.Add(entryContainer);
            Grid.SetColumn(entryContainer, 1);

            children.Add(grid);
            return this;
        }

        /// <summary>
        /// Adds a separate line (based on <seealso cref="BoxView"/>) to the property panel.
        /// </summary>
        public PropertyPanelBuilder AddSeparator() => AddSeparator(null, "");
        /// <summary>
        /// Adds a separate line (based on <seealso cref="BoxView"/>) to the property panel.
        /// </summary>
        public PropertyPanelBuilder AddSeparator(Action<BoxView>? BoxViewSetter = null, string id = "")
        {
            var boxView = new BoxView
            {
                HeightRequest = 1,
                BackgroundColor = Colors.Gray,
                HorizontalOptions = LayoutOptions.Fill
            };
            BoxViewSetter?.Invoke(boxView);
            if (!string.IsNullOrWhiteSpace(id)) Components.Add(id, boxView);
            children.Add(boxView);
            return this;
        }

        /// <summary>
        /// Adds a collapsible section with a tappable header that toggles the visibility of the content area.
        /// </summary>
        /// <param name="headerText">Header text for the collapsible section.</param>
        /// <param name="contentBuilder">Action to populate the content area using a nested builder.</param>
        /// <param name="defaultExpanded">Whether the section starts expanded. Default false (collapsed).</param>
        public PropertyPanelBuilder AddCollapsibleSection(
            string headerText,
            Action<PropertyPanelBuilder> contentBuilder,
            bool defaultExpanded = false)
        {
            var chevron = new Label
            {
                Text = defaultExpanded ? "▼" : "▶",
                FontSize = 11,
                VerticalTextAlignment = TextAlignment.Center,
                VerticalOptions = LayoutOptions.Center
            };

            var headerLabel = new Label
            {
                Text = headerText,
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                VerticalOptions = LayoutOptions.Center
            };

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = 6,
                Padding = new Thickness(0, 4)
            };
            headerGrid.Children.Add(chevron);
            headerGrid.Children.Add(headerLabel);
            Grid.SetColumn(headerLabel, 1);

            var contentPanel = new PropertyPanelBuilder();
            contentBuilder(contentPanel);
            var contentLayout = contentPanel.Build();
            contentLayout.IsVisible = defaultExpanded;
            contentLayout.Padding = new Thickness(12, 0, 0, 0);

            // Forward PropertyChanged events from the content panel to the parent
            contentPanel.PropertyChanged += (s, e) => PropertyChanged?.Invoke(this, e);

            var tapGesture = new TapGestureRecognizer();
            tapGesture.Tapped += (s, e) =>
            {
                contentLayout.IsVisible = !contentLayout.IsVisible;
                chevron.Text = contentLayout.IsVisible ? "▼" : "▶";
            };
            headerGrid.GestureRecognizers.Add(tapGesture);

            var wrapper = new VerticalStackLayout
            {
                Spacing = 4,
                Padding = new Thickness(0, 4, 0, 0)
            };
            wrapper.Children.Add(headerGrid);
            wrapper.Children.Add(contentLayout);

            children.Add(wrapper);
            return this;
        }

        /// <summary>
        /// Adds a <seealso cref="Button"/> with an associated label to the property panel.
        /// </summary>
        /// <remarks>
        /// Please note that <see cref="PropertyChanged"/> will be triggered, and <see cref="pppcea.Value"/> and <see cref="pppcea.OriginValue"/> will be <see langword="null"/> when you click on the button.
        /// </remarks>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        public PropertyPanelBuilder AddButton(string Id, string buttonText, Action<Button>? ButtonSetter = null)
        {
            var button = new Button
            {
                Text = buttonText,
                HorizontalOptions = LayoutOptions.Fill
            };
            //var label = title.LabelConfigurer();
            Properties[Id] = null!;
            ButtonSetter?.Invoke(button);
            button.Clicked += (s, e) => pppcea.CreateAndInvoke(this, Id, null!);
            children.Add(button);
            Components.Add(Id, button);
            return this;
        }

        /// <summary>
        /// Adds a simple <seealso cref="Button"/> which not use <see cref="PropertyPanelBuilder"/>'s event processing system.
        /// </summary>
        /// <remarks>
        /// Please note that <see cref="PropertyChanged"/> will NEVER be triggered, instead, you should handle <paramref name="OnClick"/> to do your own logic.
        /// </remarks>
        public PropertyPanelBuilder AddButton(string buttonText, EventHandler OnClick, Action<Button>? ButtonSetter = null)
        {
            var Id = Guid.NewGuid().ToString();
            var button = new Button
            {
                Text = buttonText,
                HorizontalOptions = LayoutOptions.Fill
            };
            Properties[Id] = null!;
            ButtonSetter?.Invoke(button);
            button.Clicked += OnClick;
            children.Add(button);
            Components.Add(Id, button);
            return this;
        }

        /// <summary>
        /// Adds a card-like option row: left icon + right (title + description), similar to the screenshot.
        /// </summary>
        /// <remarks>
        /// - By default this view is tappable and will trigger <see cref="PropertyChanged"/> with <paramref name="tappedValue"/>.
        /// - Use the setter callbacks to style borders/spacing to match your theme.
        /// </remarks>
        /// <param name="Id">The unique identifier for the property associated with this card. Cannot be null.</param>
        /// <param name="icon">Icon image source shown on the left.</param>
        /// <param name="title">Main title (first line).</param>
        /// <param name="description">Secondary description (second line).</param>
        /// <param name="defaultValue">Initial value stored in <see cref="Properties"/> for <paramref name="Id"/>.</param>
        /// <param name="tappedValue">Value sent when tapped. If null, will fall back to <paramref name="defaultValue"/>; if still null, uses new object().</param>
        /// <param name="invokeOnTap">Whether tapping triggers <see cref="PropertyChanged"/> via the unified event mechanism.</param>
        public PropertyPanelBuilder AddIconTitleDescriptionCard(
            string Id,
            ImageSource icon,
            string title,
            string description,
            object? defaultValue = null,
            object? tappedValue = null,
            bool invokeOnTap = true,
            Action<Border>? CardSetter = null,
            Action<Border>? IconContainerSetter = null,
            Action<Image>? IconSetter = null,
            Action<Label>? TitleSetter = null,
            Action<Label>? DescriptionSetter = null)
        {
            var iconImage = new Image
            {
                Source = icon,
                Aspect = Aspect.AspectFit,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            };
            IconSetter?.Invoke(iconImage);

            var iconContainer = new Border
            {
                Content = iconImage,
                Padding = new Thickness(8),
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Center
            };
            IconContainerSetter?.Invoke(iconContainer);

            var titleLabel = new Label
            {
                Text = title,
                FontAttributes = FontAttributes.Bold,
                VerticalOptions = LayoutOptions.Center
            };
            TitleSetter?.Invoke(titleLabel);

            var descriptionLabel = new Label
            {
                Text = description,
                FontSize = 12,
                VerticalOptions = LayoutOptions.Center
            };
            DescriptionSetter?.Invoke(descriptionLabel);

            var textStack = new VerticalStackLayout
            {
                Spacing = 2,
                VerticalOptions = LayoutOptions.Center,
                Children = { titleLabel, descriptionLabel }
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Star }
                },
                ColumnSpacing = 12,
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto }
                }
            };
            grid.Children.Add(iconContainer);
            grid.Children.Add(textStack);
            Grid.SetColumn(textStack, 1);

            var card = new Border
            {
                Content = grid,
                Padding = new Thickness(12),
                Margin = DefaultPadding
            };
            CardSetter?.Invoke(card);

            Properties[Id] = defaultValue!;
            var effectiveTappedValue = tappedValue ?? defaultValue ?? new object();
            if (invokeOnTap)
            {
                var tap = new TapGestureRecognizer();
                tap.Tapped += (s, e) => pppcea.CreateAndInvoke(this, Id, effectiveTappedValue);
                card.GestureRecognizers.Add(tap);
            }

            children.Add(card);
            Components.Add(Id, card);
            return this;
        }

        public PropertyPanelBuilder AddChildrensInALine(Action<PropertyPanelChildrenBuilder> childrenMaker, string id = "")
        {
            var cb = new PropertyPanelChildrenBuilder(this);
            childrenMaker(cb);
            if (!string.IsNullOrWhiteSpace(id)) Components.Add(id, cb.ToHorizentalLayout());
            children.Add(cb.ToVerticalLayout());
            return this;
        }

        public PropertyPanelBuilder AddChildrensInALine(PropertyPanelItemLabel title, Func<PropertyPanelChildrenBuilder, PropertyPanelChildrenBuilder> childrenMaker)
        {
            var cb = new PropertyPanelChildrenBuilder(this);
            AddCustomChild(title, childrenMaker(cb).ToHorizentalLayout());
            return this;
        }

        /// <summary>
        /// Adds a custom child view to the property panel layout.
        /// </summary>
        /// <remarks>
        /// If you'd like to add a Child that modify the <see cref="Properties"/>, 
        /// please use <seealso cref="AddCustomChild(Func{Action{object}, View}, string, object)"/>, 
        /// which provides a easy-use to modify <see cref="Properties"/> call <see cref="PropertyChanged"/> safely.
        /// </remarks>
        /// <param name="child">The view to add as a child to the property panel.</param>
        public PropertyPanelBuilder AddCustomChildWithID(string id, View child)
        {
            if (!string.IsNullOrWhiteSpace(id)) Components.Add(id, child);
            children.Add(child);
            return this;
        }
        /// <summary>
        /// Adds a custom child view to the property panel layout.
        /// </summary>
        /// <remarks>
        /// If you'd like to add a Child that modify the <see cref="Properties"/>, 
        /// please use <seealso cref="AddCustomChild(Func{Action{object}, View}, string, object)"/>, 
        /// which provides a easy-use to modify <see cref="Properties"/> call <see cref="PropertyChanged"/> safely.
        /// </remarks>
        /// <param name="child">The view to add as a child to the property panel.</param>
        public PropertyPanelBuilder AddCustomChild(View child)
        {
            children.Add(child);
            return this;
        }

        /// <summary>
        /// Almost same to <see cref="AddCustomChild(View)"/>, but with an associated label.
        /// </summary>
        public PropertyPanelBuilder AddCustomChild(PropertyPanelItemLabel title, View child, string id = "")
        {
            var label = title.LabelConfigure();

            var grid = new Grid
            {
                ColumnDefinitions = CreateTwoColumnDefinitions(),
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto }
                },
                Padding = DefaultPadding
            };
            grid.Children.Add(label);
            grid.Children.Add(child);
            Grid.SetColumn(child, 1);
            children.Add(grid);
            if (!string.IsNullOrWhiteSpace(id)) Components.Add(id, child);
            return this;
        }

        /// <summary>
        /// Adds a custom child view to the property panel and associates it with a property identified by the specified
        /// ID and default value.
        /// <code>
        /// Use it like this:
        /// ppb.AddCustomChild((invoker) => 
        /// {
        ///     var entry = new Entry 
        ///     {
        ///         Text = "...",
        ///         //...
        ///     }
        ///     
        ///     entry.TextChanged += (s, e) => invoker(e.NewTextValue);
        /// },
        /// "sampleEntry","text");
        /// </code>
        /// </summary>
        /// <param name="maker">
        /// A delegate that creates and returns a custom child view.
        /// <paramref name="maker"/>'s first argument is the <see cref="PropertyPanelBuilder"/>, 
        /// is used for target View's BindingContext to support an automatic-build of <see cref="PropertyChangingEventArgs"/>.
        /// 
        /// Second one is the method to invoke <see cref="PropertyChanged"/> event, and the arg will be the new value (<see cref="pppcea.Value"/>).
        /// </param>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        /// <param name="defaultValue">The default value to assign to the property identified by <paramref name="Id"/>.</param>
        public PropertyPanelBuilder AddCustomChild(Func<Action<object>, View> maker, string Id, object defaultValue)
        {
            var view = maker((o) => pppcea.CreateAndInvoke(this, Id, o));
            Components.Add(Id, view);
            children.Add(view);
            Properties[Id] = defaultValue;
            return this;
        }

        /// <summary>
        /// Almost same to <see cref="AddCustomChild(Func{Action{object}, View}, string, object)"/>, but with an associated label.
        /// <code>
        /// Use it like this:
        /// ppb.AddCustomChild("A sample entry", (invoker) => 
        /// {
        ///     var entry = new Entry 
        ///     {
        ///         Text = "...",
        ///         //...
        ///     }
        ///     
        ///     entry.TextChanged += (s, e) => invoker(e.NewTextValue);
        /// )
        /// </code>
        /// </summary>
        public PropertyPanelBuilder AddCustomChild(PropertyPanelItemLabel title, Func<Action<object>, View> maker, string Id, object defaultValue)
        {
            var child = maker((o) => pppcea.CreateAndInvoke(this, Id, o));
            Properties[Id] = defaultValue;
            var label = title.LabelConfigure();
            var grid = new Grid
            {
                ColumnDefinitions = CreateTwoColumnDefinitions(),
                RowDefinitions = new RowDefinitionCollection
                {
                    new RowDefinition { Height = GridLength.Auto }
                },
                Padding = DefaultPadding
            };
            grid.Children.Add(label);
            grid.Children.Add(child);
            Grid.SetColumn(child, 1);
            children.Add(grid); Components.Add(Id, child);
            return this;
        }

        /// <summary>
        /// Imports all property panel items from another builder to the current builder. 
        /// The items from <paramref name="another"/> are appended to the end of the current
        /// builder's collection. 
        /// </summary>
        /// <remarks>
        /// The method will modify the source builder because of one View can't appearing in 2 containers.
        /// This method also clone the source's <see cref="PropertyChanged"/> event.
        /// </remarks>
        /// <param name="another">The builder whose property panel items will be added to this builder. Cannot be null.</param>
        public PropertyPanelBuilder AddFromAnother(PropertyPanelBuilder another)
        {
            foreach (var item in another.children)
            {
                AddCustomChild(item);
            }
            another.valid = false;
            another.PropertyChanged += (_, e) => PropertyChanged?.Invoke(another, e);
            return this;

        }
        /// <summary>
        /// Imports all property panel items from another builder to the current builder. 
        /// The items from <paramref name="another"/> are appended to the end of the current
        /// builder's collection. 
        /// </summary>
        /// <remarks>
        /// The method will modify the source builder because of one View can't appearing in 2 containers.
        /// This method also clone the source's <see cref="PropertyChanged"/> event but overrides the sender object to <paramref name="anotherSender"/>.
        /// </remarks>
        /// <param name="another">The builder whose property panel items will be added to this builder. Cannot be null.</param>
        /// <param name="anotherSender">The sender object to be used when invoking the <see cref="PropertyChanged"/> event from the <paramref name="another"/> builder.</param>
        public PropertyPanelBuilder AddFromAnother(PropertyPanelBuilder another, object? anotherSender)
        {
            foreach (var item in another.children)
            {
                AddCustomChild(item);
            }
            another.valid = false;
            another.PropertyChanged += (_, e) => PropertyChanged?.Invoke(anotherSender, e);
            return this;

        }

        /// <summary>
        /// Replaces a registered component view while preserving its container position when possible.
        /// </summary>
        /// <param name="id">The component identifier previously registered in <see cref="Components"/>.</param>
        /// <param name="replacement">The new view instance to place into the panel.</param>
        /// <returns><see langword="true"/> if the component was found and replaced; otherwise <see langword="false"/>.</returns>
        public bool ReplaceComponent(string id, View replacement)
        {
            if (string.IsNullOrWhiteSpace(id) || replacement is null)
                return false;

            if (!Components.TryGetValue(id, out var original))
                return false;

            for (int i = 0; i < children.Count; i++)
            {
                if (ReferenceEquals(children[i], original))
                {
                    children[i] = replacement;
                    Components[id] = replacement;
                    return true;
                }

                if (children[i] is Grid grid && grid.Children.Contains(original))
                {
                    var row = Grid.GetRow(original);
                    var column = Grid.GetColumn(original);
                    var rowSpan = Grid.GetRowSpan(original);
                    var columnSpan = Grid.GetColumnSpan(original);

                    grid.Remove(original);
                    grid.Add(replacement);
                    Grid.SetRow(replacement, row);
                    Grid.SetColumn(replacement, column);
                    Grid.SetRowSpan(replacement, rowSpan);
                    Grid.SetColumnSpan(replacement, columnSpan);
                    Components[id] = replacement;
                    return true;
                }
            }

            return false;
        }



        /// <summary>
        /// Listens to property changes on the property panel. Same as subscribing to <see cref="PropertyChanged"/> event.
        /// </summary>
        public PropertyPanelBuilder ListenToChanges(Action<pppcea> handler)
        {
            PropertyChanged += (s, e) => handler(e);
            return this;
        }

        /// <summary>
        /// Listens to property changes on the property panel. Same as subscribing to <see cref="PropertyChanged"/> event.
        /// </summary>
        public PropertyPanelBuilder ListenToChanges(EventHandler<pppcea> handler)
        {
            PropertyChanged += handler;
            return this;
        }

        /// <summary>
        /// Appends child items conditionally.
        /// </summary>
        public PropertyPanelBuilder AppendWhen(bool condition, Action<PropertyPanelBuilder> onTrue)
        {
            if (condition)
            {
                onTrue(this);
            }
            return this;
        }

        /// <summary>
        /// Appends child items conditionally.
        /// </summary>
        public PropertyPanelBuilder AppendWhen(bool condition, Action<PropertyPanelBuilder> onTrue, Action<PropertyPanelBuilder> onFalse)
        {
            if (condition)
            {
                onTrue(this);
            }
            else
            {
                onFalse(this);
            }
            return this;
        }

        /// <summary>
        /// add the first item which condition is true, and skip the rest. If all conditions are false, do nothing.
        /// </summary>
        public PropertyPanelBuilder AppendWhen(params (Func<bool> condition, Action<PropertyPanelBuilder> onTrue)[] conditions)
        {
            foreach (var (condition, onTrue) in conditions)
            {
                if (condition())
                {
                    onTrue(this);
                    return this;
                }
            }
            return this;
        }


        /// <summary>
        /// Appends all conditionally. For each condition, if it's true, execute the corresponding onTrue action; otherwise, execute the onFalse action.
        /// </summary>
        public PropertyPanelBuilder AppendWhen(params (Func<bool> condition, Action<PropertyPanelBuilder>? onTrue, Action<PropertyPanelBuilder>? onFalse)[] conditions)
        {
            foreach (var (condition, onTrue, onFalse) in conditions)
            {
                if (condition()) onTrue?.Invoke(this);
                else onFalse?.Invoke(this);
            }
            return this;
        }

        /// <summary>
        /// Append each item in the source collection.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public PropertyPanelBuilder Foreach<T>(IEnumerable<T> source, Action<PropertyPanelBuilder, T> appender)
        {
            foreach (var item in source)
            {
                appender(this, item);
            }
            return this;
        }

        /// <summary>
        /// Get the final <seealso cref="VerticalStackLayout"/> of the panel created by this builder.
        /// </summary>
        public Layout Build()
        {
            if(!valid) throw new InvalidOperationException($"This PropertyPanel is no longer valid because it has been merged into another PropertyPanelBuilder instance.");
            var layout = new VerticalStackLayout
            {
                Spacing = 10,
                Padding = new Thickness(10)
            };
            foreach (var item in children)
            {
                layout.Children.Add(item);
            }
            return layout;
        }

        /// <summary>
        /// Get the final <seealso cref="Layout"/> of the panel created by this builder.
        /// </summary>
        /// <param name="layout">The source layout you'd like to use.</param>
        public Layout Build(Layout layout)
        {
            if (!valid) throw new InvalidOperationException($"This PropertyPanel is no longer valid because it has been merged into another PropertyPanelBuilder instance.");
            foreach (var item in children)
            {
                layout.Children.Add(item);
            }
            return layout;
        }

        public ScrollView BuildWithScrollView(Action<ScrollView>? Configurer = null)
        {
            var scrollView = new ScrollView
            {
                Content = Build(),
            };
            Configurer?.Invoke(scrollView);
            return scrollView;
        }

        internal void _InvokeInternal(pppcea e)
        {
            PropertyChanged?.Invoke(this, e);
        }

        public override bool Equals(object? obj)
        {
            if (obj is PropertyPanelBuilder ppb) return ppb.instanceID == instanceID;
            return false;
        }

        public override int GetHashCode() => instanceID.GetHashCode();
    }


    public class PropertyPanelPropertyChangedEventArgs(string id, object? newVal, object? oldVal) : EventArgs
    {
        /// <summary>
        /// Gets the unique identifier for the changed child.
        /// </summary>
        public string Id { get; set; } = id;
        /// <summary>
        /// The new value of the child. In most cases this shouldn't be null.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.NotNull]
        public object? Value { get; set; } = newVal;
        /// <summary>
        /// The origin value of the child. This may be null, or the default value provided by <see cref="PropertyPanelBuilder"/> if this event is triggered for the first time.
        /// </summary>
        public object? OriginValue { get; set; } = oldVal;

        /// <summary>
        /// Manually invokes the <see cref="PropertyPanelBuilder.PropertyChanged"/> event on the specified <see cref="PropertyPanelBuilder"/> instance.
        /// </summary>
        /// <remarks>
        /// It's not recommended to call this method directly. 
        /// Instead, use the provided mechanisms in the <see cref="PropertyPanelBuilder"/> class to trigger property change events.
        /// </remarks>
        /// <param name="b">source <see cref="PropertyPanelBuilder"/> for the target.</param>
        /// <param name="id">ID of the child</param>
        /// <param name="value">the new value</param>
        public static void CreateAndInvoke(PropertyPanelBuilder b, string id, object value)
        {
            var e = new pppcea(id, value, b.Properties.TryGetValue(id, out var val) ? val : null);
            b._InvokeInternal(e);
            b.Properties[id] = value;
        }
        /// <summary>
        /// Manually invokes the <see cref="PropertyPanelBuilder.PropertyChanged"/> event on the specified <see cref="PropertyPanelBuilder"/> instance.
        /// </summary>
        /// <remarks>
        /// It's not recommended to call this method directly. 
        /// Instead, use the provided mechanisms in the <see cref="PropertyPanelBuilder"/> class to trigger property change events.
        /// </remarks>
        /// <param name="s">source <see cref="PropertyPanelBuilder"/> for the target.</param>
        /// <param name="e">The <see cref="pppcea"/> message body.</param>
        public static void CreateAndInvoke(PropertyPanelBuilder s, pppcea e)
        {
            s._InvokeInternal(e);
            s.Properties[e.Id] = e.Value;
        }

    }

    public class PropertyPanelBuilderComparer : IEqualityComparer<PropertyPanelBuilder>
    {
        public bool Equals(PropertyPanelBuilder? x, PropertyPanelBuilder? y)
        {
            if (x is null && y is null) return true;
            if (x is null || y is null) return false;
            return x.Equals(y);
        }
        public int GetHashCode([DisallowNull] PropertyPanelBuilder obj)
        {
            return obj.GetHashCode();
        }
    }

    public enum EntryUpdateEventCallMode
    {
        OnAnyTextChange,
        OnUnfocused,
        OnUnfocusedAndValueChanged
    }

    public enum SliderUpdateEventCallMode
    {
        OnValueChanged,
        OnMouseUp
    }

    /// <summary>
    /// 指定位置元组输入框的模式，决定显示哪些子字段。
    /// </summary>
    public enum PositionTupleMode
    {
        /// <summary>X 和 Y 两个输入框（坐标位置）。</summary>
        XY,
        /// <summary>X, Y, Width, Height 四个输入框。</summary>
        XYWH,
        /// <summary>X 和 Width 两个输入框。</summary>
        XW,
        /// <summary>Width 和 Height 两个输入框。</summary>
        WH
    }

}
