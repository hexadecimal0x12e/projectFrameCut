using Microsoft.Maui;
using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using pppcea = projectFrameCut.PropertyPanel.PropertyPanelPropertyChangedEventArgs; //make code shorter

namespace projectFrameCut.PropertyPanel
{
    /// <summary>
    /// Provides a builder pattern for creating a property panel layout with various UI elements such as labels, entries, checkboxes, switches, sliders, and any custom views.
    /// </summary>
    /// <remarks>
    /// This builder supports a uniform event-based property change notifications.
    /// </remarks>
    public class PropertyPanelBuilder
    {

        private VerticalStackLayout layout;

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
        /// Except <see cref="AddSeparator(Action{BoxView}?)"/>, <see cref="AddCustomChild(View)"/>, and <see cref="AddCustomChild(Func{PropertyPanelBuilder, Action{object}, View}, string, object)"/>.
        /// </remarks>
        public Thickness DefaultPadding { get; set; } = new Thickness(0, 8, 0, 0);


        /// <summary>
        /// Triggered when any property of the child items created by the preset creator changes, 
        /// or when they are added via <see cref="AddCustomChild(Func{PropertyPanelBuilder, Action{object}, View}, string, object)"/>, 
        /// provided you have correctly set up the target view's BindingContext and event invoker.        
        /// </summary>
        public event EventHandler<pppcea>? PropertyChanged;

        public PropertyPanelBuilder()
        {
            layout = new VerticalStackLayout
            {
                Spacing = 10,
                Padding = new Thickness(10)
            };
        }

        /// <summary>
        /// Adds a <seealso cref="Label"/> to the property panel.
        /// </summary>
        public PropertyPanelBuilder AddText(string content, double fontSize = 14, FontAttributes fontAttributes = FontAttributes.None)
        {
            var label = new Label
            {
                Text = content,
                FontSize = fontSize,
                FontAttributes = fontAttributes
            };
            layout.Children.Add(label);
            return this;
        }

        public PropertyPanelBuilder AddText(PropertyPanelItemLabel label)
        {
            layout.Children.Add(label.LabelConfigurer());
            return this;
        }

        /// <summary>
        /// Adds a text input box (<seealso cref="Entry"/>) with an associated label to the property panel.
        /// </summary>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        /// <param name="defaultValue">The default value to assign to the property identified by <paramref name="Id"/>.</param>
        public PropertyPanelBuilder AddEntry(string Id, PropertyPanelItemLabel title, string defaultValue, string placeholder, Action<Entry>? EntrySeter = null)
        {
            var entry = new Entry
            {
                Placeholder = placeholder,
                Text = defaultValue,
                HorizontalOptions = LayoutOptions.End,
                BindingContext = this
            };
            var label = title.LabelConfigurer();

            Properties[Id] = defaultValue;

            entry.TextChanged += (s, e) => pppcea.CreateAndInvoke(this, Id, e.NewTextValue);
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

            layout.Children.Add(grid);
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
            layout.Children.Add(grid);
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
                BindingContext = this
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
            layout.Children.Add(grid);
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
            layout.Children.Add(grid);
            return this;
        }

        /// <summary>
        /// Adds a separate line (based on <seealso cref="BoxView"/>) to the property panel.
        /// </summary>
        public PropertyPanelBuilder AddSeparator(Action<BoxView>? BoxViewSetter = null)
        {
            var boxView = new BoxView
            {
                HeightRequest = 1,
                BackgroundColor = Colors.Gray,
                HorizontalOptions = LayoutOptions.Fill
            };
            BoxViewSetter?.Invoke(boxView);
            layout.Children.Add(boxView);
            return this;
        }

        /// <summary>
        /// Adds a <seealso cref="Button"/> with an associated label to the property panel.
        /// </summary>
        /// <remarks>
        /// Please note that <see cref="PropertyChanged"/> will be triggered with a meaningless <see cref="pppcea.Value"/> and <see cref="pppcea.OriginValue"/> (both are new object()) when you click on the button.
        /// </remarks>
        /// <param name="Id">The unique identifier for the property associated with the custom child view. Cannot be null.</param>
        public PropertyPanelBuilder AddButton(string Id, PropertyPanelItemLabel title,  string buttonText, Action<Button>? ButtonSetter = null)
        {
            var button = new Button
            {
                Text = buttonText,
                HorizontalOptions = LayoutOptions.Fill
            };
            var label = title.LabelConfigurer();
            Properties[Id] = new();
            ButtonSetter?.Invoke(button);
            button.Clicked += (s, e) => pppcea.CreateAndInvoke(this, Id, new());
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
            grid.Children.Add(button);
            Grid.SetColumn(button, 1);
            layout.Children.Add(grid);
            return this;
        }

