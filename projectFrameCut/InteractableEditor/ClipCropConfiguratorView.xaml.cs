using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.InteractableEditor;

public partial class ClipCropConfiguratorView : ContentView
{
    private enum ResizeHandle
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    private bool _isInternalUiSync;
    private bool _showNumericEditors = true;
    private const string InternalCropKey = "__Internal_Crop__";
    private const double DefaultRelativeWidth = 1920;
    private const double DefaultRelativeHeight = 1080;
    private const double HandleSize = 14;
    private const double MinCropSize = 1;

    private PanGestureRecognizer? _selectionPan;
    private PanGestureRecognizer? _tlPan;
    private PanGestureRecognizer? _trPan;
    private PanGestureRecognizer? _blPan;
    private PanGestureRecognizer? _brPan;
    private PanGestureRecognizer? _rotatePan;

    private double _dragStartX;
    private double _dragStartY;
    private double _dragStartW;
    private double _dragStartH;
    private double _lastPanTotalX;
    private double _lastPanTotalY;
    private double _rotateStartHandleX;
    private double _rotateStartHandleY;
    private DateTime _lastDragNotifyTimeUtc = DateTime.MinValue;

    private static readonly TimeSpan DragNotifyInterval = TimeSpan.FromMilliseconds(50);

    private int _effectIndex;
    private EffectImplementType _implementType = EffectImplementType.NotSpecified;
    private Guid _bundleId = Guid.NewGuid();

