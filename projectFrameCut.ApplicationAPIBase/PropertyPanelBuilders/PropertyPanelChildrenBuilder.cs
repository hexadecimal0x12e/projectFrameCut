using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
#pragma warning disable CS8981 // 该类型名称仅包含小写 ascii 字符。此类名称可能会成为该语言的保留值。
using pppcea = projectFrameCut.ApplicationAPIBase.PropertyPanelBuilders.PropertyPanelPropertyChangedEventArgs;
using Switch = Microsoft.Maui.Controls.Switch; //make code shorter
#pragma warning restore CS8981 // 该类型名称仅包含小写 ascii 字符。此类名称可能会成为该语言的保留值。


namespace projectFrameCut.ApplicationAPIBase.PropertyPanelBuilders
{
    [DebuggerNonUserCode()]
    public class PropertyPanelChildrenBuilder
    {
        private List<Tuple<View, GridLength>> _children = new();

        private void addChild(View v, GridLength? width)
        {
            _children.Add(new Tuple<View, GridLength>(v, width ?? GridLength.Auto));
        }

        private readonly PropertyPanelBuilder parent;

        public PropertyPanelChildrenBuilder(PropertyPanelBuilder _parent)
        {
            parent = _parent;
        }

        public PropertyPanelChildrenBuilder AddText(string content, double fontSize = 14, GridLength? width = null, FontAttributes fontAttributes = FontAttributes.None)
        {
            var label = new Label
            {
                Text = content,
                FontSize = fontSize,
                FontAttributes = fontAttributes
            };
            addChild(label, width);
            return this;
        }

        public PropertyPanelChildrenBuilder AddText(PropertyPanelItemLabel label, GridLength? width = null)
        {
            addChild(label.LabelConfigure(), width);
            return this;
        }

        /// <summary>
        /// Adds a text input box (<seealso cref="Entry"/>) with an associated label to the property panel.
        /// </summary>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        /// <param name="defaultValue">The default value to assign to the property identified by <paramref name="Id"/>.</param>
        public PropertyPanelChildrenBuilder AddEntry(string Id, string defaultValue, string placeholder, GridLength? width = null, Action<Entry>? EntrySeter = null)
        {
            var entry = new Entry
            {
                Placeholder = placeholder,
                Text = defaultValue,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                BindingContext = this
            };
            EntrySeter?.Invoke(entry);
            parent.Properties[Id] = defaultValue;

            entry.TextChanged += (s, e) => pppcea.CreateAndInvoke(parent, Id, e.NewTextValue);
            EntrySeter?.Invoke(entry);
            addChild(entry, width);
            return this;
        }

        /// <summary>
        /// Adds a 2-state <seealso cref="CheckBox"/> with an associated label to the property panel.
        /// </summary>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        /// <param name="defaultValue">The default value to assign to the property identified by <paramref name="Id"/>.</param>
        public PropertyPanelChildrenBuilder AddCheckbox(string Id, bool defaultValue, GridLength? width = null, Action<CheckBox>? CheckboxSetter = null, Action<Label>? LabelSetter = null)
        {
            var checkbox = new CheckBox
            {
                IsChecked = defaultValue,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                BindingContext = this
            };
            CheckboxSetter?.Invoke(checkbox);
            parent.Properties[Id] = defaultValue;
            checkbox.CheckedChanged += (s, e) => pppcea.CreateAndInvoke(parent, Id, e.Value);
            CheckboxSetter?.Invoke(checkbox);
            addChild(checkbox, width);
            return this;
        }
        /// <summary>
        /// Adds a <seealso cref="Switch"/> with an associated label to the property panel.
        /// </summary>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        /// <param name="defaultValue">The default value to assign to the property identified by <paramref name="Id"/>.</param>
        public PropertyPanelChildrenBuilder AddSwitch(string Id, bool defaultValue, GridLength? width = null, Action<Switch>? SwitchSetter = null)
        {
            var swtch = new Switch
            {
                IsToggled = defaultValue,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                BindingContext = this
            };
            SwitchSetter?.Invoke(swtch);
            parent.Properties[Id] = defaultValue;
            swtch.Toggled += (s, e) => pppcea.CreateAndInvoke(parent, Id, e.Value);
            SwitchSetter?.Invoke(swtch);

            addChild(swtch, width);
            return this;
        }

