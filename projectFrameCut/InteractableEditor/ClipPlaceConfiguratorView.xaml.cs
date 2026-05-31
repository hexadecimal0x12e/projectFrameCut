using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;

namespace projectFrameCut.InteractableEditor;

public partial class ClipPlaceConfiguratorView : ContentView
{
    private enum ResizeHandle
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    private const double DefaultRelativeWidth = 1920;
    private const double DefaultRelativeHeight = 1080;
    private const double HandleSize = 14;
    private const double MinClipSize = 1;

    private bool _isInternalUiSync;
    private double _dragStartX;
    private double _dragStartY;
    private double _dragStartW;
    private double _dragStartH;

    private PanGestureRecognizer? _selectionPan;
    private PanGestureRecognizer? _tlPan;
    private PanGestureRecognizer? _trPan;
    private PanGestureRecognizer? _blPan;
    private PanGestureRecognizer? _brPan;

    public static readonly BindableProperty EnabledProperty = BindableProperty.Create(
        nameof(Enabled),
        typeof(bool),
        typeof(ClipPlaceConfiguratorView),
        true,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public static readonly BindableProperty TargetXProperty = BindableProperty.Create(
        nameof(TargetX),
        typeof(int),
        typeof(ClipPlaceConfiguratorView),
        0,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public static readonly BindableProperty TargetYProperty = BindableProperty.Create(
        nameof(TargetY),
        typeof(int),
        typeof(ClipPlaceConfiguratorView),
        0,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public static readonly BindableProperty TargetWidthProperty = BindableProperty.Create(
        nameof(TargetWidth),
        typeof(int),
        typeof(ClipPlaceConfiguratorView),
        1,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public static readonly BindableProperty TargetHeightProperty = BindableProperty.Create(
        nameof(TargetHeight),
        typeof(int),
        typeof(ClipPlaceConfiguratorView),
        1,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public static readonly BindableProperty RelativeWidthProperty = BindableProperty.Create(
        nameof(RelativeWidth),
        typeof(int),
        typeof(ClipPlaceConfiguratorView),
        0,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public static readonly BindableProperty RelativeHeightProperty = BindableProperty.Create(
        nameof(RelativeHeight),
        typeof(int),
        typeof(ClipPlaceConfiguratorView),
        0,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public event Action<ClipPositionTuple>? ConfigurationChanged;

    public bool Enabled
    {
        get => (bool)GetValue(EnabledProperty);
        set => SetValue(EnabledProperty, value);
    }

    public int TargetX
    {
        get => (int)GetValue(TargetXProperty);
        set => SetValue(TargetXProperty, value);
    }

    public int TargetY
    {
        get => (int)GetValue(TargetYProperty);
        set => SetValue(TargetYProperty, value);
    }

    public int TargetWidth
    {
        get => (int)GetValue(TargetWidthProperty);
        set => SetValue(TargetWidthProperty, value);
    }

    public int TargetHeight
    {
        get => (int)GetValue(TargetHeightProperty);
        set => SetValue(TargetHeightProperty, value);
    }

    public int RelativeWidth
    {
        get => (int)GetValue(RelativeWidthProperty);
        set => SetValue(RelativeWidthProperty, value);
    }

    public int RelativeHeight
    {
        get => (int)GetValue(RelativeHeightProperty);
        set => SetValue(RelativeHeightProperty, value);
    }

    public ClipPlaceConfiguratorView()
    {
        InitializeComponent();
        InitGestureRecognizers();
        SyncUiFromProperties();
        ValidateAndNotify();
    }

    public void LoadFromPosition(ClipPositionTuple position)
    {
        TargetX = position.TargetX;
        TargetY = position.TargetY;
        TargetWidth = Math.Max(1, position.TargetWidth);
        TargetHeight = Math.Max(1, position.TargetHeight);
    }

    public void LoadFromClip(ClipElementUI? clip)
    {
        if (clip is null)
        {
            LoadFromPosition(new ClipPositionTuple(0, 0, 1, 1, false));
            return;
        }

        LoadFromPosition(new ClipPositionTuple(clip.TargetX, clip.TargetY, clip.TargetWidth, clip.TargetHeight, false));
    }

    public ClipPositionTuple BuildPositionTuple()
        => new(TargetX, TargetY, TargetWidth, TargetHeight, false);

    private static void OnAnyBindablePropertyChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not ClipPlaceConfiguratorView view || view._isInternalUiSync)
        {
            return;
        }

        view.SyncUiFromProperties();
        view.ValidateAndNotify();
    }

    private void InitGestureRecognizers()
    {
        _selectionPan ??= new PanGestureRecognizer();
        _tlPan ??= new PanGestureRecognizer();
        _trPan ??= new PanGestureRecognizer();
        _blPan ??= new PanGestureRecognizer();
        _brPan ??= new PanGestureRecognizer();

        _selectionPan.PanUpdated += OnSelectionPanUpdated;
        _tlPan.PanUpdated += (_, e) => OnResizePanUpdated(ResizeHandle.TopLeft, e);
        _trPan.PanUpdated += (_, e) => OnResizePanUpdated(ResizeHandle.TopRight, e);
        _blPan.PanUpdated += (_, e) => OnResizePanUpdated(ResizeHandle.BottomLeft, e);
        _brPan.PanUpdated += (_, e) => OnResizePanUpdated(ResizeHandle.BottomRight, e);

        ClipSelection.GestureRecognizers.Clear();
        HandleTL.GestureRecognizers.Clear();
        HandleTR.GestureRecognizers.Clear();
        HandleBL.GestureRecognizers.Clear();
        HandleBR.GestureRecognizers.Clear();

        ClipSelection.GestureRecognizers.Add(_selectionPan);
        HandleTL.GestureRecognizers.Add(_tlPan);
        HandleTR.GestureRecognizers.Add(_trPan);
        HandleBL.GestureRecognizers.Add(_blPan);
        HandleBR.GestureRecognizers.Add(_brPan);
    }

    private void SyncUiFromProperties()
    {
        _isInternalUiSync = true;
        try
        {
            SummaryLabel.Text = $"({TargetX}, {TargetY})  {TargetWidth} x {TargetHeight}";
            UpdateCanvasVisuals();
        }
        finally
        {
            _isInternalUiSync = false;
        }
    }

    private void ValidateAndNotify()
    {
        var workspaceW = Math.Max(1, (int)Math.Round(GetWorkspaceWidth()));
        var workspaceH = Math.Max(1, (int)Math.Round(GetWorkspaceHeight()));

        var normalizedWidth = Math.Clamp(TargetWidth, (int)MinClipSize, workspaceW);
        var normalizedHeight = Math.Clamp(TargetHeight, (int)MinClipSize, workspaceH);
        var normalizedX = Math.Clamp(TargetX, 0, Math.Max(0, workspaceW - normalizedWidth));
        var normalizedY = Math.Clamp(TargetY, 0, Math.Max(0, workspaceH - normalizedHeight));

        if (normalizedX != TargetX) TargetX = normalizedX;
        if (normalizedY != TargetY) TargetY = normalizedY;
        if (normalizedWidth != TargetWidth) TargetWidth = normalizedWidth;
        if (normalizedHeight != TargetHeight) TargetHeight = normalizedHeight;

        SummaryLabel.TextColor = Enabled ? Color.FromRgb(0xD0, 0xD0, 0xD0) : Colors.Gray;
        SummaryLabel.Text = $"({TargetX}, {TargetY})  {TargetWidth} x {TargetHeight}";

        ConfigurationChanged?.Invoke(BuildPositionTuple());
    }

    private void PlaceCanvas_SizeChanged(object? sender, EventArgs e)
    {
        UpdateCanvasVisuals();
    }

    private double GetWorkspaceWidth()
    {
        if (RelativeWidth > 0)
        {
            return RelativeWidth;
        }

        return Math.Max(DefaultRelativeWidth, TargetX + TargetWidth);
    }

    private double GetWorkspaceHeight()
    {
        if (RelativeHeight > 0)
        {
            return RelativeHeight;
        }

        return Math.Max(DefaultRelativeHeight, TargetY + TargetHeight);
    }

    private Rect GetViewportRect()
    {
        var canvasW = PlaceCanvas.Width;
        var canvasH = PlaceCanvas.Height;
        if (canvasW <= 0 || canvasH <= 0)
        {
            return Rect.Zero;
        }

        var workspaceW = Math.Max(1, GetWorkspaceWidth());
        var workspaceH = Math.Max(1, GetWorkspaceHeight());

        var ratioCanvas = canvasW / canvasH;
        var ratioWorkspace = workspaceW / workspaceH;

        double drawW;
        double drawH;
        double offX;
        double offY;

        if (ratioWorkspace > ratioCanvas)
        {
            drawW = canvasW;
            drawH = drawW / ratioWorkspace;
            offX = 0;
            offY = (canvasH - drawH) / 2;
        }
        else
        {
            drawH = canvasH;
            drawW = drawH * ratioWorkspace;
            offY = 0;
            offX = (canvasW - drawW) / 2;
        }

        return new Rect(offX, offY, drawW, drawH);
    }

    private void UpdateCanvasVisuals()
    {
        if (PlaceCanvas.Width <= 0 || PlaceCanvas.Height <= 0)
        {
            return;
        }

        var viewport = GetViewportRect();
        AbsoluteLayout.SetLayoutBounds(PlaceViewport, viewport);

        var workspaceW = Math.Max(1, GetWorkspaceWidth());
        var workspaceH = Math.Max(1, GetWorkspaceHeight());
        var scaleX = viewport.Width / workspaceW;
        var scaleY = viewport.Height / workspaceH;

        var x = Math.Clamp(TargetX, 0, workspaceW - MinClipSize);
        var y = Math.Clamp(TargetY, 0, workspaceH - MinClipSize);
        var w = Math.Clamp(TargetWidth, MinClipSize, workspaceW - x);
        var h = Math.Clamp(TargetHeight, MinClipSize, workspaceH - y);

        var displayX = viewport.X + x * scaleX;
        var displayY = viewport.Y + y * scaleY;
        var displayW = w * scaleX;
        var displayH = h * scaleY;

        AbsoluteLayout.SetLayoutBounds(ClipSelection, new Rect(displayX, displayY, displayW, displayH));

        var handleSize = HandleSize;
        AbsoluteLayout.SetLayoutBounds(HandleTL, new Rect(displayX - handleSize / 2, displayY - handleSize / 2, handleSize, handleSize));
        AbsoluteLayout.SetLayoutBounds(HandleTR, new Rect(displayX + displayW - handleSize / 2, displayY - handleSize / 2, handleSize, handleSize));
        AbsoluteLayout.SetLayoutBounds(HandleBL, new Rect(displayX - handleSize / 2, displayY + displayH - handleSize / 2, handleSize, handleSize));
        AbsoluteLayout.SetLayoutBounds(HandleBR, new Rect(displayX + displayW - handleSize / 2, displayY + displayH - handleSize / 2, handleSize, handleSize));

        ClipSelection.IsVisible = Enabled;
        HandleTL.IsVisible = Enabled;
        HandleTR.IsVisible = Enabled;
        HandleBL.IsVisible = Enabled;
        HandleBR.IsVisible = Enabled;
    }

    private void OnSelectionPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (!Enabled)
        {
            return;
        }

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _dragStartX = TargetX;
                _dragStartY = TargetY;
                _dragStartW = TargetWidth;
                _dragStartH = TargetHeight;
                break;
            case GestureStatus.Running:
                ApplyRect(_dragStartX + e.TotalX, _dragStartY + e.TotalY, _dragStartW, _dragStartH);
                break;
        }
    }

    private void OnResizePanUpdated(ResizeHandle handle, PanUpdatedEventArgs e)
    {
        if (!Enabled)
        {
            return;
        }

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _dragStartX = TargetX;
                _dragStartY = TargetY;
                _dragStartW = TargetWidth;
                _dragStartH = TargetHeight;
                break;
            case GestureStatus.Running:
                ApplyResize(handle, e.TotalX, e.TotalY);
                break;
        }
    }

    private void ApplyRect(double x, double y, double w, double h)
    {
        var workspaceW = Math.Max(1, GetWorkspaceWidth());
        var workspaceH = Math.Max(1, GetWorkspaceHeight());
        var rect = ClampRect(x, y, w, h, workspaceW, workspaceH);

        _isInternalUiSync = true;
        try
        {
            TargetX = (int)Math.Round(rect.X);
            TargetY = (int)Math.Round(rect.Y);
            TargetWidth = Math.Max(1, (int)Math.Round(rect.Width));
            TargetHeight = Math.Max(1, (int)Math.Round(rect.Height));
        }
        finally
        {
            _isInternalUiSync = false;
        }

        SyncUiFromProperties();
        ValidateAndNotify();
    }

    private void ApplyResize(ResizeHandle handle, double deltaX, double deltaY)
    {
        var x = _dragStartX;
        var y = _dragStartY;
        var w = _dragStartW;
        var h = _dragStartH;

        switch (handle)
        {
            case ResizeHandle.TopLeft:
                x += deltaX;
                y += deltaY;
                w -= deltaX;
                h -= deltaY;
                break;
            case ResizeHandle.TopRight:
                y += deltaY;
                w += deltaX;
                h -= deltaY;
                break;
            case ResizeHandle.BottomLeft:
                x += deltaX;
                w -= deltaX;
                h += deltaY;
                break;
            case ResizeHandle.BottomRight:
                w += deltaX;
                h += deltaY;
                break;
        }

        ApplyRect(x, y, w, h);
    }

    private static Rect ClampRect(double x, double y, double w, double h, double workspaceW, double workspaceH)
    {
        w = Math.Max(MinClipSize, w);
        h = Math.Max(MinClipSize, h);

        if (x < 0)
        {
            w += x;
            x = 0;
        }

        if (y < 0)
        {
            h += y;
            y = 0;
        }

        if (x + w > workspaceW)
        {
            w = Math.Max(MinClipSize, workspaceW - x);
        }

        if (y + h > workspaceH)
        {
            h = Math.Max(MinClipSize, workspaceH - y);
        }

        if (x > workspaceW - MinClipSize)
        {
            x = Math.Max(0, workspaceW - MinClipSize);
            w = MinClipSize;
        }

        if (y > workspaceH - MinClipSize)
        {
            y = Math.Max(0, workspaceH - MinClipSize);
            h = MinClipSize;
        }

        return new Rect(x, y, Math.Max(MinClipSize, w), Math.Max(MinClipSize, h));
    }
}