    public static readonly BindableProperty EnabledProperty = BindableProperty.Create(
        nameof(Enabled),
        typeof(bool),
        typeof(ClipCropConfiguratorView),
        true,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public static readonly BindableProperty DisableMoveProperty = BindableProperty.Create(
        nameof(DisableMove),
        typeof(bool),
        typeof(ClipCropConfiguratorView),
        false);

    public static readonly BindableProperty DisableHorizontalResizeProperty = BindableProperty.Create(
        nameof(DisableHorizontalResize),
        typeof(bool),
        typeof(ClipCropConfiguratorView),
        false);

    public static readonly BindableProperty DisableVerticalResizeProperty = BindableProperty.Create(
        nameof(DisableVerticalResize),
        typeof(bool),
        typeof(ClipCropConfiguratorView),
        false);

    public static readonly BindableProperty StartXProperty = BindableProperty.Create(
        nameof(StartX),
        typeof(int),
        typeof(ClipCropConfiguratorView),
        0,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public static readonly BindableProperty StartYProperty = BindableProperty.Create(
        nameof(StartY),
        typeof(int),
        typeof(ClipCropConfiguratorView),
        0,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public static readonly BindableProperty CropWidthProperty = BindableProperty.Create(
        nameof(CropWidth),
        typeof(int),
        typeof(ClipCropConfiguratorView),
        1,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public static readonly BindableProperty CropHeightProperty = BindableProperty.Create(
        nameof(CropHeight),
        typeof(int),
        typeof(ClipCropConfiguratorView),
        1,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public static readonly BindableProperty RelativeWidthProperty = BindableProperty.Create(
        nameof(RelativeWidth),
        typeof(int),
        typeof(ClipCropConfiguratorView),
        0,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public static readonly BindableProperty RelativeHeightProperty = BindableProperty.Create(
        nameof(RelativeHeight),
        typeof(int),
        typeof(ClipCropConfiguratorView),
        0,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public static readonly BindableProperty AngleProperty = BindableProperty.Create(
        nameof(Angle),
        typeof(float),
        typeof(ClipCropConfiguratorView),
        0f,
        BindingMode.TwoWay,
        propertyChanged: OnAnyBindablePropertyChanged);

    public event EventHandler<IEffectProvider>? ConfigurationChanged;

    public bool Enabled
    {
        get => (bool)GetValue(EnabledProperty);
        set => SetValue(EnabledProperty, value);
    }

    public bool DisableMove
    {
        get => (bool)GetValue(DisableMoveProperty);
        set => SetValue(DisableMoveProperty, value);
    }

    public bool DisableHorizontalResize
    {
        get => (bool)GetValue(DisableHorizontalResizeProperty);
        set => SetValue(DisableHorizontalResizeProperty, value);
    }

    public bool DisableVerticalResize
    {
        get => (bool)GetValue(DisableVerticalResizeProperty);
        set => SetValue(DisableVerticalResizeProperty, value);
    }

    public int StartX
    {
        get => (int)GetValue(StartXProperty);
        set => SetValue(StartXProperty, value);
    }

    public int StartY
    {
        get => (int)GetValue(StartYProperty);
        set => SetValue(StartYProperty, value);
    }

    public int CropWidth
    {
        get => (int)GetValue(CropWidthProperty);
        set => SetValue(CropWidthProperty, value);
    }

    public int CropHeight
    {
        get => (int)GetValue(CropHeightProperty);
        set => SetValue(CropHeightProperty, value);
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

    public float Angle
    {
        get => (float)GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    public ClipCropConfiguratorView()
    {
        InitializeComponent();
        EnableSwitchText.Text = LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources._Enabled;
        AngleSliderText.Text = LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.General_Rotation;
        InitGestureRecognizers();
        SyncUiFromProperties();
        InvokeConfigurationChanged();
    }

    private static int ReadIntValue(object? raw, int fallback)
    {
        if (raw is null)
        {
            return fallback;
        }

        if (raw is int i)
        {
            return i;
        }

        if (raw is long l)
        {
            return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        }

        if (raw is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n))
            {
                return n;
            }

            if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var parsedFromString))
            {
                return parsedFromString;
            }
        }

        return int.TryParse(raw.ToString(), out var parsed) ? parsed : fallback;
    }

    private static float ReadFloatValue(object? raw, float fallback)
    {
        if (raw is null)
        {
            return fallback;
        }

        if (raw is float f)
        {
            return f;
        }

        if (raw is double d)
        {
            return (float)d;
        }

        if (raw is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Number && je.TryGetSingle(out var n))
            {
                return n;
            }

            if (je.ValueKind == JsonValueKind.String && float.TryParse(je.GetString(), out var parsedFromString))
            {
                return parsedFromString;
            }
        }

        return float.TryParse(raw.ToString(), out var parsed) ? parsed : fallback;
    }

    private static int ReadIntParameter(IReadOnlyDictionary<string, object>? parameters, string key, int fallback)
    {
        if (parameters != null && parameters.TryGetValue(key, out var raw))
        {
            return ReadIntValue(raw, fallback);
        }

        return fallback;
    }

    private static int ReadIntParameter(Dictionary<string, IEffectArgumentField>? fields, string key, int fallback)
    {
        if (fields != null && fields.TryGetValue(key, out var field) && field is StaticEffectArgumentField sf)
        {
            return ReadIntValue(sf.Value, fallback);
        }
        return fallback;
    }

    private static float ReadFloatParameter(IReadOnlyDictionary<string, object>? parameters, string key, float fallback)
    {
        if (parameters != null && parameters.TryGetValue(key, out var raw))
        {
            return ReadFloatValue(raw, fallback);
        }

        return fallback;
    }

    private static float ReadFloatParameter(Dictionary<string, IEffectArgumentField>? fields, string key, float fallback)
    {
        if (fields != null && fields.TryGetValue(key, out var field) && field is StaticEffectArgumentField sf)
        {
            return ReadFloatValue(sf.Value, fallback);
        }
        return fallback;
    }

    public void LoadFromProvider(IEffectProvider? bundle, IEffect? fallbackEffect = null)
    {
        if (bundle is null || !string.Equals(bundle.TypeName, "Crop", StringComparison.Ordinal))
        {
            LoadFromEffect(fallbackEffect);
            return;
        }

        _bundleId = bundle.Id == Guid.Empty ? _bundleId : bundle.Id;
        _effectIndex = fallbackEffect?.Index ?? 0;
        _implementType = fallbackEffect?.ImplementType ?? EffectImplementType.NotSpecified;

        Enabled = bundle.Enabled;
        StartX = Math.Max(0, ReadIntParameter(bundle.Fields, "StartX", 0));
        StartY = Math.Max(0, ReadIntParameter(bundle.Fields, "StartY", 0));
        CropWidth = Math.Max(1, ReadIntParameter(bundle.Fields, "Width", 1));
        CropHeight = Math.Max(1, ReadIntParameter(bundle.Fields, "Height", 1));
        RelativeWidth = Math.Max(0, fallbackEffect?.RelativeWidth ?? RelativeWidth);
        RelativeHeight = Math.Max(0, fallbackEffect?.RelativeHeight ?? RelativeHeight);
        Angle = ReadFloatParameter(bundle.Fields, "Angle", 0f);
    }

    public void LoadFromEffect(IEffect? effect)
    {
        if (effect is null)
        {
            _effectIndex = 0;
            _implementType = EffectImplementType.NotSpecified;
            Enabled = true;
            StartX = 0;
            StartY = 0;
            CropWidth = 1;
            CropHeight = 1;
            RelativeWidth = 0;
            RelativeHeight = 0;
            Angle = 0f;
            return;
        }


        if (!string.Equals(effect.TypeName, "Crop", StringComparison.Ordinal))
        {
            LoadFromEffect((IEffect?)null);
            return;
        }

        _effectIndex = effect.Index;
        _implementType = effect.ImplementType;

        Enabled = effect.Enabled;
        StartX = Math.Max(0, ReadIntParameter(effect.Parameters, "StartX", 0));
        StartY = Math.Max(0, ReadIntParameter(effect.Parameters, "StartY", 0));
        CropWidth = Math.Max(1, ReadIntParameter(effect.Parameters, "Width", 1));
        CropHeight = Math.Max(1, ReadIntParameter(effect.Parameters, "Height", 1));
        RelativeWidth = Math.Max(0, effect.RelativeWidth);
        RelativeHeight = Math.Max(0, effect.RelativeHeight);
        Angle = ReadFloatParameter(effect.Parameters, "Angle", 0f);
    }

    public void LoadFromEffect(CropEffect_IPicture? effect) => LoadFromEffect((IEffect?)effect);

    public void LoadFromEffect(CropEffect_HwAccel? effect) => LoadFromEffect((IEffect?)effect);

    public IEffectProvider BuildEffectProvider(Guid? providerId = null)
    {
        _bundleId = providerId ?? (_bundleId == Guid.Empty ? Guid.NewGuid() : _bundleId);
        var provider = new CropEffectProvider
        {
            Id = _bundleId,
            Enabled = Enabled,
            Name = InternalCropKey,
            Fields = new Dictionary<string, IEffectArgumentField>
            {
                { "StartX", new StaticEffectArgumentField(Math.Max(0, StartX), EffectArgumentFieldType.Integer) },
                { "StartY", new StaticEffectArgumentField(Math.Max(0, StartY), EffectArgumentFieldType.Integer) },
                { "Width", new StaticEffectArgumentField(Math.Max(1, CropWidth), EffectArgumentFieldType.Integer) },
                { "Height", new StaticEffectArgumentField(Math.Max(1, CropHeight), EffectArgumentFieldType.Integer) },
                { "Angle", new StaticEffectArgumentField(Angle, EffectArgumentFieldType.Numeric) }
            }
        };
        provider.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        provider.SetFinalOutputSource(true);
        return provider;
    }

    public IEffect BuildEffect(EffectImplementType? implementType = null)
    {
        var provider = BuildEffectProvider();
        var actualImplementType = implementType ?? _implementType;
        if (actualImplementType != EffectImplementType.NotSpecified
            && Array.IndexOf(provider.SupportsImplementTypes, actualImplementType) < 0)
        {
            actualImplementType = EffectImplementType.NotSpecified;
        }

        var effect = provider.RestoreInstance(actualImplementType);
        effect.Enabled = Enabled;
        effect.RelativeWidth = Math.Max(0, RelativeWidth);
        effect.RelativeHeight = Math.Max(0, RelativeHeight);
        effect.Name = InternalCropKey;
        effect.Index = _effectIndex;
        effect.Id = InternalCropKey;
        return effect;
    }

    private static void OnAnyBindablePropertyChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not ClipCropConfiguratorView view || view._isInternalUiSync)
        {
            return;
        }

        view.SyncUiFromProperties();
        view.UpdateCanvasVisuals();
        view.InvokeConfigurationChanged();
    }

    private void SyncUiFromProperties()
    {
        _isInternalUiSync = true;
        try
        {
            EnabledSwitch.IsToggled = Enabled;
            AngleSlider.Value = Angle;
            AngleValueLabel.Text = $"{Angle:0.#}°";
            UpdateCanvasVisuals();
        }
        finally
        {
            _isInternalUiSync = false;
        }
    }

    public void InvokeConfigurationChanged()
    {
        var normalizedStartX = Math.Max(0, StartX);
        var normalizedStartY = Math.Max(0, StartY);
        var normalizedWidth = Math.Max(1, CropWidth);
        var normalizedHeight = Math.Max(1, CropHeight);
        var normalizedRelativeWidth = Math.Max(0, RelativeWidth);
        var normalizedRelativeHeight = Math.Max(0, RelativeHeight);
        var normalizedAngle = NormalizeAngle(Angle);

        if (normalizedStartX != StartX) StartX = normalizedStartX;
        if (normalizedStartY != StartY) StartY = normalizedStartY;
        if (normalizedWidth != CropWidth) CropWidth = normalizedWidth;
        if (normalizedHeight != CropHeight) CropHeight = normalizedHeight;
        if (normalizedRelativeWidth != RelativeWidth) RelativeWidth = normalizedRelativeWidth;
        if (normalizedRelativeHeight != RelativeHeight) RelativeHeight = normalizedRelativeHeight;
        if (Math.Abs(normalizedAngle - Angle) > float.Epsilon) Angle = normalizedAngle;
        ConfigurationChanged?.Invoke(this, BuildEffectProvider());
    }

    private void EnabledSwitch_Toggled(object? sender, ToggledEventArgs e)
    {
        if (_isInternalUiSync)
        {
            return;
        }

        Enabled = e.Value;
        UpdateCanvasVisuals();
        InvokeConfigurationChanged();
    }

    private void AngleSlider_ValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_isInternalUiSync)
        {
            return;
        }

        Angle = (float)e.NewValue;
        UpdateCanvasVisuals();
        InvokeConfigurationChanged();
    }

