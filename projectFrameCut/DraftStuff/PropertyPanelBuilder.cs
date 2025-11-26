using Microsoft.Maui;
using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
#pragma warning disable CS8981 // 该类型名称仅包含小写 ascii 字符。此类名称可能会成为该语言的保留值。
using pppcea = projectFrameCut.PropertyPanel.PropertyPanelPropertyChangedEventArgs;
using Switch = Microsoft.Maui.Controls.Switch; //make code shorter
#pragma warning restore CS8981 // 该类型名称仅包含小写 ascii 字符。此类名称可能会成为该语言的保留值。

namespace projectFrameCut.PropertyPanel
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

        //private VerticalStackLayout layout;

        private List<View> children = new();
        public Dictionary<string, View> Components { get; private init; } = new();

        /// <summary>
        /// Gets a collection of custom properties associated with the object.
        /// </summary>
        public Dictionary<string, object> Properties { get; } = new Dictionary<string, object>();

        /// <summary>
        /// Get or set the default ratio of length of the content area (the second column) by their labels (the first column).
        /// </summary>
        public double WidthOfContent { get; set; } = 5;

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
            childBuilder = new PropertyPanelChildrenBuilder(this);
            //layout = new VerticalStackLayout
            //{
            //    Spacing = 10,
            //    Padding = new Thickness(10)
            //};
        }

        /// <summary>
        /// Adds a <seealso cref="Label"/> to the property panel.
        /// </summary>
        public PropertyPanelBuilder AddText(string content, string Id = "",double fontSize = 14, FontAttributes fontAttributes = FontAttributes.None)
        {
            var label = new Label
            {
                Text = content,
                FontSize = fontSize,
                FontAttributes = fontAttributes
            };
            if(!string.IsNullOrWhiteSpace(Id)) Components.Add(Id, label);
            children.Add(label);
            return this;
        }

        public PropertyPanelBuilder AddText(PropertyPanelItemLabel label, string Id = "")
        {
            var l = label.LabelConfigurer();
            if (!string.IsNullOrWhiteSpace(Id)) Components.Add(Id, l);
            children.Add(l);
            return this;
        }

        /// <summary>
        /// Adds a text input box (<seealso cref="Entry"/>) with an associated label to the property panel.
        /// </summary>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        /// <param name="defaultValue">The default value to assign to the property identified by <paramref name="Id"/>.</param>
        public PropertyPanelBuilder AddEntry(string Id, PropertyPanelItemLabel title, string defaultValue, string placeholder, Action<Entry>? EntrySeter = null, EntryUpdateEventCallMode mode = EntryUpdateEventCallMode.OnUnfocusedAndUnchanged)
        {
            var entry = new Entry
            {
                Placeholder = placeholder,
                Text = defaultValue,
                HorizontalOptions = LayoutOptions.Fill,
                BindingContext = this
            };
            var label = title.LabelConfigurer();

            Properties[Id] = defaultValue;
            switch (mode)
            {
                case EntryUpdateEventCallMode.OnAnyTextChange:
                    entry.TextChanged += (s, e) => pppcea.CreateAndInvoke(this, Id, e.NewTextValue);
                    break;
                case EntryUpdateEventCallMode.OnUnfocusedAndUnchanged:
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
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(WidthOfContent, GridUnitType.Star) }
                },
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
            var label = title.LabelConfigurer();
            Properties[Id] = defaultValue;
            checkbox.CheckedChanged += (s, e) => pppcea.CreateAndInvoke(this, Id, e.Value);
            CheckboxSetter?.Invoke(checkbox);
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(WidthOfContent, GridUnitType.Star) }
                },
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
            var label = title.LabelConfigurer();
            Properties[Id] = defaultValue;
            swtch.Toggled += (s, e) => pppcea.CreateAndInvoke(this, Id, e.Value);
            SwitchSetter?.Invoke(swtch);
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(WidthOfContent, GridUnitType.Star) }
                },
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

            var label = title.LabelConfigurer();
            Properties[Id] = defaultOne;
            picker.SelectedIndexChanged += (s, e) => pppcea.CreateAndInvoke(this, Id, picker.SelectedItem as string);
            PickerSetter?.Invoke(picker);
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(WidthOfContent, GridUnitType.Star) }
                },
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
        public PropertyPanelBuilder AddSlider(string Id, PropertyPanelItemLabel title, double min, double max, double defaultValue, Action<Slider>? SliderSetter = null)
        {
            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = defaultValue,
                HorizontalOptions = LayoutOptions.FillAndExpand,
                BindingContext = this
            };
            var label = title.LabelConfigurer();

            Properties[Id] = defaultValue;
            slider.ValueChanged += (s, e) => pppcea.CreateAndInvoke(this, Id, e.NewValue);
            SliderSetter?.Invoke(slider);

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(WidthOfContent, GridUnitType.Star) }
                },
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
            //var grid = new Grid
            //{
            //    ColumnDefinitions = new ColumnDefinitionCollection
            //    {
            //        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            //        new ColumnDefinition { Width = new GridLength(WidthOfContent, GridUnitType.Star) }
            //    },
            //    RowDefinitions = new RowDefinitionCollection
            //    {
            //        new RowDefinition { Height = GridLength.Auto }
            //    },
            //    Padding = DefaultPadding
            //};
            //grid.Children.Add(label);
            //grid.Children.Add(button);
            //Grid.SetColumn(button, 1);
            children.Add(button); 
            Components.Add(Id, button);
            return this;
        }

        public PropertyPanelBuilder AddChildrensInALine(Action<PropertyPanelChildrenBuilder> childrenMaker, string id = "")
        {
            var cb = new PropertyPanelChildrenBuilder(this);
            childrenMaker(cb);
            if(!string.IsNullOrWhiteSpace(id)) Components.Add(id, cb.ToHorizentalLayout());
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
        /// which provides a eazy-use to modify <see cref="Properties"/> call <see cref="PropertyChanged"/> safely.
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
            var label = title.LabelConfigurer();

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(WidthOfContent, GridUnitType.Star) }
                },
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
            if(!string.IsNullOrWhiteSpace(id)) Components.Add(id, child);
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
            var label = title.LabelConfigurer();
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(WidthOfContent, GridUnitType.Star) }
                },
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
            
            another.PropertyChanged += (_, e) => PropertyChanged?.Invoke(another, e);
            return this;

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
        /// Get the final <seealso cref="VerticalStackLayout"/> of the panel created by this builder.
        /// </summary>
        public Layout Build()
        {
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
            foreach (var item in children)
            {
                layout.Children.Add(item);
            }
            return layout;
        }

        internal void _InvokeInternal(pppcea e)
        {
            PropertyChanged?.Invoke(this, e);
        }
    }

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
            addChild(label.LabelConfigurer(), width);
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
        /// Creates and invokes a <see cref="PropertyPanelBuilder.PropertyChanged"/> event on the specified <see cref="PropertyPanelBuilder"/> instance.
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

    }

    public enum EntryUpdateEventCallMode
    {
        OnAnyTextChange,
        OnUnfocusedAndUnchanged
    }

    public class SingleLineLabel(string text, int fontsize = 14, FontAttributes fontAttributes = FontAttributes.None) : PropertyPanelItemLabel
    {
        public override View LabelConfigurer() => new Label { Text = text, FontSize = fontsize, FontAttributes = fontAttributes, VerticalOptions = LayoutOptions.Center };

        public static implicit operator SingleLineLabel(string text) => new SingleLineLabel(text);
    }

    public class TitleAndDescriptionLineLabel(string title, string description, int titleFontSize = 20, int contentFontSize = 12) : PropertyPanelItemLabel
    {
        public override View LabelConfigurer() => new VerticalStackLayout
        {
            Children =
            {
                new Label { Text = title, FontSize = titleFontSize, FontAttributes = FontAttributes.Bold },
                new Label { Text = description, FontSize = contentFontSize }
            }
        };
    }

    public class PropertyPanelItemLabel
    {
        private View? _view;

        public PropertyPanelItemLabel() { }
        public PropertyPanelItemLabel(View v) => _view = v;
        public virtual View LabelConfigurer() => _view ?? throw new NullReferenceException("Trying to set a null label.");

        public static implicit operator PropertyPanelItemLabel(string text) => new SingleLineLabel(text);

        public static implicit operator PropertyPanelItemLabel(Label src) => new PropertyPanelItemLabel { _view = src };
    }



}
