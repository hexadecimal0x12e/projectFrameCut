using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using Color = Microsoft.Maui.Graphics.Color;
using Image = Microsoft.Maui.Controls.Image;
using IPicture = projectFrameCut.Shared.IPicture;
using Point = Microsoft.Maui.Graphics.Point;
using Rect = Microsoft.Maui.Graphics.Rect;

namespace projectFrameCut.DraftStuff;

public enum NodeKind { Effect, Input, Output }

public partial class DraftEffectBindingView : ContentView
{
    private const string ExtraDataInputXKey = "__DraftEffectBindingView_InputX__";
    private const string ExtraDataInputYKey = "__DraftEffectBindingView_InputY__";
    private const string ExtraDataOutputXKey = "__DraftEffectBindingView_OutputX__";
    private const string ExtraDataOutputYKey = "__DraftEffectBindingView_OutputY__";
    private const string ExtraDataViewScaleKey = "__DraftEffectBindingView_ViewScale__";
    private const string ExtraDataViewPanXKey = "__DraftEffectBindingView_ViewPanX__";
    private const string ExtraDataViewPanYKey = "__DraftEffectBindingView_ViewPanY__";

    private ClipElementUI? _clip;
    private DraftPage? _page;
    private Dictionary<Guid, NodeViewModel> _nodes = new();
    private ConnectionsDrawable _drawable;
    private NodeViewModel? _selectedNode;
    private NodePort? _pendingConnectionSource;

    private NodeViewModel? _inputNode;
    private NodeViewModel? _outputNode;

    private double _panStartX, _panStartY;
    private bool _isDraggingNodeOrPort;
    private double _startScale = 1.0;
    private const double PanelMinWidth = 230;
    private const double PanelMaxWidth = 800;
    private double _panelStartWidth;
    private double _panelWidthBeforeCollapse = 300;

    public bool PanelCollapsed { get; private set; }

    private bool _showIsNotVisibleInEffectEditorEffect;
    private bool _subscribedToPageEvents;

    /// <summary>
    /// Raised when effect bundles or connections have been modified inside this view.
    /// Subscribers (e.g. ClipInfoBuilder's effect tab) should rebuild effects and refresh UI.
    /// </summary>
    public event Action? EffectBundlesChanged;

    private void NotifyEffectBundlesChanged() => EffectBundlesChanged?.Invoke();

    private const double NodeDefaultWidth = 150;
    private const double NodeDefaultHeight = 80;
    private const double NodeSpacing = 20;
    private const int NodePlacementMaxAttempts = 200;


    public DraftEffectBindingView()
    {
        BindingContext = this;
        InitializeComponent();
        _drawable = new ConnectionsDrawable(_nodes);
        ConnectionsLayer.Drawable = _drawable;

        ZoomInButton.Clicked += OnZoomIn;
        ZoomOutButton.Clicked += OnZoomOut;
        ResetButton.Clicked += OnReset;

        InfoLabel.Text = PPLocalizedResources.EffectBindView_Hint;

        _panelWidthBeforeCollapse = RightPanelColumn.Width.Value;
        UpdatePanelToggleText();

    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
#if WINDOWS
        if (Handler?.PlatformView is Microsoft.UI.Xaml.UIElement platformView)
        {
            platformView.PointerWheelChanged -= OnWindowsPointerWheelChanged;
            platformView.PointerWheelChanged += OnWindowsPointerWheelChanged;
        }
#endif
        if (Handler == null) UnsubscribeFromPageEvents();
    }

#if WINDOWS
    private void OnWindowsPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(null).Properties;
        if (properties.MouseWheelDelta != 0)
        {
            // Check for Ctrl key state
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            if ((state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down)
            {
                int delta = properties.MouseWheelDelta;
                // Standard mouse wheel delta is 120
                double zoomFactor = delta > 0 ? 1.1 : 0.9;

                double currentScale = NodesContainer.Scale;
                double targetScale = Math.Clamp(currentScale * zoomFactor, 0.2, 5.0);

                // Get mouse position relative to the view
                var point = e.GetCurrentPoint((Microsoft.UI.Xaml.UIElement)sender).Position;

                ApplyZoom(targetScale, point.X, point.Y);

                e.Handled = true;
            }
        }
    }
#endif

    private void OnZoomIn(object? sender, EventArgs e)
    {
        double targetScale = Math.Min(NodesContainer.Scale * 1.2, 5.0);
        // Zoom to center of the view
        ApplyZoom(targetScale, this.Width / 2, this.Height / 2);
    }

    private void OnZoomOut(object? sender, EventArgs e)
    {
        double targetScale = Math.Max(NodesContainer.Scale / 1.2, 0.2);
        // Zoom to center of the view
        ApplyZoom(targetScale, this.Width / 2, this.Height / 2);
    }

    private void OnCanvasPinchUpdated(object sender, PinchGestureUpdatedEventArgs e)
    {
        if (e.Status == GestureStatus.Started)
        {
            _startScale = NodesContainer.Scale;
        }
        else if (e.Status == GestureStatus.Running)
        {
            double targetScale = Math.Clamp(_startScale * e.Scale, 0.2, 5.0);

            if (sender is View v)
            {
                double focalX = e.ScaleOrigin.X * v.Width;
                double focalY = e.ScaleOrigin.Y * v.Height;

                ApplyZoom(targetScale, focalX, focalY);
            }
        }
    }

    private void ApplyZoom(double targetScale, double focalX, double focalY)
    {
        double oldScale = NodesContainer.Scale;
        double oldTransX = NodesContainer.TranslationX;
        double oldTransY = NodesContainer.TranslationY;

        // Calculate the content point under the focal point relative to standard 0,0 anchor
        // Screen = Content * Scale + Trans
        // Content = (Screen - Trans) / Scale

        double contentX = (focalX - oldTransX) / oldScale;
        double contentY = (focalY - oldTransY) / oldScale;

        // NewTrans = Screen - Content * NewScale
        double newTransX = focalX - (contentX * targetScale);
        double newTransY = focalY - (contentY * targetScale);

        NodesContainer.Scale = targetScale;
        NodesContainer.TranslationX = newTransX;
        NodesContainer.TranslationY = newTransY;

        _drawable.PanX = newTransX;
        _drawable.PanY = newTransY;
        UpdateDrawableScale();
        SaveViewTransform();
    }

    private void OnReset(object? sender, EventArgs e)
    {
        NodesContainer.AnchorX = 0;
        NodesContainer.AnchorY = 0;

        NodesContainer.Scale = 1.0;
        NodesContainer.TranslationX = 0;
        NodesContainer.TranslationY = 0;

        _drawable.PanX = 0;
        _drawable.PanY = 0;
        UpdateDrawableScale();
        ConnectionsLayer.Invalidate();
        SaveViewTransform();
    }