    private void CropCanvas_SizeChanged(object? sender, EventArgs e)
    {
        UpdateCanvasVisuals();
    }

    private void InitGestureRecognizers()
    {
        _selectionPan ??= new PanGestureRecognizer();
        _tlPan ??= new PanGestureRecognizer();
        _trPan ??= new PanGestureRecognizer();
        _blPan ??= new PanGestureRecognizer();
        _brPan ??= new PanGestureRecognizer();
        _rotatePan ??= new PanGestureRecognizer();

        _selectionPan.PanUpdated += OnSelectionPanUpdated;
        _tlPan.PanUpdated += (_, e) => OnResizePanUpdated(ResizeHandle.TopLeft, e);
        _trPan.PanUpdated += (_, e) => OnResizePanUpdated(ResizeHandle.TopRight, e);
        _blPan.PanUpdated += (_, e) => OnResizePanUpdated(ResizeHandle.BottomLeft, e);
        _brPan.PanUpdated += (_, e) => OnResizePanUpdated(ResizeHandle.BottomRight, e);
        _rotatePan.PanUpdated += OnRotatePanUpdated;

        CropSelection.GestureRecognizers.Clear();
        HandleTL.GestureRecognizers.Clear();
        HandleTR.GestureRecognizers.Clear();
        HandleBL.GestureRecognizers.Clear();
        HandleBR.GestureRecognizers.Clear();
        HandleRotate.GestureRecognizers.Clear();

        CropSelection.GestureRecognizers.Add(_selectionPan);
        HandleTL.GestureRecognizers.Add(_tlPan);
        HandleTR.GestureRecognizers.Add(_trPan);
        HandleBL.GestureRecognizers.Add(_blPan);
        HandleBR.GestureRecognizers.Add(_brPan);
        HandleRotate.GestureRecognizers.Add(_rotatePan);
    }