        /// <summary>
        /// Adds a custom child view to the property panel layout.
        /// </summary>
        /// <remarks>
        /// If you'd like to add a Child that modify the <see cref="Properties"/>, 
        /// please use <seealso cref="AddCustomChild(Func{PropertyPanelBuilder, Action{object}, View}, string, object)"/>, 
        /// which provides a eazy-use to modify <see cref="Properties"/> call <see cref="PropertyChanged"/> safely.
        /// </remarks>
        /// <param name="child">The view to add as a child to the property panel.</param>
        public PropertyPanelBuilder AddCustomChild(View child)
        {
            layout.Children.Add(child);
            return this;
        }

        /// <summary>
        /// Almost same to <see cref="AddCustomChild(View)"/>, but with an associated label.
        /// </summary>
        public PropertyPanelBuilder AddCustomChild(PropertyPanelItemLabel title, View child)
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
            layout.Children.Add(grid);
            return this;
        }

        /// <summary>
        /// Adds a custom child view to the property panel and associates it with a property identified by the specified
        /// ID and default value.
        /// Please don't forget to assign View.BindingContext to support an automatic-build of <see cref="PropertyChangingEventArgs"/>. 
        /// <code>
        /// Use it like this:
        /// ppb.AddCustomChild((ppb, invoker) => 
        /// {
        ///     var entry = new Entry 
        ///     {
        ///         Text = "...",
        ///         //...
        ///         BindingContext = ppb
        ///     }
        ///     
        ///     entry.TextChanged += (s, e) => invoker(e.NewTextValue);
        /// )
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
        public PropertyPanelBuilder AddCustomChild(Func<PropertyPanelBuilder, Action<object>, View> maker, string Id, object defaultValue)
        {
            layout.Children.Add(maker(this, (o) => pppcea.CreateAndInvoke(this, Id, o)));
            Properties[Id] = defaultValue;
            return this;
        }

        /// <summary>
        /// Almost same to <see cref="AddCustomChild(Func{PropertyPanelBuilder, Action{object}, View}, string, object)"/>, but with an associated label.
        /// </summary>
        /// <remarks>
        /// Please don't forget to assign View.BindingContext to support an automatic-build of <see cref="PropertyChangingEventArgs"/>. 
        /// </remarks>
        public PropertyPanelBuilder AddCustomChild(PropertyPanelItemLabel title, Func<PropertyPanelBuilder, Action<object>, View> maker, string Id, object defaultValue)
        {
            var child = maker(this, (o) => pppcea.CreateAndInvoke(this, Id, o));
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
            layout.Children.Add(grid);
            return this;
        }

        /// <summary>
        /// Imports all property panel items from another builder to the current builder. 
        /// The items from <paramref name="another"/> are appended to the end of the current
        /// builder's collection. 
        /// </summary>
        /// <remarks>
        /// The method will modify the source builder because of one View can't appering in 2 containers.
        /// This method also clone the source's <see cref="PropertyChanged"/> event.
        /// </remarks>
        /// <param name="another">The builder whose property panel items will be added to this builder. Cannot be null.</param>
        public PropertyPanelBuilder AddFromAnother(PropertyPanelBuilder another)
        {
            var c = another.Build();
            View? t = null;
            while (c.Children.Remove(t = (View?)c.Children.FirstOrDefault((View?)null)))
            {
                if (t is not null) AddCustomChild(t);
            }
            another.PropertyChanged += (_, e) => PropertyChanged?.Invoke(another, e);
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
        /// Get the final <seealso cref="VerticalStackLayout"/> of the childs created by this builder.
        /// </summary>
        public VerticalStackLayout Build()
        {
            return layout;
        }

        internal void _InvokeInternal(pppcea e)
        {
            PropertyChanged?.Invoke(this, e);
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
        [NotNull]
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

    public class SingleLineLabel(string text, int fontsize = 14, FontAttributes fontAttributes = FontAttributes.None) : PropertyPanelItemLabel
    {
        public override View LabelConfigurer() => new Label { Text = text, FontSize = fontsize, FontAttributes = fontAttributes };

        public static implicit operator SingleLineLabel(string text) => new SingleLineLabel(text);
    }

    public class TitleAndDescriptionLineLabel(string title, string description, int titleFontSize = 18, int contentFontSize = 14) : PropertyPanelItemLabel
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
