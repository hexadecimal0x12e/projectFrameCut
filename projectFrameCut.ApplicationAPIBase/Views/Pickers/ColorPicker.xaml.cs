namespace projectFrameCut.ApplicationAPIBase.Views.Pickers;

public partial class ColorPicker : ContentView
{
    public static readonly BindableProperty SelectedColorProperty = BindableProperty.Create(
        propertyName: nameof(SelectedColor),
        returnType: typeof(Color),
        declaringType: typeof(ColorPicker),
        defaultValue: Colors.White,
        defaultBindingMode: BindingMode.TwoWay,
        propertyChanged: OnSelectedColorChanged);

    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public event EventHandler<Color>? SelectedColorChanged;

    private bool _isUpdatingUi;
    private double _hue;
    private double _saturation;
    private double _value = 1.0;
    private double _alpha = 1.0;
    private Color _lastPublishedColor = Colors.Transparent;
    // fields for pan (drag) handling
    private double _panStartX;
    private double _panStartY;
    private bool _isPanning;
    private int _colorModeIndex = 0; // 0=RGB,1=HSV,2=HSL

    public ColorPicker()
    {
        InitializeComponent();
        ColorWheelHost.SizeChanged += (_, _) => UpdateWheelThumbPosition();
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnWheelPanUpdated;
        ColorWheelHost.GestureRecognizers.Add(pan);
        // hook picker change if available
        try
        {
            ColorModePicker.SelectedIndexChanged += (s, e) => OnColorModeChanged(s, e);
        }
        catch
        {
            // ignore if XAML hooking already wired
        }
        SyncFromColor(Colors.White);
        ApplyColorToUi();
    }

    private void OnExpandToggleTapped(object? sender, EventArgs e)
    {
        if (DetailsPanel == null)
        {
            return;
        }

        DetailsPanel.IsVisible = !DetailsPanel.IsVisible;
        if (ExpandIconLabel != null)
        {
            ExpandIconLabel.Text = DetailsPanel.IsVisible ? "^" : "v";
        }

        if (ExpandTextLabel != null)
        {
            ExpandTextLabel.Text = DetailsPanel.IsVisible ? "Less" : "More";
        }
    }

    private void OnColorModeChanged(object? sender, EventArgs e)
    {
        if (ColorModePicker == null)
        {
            return;
        }

        _colorModeIndex = ColorModePicker.SelectedIndex;
        UpdateChannelLabels();
        ApplyColorToUi();
    }

    private void OnWheelPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (_isUpdatingUi)
        {
            return;
        }

        if (ColorWheelHost.Width <= 0 || ColorWheelHost.Height <= 0)
        {
            return;
        }

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartX = WheelThumb.TranslationX;
                _panStartY = WheelThumb.TranslationY;
                _isPanning = true;
                break;

            case GestureStatus.Running:
                var newX = _panStartX + e.TotalX;
                var newY = _panStartY + e.TotalY;
                var radius = Math.Min(ColorWheelHost.Width, ColorWheelHost.Height) / 2.0;
                var distance = Math.Sqrt((newX * newX) + (newY * newY));
                if (distance > radius)
                {
                    newX = newX / distance * radius;
                    newY = newY / distance * radius;
                    distance = radius;
                }