    private double GetWorkspaceWidth()
    {
        if (RelativeWidth > 0)
        {
            return RelativeWidth;
        }

        return Math.Max(DefaultRelativeWidth, StartX + CropWidth);
    }

    private double GetWorkspaceHeight()
    {
        if (RelativeHeight > 0)
        {
            return RelativeHeight;
        }

        return Math.Max(DefaultRelativeHeight, StartY + CropHeight);
    }

    private Rect GetViewportRect()
    {
        double canvasW = CropCanvas.Width;
        double canvasH = CropCanvas.Height;
        if (canvasW <= 0 || canvasH <= 0)
        {
            return Rect.Zero;
        }

        double workspaceW = Math.Max(1, GetWorkspaceWidth());
        double workspaceH = Math.Max(1, GetWorkspaceHeight());

        double ratioCanvas = canvasW / canvasH;
        double ratioWorkspace = workspaceW / workspaceH;

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
        if (CropCanvas.Width <= 0 || CropCanvas.Height <= 0)
        {
            return;
        }

        Rect viewport = GetViewportRect();
        AbsoluteLayout.SetLayoutBounds(CropViewport, viewport);

        double workspaceW = Math.Max(1, GetWorkspaceWidth());
        double workspaceH = Math.Max(1, GetWorkspaceHeight());

        double scaleX = viewport.Width / workspaceW;
        double scaleY = viewport.Height / workspaceH;

        double x = Math.Clamp(StartX, 0, workspaceW - MinCropSize);
        double y = Math.Clamp(StartY, 0, workspaceH - MinCropSize);
        double w = Math.Clamp(CropWidth, MinCropSize, workspaceW - x);
        double h = Math.Clamp(CropHeight, MinCropSize, workspaceH - y);

        double displayX = viewport.X + x * scaleX;
        double displayY = viewport.Y + y * scaleY;
        double displayW = w * scaleX;
        double displayH = h * scaleY;

        AbsoluteLayout.SetLayoutBounds(CropSelection, new Rect(displayX, displayY, displayW, displayH));
        CropSelection.AnchorX = 0.5;
        CropSelection.AnchorY = 0.5;
        CropSelection.Rotation = Angle;

        double cx = displayX + displayW / 2;
        double cy = displayY + displayH / 2;
        var center = new Point(cx, cy);

        Point tl = RotatePoint(new Point(displayX, displayY), center, Angle);
        Point tr = RotatePoint(new Point(displayX + displayW, displayY), center, Angle);
        Point bl = RotatePoint(new Point(displayX, displayY + displayH), center, Angle);
        Point br = RotatePoint(new Point(displayX + displayW, displayY + displayH), center, Angle);

        double hw = HandleSize;
        AbsoluteLayout.SetLayoutBounds(HandleTL, new Rect(tl.X - hw / 2, tl.Y - hw / 2, hw, hw));
        AbsoluteLayout.SetLayoutBounds(HandleTR, new Rect(tr.X - hw / 2, tr.Y - hw / 2, hw, hw));
        AbsoluteLayout.SetLayoutBounds(HandleBL, new Rect(bl.X - hw / 2, bl.Y - hw / 2, hw, hw));
        AbsoluteLayout.SetLayoutBounds(HandleBR, new Rect(br.X - hw / 2, br.Y - hw / 2, hw, hw));

        double rotateOffset = displayH / 2 + 26;
        Point rotateHandle = RotatePoint(new Point(cx, cy - rotateOffset), center, Angle);
        AbsoluteLayout.SetLayoutBounds(HandleRotate, new Rect(rotateHandle.X - 8, rotateHandle.Y - 8, 16, 16));

        CropSelection.IsVisible = Enabled;
        HandleTL.IsVisible = Enabled;
        HandleTR.IsVisible = Enabled;
        HandleBL.IsVisible = Enabled;
        HandleBR.IsVisible = Enabled;
        HandleRotate.IsVisible = Enabled;

        if (RelativeWidth > 0 && StartX + CropWidth > RelativeWidth)
        {
            SummaryLabel.TextColor = Colors.Red;
            SummaryLabel.Text = Localized.InteractableEditor_ClipCropConfiguratorView_WidthOverflow;
        }
        else if (RelativeHeight > 0 && StartY + CropHeight > RelativeHeight)
        {
            SummaryLabel.TextColor = Colors.Red;
            SummaryLabel.Text = Localized.InteractableEditor_ClipCropConfiguratorView_HeightOverflow;
        }
        else
        {
            SummaryLabel.TextColor = Color.FromRgb(0xD0, 0xD0, 0xD0);
            SummaryLabel.Text = $"({StartX}, {StartY}) {CropWidth} x {CropHeight}  {Angle:0.#}°";
        }
    }