    private void OnSplitterPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panelStartWidth = RightPanelColumn.Width.Value;
                if (PanelCollapsed)
                {
                    PanelCollapsed = false;
                    RightPanel.IsVisible = true;
                }
                break;
            case GestureStatus.Running:
                double newWidth = Math.Clamp(_panelStartWidth - e.TotalX, PanelMinWidth, PanelMaxWidth);
                RightPanelColumn.Width = new GridLength(newWidth, GridUnitType.Absolute);
                _panelWidthBeforeCollapse = newWidth;
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                UpdatePanelToggleText();
                break;
        }
    }

    private void OnTogglePanelClicked(object? sender, EventArgs e)
    {
        if (PanelCollapsed)
        {
            double restoredWidth = Math.Clamp(_panelWidthBeforeCollapse, PanelMinWidth, PanelMaxWidth);
            RightPanelColumn.Width = new GridLength(restoredWidth, GridUnitType.Absolute);
            RightPanel.IsVisible = true;
            PanelCollapsed = false;
        }
        else
        {
            _panelWidthBeforeCollapse = Math.Clamp(RightPanelColumn.Width.Value, PanelMinWidth, PanelMaxWidth);
            RightPanelColumn.Width = new GridLength(0, GridUnitType.Absolute);
            RightPanel.IsVisible = false;
            PanelCollapsed = true;
        }
        OnPropertyChanged(nameof(PanelCollapsed));
        UpdatePanelToggleText();
    }

    private void UpdatePanelToggleText()
    {
        TogglePanelButton.Text = PanelCollapsed ? "<" : ">";
        OnPropertyChanged(nameof(PanelCollapsed));
    }

    private void UpdateDrawableScale()
    {
        // Ensure Anchors are 0,0 so our math (x * scale + pan) holds true
        NodesContainer.AnchorX = 0;
        NodesContainer.AnchorY = 0;

        _drawable.Scale = NodesContainer.Scale;
        ConnectionsLayer.Invalidate();
    }

    public void LoadClip(ClipElementUI clip, DraftPage? page = null, bool showIsNotVisibleInEffectEditorEffect = false)
    {
        _clip = clip;
        _page = page;
        _showIsNotVisibleInEffectEditorEffect = showIsNotVisibleInEffectEditorEffect;
        UpdateAddEffectsPanel();
        _nodes.Clear();
        NodesContainer.Children.Clear();
        PropertiesPanel.Children.Clear();

        // Reset View Transform
        NodesContainer.Scale = 1.0;
        NodesContainer.TranslationX = 0;
        NodesContainer.TranslationY = 0;
        NodesContainer.AnchorX = 0;
        NodesContainer.AnchorY = 0;
        _drawable.PanX = 0;
        _drawable.PanY = 0;
        _drawable.Scale = 1.0;

        // Create System Nodes
        bool inputHasPosition = _clip?.ExtraData?.ContainsKey(ExtraDataInputXKey) == true && _clip?.ExtraData?.ContainsKey(ExtraDataInputYKey) == true;
        var inputX = GetExtraDataDouble(ExtraDataInputXKey, 50);
        var inputY = GetExtraDataDouble(ExtraDataInputYKey, 150);
        _inputNode = new NodeViewModel
        {
            Kind = NodeKind.Input,
            X = inputX,
            Y = inputY,
            Id = IEffectBundle.InputAnchorGUID,
            Bundle = null,
            InputAnchorID = IEffectBundle.NoConnectionGUID,
            OutputAnchorID = IEffectBundle.NoConnectionGUID,
            DisplayName = PPLocalizedResources.EffectBind_SourcePicture
        };
        if (!inputHasPosition)
        {
            var pos = FindNonOverlappingPosition(_inputNode, inputX, inputY);
            _inputNode.X = pos.X;
            _inputNode.Y = pos.Y;
        }
        AddNode(_inputNode);

        if (_clip.EffectBundles != null)
        {
            // The logic to load factories and instantiate IEffectBundle has been removed.
            // We iterate bundleData directly to create visual nodes.

            var effectTarget = _clip.GetEffectTarget();
            foreach (var bundle in _clip.EffectBundles.Values)
            {
                // Skip bundles that are internal/special effects (e.g. Crop, Place, Resize)
                // to keep consistent with ClipInfoBuilder.BuildEffectTab filtering behavior.
                if (!showIsNotVisibleInEffectEditorEffect && (bundle.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor) || !bundle.Target.HasFlag(_clip.GetEffectTarget())))
                    continue;

                // Placeholder Node Creation
                // We assume 1 input port "Input" because we cannot inspect the real effect logic anymore.
                var node = new NodeViewModel
                {
                    Id = bundle.Id,
                    Kind = NodeKind.Effect,
                    Bundle = bundle,
                    InputPortNames = bundle.InputAnchorsDisplayName ?? [string.IsNullOrWhiteSpace(bundle.InputAnchorDisplayName) ? "Frame" : bundle.InputAnchorDisplayName],
                    DisplayName = bundle.Name
                };

                if (bundle.Parameters.TryGetValue("__DraftEffectBindingView_InteractiveEditorX__", out var xObj) && bundle.Parameters.TryGetValue("__DraftEffectBindingView_InteractiveEditorY__", out var yObj) && xObj is double x && yObj is double y)
                {
                    node.X = x;
                    node.Y = y;
                }
                else
                {
                    var startX = 250 + (_nodes.Count * 50);
                    var startY = 150 + (_nodes.Count * 20);
                    var pos = FindNonOverlappingPosition(node, startX, startY);
                    node.X = pos.X;
                    node.Y = pos.Y;
                }

                AddNode(node);
            }
        }

        // Create Output Node
        // Position it far right
        double maxX = _nodes.Max(kvp => kvp.Value.X);
        bool outputHasPosition = _clip?.ExtraData?.ContainsKey(ExtraDataOutputXKey) == true && _clip?.ExtraData?.ContainsKey(ExtraDataOutputYKey) == true;
        var outputX = GetExtraDataDouble(ExtraDataOutputXKey, Math.Max(maxX + 200, 600));
        var outputY = GetExtraDataDouble(ExtraDataOutputYKey, 150);
        _outputNode = new NodeViewModel
        {
            Kind = NodeKind.Output,
            X = outputX,
            Y = outputY,
            Id = IEffectBundle.OutputAnchorGUID,
            Bundle = null,
            InputAnchorID = IEffectBundle.NoConnectionGUID,
            OutputAnchorID = IEffectBundle.NoConnectionGUID,
            DisplayName = PPLocalizedResources.EffectBind_FinalResult
        };
        if (!outputHasPosition)
        {
            var pos = FindNonOverlappingPosition(_outputNode, outputX, outputY);
            _outputNode.X = pos.X;
            _outputNode.Y = pos.Y;
        }
        AddNode(_outputNode);

        SubscribeToPageEvents();
        RebuildConnections();
        ApplySavedViewTransform();
        ConnectionsLayer.Invalidate();
    }

    /// <summary>
    /// Reloads all effect data from the current clip.
    /// Call this when external code (e.g. ClipInfoBuilder) has modified effect bundles
    /// and the binding view needs to reflect the latest state.
    /// </summary>
    public void Reload()
    {
        if (_clip == null) return;
        LoadClip(_clip, _page, _showIsNotVisibleInEffectEditorEffect);
    }

    private void AddNode(NodeViewModel node)
    {
        var view = CreateNodeView(node);
        node.View = view;
        NodesContainer.Add(view);
        AbsoluteLayout.SetLayoutBounds(view, new Rect(node.X, node.Y, -1, AbsoluteLayout.AutoSize));
        _nodes.Add(node.Id, node);
    }

    private Rect GetNodeBounds(NodeViewModel node, double x, double y)
    {
        double width = node.View?.Width > 0 ? node.View.Width : NodeDefaultWidth;
        double height = node.View?.Height > 0 ? node.View.Height : NodeDefaultHeight;
        return new Rect(x, y, width, height);
    }

    private Rect GetNodeBoundsPadded(NodeViewModel node, double x, double y)
    {
        var rect = GetNodeBounds(node, x, y);
        if (NodeSpacing <= 0) return rect;
        double pad = NodeSpacing / 2.0;
        return new Rect(rect.X - pad, rect.Y - pad, rect.Width + (pad * 2), rect.Height + (pad * 2));
    }

    private static bool RectsOverlap(Rect a, Rect b)
    {
        return a.X < b.X + b.Width &&
               a.X + a.Width > b.X &&
               a.Y < b.Y + b.Height &&
               a.Y + a.Height > b.Y;
    }

    private bool IsOverlapping(NodeViewModel node, double x, double y)
    {
        var candidate = GetNodeBoundsPadded(node, x, y);
        foreach (var other in _nodes.Values)
        {
            if (other == node) continue;
            var otherRect = GetNodeBoundsPadded(other, other.X, other.Y);
            if (RectsOverlap(candidate, otherRect)) return true;
        }
        return false;
    }

    private Point FindNonOverlappingPosition(NodeViewModel node, double startX, double startY)
    {
        if (!IsOverlapping(node, startX, startY)) return new Point(startX, startY);

        var size = GetNodeBounds(node, startX, startY);
        double stepX = size.Width + NodeSpacing;
        double stepY = size.Height + NodeSpacing;
        int attempts = 0;
        int maxRadius = 10;

        for (int radius = 1; radius <= maxRadius && attempts < NodePlacementMaxAttempts; radius++)
        {
            for (int dx = -radius; dx <= radius && attempts < NodePlacementMaxAttempts; dx++)
            {
                for (int dy = -radius; dy <= radius && attempts < NodePlacementMaxAttempts; dy++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;
                    double x = startX + (dx * stepX);
                    double y = startY + (dy * stepY);
                    attempts++;
                    if (!IsOverlapping(node, x, y)) return new Point(x, y);
                }
            }
        }

        return new Point(startX, startY);
    }

    private bool TryMoveNodeWithoutOverlap(NodeViewModel node, double proposedX, double proposedY, out double resolvedX, out double resolvedY, out bool appliedX, out bool appliedY)
    {
        if (!IsOverlapping(node, proposedX, proposedY))
        {
            resolvedX = proposedX;
            resolvedY = proposedY;
            appliedX = true;
            appliedY = true;
            return true;
        }

        resolvedX = node.X;
        resolvedY = node.Y;
        appliedX = false;
        appliedY = false;
        bool moved = false;

        if (!IsOverlapping(node, proposedX, node.Y))
        {
            resolvedX = proposedX;
            appliedX = true;
            moved = true;
        }

        if (!IsOverlapping(node, resolvedX, proposedY))
        {
            resolvedY = proposedY;
            appliedY = true;
            moved = true;
        }

        return moved;
    }

    private VerticalStackLayout CreateNodeView(NodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var container = new VerticalStackLayout
        {
            Spacing = 0,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = false
        };

        var frame = new Border
        {
            Stroke = node.Kind == NodeKind.Effect ? Colors.Gray : Colors.White,
            StrokeThickness = node.Kind == NodeKind.Effect ? 2 : 4,
            StrokeShape = new RoundRectangle { CornerRadius = 5 },
            BackgroundColor = Color.FromArgb("#2d2d2d"),
            Padding = 5,
            HeightRequest = 80,
            MinimumWidthRequest = 150,
            ZIndex = 2
        };

        var title = node.Kind switch
        {
            NodeKind.Input => PPLocalizedResources.EffectBind_SourcePicture,
            NodeKind.Output => PPLocalizedResources.EffectBind_FinalResult,
            _ => node?.DisplayName ?? node?.Bundle?.TypeName ?? "?"
        };

        var label = new Label
        {
            Text = title,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            FontSize = 14,
            FontAttributes = node?.Kind == NodeKind.Effect ? FontAttributes.None : FontAttributes.Bold,
            LineBreakMode = LineBreakMode.NoWrap
        };

        ToolTipProperties.SetText(label, title);

        // Ports
        View inputPortView;

        if (node.InputPortNames != null && node.InputPortNames.Length > 0)
        {
            var stack = new VerticalStackLayout { Spacing = 5, VerticalOptions = LayoutOptions.Center };
            for (int i = 0; i < node.InputPortNames.Length; i++)
            {
                var portName = node.InputPortNames[i];
                var portBox = new BoxView { Color = Colors.Green, WidthRequest = 15, HeightRequest = 15 };

                int portIndex = i; // Capture for lambda

                var pTap = new TapGestureRecognizer();
                pTap.Tapped += (s, e) => OnPortClicked(node, true, portIndex);
                portBox.GestureRecognizers.Add(pTap);

                var pPan = new PanGestureRecognizer();
                pPan.PanUpdated += (s, e) => OnPortPan(node, e, true, portIndex);
                portBox.GestureRecognizers.Add(pPan);

                var row = new HorizontalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
                row.Add(portBox);
                if (!string.IsNullOrEmpty(portName))
                {
                    var l = new Label
                    {
                        Text = portName,
                        TextColor = Colors.LightGray,
                        FontSize = 10,
                        VerticalOptions = LayoutOptions.Center
                    };
                    ToolTipProperties.SetText(l, portName);
                    row.Add(l);
                }

                stack.Add(row);
            }
            inputPortView = stack;
        }
        else
        {
            var box = new BoxView { Color = Colors.Green, WidthRequest = 15, HeightRequest = 15, VerticalOptions = LayoutOptions.Center };
            // Original single port logic
            var inputTap = new TapGestureRecognizer();
            inputTap.Tapped += (s, e) => OnPortClicked(node, true);
            box.GestureRecognizers.Add(inputTap);

            var inputPan = new PanGestureRecognizer();
            inputPan.PanUpdated += (s, e) => OnPortPan(node, e, true);
            box.GestureRecognizers.Add(inputPan);
            ToolTipProperties.SetText(box, PPLocalizedResources.EffectBind_InputAnchor);

            inputPortView = box;
        }

        var outputPort = new BoxView { Color = Colors.Red, WidthRequest = 15, HeightRequest = 15, VerticalOptions = LayoutOptions.Center };
        ToolTipProperties.SetText(outputPort, node?.Bundle?.OutputAnchorDisplayName ?? PPLocalizedResources.EffectBind_OutputAnchor);

        // Handle visibility for System Nodes
        if (node.Kind == NodeKind.Input) inputPortView.IsVisible = false; // Hide Input on Input Node
        if (node.Kind == NodeKind.Output) outputPort.Color = Colors.Transparent; // Hide Output on Output Node

        // Interaction
        UIServices.RegisterSelectOrContextMenu(
            frame,
            OnSelected: () =>
            {
                SelectNode(node);
            },
            OnClicked: () =>
            {
#if ANDROID || IOS
                SelectNode(node);
#elif WINDOWS || MACCATALYST
                if (node.Kind == NodeKind.Effect)
                {
                    DisconnectNode(node);
                }
                else
                {
                    SelectNode(node);
                }
#endif
            },
            OnContextMenuClick: async () => await ShowContextMenu(node)
        );

        var bodyActionContainer = new Grid();

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (s, e) => OnNodePan(node, e);
        bodyActionContainer.GestureRecognizers.Add(pan);

        // Output Port Interaction (Single Output mostly)
        var outputTap = new TapGestureRecognizer();
        outputTap.Tapped += (s, e) => OnPortClicked(node, false);
        outputPort.GestureRecognizers.Add(outputTap);

        var outputPan = new PanGestureRecognizer();
        outputPan.PanUpdated += (s, e) => OnPortPan(node, e, false);
        outputPort.GestureRecognizers.Add(outputPan);

        // Layout
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Star }, new ColumnDefinition { Width = 20 } } };

        bodyActionContainer.Add(label);

        layout.Add(inputPortView, 0, 0);
        layout.Add(bodyActionContainer, 1, 0);
        layout.Add(outputPort, 2, 0);

        if (node.OutputAnchorID == IEffectBundle.NoConnectionGUID && node.InputAnchorID != IEffectBundle.NoConnectionGUID)
        {
            frame.Opacity = 0.8;
        }

        frame.SizeChanged += (s, e) => ConnectionsLayer.Invalidate();

        frame.Content = layout;

        container.Add(frame);

        var arrow = new BoxView
        {
            Color = Colors.White,
            WidthRequest = 2,
            HeightRequest = 20,
            HorizontalOptions = LayoutOptions.Center,
            IsVisible = false // Hidden initially
        };

        var previewBorder = new Border
        {
            Stroke = Colors.White,
            StrokeThickness = 2,
            StrokeShape = new RoundRectangle { CornerRadius = 5 },
            BackgroundColor = Colors.Black,
            Padding = 0,
            WidthRequest = node.Width,
            HeightRequest = 120,
            HorizontalOptions = LayoutOptions.Center,
            IsVisible = false // Hidden initially
        };

        var previewImage = new Microsoft.Maui.Controls.Image
        {
            Aspect = Aspect.AspectFit,
        };

        previewBorder.Content = previewImage;

        container.Add(arrow);
        container.Add(previewBorder);

        // Property to update preview
        node.PreviewImageChanged += (s, img) =>
        {
            previewImage.Source = img;
            bool hasImage = img != null;
            arrow.IsVisible = hasImage;
            previewBorder.IsVisible = hasImage;
        };

        // Trigger initial update if already set
        if (node.PreviewImage != null)
        {
            previewImage.Source = node.PreviewImage;
            arrow.IsVisible = true;
            previewBorder.IsVisible = true;
        }

        return container;
    }

    private void OnNodePan(NodeViewModel node, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _isDraggingNodeOrPort = true;
                node.DragStartX = node.X;
                node.DragStartY = node.Y;
                node.DragTotalX = 0;
                node.DragTotalY = 0;
                ConnectionsLayer.Invalidate(); // Redraw connections while dragging
                break;
            case GestureStatus.Running:
                // Adjust movement delta by scale factor to keep mouse sync
                double scale = NodesContainer.Scale;
                if (scale <= 0) scale = 0.1;

                double deltaX = (e.TotalX - node.DragTotalX) / scale;
                double deltaY = (e.TotalY - node.DragTotalY) / scale;

                double proposedX = node.X + deltaX;
                double proposedY = node.Y + deltaY;
                if (TryMoveNodeWithoutOverlap(node, proposedX, proposedY, out var resolvedX, out var resolvedY, out var appliedX, out var appliedY))
                {
                    node.X = resolvedX;
                    node.Y = resolvedY;
                    if (appliedX) node.DragTotalX = e.TotalX;
                    if (appliedY) node.DragTotalY = e.TotalY;
                }

                // Use AutoSize for height while dragging so preview (if present)
                // isn't clipped by a fixed height.
                AbsoluteLayout.SetLayoutBounds(node.View, new Rect(node.X, node.Y, -1, AbsoluteLayout.AutoSize));
                // Ensure layout height is AutoSize after drag ends so preview remains visible.
                AbsoluteLayout.SetLayoutBounds(node.View, new Rect(node.X, node.Y, -1, AbsoluteLayout.AutoSize));
                ConnectionsLayer.Invalidate();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _isDraggingNodeOrPort = false;
                if (node.Kind == NodeKind.Effect)
                {
                    node?.Bundle?.Parameters?["__DraftEffectBindingView_InteractiveEditorX__"] = node.X;
                    node?.Bundle?.Parameters?["__DraftEffectBindingView_InteractiveEditorY__"] = node.Y;
                }
                else
                {
                    SaveSystemNodePosition(node);
                }
                ConnectionsLayer.Invalidate();
                break;
        }
    }

    private void SaveSystemNodePosition(NodeViewModel node)
    {
        if (_clip == null) return;
        _clip.ExtraData ??= new Dictionary<string, object>();

        switch (node.Kind)
        {
            case NodeKind.Input:
                _clip.ExtraData[ExtraDataInputXKey] = node.X;
                _clip.ExtraData[ExtraDataInputYKey] = node.Y;
                break;
            case NodeKind.Output:
                _clip.ExtraData[ExtraDataOutputXKey] = node.X;
                _clip.ExtraData[ExtraDataOutputYKey] = node.Y;
                break;
        }
    }

    private double GetExtraDataDouble(string key, double fallback)
    {
        if (_clip?.ExtraData == null) return fallback;
        if (_clip.ExtraData.TryGetValue(key, out var value))
        {
            if (value is JsonElement e && e.TryGetDouble(out var jd)) return jd;
            if (value is double d) return d;
            if (value is float f) return f;
            if (value is int i) return i;
            if (value is long l) return l;
            if (value is decimal m) return (double)m;
            if (value is string s && double.TryParse(s, out var parsed)) return parsed;
        }
        return fallback;
    }

    private void SaveViewTransform()
    {
        if (_clip == null) return;
        _clip.ExtraData ??= new Dictionary<string, object>();
        _clip.ExtraData[ExtraDataViewScaleKey] = NodesContainer.Scale;
        _clip.ExtraData[ExtraDataViewPanXKey] = NodesContainer.TranslationX;
        _clip.ExtraData[ExtraDataViewPanYKey] = NodesContainer.TranslationY;
    }

    private void ApplySavedViewTransform()
    {
        if (_clip == null) return;
        double scale = GetExtraDataDouble(ExtraDataViewScaleKey, 1.0);
        double panX = GetExtraDataDouble(ExtraDataViewPanXKey, 0.0);
        double panY = GetExtraDataDouble(ExtraDataViewPanYKey, 0.0);

        NodesContainer.AnchorX = 0;
        NodesContainer.AnchorY = 0;
        NodesContainer.Scale = Math.Clamp(scale, 0.2, 5.0);
        NodesContainer.TranslationX = panX;
        NodesContainer.TranslationY = panY;

        _drawable.PanX = panX;
        _drawable.PanY = panY;
        UpdateDrawableScale();
    }

    private void SubscribeToPageEvents()
    {
        if (_page == null || _subscribedToPageEvents) return;
        _page.OnClipChanged += OnPageClipChanged;
        _subscribedToPageEvents = true;
        Unloaded += OnViewUnloaded;
    }

    private void UnsubscribeFromPageEvents()
    {
        if (_page == null || !_subscribedToPageEvents) return;
        _page.OnClipChanged -= OnPageClipChanged;
        _subscribedToPageEvents = false;
        Unloaded -= OnViewUnloaded;
    }

    private void OnViewUnloaded(object? sender, EventArgs e)
    {
        UnsubscribeFromPageEvents();
    }

    private void OnPageClipChanged(object? sender, ClipUpdateEventArgs e)
    {
        if (_clip == null) return;
        if (e.SourceId != _clip.Id) return;
        if (e.Reason != ClipUpdateReason.PropertyChanged) return;

        Dispatcher.Dispatch(() =>
        {
            if (_clip == null) return;
            // Avoid re-entrant Reload() if we're the one making changes
            if (_isDraggingNodeOrPort) return;
            Reload();
        });
    }

    private void OnPortPan(NodeViewModel node, PanUpdatedEventArgs e, bool isInput, int portIndex = 0)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _isDraggingNodeOrPort = true;
                _drawable.DragSourceNode = node;
                _drawable.IsDraggingFromInput = isInput;
                _drawable.DragSourcePortIndex = portIndex;
                // Initialize drag point at the port location (Screen Space)
                // Use current scale for initial position calculation
                double s = NodesContainer.Scale;

                double portY = 40;
                if (isInput && node.InputPortNames != null && node.InputPortNames.Length > 1)
                {
                    double portSize = 15;
                    double spacing = 5;
                    int n = node.InputPortNames.Length;
                    double stackHeight = n * portSize + (n - 1) * spacing;
                    double startY = (80 - stackHeight) / 2;
                    portY = startY + portIndex * (portSize + spacing) + (portSize / 2);
                }

                if (isInput)
                    _drawable.DragPoint = new Point((node.X * s) + _drawable.PanX, (node.Y * s) + _drawable.PanY + portY * s);
                else
                    _drawable.DragPoint = new Point((node.X * s) + _drawable.PanX + node.Width * s, (node.Y * s) + _drawable.PanY + 40 * s);
                break;

            case GestureStatus.Running:
                // Calculate new drag point based on start + delta
                // Note: e.TotalX/Y are in screen coords, so we add them directly to screen pos

                // Re-calculate Start Point in Screen Space
                double scale = NodesContainer.Scale;

                double startPortY = 40;
                if (isInput && node.InputPortNames != null && node.InputPortNames.Length > 1)
                {
                    double portSize = 15;
                    double spacing = 5;
                    int n = node.InputPortNames.Length;
                    double stackHeight = n * portSize + (n - 1) * spacing;
                    double startY = (80 - stackHeight) / 2;
                    startPortY = startY + portIndex * (portSize + spacing) + (portSize / 2);
                }

                double baseX = (isInput ? node.X : node.X + node.Width) * scale + _drawable.PanX;
                double baseY = (node.Y + startPortY) * scale + _drawable.PanY;

                _drawable.DragPoint = new Point(baseX + e.TotalX, baseY + e.TotalY);
                ConnectionsLayer.Invalidate();
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _isDraggingNodeOrPort = false;
                if (_drawable.DragPoint.HasValue)
                {
                    var dropPoint = _drawable.DragPoint.Value;
                    NodeViewModel? match = null;
                    int matchPortIndex = 0;
                    double finalScale = NodesContainer.Scale;

                    // Hit Test
                    foreach (var kvp in _nodes)
                    {
                        var candidate = kvp.Value;
                        if (candidate == node) continue;

                        if (isInput)
                        {
                            // Dragging FROM Input, looking for Output
                            if (candidate.Kind == NodeKind.Output) continue;

                            // Target in Screen Space (Output Port)
                            double targetX = (candidate.X + candidate.Width) * finalScale + _drawable.PanX;
                            double targetY = (candidate.Y + 40) * finalScale + _drawable.PanY;

                            if (Math.Abs(targetX - dropPoint.X) < 40 && Math.Abs(targetY - dropPoint.Y) < 40)
                            {
                                match = candidate;
                                matchPortIndex = 0;
                                break;
                            }
                        }
                        else
                        {
                            // Dragging FROM Output, looking for Input
                            if (candidate.Kind == NodeKind.Input) continue;

                            // If Candidate has Multiple Inputs, Check which one we hit
                            if (candidate.InputPortNames != null && candidate.InputPortNames.Length > 1)
                            {
                                double portSize = 15;
                                double spacing = 5;
                                int n = candidate.InputPortNames.Length;
                                double stackHeight = n * portSize + (n - 1) * spacing;
                                double startY = (80 - stackHeight) / 2;

                                for (int i = 0; i < n; i++)
                                {
                                    double thisPortY = startY + i * (portSize + spacing) + (portSize / 2);

                                    double targetX = candidate.X * finalScale + _drawable.PanX;
                                    double targetY = (candidate.Y + thisPortY) * finalScale + _drawable.PanY;

                                    if (Math.Abs(targetX - dropPoint.X) < 30 && Math.Abs(targetY - dropPoint.Y) < 30)
                                    {
                                        match = candidate;
                                        matchPortIndex = i;
                                        break;
                                    }
                                }
                                if (match != null) break;
                            }
                            else
                            {
                                double targetX = candidate.X * finalScale + _drawable.PanX;
                                double targetY = (candidate.Y + 40) * finalScale + _drawable.PanY;
                                if (Math.Abs(targetX - dropPoint.X) < 40 && Math.Abs(targetY - dropPoint.Y) < 40)
                                {
                                    match = candidate;
                                    matchPortIndex = 0;
                                    break;
                                }
                            }
                        }
                    }

                    if (match != null)
                    {
                        if (isInput)
                        {
                            // Dragged from Input (Target) -> Found Output (Source)
                            // node is Target, match is Source
                            ConnectNodes(match, node, portIndex); // portIndex is from drag start (Target Input Port)
                        }
                        else
                        {
                            // Dragged from Output (Source) -> Found Input (Target)
                            // node is Source, match is Target
                            ConnectNodes(node, match, matchPortIndex);
                        }
                    }
                    else
                    {
                        SetStatusText(PPLocalizedResources.EffectBindView_ConnectedFail);
                    }
                }

                _drawable.DragSourceNode = null;
                _drawable.DragPoint = null;
                ConnectionsLayer.Invalidate();
                RebuildConnections();
                break;
        }
    }

    private void OnCanvasPanUpdated(object sender, PanUpdatedEventArgs e)
    {
        if (_isDraggingNodeOrPort) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartX = NodesContainer.TranslationX;
                _panStartY = NodesContainer.TranslationY;
                break;
            case GestureStatus.Running:
                NodesContainer.TranslationX = _panStartX + e.TotalX;
                NodesContainer.TranslationY = _panStartY + e.TotalY;

                _drawable.PanX = NodesContainer.TranslationX;
                _drawable.PanY = NodesContainer.TranslationY;
                ConnectionsLayer.Invalidate();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                SaveViewTransform();
                break;
        }
    }

    private void SelectNode(NodeViewModel node)
    {
        if (_selectedNode != null && _selectedNode.View is Border b) b.Stroke = Colors.Gray;
        _selectedNode = node;
        if (node.View is Border b2) b2.Stroke = Colors.Yellow;

        PropertiesPanel.Children.Clear();
        AfterEffectBigPreview.Source = node.PreviewImage;

        PropertiesPanel.Children.Add(new Label { Text = node.DisplayName, FontAttributes = FontAttributes.Bold, HorizontalOptions = LayoutOptions.Center });


        if (node.Kind != NodeKind.Effect)
        {
            return;
        }

        try
        {
            ArgumentNullException.ThrowIfNull(node.Bundle);
            var ppb = node.Bundle.CreateUI();
            ArgumentNullException.ThrowIfNull(ppb, $"CreateUI() for {node.Bundle?.TypeName}");
            ppb.PropertyChanged += (s, args) =>
            {
                ArgumentNullException.ThrowIfNull(node.Bundle);
                node.Bundle.Parameters = node.Bundle.HandlePropertyPanelChange(args);
                RebuildConnections();
                ConnectionsLayer.Invalidate();
                NotifyEffectBundlesChanged();
            };


            PropertiesPanel.Children.Add(ppb.BuildWithScrollView());
        }
        catch (Exception ex)
        {
            PropertiesPanel.Children.Add(new Label { Text = $"Error loading properties. {Environment.NewLine}{Localized._ExceptionTemplate(ex)}" });
        }

    }

    private async Task ShowContextMenu(NodeViewModel node)
    {
        if (node.Kind != NodeKind.Effect) return;
        string[] commands = [
            PPLocalizedResources.EffectBindView_Configure,
            PPLocalizedResources.EffectBindView_Disconnect,
            Localized.DraftPage_ContextMenu_Delete
            ];
        async Task process(int command)
        {
            switch (command)
            {
                case 0:
                    {
                        SelectNode(node);
                        RightTabView.SelectedIndex = 0;
                        break;
                    }
                case 1:
                    {
                        DisconnectNode(node);
                        break;
                    }
                case 2:
                    {
                        RemoveEffect(node);
                        break;
                    }
                default:
                    break;
            }

        }

        //if (IContextMenuBuilder.Default is IContextMenuBuilder)
        //{
        //    var b = IContextMenuBuilder.Default.CreateNewInstance();
        //    for (int i = 0; i < commands.Length; i++)
        //    {
        //        b.AddCommand(commands[i], async () => await process(i));
        //    }
        //    if (node?.View is not null) b.TryShow(node.View);
        //}
        //else
        {
            if (Parent is MultiWindowItem i)
            {
                var r = await i.DisplayActionSheetAsync(Localized.HomePage_ProjectContextMenu(node.DisplayName), Localized._Cancel, null, commands);
                await process(Array.IndexOf(commands, r));
            }
            else
            {
                var r = await (_page?.DisplayActionSheetAsync(Localized.HomePage_ProjectContextMenu(node.DisplayName), Localized._Cancel, null, commands) ?? new Task<string>(() => "")) ?? "";
                await process(Array.IndexOf(commands, r));
            }
        }
    }

    private void RemoveEffect(NodeViewModel node)
    {
        if (_clip == null) return;
        if (node.Kind != NodeKind.Effect) return;

        if (_pendingConnectionSource?.Node == node) _pendingConnectionSource = null;

        if (_drawable.DragSourceNode == node)
        {
            _drawable.DragSourceNode = null;
            _drawable.DragPoint = null;
        }

        DisconnectNode(node);

        _clip.EffectBundles?.Remove(node.Id);
        ClipInfoBuilder.RebuildAllEffects(_clip);

        _nodes.Remove(node.Id);

        if (node.View != null)
        {
            NodesContainer.Children.Remove(node.View);
        }

        if (_selectedNode == node)
        {
            _selectedNode = null;
            PropertiesPanel.Children.Clear();
            PropertiesPanel.Children.Add(new Label { Text = Localized.DraftPage_PropertyPanel_SelectToContinue });
        }

        RebuildConnections();
        ConnectionsLayer.Invalidate();
        SetStatusText(Localized._Done);
    }

    private void OnPortClicked(NodeViewModel node, bool isInput, int portIndex = 0)
    {
        if (_pendingConnectionSource == null)
        {
            if (!isInput)
            {
                _pendingConnectionSource = new NodePort { Node = node, IsInput = false };
            }
        }
        else
        {
            if (isInput)
            {
                if (_pendingConnectionSource.Value.Node != node)
                {
                    ConnectNodes(_pendingConnectionSource.Value.Node, node, portIndex);
                }
                _pendingConnectionSource = null;
            }
            else
            {
                _pendingConnectionSource = new NodePort { Node = node, IsInput = false };
            }
        }
    }

    private void ConnectNodes(NodeViewModel source, NodeViewModel target, int targetPortIndex = 0)
    {
        if (target.InputPortNames is null) ConnectNodesForOneinOneout(source, target);
        else ConnectNodesForOneinMultiout(source, target, targetPortIndex);

        SetStatusText(PPLocalizedResources.EffectBindView_Connected(source?.DisplayName ?? "Unknown", target?.DisplayName ?? "Unknown"));
        NotifyEffectBundlesChanged();
    }

    private void ConnectNodesForOneinOneout(NodeViewModel source, NodeViewModel target)
    {
        Dictionary<Guid, NodeViewModel> newList = new(_nodes);
        foreach (var item in _nodes)
        {
            if (item.Key == source.Id)
            {
                var n = item.Value;
                n.OutputAnchorID = target.Id;
                newList[item.Key] = n;

            }
            else if (item.Key == target.Id)
            {
                var n = item.Value;
                n.InputAnchorID = source.Id;
                newList[item.Key] = n;

            }
            else if (item.Value.OutputAnchorID == target.Id)
            {
                var n = item.Value;
                n.OutputAnchorID = IEffectBundle.NoConnectionGUID;
                newList[item.Key] = n;
            }
        }
        _nodes = newList;
    }

    private void ConnectNodesForOneinMultiout(NodeViewModel source, NodeViewModel target, int targetPortIndex)
    {
        Dictionary<Guid, NodeViewModel> newList = new(_nodes);
        foreach (var item in _nodes)
        {
            if (item.Key == source.Id)
            {
                var n = item.Value;
                n.OutputAnchorID = target.Id;
                newList[item.Key] = n;

            }
            else if (item.Key == target.Id)
            {
                var n = item.Value;
                ArgumentNullException.ThrowIfNull(n.InputPortNames);
                if (n.InputAnchorIDs is null) n.InputAnchorIDs = Enumerable.Repeat(IEffectBundle.NoConnectionGUID, n.InputPortNames.Length).ToList();
                n.InputAnchorIDs[targetPortIndex] = source.Id;
                newList[item.Key] = n;

            }
            else if (item.Value.OutputAnchorID == target.Id)
            {
                var n = item.Value;
                n.OutputAnchorID = IEffectBundle.NoConnectionGUID;
                newList[item.Key] = n;
            }
        }
        _nodes = newList;
    }


    private void DisconnectNode(NodeViewModel node)
    {
        Dictionary<Guid, NodeViewModel> newList = new(_nodes);

        switch (node.Kind)
        {
            case NodeKind.Input:
                {
                    foreach (var item in _nodes)
                    {
                        if (item.Value.InputAnchorID == IEffectBundle.InputAnchorGUID)
                        {
                            item.Value.InputAnchorID = IEffectBundle.NoConnectionGUID;
                            newList[item.Key] = item.Value;
                        }
                    }

                    break;
                }
            case NodeKind.Output:
                {
                    foreach (var item in _nodes)
                    {
                        if (item.Value.OutputAnchorID == IEffectBundle.OutputAnchorGUID)
                        {
                            item.Value.OutputAnchorID = IEffectBundle.NoConnectionGUID;
                            newList[item.Key] = item.Value;
                        }

                    }

                    break;
                }
            default:
                {
                    node.InputAnchorID = IEffectBundle.NoConnectionGUID;
                    node.OutputAnchorID = IEffectBundle.NoConnectionGUID;
                    if (node.InputAnchorIDs is not null) node.InputAnchorIDs = Enumerable.Repeat(IEffectBundle.NoConnectionGUID, node.InputAnchorIDs.Count).ToList();
                    newList[node.Id] = node;

                    foreach (var item in _nodes)
                    {
                        if (item.Key == node.Id) continue;

                        var other = item.Value;
                        bool changed = false;

                        if (other.InputAnchorID == node.Id)
                        {
                            other.InputAnchorID = IEffectBundle.NoConnectionGUID;
                            changed = true;
                        }

                        if (other.InputAnchorIDs is not null)
                        {
                            for (int i = 0; i < other.InputAnchorIDs.Count; i++)
                            {
                                if (other.InputAnchorIDs[i] == node.Id)
                                {
                                    other.InputAnchorIDs[i] = IEffectBundle.NoConnectionGUID;
                                    changed = true;
                                }
                            }
                        }

                        if (other.OutputAnchorID == node.Id)
                        {
                            other.OutputAnchorID = IEffectBundle.NoConnectionGUID;
                            changed = true;
                        }

                        if (changed) newList[item.Key] = other;
                    }

                    break;
                }
        }

        _nodes = newList;

        SetStatusText(PPLocalizedResources.EffectBindView_Disconnected(node?.DisplayName ?? "Unknown"));

        RebuildConnections();
        ConnectionsLayer.Invalidate();
        NotifyEffectBundlesChanged();
    }

    private void RebuildConnections()
    {
        _drawable.Connections.Clear();

        bool TryAddConnection(Guid sourceId, NodeViewModel target, int targetPortIndex)
        {
            if (_nodes.TryGetValue(sourceId, out var sourceNode))
            {
                _drawable.Connections.Add((sourceNode, target, targetPortIndex));
                return true;
            }

            return false;
        }

        // 将可能指向隐藏 Bundle 的 sourceId 沿着链路向上追溯，
        // 找到第一个可见节点（或 InputAnchorGUID / NoConnectionGUID）。
        Guid ResolveVisibleSourceId(Guid sourceId)
        {
            while (sourceId != IEffectBundle.InputAnchorGUID
                   && sourceId != IEffectBundle.NoConnectionGUID
                   && !_nodes.ContainsKey(sourceId))
            {
                if (_clip?.EffectBundles?.TryGetValue(sourceId, out var hiddenBundle) == true)
                    sourceId = hiddenBundle.BindedInputId;
                else
                    return IEffectBundle.NoConnectionGUID;
            }
            return sourceId;
        }

        if (!_nodes.Any(n => n.Value.Kind == NodeKind.Effect && n.Value.OutputAnchorID == IEffectBundle.OutputAnchorGUID)
            && !(_clip?.EffectBundles?.Values.Any(b => b.BindedOutputId == IEffectBundle.OutputAnchorGUID
                                                    && !b.Target.HasFlag(EffectTarget.SpeedVariance)
                                                    && !b.Target.HasFlag(EffectTarget.Mixture)) ?? false))
        {
            _drawable.Connections.Add((_inputNode ?? throw new NullReferenceException(), _outputNode ?? throw new NullReferenceException(), 0));

        }

        foreach (var kvp in _nodes)
        {
            if (kvp.Key == IEffectBundle.InputAnchorGUID) continue;
            var item = kvp.Value;
            if (item.InputAnchorIDs.ListAny())
            {
                for (int i = 0; i < item.InputAnchorIDs.Count; i++)
                {
                    var inputId = item.InputAnchorIDs[i];
                    if (inputId == IEffectBundle.NoConnectionGUID) continue;

                    var resolvedId = ResolveVisibleSourceId(inputId);
                    if (resolvedId == IEffectBundle.NoConnectionGUID
                        || !TryAddConnection(resolvedId, item, i))
                    {
                        // Ensure stale or deleted node references do not keep crashing the editor.
                        item.InputAnchorIDs[i] = IEffectBundle.NoConnectionGUID;
                    }
                }
            }
            else
            {
                if (item.InputAnchorID != IEffectBundle.NoConnectionGUID)
                {
                    var resolvedId = ResolveVisibleSourceId(item.InputAnchorID);
                    if (resolvedId == IEffectBundle.NoConnectionGUID
                        || !TryAddConnection(resolvedId, item, 0))
                    {
                        item.InputAnchorID = IEffectBundle.NoConnectionGUID;
                    }
                }

            }

            if (item.Kind == NodeKind.Effect)
            {
                // 追踪输出链：如果 output 指向隐藏 Bundle，沿链向下找到 OutputAnchorGUID 或可见节点
                var resolvedOutput = item.OutputAnchorID;
                while (resolvedOutput != IEffectBundle.OutputAnchorGUID
                       && resolvedOutput != IEffectBundle.NoConnectionGUID
                       && !_nodes.ContainsKey(resolvedOutput))
                {
                    if (_clip?.EffectBundles?.TryGetValue(resolvedOutput, out var hiddenBundle) == true)
                        resolvedOutput = hiddenBundle.BindedOutputId;
                    else
                        break;
                }
                if (resolvedOutput == IEffectBundle.OutputAnchorGUID)
                {
                    _drawable.Connections.Add((item, _outputNode ?? throw new NullReferenceException(), 0));
                }
            }

            if (kvp.Key == IEffectBundle.OutputAnchorGUID) continue;

            bool hasInput = item.InputAnchorIDs.ListAny()
                ? item.InputAnchorIDs.Any(id => id != IEffectBundle.NoConnectionGUID)
                : item.InputAnchorID != IEffectBundle.NoConnectionGUID;
            bool hasOutput = item.OutputAnchorID != IEffectBundle.NoConnectionGUID;

            item.View?.Opacity = hasInput && hasOutput ? 1 : 0.8;
        }

    }

    public void SetStatusText(string text)
    {
        Dispatcher.Dispatch(() =>
        {
            InfoLabel.Text = text;
        });
        _page?.SetStatusText(text);

    }


    private async Task GeneratePreviews()
    {
        if (_clip == null || _page == null) return;
        if (_clip.ClipType != ClipMode.VideoClip && _clip.ClipType != ClipMode.PhotoClip) return;

        // Get source image
        var clipId = _clip.Id;
        ClipInfoBuilder.RebuildAllEffects(_clip);
        var clip = _page.previewer.Clips?.FirstOrDefault(c => c.Id == clipId);
        if (clip == null) return;

        Dictionary<string, object> localCache = new(), globalCache = new(); //for bindable effect
        var w = _page.previewWidth;
        var h = _page.previewHeight;
        var projectRelativeWidth = Math.Max(1, _page.ProjectInfo.RelativeWidth);
        var projectRelativeHeight = Math.Max(1, _page.ProjectInfo.RelativeHeight);

        try
        {
            var srcFrame = clip.GetFrameRelativeToStartPointOfSource(0, 1280, 720, true, 8);
            if (_inputNode is not null)
            {
                await UpdateNodePreview(_inputNode, srcFrame);
            }
            srcFrame.CanBeDisposed = false;
            var frame = new OneFrame(42, clip, srcFrame)
            {
                Effects = _clip.Effects?.Values?.ToArray() ?? []
            };

            // Build a robust effect → node mapping.
            // BindedEffectGroupID might not always be correctly configured,
            // so we use multiple strategies to find the right node.
            var effectToNodeMapping = new Dictionary<IEffect, NodeViewModel>();
            if (_clip.EffectBundles != null && _clip.Effects != null)
            {
                foreach (var effect in _clip.Effects.Values)
                {
                    if (!Guid.TryParse(effect.Id, out _)) effect.Id = Guid.NewGuid().ToString();
                    NodeViewModel? node = null;

                    // Strategy 1: Match by BindedEffectGroupID (the canonical approach)
                    if (effect.BindedEffectGroupID != null && Guid.TryParse(effect.BindedEffectGroupID, out var gid))
                    {
                        _nodes.TryGetValue(gid, out node);
                    }

                    // Strategy 2: Match by TypeName between effect and bundle → node
                    if (node == null)
                    {
                        foreach (var bundle in _clip.EffectBundles.Values)
                        {
                            if (string.Equals(bundle.TypeName, effect.TypeName, StringComparison.Ordinal)
                                && _nodes.TryGetValue(bundle.Id, out var n))
                            {
                                node = n;
                                // Fix the BindedEffectGroupID so future code paths
                                // also see the correct value.
                                effect.BindedEffectGroupID = bundle.Id.ToString();
                                Log($"Successfully mapped Effect {effect.Name}/{effect.Id}/{effect.TypeName} with Bundle {bundle.Name}/{bundle.TypeName}/{bundle.Id}");
                                break;
                            }
                        }
                    }

                    if (node != null)
                    {
                        effectToNodeMapping[effect] = node;
                    }
                }
            }

            // Use AfterEffect callback to receive intermediate pictures after each effect
            var result = Timeline.MixtureLayers([frame], 0, w, h, 8, async (effect, pic) =>
            {
                try
                {
                    if (pic == null) return;

                    if (effectToNodeMapping.TryGetValue(effect, out var node))
                    {
                        LogDiagnostic($"Showing result on node {node.DisplayName}...");
                        await UpdateNodePreview(node, pic);
                    }
                    else
                    {
                        // No matching node — this is normal for internal effects
                        // (e.g. Crop, Place, Resize) that don't appear as UI nodes.
                        LogDiagnostic($"No UI node for effect '{effect.TypeName}' (BindedEffectGroupID={effect.BindedEffectGroupID})");
                    }

                }
                catch (Exception ex)
                {
                    Log(ex, "AfterEffect callback", this);
#if DEBUG
                    if (await _page.DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(0, ex), "Throw", Localized._OK)) throw;
#else
                    await _page.DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(0, ex), Localized._OK);
#endif
                }
            },
            projectRelativeWidth: projectRelativeWidth,
            projectRelativeHeight: projectRelativeHeight);

            if (_outputNode is not null && result is not null)
            {
                await UpdateNodePreview(_outputNode, result);
            }
            else
            {
                LogDiagnostic($"Cannot set preview for output node. Output node is null: {_outputNode == null}, result is null: {result == null}");
            }


            srcFrame.CanBeDisposed = true;
            srcFrame.Dispose(true);
        }
        catch (Exception ex)
        {
            Log(ex, $"Failed to render", this);
#if DEBUG
            if (await _page.DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(0, ex), "Throw", Localized._OK)) throw;