                _hue = (Math.Atan2(newY, newX) * (180.0 / Math.PI) + 360.0) % 360.0;
                _saturation = Clamp01(distance / radius);
                ApplyColorToUi();
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _isPanning = false;
                break;
        }
    }

    private static void OnSelectedColorChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not ColorPicker picker || picker._isUpdatingUi)
        {
            return;
        }

        if (newValue is Color color)
        {
            picker.SyncFromColor(color);
            picker.ApplyColorToUi();
            picker.SelectedColorChanged?.Invoke(picker, color);
        }
    }

    private void OnWheelTapped(object? sender, TappedEventArgs e)
    {
        var position = e.GetPosition(ColorWheelHost);
        if (!position.HasValue)
        {
            return;
        }

        var centerX = ColorWheelHost.Width / 2.0;
        var centerY = ColorWheelHost.Height / 2.0;
        if (centerX <= 0 || centerY <= 0)
        {
            return;
        }

        var dx = position.Value.X - centerX;
        var dy = position.Value.Y - centerY;
        var radius = Math.Min(centerX, centerY);
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance > radius)
        {
            dx = dx / distance * radius;
            dy = dy / distance * radius;
            distance = radius;
        }

        _hue = (Math.Atan2(dy, dx) * (180.0 / Math.PI) + 360.0) % 360.0;
        _saturation = Clamp01(distance / radius);

        ApplyColorToUi();
    }

    private void OnValueSliderChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_isUpdatingUi)
        {
            return;
        }

        _value = Clamp01(e.NewValue);
        ApplyColorToUi();
    }

    private void OnAlphaSliderChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_isUpdatingUi)
        {
            return;
        }

        _alpha = Clamp01(e.NewValue);
        ApplyColorToUi();
    }

    private void OnHexEntryCompleted(object? sender, EventArgs e)
    {
        ApplyHexInput();
    }

    private void OnHexEntryUnfocused(object? sender, FocusEventArgs e)
    {
        ApplyHexInput();
    }

    private void OnRgbEntryCompleted(object? sender, EventArgs e)
    {
        ApplyRgbInput();
    }

    private void OnRgbEntryUnfocused(object? sender, FocusEventArgs e)
    {
        ApplyRgbInput();
    }

    private void OnOpacityEntryCompleted(object? sender, EventArgs e)
    {
        ApplyOpacityInput();
    }

    private void OnOpacityEntryUnfocused(object? sender, FocusEventArgs e)
    {
        ApplyOpacityInput();
    }

    private void ApplyHexInput()
    {
        if (_isUpdatingUi)
        {
            return;
        }

        if (!TryParseHexColor(HexEntry.Text, out var parsed))
        {
            ApplyColorToUi();
            return;
        }

        SyncFromColor(parsed);
        ApplyColorToUi();
    }

    private void ApplyRgbInput()
    {
        if (_isUpdatingUi)
        {
            return;
        }

        // Interpret entries based on current color mode
        try
        {
            if (_colorModeIndex == 0)
            {
                if (!TryParseByte(RedEntry.Text, out var r) || !TryParseByte(GreenEntry.Text, out var g) || !TryParseByte(BlueEntry.Text, out var b))
                {
                    ApplyColorToUi();
                    return;
                }

                var color = Color.FromRgba(r / 255.0, g / 255.0, b / 255.0, _alpha);
                SyncFromColor(color);
                ApplyColorToUi();
                return;
            }

            if (_colorModeIndex == 1)
            {
                // HSV: RedEntry=H (0-360), GreenEntry=S (0-100%), BlueEntry=V (0-100%)
                if (!double.TryParse(RedEntry.Text?.Trim(), out var h))
                {
                    ApplyColorToUi();
                    return;
                }

                if (!double.TryParse(GreenEntry.Text?.Trim().Replace("%", string.Empty), out var s))
                {
                    ApplyColorToUi();
                    return;
                }

                if (!double.TryParse(BlueEntry.Text?.Trim().Replace("%", string.Empty), out var v))
                {
                    ApplyColorToUi();
                    return;
                }

                var color = HsvToColor(h, Clamp01(s / 100.0), Clamp01(v / 100.0), _alpha);
                SyncFromColor(color);
                ApplyColorToUi();
                return;
            }

            // HSL
            if (_colorModeIndex == 2)
            {
                if (!double.TryParse(RedEntry.Text?.Trim(), out var h))
                {
                    ApplyColorToUi();
                    return;
                }

                if (!double.TryParse(GreenEntry.Text?.Trim().Replace("%", string.Empty), out var s))
                {
                    ApplyColorToUi();
                    return;
                }

                if (!double.TryParse(BlueEntry.Text?.Trim().Replace("%", string.Empty), out var l))
                {
                    ApplyColorToUi();
                    return;
                }

                var color = HslToColor(h, Clamp01(s / 100.0), Clamp01(l / 100.0), _alpha);
                SyncFromColor(color);
                ApplyColorToUi();
                return;
            }
        }
        catch
        {
            ApplyColorToUi();
        }
    }

    private void ApplyOpacityInput()
    {
        if (_isUpdatingUi)
        {
            return;
        }

        if (!TryParseOpacity(OpacityEntry.Text, out var opacity))
        {
            ApplyColorToUi();
            return;
        }

        _alpha = Clamp01(opacity);
        ApplyColorToUi();
    }

    private void SyncFromColor(Color color)
    {
        RgbToHsv(color.Red, color.Green, color.Blue, out _hue, out _saturation, out _value);
        _alpha = Clamp01(color.Alpha);
    }

    private void ApplyColorToUi()
    {
        Color? publishedColor = null;

        _isUpdatingUi = true;
        try
        {
            var rgbColor = HsvToColor(_hue, _saturation, _value, 1.0);
            var finalColor = Color.FromRgba(rgbColor.Red, rgbColor.Green, rgbColor.Blue, _alpha);
            publishedColor = finalColor;
            SelectedColor = finalColor;
            OnPropertyChanged(nameof(SelectedColor));

            ValueSlider.Value = _value;
            AlphaSlider.Value = _alpha;

            var r8 = ToByte(finalColor.Red);
            var g8 = ToByte(finalColor.Green);
            var b8 = ToByte(finalColor.Blue);
            var a8 = ToByte(finalColor.Alpha);
            // Display channel values according to selected mode
            if (_colorModeIndex == 0)
            {
                RedEntry.Text = r8.ToString();
                GreenEntry.Text = g8.ToString();
                BlueEntry.Text = b8.ToString();
            }
            else if (_colorModeIndex == 1)
            {
                RgbToHsv(finalColor.Red, finalColor.Green, finalColor.Blue, out var hh, out var ss, out var vv);
                RedEntry.Text = Math.Round(hh, 1).ToString();
                GreenEntry.Text = Math.Round(ss * 100).ToString() + "%";
                BlueEntry.Text = Math.Round(vv * 100).ToString() + "%";
            }
            else
            {
                RgbToHsl(finalColor.Red, finalColor.Green, finalColor.Blue, out var hh2, out var ss2, out var ll2);
                RedEntry.Text = Math.Round(hh2, 1).ToString();
                GreenEntry.Text = Math.Round(ss2 * 100).ToString() + "%";
                BlueEntry.Text = Math.Round(ll2 * 100).ToString() + "%";
            }
            OpacityEntry.Text = $"{Math.Round(_alpha * 100)}%";
            HexEntry.Text = $"#{a8:X2}{r8:X2}{g8:X2}{b8:X2}";

            UpdateWheelThumbPosition();
            UpdatePreviewStrip(finalColor, rgbColor);
        }
        finally
        {
            _isUpdatingUi = false;
        }

        if (publishedColor is Color c && !AreColorsClose(_lastPublishedColor, c))
        {
            _lastPublishedColor = c;
            SelectedColorChanged?.Invoke(this, c);
        }
    }

    private void UpdatePreviewStrip(Color finalColor, Color opaqueColor)
    {
        PreviewStrip.Background = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(finalColor, 0),
                new GradientStop(opaqueColor, 1),
            },
            new Point(0.5, 0),
            new Point(0.5, 1));
    }

    private void UpdateWheelThumbPosition()
    {
        if (ColorWheelHost.Width <= 0 || ColorWheelHost.Height <= 0)
        {
            return;
        }

        var angle = _hue * Math.PI / 180.0;
        var radius = Math.Min(ColorWheelHost.Width, ColorWheelHost.Height) / 2.0;
        var trackRadius = radius * _saturation;
        var x = Math.Cos(angle) * trackRadius;
        var y = Math.Sin(angle) * trackRadius;

        WheelThumb.TranslationX = x;
        WheelThumb.TranslationY = y;
    }

    private void UpdateChannelLabels()
    {
        // 0=RGB,1=HSV,2=HSL
        switch (_colorModeIndex)
        {
            case 0:
                RedLabel.Text = "Red";
                GreenLabel.Text = "Green";
                BlueLabel.Text = "Blue";
                break;
            case 1:
                RedLabel.Text = "H";
                GreenLabel.Text = "S";
                BlueLabel.Text = "V";
                break;
            default:
                RedLabel.Text = "H";
                GreenLabel.Text = "S";
                BlueLabel.Text = "L";
                break;
        }
    }

    private static Color HslToColor(double h, double s, double l, double a)
    {
        h = (h % 360.0 + 360.0) % 360.0;
        s = Clamp01(s);
        l = Clamp01(l);

        if (s == 0)
        {
            return Color.FromRgba(l, l, l, Clamp01(a));
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double hk = h / 360.0;

        static double Hue2Rgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
            return p;
        }

        var r = Hue2Rgb(p, q, hk + 1.0 / 3.0);
        var g = Hue2Rgb(p, q, hk);
        var b = Hue2Rgb(p, q, hk - 1.0 / 3.0);
        return Color.FromRgba(r, g, b, Clamp01(a));
    }

    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2.0;

        if (Math.Abs(max - min) < double.Epsilon)
        {
            h = 0;
            s = 0;
            return;
        }

        var d = max - min;
        s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        if (max == r)
        {
            h = 60.0 * (((g - b) / d) % 6.0);
        }
        else if (max == g)
        {
            h = 60.0 * (((b - r) / d) + 2.0);
        }
        else
        {
            h = 60.0 * (((r - g) / d) + 4.0);
        }

        if (h < 0) h += 360.0;
    }

    private static Color HsvToColor(double h, double s, double v, double a)
    {
        h = (h % 360.0 + 360.0) % 360.0;
        s = Clamp01(s);
        v = Clamp01(v);

        if (s <= 0.0)
        {
            return Color.FromRgba(v, v, v, Clamp01(a));
        }

        var sector = h / 60.0;
        var i = (int)Math.Floor(sector);
        var f = sector - i;
        var p = v * (1.0 - s);
        var q = v * (1.0 - (s * f));
        var t = v * (1.0 - (s * (1.0 - f)));

        return i switch
        {
            0 => Color.FromRgba(v, t, p, Clamp01(a)),
            1 => Color.FromRgba(q, v, p, Clamp01(a)),
            2 => Color.FromRgba(p, v, t, Clamp01(a)),
            3 => Color.FromRgba(p, q, v, Clamp01(a)),
            4 => Color.FromRgba(t, p, v, Clamp01(a)),
            _ => Color.FromRgba(v, p, q, Clamp01(a)),
        };
    }

    private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        v = max;
        s = max <= 0 ? 0 : delta / max;

        if (delta <= 0)
        {
            h = 0;
            return;
        }

        if (max == r)
        {
            h = 60.0 * (((g - b) / delta) % 6.0);
        }
        else if (max == g)
        {
            h = 60.0 * (((b - r) / delta) + 2.0);
        }
        else
        {
            h = 60.0 * (((r - g) / delta) + 4.0);
        }

        if (h < 0)
        {
            h += 360.0;
        }
    }

    private static bool TryParseHexColor(string? text, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();
        if (!normalized.StartsWith("#", StringComparison.Ordinal))
        {
            normalized = "#" + normalized;
        }

        try
        {
            color = Color.FromArgb(normalized);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseByte(string? text, out int value)
    {
        value = 0;
        if (!int.TryParse(text?.Trim(), out var parsed))
        {
            return false;
        }

        value = Math.Clamp(parsed, 0, 255);
        return true;
    }

    private static bool TryParseOpacity(string? text, out double value)
    {
        value = 1.0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim().Replace("%", string.Empty, StringComparison.Ordinal);
        if (!double.TryParse(normalized, out var parsed))
        {
            return false;
        }

        if (parsed > 1.0)
        {
            value = parsed / 100.0;
        }
        else
        {
            value = parsed;
        }

        value = Clamp01(value);
        return true;
    }

    private static int ToByte(double unit)
    {
        return (int)Math.Round(Clamp01(unit) * 255.0);
    }

    private static double Clamp01(double value)
    {
        if (value < 0)
        {
            return 0;
        }

        if (value > 1)
        {
            return 1;
        }

        return value;
    }

    private static bool AreColorsClose(Color a, Color b)
    {
        const double eps = 1.0 / 255.0;
        return Math.Abs(a.Red - b.Red) < eps
            && Math.Abs(a.Green - b.Green) < eps
            && Math.Abs(a.Blue - b.Blue) < eps
            && Math.Abs(a.Alpha - b.Alpha) < eps;
    }
}