        /// <summary>
        /// Adds a <seealso cref="Slider"/> with an associated label to the property panel.
        /// </summary>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        /// <param name="defaultValue">The default value to assign to the property identified by <paramref name="Id"/>.</param>
        public PropertyPanelChildrenBuilder AddSlider(string Id, double min, double max, double defaultValue, GridLength? width = null, Action<Slider>? SliderSetter = null)
        {
            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = defaultValue,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                BindingContext = this
            };
            SliderSetter?.Invoke(slider);
            parent.Properties[Id] = defaultValue;
            slider.ValueChanged += (s, e) => pppcea.CreateAndInvoke(parent, Id, e.NewValue);
            SliderSetter?.Invoke(slider);

            addChild(slider, width);
            return this;
        }

        /// <summary>
        /// Adds a separate line (based on <seealso cref="BoxView"/>) to the property panel.
        /// </summary>
        public PropertyPanelChildrenBuilder AddSeparator(GridLength? width = null, Action<BoxView>? BoxViewSetter = null)
        {
            var boxView = new BoxView
            {
                HeightRequest = 1,
                BackgroundColor = Colors.Gray,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };
            BoxViewSetter?.Invoke(boxView);
            addChild(boxView, width);
            return this;
        }

        /// <summary>
        /// Adds a <seealso cref="Button"/> with an associated label to the property panel.
        /// </summary>
        /// <remarks>
        /// Please note that PropertyChanged will be triggered with a meaningless <see cref="pppcea.Value"/> and <see cref="pppcea.OriginValue"/> (both are new object()) when you click on the button.
        /// </remarks>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        public PropertyPanelChildrenBuilder AddButton(string Id, string buttonText, GridLength? width = null, Action<Button>? ButtonSetter = null)
        {
            var button = new Button
            {
                Text = buttonText,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };
            ButtonSetter?.Invoke(button);
            parent.Properties[Id] = new();
            ButtonSetter?.Invoke(button);
            button.Clicked += (s, e) => pppcea.CreateAndInvoke(parent, Id, new());

            addChild(button, width);
            return this;
        }

        /// <summary>
        /// Adds a card-like option row (icon + title/description) as a child in this line builder.
        /// </summary>
        public PropertyPanelChildrenBuilder AddIconTitleDescriptionCard(
            string Id,
            ImageSource icon,
            string title,
            string description,
            object? defaultValue = null,
            object? tappedValue = null,
            bool invokeOnTap = true,
            GridLength? width = null,
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
                Padding = new Thickness(12)
            };
            CardSetter?.Invoke(card);

            parent.Properties[Id] = defaultValue!;
            var effectiveTappedValue = tappedValue ?? defaultValue ?? new object();
            if (invokeOnTap)
            {
                var tap = new TapGestureRecognizer();
                tap.Tapped += (s, e) => pppcea.CreateAndInvoke(parent, Id, effectiveTappedValue);
                card.GestureRecognizers.Add(tap);
            }

            addChild(card, width);
            return this;
        }

        public PropertyPanelChildrenBuilder AddChild(View v, GridLength? width = null)
        {
            addChild(v, width);
            return this;
        }
        public View ToHorizentalLayout()
        {
            Grid views = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = GridLength.Star }
                },
                RowDefinitions = new RowDefinitionCollection()
            };
            foreach (var item in _children)
            {
                Grid.SetRow(item.Item1, views.RowDefinitions.Count);
                views.Add(item.Item1);
                views.RowDefinitions.Add(new RowDefinition { Height = item.Item2 });
            }
            return views;
        }

        public View ToVerticalLayout()
        {

            Grid views = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection(),
            };

            foreach (var item in _children)
            {
                Grid.SetColumn(item.Item1, views.ColumnDefinitions.Count);
                views.Add(item.Item1);
                views.ColumnDefinitions.Add(new ColumnDefinition { Width = item.Item2 });
            }


            return views;


        }
    }

}
