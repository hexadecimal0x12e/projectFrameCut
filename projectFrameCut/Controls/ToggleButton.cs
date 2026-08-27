using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace projectFrameCut.Controls;

/// <summary>
/// A button that keeps an on/off state after it is clicked.
/// </summary>
public class ToggleButton : Button
{
    public static readonly BindableProperty IsToggledProperty = BindableProperty.Create(
        nameof(IsToggled),
        typeof(bool),
        typeof(ToggleButton),
        false,
        BindingMode.TwoWay,
        propertyChanged: OnIsToggledChanged);

    public static readonly BindableProperty OnBackgroundColorProperty = BindableProperty.Create(
        nameof(OnBackgroundColor),
        typeof(Color),
        typeof(ToggleButton),
        Color.FromArgb("#FFD272"),
        propertyChanged: OnStateColorChanged);

    public static readonly BindableProperty OffBackgroundColorProperty = BindableProperty.Create(
        nameof(OffBackgroundColor),
        typeof(Color),
        typeof(ToggleButton),
        Color.FromArgb("#404040"),
        propertyChanged: OnStateColorChanged);

    public static readonly BindableProperty OnTextColorProperty = BindableProperty.Create(
        nameof(OnTextColor),
        typeof(Color),
        typeof(ToggleButton),
        Color.FromArgb("#242424"),
        propertyChanged: OnStateColorChanged);

    public static readonly BindableProperty OffTextColorProperty = BindableProperty.Create(
        nameof(OffTextColor),
        typeof(Color),
        typeof(ToggleButton),
        Color.FromArgb("#E1E1E1"),
        propertyChanged: OnStateColorChanged);

    public ToggleButton()
    {
        Clicked += (_, _) => IsToggled = !IsToggled;
        UpdateStateAppearance();
    }

    public bool IsToggled
    {
        get => (bool)GetValue(IsToggledProperty);
        set => SetValue(IsToggledProperty, value);
    }

    public Color OnBackgroundColor
    {
        get => (Color)GetValue(OnBackgroundColorProperty);
        set => SetValue(OnBackgroundColorProperty, value);
    }

    public Color OffBackgroundColor
    {
        get => (Color)GetValue(OffBackgroundColorProperty);
        set => SetValue(OffBackgroundColorProperty, value);
    }

    public Color OnTextColor
    {
        get => (Color)GetValue(OnTextColorProperty);
        set => SetValue(OnTextColorProperty, value);
    }

    public Color OffTextColor
    {
        get => (Color)GetValue(OffTextColorProperty);
        set => SetValue(OffTextColorProperty, value);
    }

    public event EventHandler<ToggledEventArgs>? Toggled;

    private static void OnIsToggledChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var button = (ToggleButton)bindable;
        button.UpdateStateAppearance();
        button.SyncPlatformToggleState();
        button.Toggled?.Invoke(button, new ToggledEventArgs((bool)newValue));
    }

    private static void OnStateColorChanged(BindableObject bindable, object oldValue, object newValue)
        => ((ToggleButton)bindable).UpdateStateAppearance();

    private void UpdateStateAppearance()
    {
        BackgroundColor = IsToggled ? OnBackgroundColor : OffBackgroundColor;
        TextColor = IsToggled ? OnTextColor : OffTextColor;
        VisualStateManager.GoToState(this, IsToggled ? "ToggledOn" : "ToggledOff");
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        SyncPlatformToggleState();
    }

    private void SyncPlatformToggleState()
    {
#if LINUX
        if (Handler?.PlatformView is Gtk.ToggleButton platformButton && platformButton.Active != IsToggled)
        {
            platformButton.Active = IsToggled;
        }
#endif
    }
}