#else
            await _page.DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(0, ex), Localized._OK);
#endif
        }

    }

    private async Task UpdateNodePreview(NodeViewModel node, IPicture picture)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                using var stream = new MemoryStream();
                await Task.Run(() => picture.SaveToSixLaborsImage().SaveAsPng(stream)); // Assuming SaveAsPng exists as extension
                stream.Position = 0;
                var imageSource = ImageSource.FromStream(() => new MemoryStream(stream.ToArray()));
                node.PreviewImage = imageSource;
            }
            catch (Exception ex)
            {
                Log(ex, "Failed to update preview image", this);
            }
        });
    }

    [DebuggerDisplay("{Id}, {Bundle?.TypeName}")]
    class NodeViewModel
    {
        public required Guid Id;
        public required NodeKind Kind;
        public required IEffectBundle? Bundle;
        public View? View;
        public double X, Y;
        public bool IsBindable => Bundle is not null && (Bundle.TypeOfEffect == EffectType.BindableEffect || Bundle.TypeOfEffect == EffectType.AudioBindableEffect);
        public string[]? InputPortNames;

        public Guid InputAnchorID { get => Bundle?.BindedInputId ?? field; set { if (Bundle != null) Bundle.BindedInputId = value; else field = value; } }
        public Guid OutputAnchorID { get => Bundle?.BindedOutputId ?? field; set { if (Bundle != null) Bundle.BindedOutputId = value; else field = value; } }
        public List<Guid>? InputAnchorIDs { get => Bundle?.BindedInputIds; set { Bundle?.BindedInputIds = value; } }

        public double DragStartX, DragStartY;
        public double DragTotalX, DragTotalY;

        public double Width => View?.Width > 0 ? View.Width : 150;

        public string DisplayName { get; set; } = "?";

        public ImageSource? PreviewImage { get; set { field = value; PreviewImageChanged?.Invoke(this, value); } }
        public event EventHandler<ImageSource?>? PreviewImageChanged;


    }

    struct NodePort
    {
        public NodeViewModel? Node;
        public bool IsInput;
        public int PortIndex;
    }

    private void UpdateAddEffectsPanel()
    {
        AddEffectsPanel.Children.Clear();

        if (_clip is null || _page is null) return;


        AddEffectsPanel.Children.Add(ClipInfoBuilder.BuildAddEffectPanel(
            _clip.ClipType switch { ClipMode.Special => EffectTarget.NotSpecified, ClipMode.MarkingClip => EffectTarget.NotSpecified, ClipMode.AudioClip => EffectTarget.Audio, _ => EffectTarget.Video },
            _page,
            EffectServices.GetAvailableEffectBundles(),
            new(),
            (s, e) =>
            {
                if (e.Id == "AddBundle")
                {
                    var BundleType = e.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(BundleType)) AddBundle(BundleType);
                }
            },
            hideKeyFramedBundles: true
        ));
    }

    private void AddBundle(string bundleTypeName)
    {
        if (_clip == null) return;

        var bundlesFactories = EffectServices.GetAvailableEffectBundles();

        if (bundlesFactories.TryGetValue(bundleTypeName, out var factory))
        {
            var instance = factory();
            instance.Id = Guid.NewGuid();
            instance.BindedInputId = IEffectBundle.NoConnectionGUID;
            instance.BindedOutputId = IEffectBundle.NoConnectionGUID;
            _clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();
            _clip.EffectBundles[instance.Id] = instance;
            ClipInfoBuilder.RebuildAllEffects(_clip);

            LoadClip(_clip, _page);
        }

        SetStatusText(Localized._Done);
        NotifyEffectBundlesChanged();
    }

    private async void GeneratePreviewButton_Clicked(object sender, EventArgs e)
    {
        GeneratePreviewButton.IsEnabled = false;
        BottomCommandBar.Children.Insert(0, new ActivityIndicator { IsRunning = true });
        await GeneratePreviews();
        GeneratePreviewButton.IsEnabled = true;
        BottomCommandBar.Children.RemoveAt(0);

    }

    class ConnectionsDrawable : IDrawable
    {
        private List<NodeViewModel> _nodes;
        public List<(NodeViewModel From, NodeViewModel To, int ToPortIndex)> Connections = new();
        public double PanX, PanY;
        public double Scale = 1.0;

        // Dragging State
        public NodeViewModel? DragSourceNode;
        public bool IsDraggingFromInput;
        public int DragSourcePortIndex; // 0 for output usually
        public Point? DragPoint;

        public ConnectionsDrawable(Dictionary<Guid, NodeViewModel> nodes)
        {
            _nodes = nodes.Values.ToList();
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.SaveState();

            canvas.StrokeColor = Colors.White;
            canvas.StrokeSize = (float)(2 * Scale);

            foreach (var (from, to, toIdx) in Connections)
            {
                var start = Transform(from.X + from.Width, from.Y + 40);

                double portY = 40;
                if (to.InputPortNames != null && to.InputPortNames.Length > 1)
                {
                    double portSize = 15;
                    double spacing = 5;
                    int n = to.InputPortNames.Length;
                    double stackHeight = n * portSize + (n - 1) * spacing;
                    double startY = (80 - stackHeight) / 2;
                    portY = startY + toIdx * (portSize + spacing) + (portSize / 2);
                }

                var end = Transform(to.X, to.Y + portY);

                DrawCurve(canvas, start, end);
            }

            if (DragSourceNode != null && DragPoint.HasValue)
            {
                Point start, end;
                if (IsDraggingFromInput)
                {
                    // Dragging FROM Input (Port) -> Mouse
                    // DragSourcePortIndex matters here
                    start = DragPoint.Value; // This point is already calculated in OnPortPan
                                             // But wait, IsDraggingFromInput here means we started dragging ON an input port.
                                             // Usually we draw a line FROM that port TO mouse.

                    double portY = 40;
                    if (DragSourceNode.InputPortNames != null && DragSourceNode.InputPortNames.Length > 1)
                    {
                        double portSize = 15;
                        double spacing = 5;
                        int n = DragSourceNode.InputPortNames.Length;
                        double stackHeight = n * portSize + (n - 1) * spacing;
                        double startY = (80 - stackHeight) / 2;
                        portY = startY + DragSourcePortIndex * (portSize + spacing) + (portSize / 2);
                    }

                    // Logic was reversed in previous code?
                    // "Dragging INTO input. Line starts at Mouse, ends at Input"
                    // No, IsDraggingFromInput means we grabbed the input handle.
                    // Usually that means we are pulling a wire OUT of it to connect to an output?
                    // Or we are dragging an EXISTING connection away?
                    // The UI intent in OnPortClick seems to be: Tap input -> Wait for tap on node.
                    // Drag intent: Drag from Input -> Hit Output.
                    // Visuals: Start at Input Port, End at Mouse.

                    start = Transform(DragSourceNode.X, DragSourceNode.Y + portY);
                    end = DragPoint.Value;
                }
                else
                {
                    // Dragging FROM Output -> Mouse
                    start = Transform(DragSourceNode.X + DragSourceNode.Width, DragSourceNode.Y + 40);
                    end = DragPoint.Value;
                }

                canvas.StrokeColor = Colors.Yellow;
                DrawCurve(canvas, start, end);
            }

            canvas.RestoreState();
        }

        private Point Transform(double x, double y)
        {
            // We need to match how NodesContainer renders.
            // If we Set AnchorPoints to 0,0 in OnReset, this simple math works:
            // return new Point(x * Scale + PanX, y * Scale + PanY);

            // But if Anchor is Center (default), it expands from center.
            // Width/Height of NodesContainer matters.

            // Let's strictly use (X * Scale) + PanX for now and ensure Anchor is handled in View.
            return new Point(x * Scale + PanX, y * Scale + PanY);
        }


        private void DrawCurve(ICanvas canvas, Point start, Point end)
        {
            var path = new PathF();
            path.MoveTo((float)start.X, (float)start.Y);
            float controlPointOffset = 50;
            path.CurveTo((float)(start.X + controlPointOffset), (float)start.Y, (float)(end.X - controlPointOffset), (float)end.Y, (float)end.X, (float)end.Y);
            canvas.DrawPath(path);
        }
    }
}