    private void OnRotatePanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (!Enabled)
        {
            return;
        }

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                if (TryGetRotateHandleCenter(out Point handleCenter))
                {
                    _rotateStartHandleX = handleCenter.X;
                    _rotateStartHandleY = handleCenter.Y;
                }
                _lastPanTotalX = 0;
                _lastPanTotalY = 0;
                _lastDragNotifyTimeUtc = DateTime.MinValue;
                break;

            case GestureStatus.Running:
                _lastPanTotalX = e.TotalX;
                _lastPanTotalY = e.TotalY;
                ApplyRotatePan(e.TotalX, e.TotalY, finalize: false, notify: false);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                ApplyRotatePan(_lastPanTotalX, _lastPanTotalY, finalize: true);
                break;
        }
    }

    private void OnSelectionPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (!Enabled || DisableMove)
        {
            return;
        }

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _dragStartX = StartX;
                _dragStartY = StartY;
                _dragStartW = CropWidth;
                _dragStartH = CropHeight;
                _lastPanTotalX = 0;
                _lastPanTotalY = 0;
                _lastDragNotifyTimeUtc = DateTime.MinValue;
                break;

            case GestureStatus.Running:
                _lastPanTotalX = e.TotalX;
                _lastPanTotalY = e.TotalY;
                ApplyMovePan(e.TotalX, e.TotalY, finalize: false, notify: false);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                ApplyMovePan(_lastPanTotalX, _lastPanTotalY, finalize: true);
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
                _dragStartX = StartX;
                _dragStartY = StartY;
                _dragStartW = CropWidth;
                _dragStartH = CropHeight;
                _lastPanTotalX = 0;
                _lastPanTotalY = 0;
                _lastDragNotifyTimeUtc = DateTime.MinValue;
                break;

            case GestureStatus.Running:
                _lastPanTotalX = e.TotalX;
                _lastPanTotalY = e.TotalY;
                ApplyResizePan(handle, e.TotalX, e.TotalY, finalize: false, notify: false);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                ApplyResizePan(handle, _lastPanTotalX, _lastPanTotalY, finalize: true);
                break;
        }
    }

    private void ApplyMovePan(double totalX, double totalY, bool finalize, bool? notify = null)
    {
        Rect viewport = GetViewportRect();
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        double workspaceW = Math.Max(1, GetWorkspaceWidth());
        double workspaceH = Math.Max(1, GetWorkspaceHeight());

        double dx = totalX * workspaceW / viewport.Width;
        double dy = totalY * workspaceH / viewport.Height;

        double x = Math.Clamp(_dragStartX + dx, 0, workspaceW - _dragStartW);
        double y = Math.Clamp(_dragStartY + dy, 0, workspaceH - _dragStartH);

        ApplyRect(
            x,
            y,
            _dragStartW,
            _dragStartH,
            syncTextEntries: finalize,
            notifyChange: notify ?? (finalize || ShouldNotifyDuringDrag()));
    }

    private void ApplyResizePan(ResizeHandle handle, double totalX, double totalY, bool finalize, bool? notify = null)
    {
        Rect viewport = GetViewportRect();
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        double workspaceW = Math.Max(1, GetWorkspaceWidth());
        double workspaceH = Math.Max(1, GetWorkspaceHeight());

        double dx = totalX * workspaceW / viewport.Width;
        double dy = totalY * workspaceH / viewport.Height;

        if (DisableHorizontalResize) dx = 0;
        if (DisableVerticalResize) dy = 0;

        double x = _dragStartX;
        double y = _dragStartY;
        double w = _dragStartW;
        double h = _dragStartH;

        if (handle == ResizeHandle.TopLeft)
        {
            w = Math.Max(MinCropSize, _dragStartW - dx);
            h = Math.Max(MinCropSize, _dragStartH - dy);
            x = _dragStartX + (_dragStartW - w);
            y = _dragStartY + (_dragStartH - h);
        }
        else if (handle == ResizeHandle.TopRight)
        {
            w = Math.Max(MinCropSize, _dragStartW + dx);
            h = Math.Max(MinCropSize, _dragStartH - dy);
            y = _dragStartY + (_dragStartH - h);
        }
        else if (handle == ResizeHandle.BottomLeft)
        {
            w = Math.Max(MinCropSize, _dragStartW - dx);
            h = Math.Max(MinCropSize, _dragStartH + dy);
            x = _dragStartX + (_dragStartW - w);
        }
        else if (handle == ResizeHandle.BottomRight)
        {
            w = Math.Max(MinCropSize, _dragStartW + dx);
            h = Math.Max(MinCropSize, _dragStartH + dy);
        }

        x = Math.Clamp(x, 0, workspaceW - MinCropSize);
        y = Math.Clamp(y, 0, workspaceH - MinCropSize);
        w = Math.Clamp(w, MinCropSize, workspaceW - x);
        h = Math.Clamp(h, MinCropSize, workspaceH - y);

        ApplyRect(
            x,
            y,
            w,
            h,
            syncTextEntries: finalize,
            notifyChange: notify ?? (finalize || ShouldNotifyDuringDrag()));
    }

    private bool ShouldNotifyDuringDrag()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastDragNotifyTimeUtc >= DragNotifyInterval)
        {
            _lastDragNotifyTimeUtc = now;
            return true;
        }

        return false;
    }

    private void ApplyRotatePan(double totalX, double totalY, bool finalize, bool? notify = null)
    {
        if (!TryGetDisplayRect(out Rect displayRect))
        {
            return;
        }

        double centerX = displayRect.X + displayRect.Width / 2;
        double centerY = displayRect.Y + displayRect.Height / 2;
        double currentX = _rotateStartHandleX + totalX;
        double currentY = _rotateStartHandleY + totalY;

        double vx = currentX - centerX;
        double vy = currentY - centerY;
        if (Math.Abs(vx) < double.Epsilon && Math.Abs(vy) < double.Epsilon)
        {
            return;
        }

        double angle = Math.Atan2(vy, vx) * 180.0 / Math.PI + 90.0;
        angle = NormalizeAngle((float)angle);

        _isInternalUiSync = true;
        try
        {
            Angle = (float)angle;
            AngleSlider.Value = angle;
            AngleValueLabel.Text = $"{Angle:0.#}°";
        }
        finally
        {
            _isInternalUiSync = false;
        }

        UpdateCanvasVisuals();
        if (notify ?? (finalize || ShouldNotifyDuringDrag()))
        {
            InvokeConfigurationChanged();
        }
    }

    private void ApplyRect(double x, double y, double w, double h, bool syncTextEntries, bool notifyChange)
    {
        _isInternalUiSync = true;
        try
        {
            StartX = (int)Math.Round(Math.Max(0, x), MidpointRounding.AwayFromZero);
            StartY = (int)Math.Round(Math.Max(0, y), MidpointRounding.AwayFromZero);
            CropWidth = (int)Math.Round(Math.Max(MinCropSize, w), MidpointRounding.AwayFromZero);
            CropHeight = (int)Math.Round(Math.Max(MinCropSize, h), MidpointRounding.AwayFromZero);
        }
        finally
        {
            _isInternalUiSync = false;
        }

        UpdateCanvasVisuals();

        if (notifyChange)
        {
            InvokeConfigurationChanged();
        }
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > 180f)
        {
            angle -= 360f;
        }

        while (angle <= -180f)
        {
            angle += 360f;
        }

        return angle;
    }

    private static Point RotatePoint(Point point, Point center, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);

        double dx = point.X - center.X;
        double dy = point.Y - center.Y;

        return new Point(
            center.X + dx * cos - dy * sin,
            center.Y + dx * sin + dy * cos);
    }

    private bool TryGetDisplayRect(out Rect displayRect)
    {
        displayRect = Rect.Zero;
        Rect viewport = GetViewportRect();
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return false;
        }

        double workspaceW = Math.Max(1, GetWorkspaceWidth());
        double workspaceH = Math.Max(1, GetWorkspaceHeight());
        double scaleX = viewport.Width / workspaceW;
        double scaleY = viewport.Height / workspaceH;

        double x = Math.Clamp(StartX, 0, workspaceW - MinCropSize);
        double y = Math.Clamp(StartY, 0, workspaceH - MinCropSize);
        double w = Math.Clamp(CropWidth, MinCropSize, workspaceW - x);
        double h = Math.Clamp(CropHeight, MinCropSize, workspaceH - y);

        displayRect = new Rect(
            viewport.X + x * scaleX,
            viewport.Y + y * scaleY,
            w * scaleX,
            h * scaleY);
        return true;
    }

    private bool TryGetRotateHandleCenter(out Point center)
    {
        center = Point.Zero;
        if (!TryGetDisplayRect(out Rect displayRect))
        {
            return false;
        }

        double cx = displayRect.X + displayRect.Width / 2;
        double cy = displayRect.Y + displayRect.Height / 2;
        double rotateOffset = displayRect.Height / 2 + 26;
        center = RotatePoint(new Point(cx, cy - rotateOffset), new Point(cx, cy), Angle);
        return true;
    }
}
