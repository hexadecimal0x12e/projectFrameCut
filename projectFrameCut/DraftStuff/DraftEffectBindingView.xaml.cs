using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using Color = Microsoft.Maui.Graphics.Color;
using Image = Microsoft.Maui.Controls.Image;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using Point = Microsoft.Maui.Graphics.Point;
using Rect = Microsoft.Maui.Graphics.Rect;

namespace projectFrameCut.DraftStuff;

public enum NodeKind { Effect, Input, Output, FreeField }

public enum PortKind { AnchorInput, AnchorOutput, ParamBind }

/// <summary>
/// A single port on a node. <see cref="Key"/> is the anchor id (an <see cref="IEffectProvider.InFields"/> key)
/// for <see cref="PortKind.AnchorInput"/>, the <see cref="IEffectProvider.OutField"/> id for
/// <see cref="PortKind.AnchorOutput"/>, or the field id for <see cref="PortKind.ParamBind"/>.
/// </summary>
struct NodePort
{
    public PortKind Kind;
    public string Key;
    public EffectArgumentFieldType FieldType;
    public string DisplayName;
    public int Index;
}

public partial class DraftEffectBindingView : ContentView
{
    private const string ExtraDataInputXKey = "__DraftEffectBindingView_InputX__";
    private const string ExtraDataInputYKey = "__DraftEffectBindingView_InputY__";
    private const string ExtraDataOutputXKey = "__DraftEffectBindingView_OutputX__";
    private const string ExtraDataOutputYKey = "__DraftEffectBindingView_OutputY__";
    private const string ExtraDataViewScaleKey = "__DraftEffectBindingView_ViewScale__";
    private const string ExtraDataViewPanXKey = "__DraftEffectBindingView_ViewPanX__";
    private const string ExtraDataViewPanYKey = "__DraftEffectBindingView_ViewPanY__";

    private const string ExtraDataFreeFieldXKeyPrefix = "__FreeFieldNode_";
    private const string ExtraDataFreeFieldXKeySuffix = "_X__";
    private const string ExtraDataFreeFieldYKeySuffix = "_Y__";
    private const string ParamDirectionKey = "__DraftEffectBindingView_ParamDirection__";

    private ClipElementUI? _clip;
    private DraftPage? _page;
    private Dictionary<Guid, NodeViewModel> _nodes = new();
    private ConnectionsDrawable _drawable;
    private NodeViewModel? _selectedNode;
    private NodeViewModel? _pendingConnectionSource;

    private NodeViewModel? _inputNode;
    private NodeViewModel? _outputNode;

    private readonly Dictionary<Guid, NodeViewModel> _freeFieldNodes = new();
    private bool _drawerExpanded;

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

    // ── Node geometry (shared by view, drag and drawable) ────────────────
    private const double FrameHeight = 80;
    private const double PortSize = 15;
    private const double PortSpacing = 5;
    private const double ParamRowHeight = 24;

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

    private void UpdateDrawableScale()
    {
        _drawable.Scale = NodesContainer.Scale;
        _drawable.PanX = NodesContainer.TranslationX;
        _drawable.PanY = NodesContainer.TranslationY;
    }

    private void OnSplitterPanUpdated(object sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panelStartWidth = RightPanelColumn.Width.Value;
                break;
            case GestureStatus.Running:
                double newWidth = Math.Clamp(_panelStartWidth - e.TotalX, PanelMinWidth, PanelMaxWidth);
                RightPanelColumn.Width = new GridLength(newWidth);
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                break;
        }
    }

    private void OnTogglePanelClicked(object? sender, EventArgs e)
    {
        if (PanelCollapsed)
        {
            RightPanelColumn.Width = new GridLength(_panelWidthBeforeCollapse);
            PanelCollapsed = false;
        }
        else
        {
            _panelWidthBeforeCollapse = RightPanelColumn.Width.Value;
            RightPanelColumn.Width = new GridLength(0);
            PanelCollapsed = true;
        }
        UpdatePanelToggleText();
    }

    private void UpdatePanelToggleText()
    {
        if (TogglePanelButton != null)
            TogglePanelButton.Text = PanelCollapsed ? "<" : ">";
    }

    public void LoadClip(ClipElementUI clip, DraftPage? page = null, bool showIsNotVisibleInEffectEditorEffect = false)
    {
        _clip = clip;
        _page = page;
        _showIsNotVisibleInEffectEditorEffect = showIsNotVisibleInEffectEditorEffect;
        UpdateAddEffectsPanel();
        _nodes.Clear();
        _freeFieldNodes.Clear();
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
            Id = IEffectProvider.InputAnchorGUID,
            Bundle = null,
            InputAnchorID = IEffectProvider.NoConnectionGUID,
            OutputAnchorID = IEffectProvider.NoConnectionGUID,
            DisplayName = PPLocalizedResources.EffectBind_SourcePicture
        };
        _inputNode.OutputPort = new NodePort { Kind = PortKind.AnchorOutput, Key = EffectProviderAnchorExtensions.InputKey, FieldType = EffectArgumentFieldType.IPicture, DisplayName = PPLocalizedResources.EffectBind_SourcePicture, Index = 0 };
        if (!inputHasPosition)
        {
            var pos = FindNonOverlappingPosition(_inputNode, inputX, inputY);
            _inputNode.X = pos.X;
            _inputNode.Y = pos.Y;
        }
        AddNode(_inputNode);

        if (_clip.EffectProviders != null)
        {
            foreach (var bundle in _clip.EffectProviders.Values)
            {
                // Skip bundles that are internal/special effects (e.g. Crop, Place, Resize)
                // to keep consistent with ClipInfoBuilder.BuildEffectTab filtering behavior.
                if (!showIsNotVisibleInEffectEditorEffect && (bundle.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor) || !bundle.Target.HasFlag(_clip.GetEffectTarget())))
                    continue;

                var node = new NodeViewModel
                {
                    Id = bundle.Id,
                    Kind = NodeKind.Effect,
                    Bundle = bundle,
                    DisplayName = bundle.Name
                };
                node.BuildPortsFromProvider();

                if (bundle.MetaData is { } md
                    && md.TryGetValue("__DraftEffectBindingView_InteractiveEditorX__", out var xObj)
                    && md.TryGetValue("__DraftEffectBindingView_InteractiveEditorY__", out var yObj)
                    && xObj is double x && yObj is double y)
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

        // Recreate FreeField reference nodes from the pool + saved positions.
        foreach (var ff in EffectFieldPool.EnumerateFreeFields())
        {
            if (!ff.Field.IsDynamic && !ff.Field.IsDynamicAtRenderTime) continue;
            RestoreFreeFieldReferenceNode(ff);
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
            Id = IEffectProvider.OutputAnchorGUID,
            Bundle = null,
            InputAnchorID = IEffectProvider.NoConnectionGUID,
            OutputAnchorID = IEffectProvider.NoConnectionGUID,
            DisplayName = PPLocalizedResources.EffectBind_FinalResult
        };
        _outputNode.InputPorts.Add(new NodePort { Kind = PortKind.AnchorInput, Key = EffectProviderAnchorExtensions.InputKey, FieldType = EffectArgumentFieldType.IPicture, DisplayName = PPLocalizedResources.EffectBind_FinalResult, Index = 0 });
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

    /// <summary>
    /// Rebuild the view of a single node (used after param direction toggle or a right-panel edit).
    /// </summary>
    private void RecreateNodeView(NodeViewModel node)
    {
        if (node.View != null)
        {
            NodesContainer.Children.Remove(node.View);
        }
        var view = CreateNodeView(node);
        node.View = view;
        NodesContainer.Add(view);
        AbsoluteLayout.SetLayoutBounds(view, new Rect(node.X, node.Y, -1, AbsoluteLayout.AutoSize));
        ConnectionsLayer.Invalidate();
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

    // ── Node geometry helpers (node-local Y offsets) ─────────────────────
    private static double GetOutputPortY(NodeViewModel n) => n.ParamsTopHeight + FrameHeight / 2;

    private static double GetInputPortY(NodeViewModel n, int idx)
    {
        int count = n.InputPorts.Count;
        if (count <= 1) return n.ParamsTopHeight + FrameHeight / 2;
        double stack = count * PortSize + (count - 1) * PortSpacing;
        double start = n.ParamsTopHeight + FrameHeight / 2 - stack / 2;
        return start + idx * (PortSize + PortSpacing) + PortSize / 2;
    }

    private static double GetParamPortY(NodeViewModel n, int idx) => n.ParamPortYOffsets.Length > idx ? n.ParamPortYOffsets[idx] : n.ParamsTopHeight + FrameHeight / 2;

    private static string HumanizePortName(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return key ?? string.Empty;
        return key switch
        {
            EffectProviderAnchorExtensions.InputKey => "Input",
            EffectProviderAnchorExtensions.OutputKey => "Output",
            _ => key,
        };
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

        var borderColor = node.Kind switch
        {
            NodeKind.Effect => Colors.Gray,
            NodeKind.FreeField => Color.FromArgb("#9B59B6"),
            _ => Colors.White,
        };

        var frame = new Border
        {
            Stroke = borderColor,
            StrokeThickness = node.Kind == NodeKind.Effect || node.Kind == NodeKind.FreeField ? 2 : 4,
            StrokeShape = new RoundRectangle { CornerRadius = 5 },
            BackgroundColor = Color.FromArgb("#2d2d2d"),
            Padding = 5,
            HeightRequest = FrameHeight,
            MinimumWidthRequest = NodeDefaultWidth,
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

        // ── Parameter display strip (above or below the frame, read-only) ──
        VerticalStackLayout? paramStack = null;
        bool paramsOnTop = true;
        if (node.Kind == NodeKind.Effect && node.ParamPorts.Count > 0)
        {
            paramsOnTop = GetParamDirection(node);
            paramStack = BuildParamStack(node, paramsOnTop);
        }
        // Only a top strip shifts the frame down; a bottom strip sits below it.
        node.ParamsTopHeight = paramStack is not null && paramsOnTop ? node.ParamPorts.Count * ParamRowHeight : 0;

        if (paramStack is not null && paramsOnTop) container.Add(paramStack);

        // Ports
        View inputPortView;

        if (node.InputPorts.Count == 0)
        {
            // Explicitly no picture input (e.g. value providers): reserve the column but render no port.
            inputPortView = new BoxView { Color = Colors.Transparent, WidthRequest = PortSize, HeightRequest = PortSize, InputTransparent = true };
        }
        else if (node.InputPorts.Count > 1)
        {
            var stack = new VerticalStackLayout { Spacing = PortSpacing, VerticalOptions = LayoutOptions.Center };
            for (int i = 0; i < node.InputPorts.Count; i++)
            {
                var port = node.InputPorts[i];
                var portBox = new BoxView { Color = PortTypeHelper.GetTypeColor(port.FieldType), WidthRequest = PortSize, HeightRequest = PortSize };

                int portIndex = i; // Capture for lambda

                var pTap = new TapGestureRecognizer();
                pTap.Tapped += (s, e) => OnPortClicked(node, true, portIndex);
                portBox.GestureRecognizers.Add(pTap);

                var pPan = new PanGestureRecognizer();
                pPan.PanUpdated += (s, e) => OnPortPan(node, e, true, portIndex, PortKind.AnchorInput);
                portBox.GestureRecognizers.Add(pPan);

                ToolTipProperties.SetText(portBox, port.DisplayName);

                var row = new HorizontalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
                row.Add(portBox);
                if (!string.IsNullOrEmpty(port.DisplayName))
                {
                    var l = new Label
                    {
                        Text = port.DisplayName,
                        TextColor = Colors.LightGray,
                        FontSize = 10,
                        VerticalOptions = LayoutOptions.Center
                    };
                    ToolTipProperties.SetText(l, port.DisplayName);
                    row.Add(l);
                }

                stack.Add(row);
            }
            inputPortView = stack;
        }
        else
        {
            var port = node.InputPorts[0];
            var box = new BoxView { Color = PortTypeHelper.GetTypeColor(port.FieldType), WidthRequest = PortSize, HeightRequest = PortSize, VerticalOptions = LayoutOptions.Center };
            var inputTap = new TapGestureRecognizer();
            inputTap.Tapped += (s, e) => OnPortClicked(node, true, 0);
            box.GestureRecognizers.Add(inputTap);

            var inputPan = new PanGestureRecognizer();
            inputPan.PanUpdated += (s, e) => OnPortPan(node, e, true, 0, PortKind.AnchorInput);
            box.GestureRecognizers.Add(inputPan);
            ToolTipProperties.SetText(box, port.DisplayName);

            inputPortView = box;
        }

        var outputPort = new BoxView { Color = PortTypeHelper.GetTypeColor(node.OutputPort?.FieldType ?? EffectArgumentFieldType.Unknown), WidthRequest = PortSize, HeightRequest = PortSize, VerticalOptions = LayoutOptions.Center };
        if (node.OutputPort is { } op) ToolTipProperties.SetText(outputPort, op.DisplayName);

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

        if (node.OutputAnchorID == IEffectProvider.NoConnectionGUID && node.InputAnchorID != IEffectProvider.NoConnectionGUID)
        {
            frame.Opacity = 0.8;
        }

        frame.SizeChanged += (s, e) => ConnectionsLayer.Invalidate();

        frame.Content = layout;

        container.Add(frame);

        if (paramStack is not null && !paramsOnTop) container.Add(paramStack);

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

    /// <summary>
    /// Build the read-only parameter strip shown above/below an effect node.
    /// Each row = a bind dot (ParamBind port) + parameter name + current value.
    /// </summary>
    private VerticalStackLayout BuildParamStack(NodeViewModel node, bool paramsOnTop)
    {
        if (node.Bundle == null || node.ParamPorts.Count == 0) return null!;
        var stack = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(4, 2), MinimumWidthRequest = NodeDefaultWidth };
        node.ParamPortYOffsets = new double[node.ParamPorts.Count];
        double baseY = paramsOnTop ? 0 : FrameHeight;

        for (int i = 0; i < node.ParamPorts.Count; i++)
        {
            var p = node.ParamPorts[i];
            var field = node.Bundle.Fields.TryGetValue(p.Key, out var f) ? f : null;
            bool isBound = field is { IsDynamic: true };

            var row = new HorizontalStackLayout { Spacing = 4, HeightRequest = ParamRowHeight, VerticalOptions = LayoutOptions.Center };

            // Bind dot: drag to a value output to bind, tap to open the bind picker.
            var dot = new BoxView { Color = PortTypeHelper.GetTypeColor(p.FieldType), WidthRequest = 9, HeightRequest = 9, VerticalOptions = LayoutOptions.Center };
            int rowIndex = i;
            var dotTap = new TapGestureRecognizer();
            dotTap.Tapped += async (s, e) => await OnParamPortTapped(node, p.Key);
            dot.GestureRecognizers.Add(dotTap);
            var dotPan = new PanGestureRecognizer();
            dotPan.PanUpdated += (s, e) => OnPortPan(node, e, true, rowIndex, PortKind.ParamBind);
            dot.GestureRecognizers.Add(dotPan);
            var boundSourceId = field is DynamicEffectParamField df ? df.BoundProviderId : null;
            ToolTipProperties.SetText(dot, isBound && boundSourceId is not null ? $"Bound: {GetBoundSourceDisplayName(node, boundSourceId)}" : "Bind");
            row.Add(dot);

            var nameLabel = new Label
            {
                Text = p.DisplayName,
                TextColor = Colors.LightGray,
                FontSize = 10,
                WidthRequest = 48,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            row.Add(nameLabel);

            var valueLabel = new Label
            {
                Text = GetParamDisplayValue(node, p.Key),
                TextColor = isBound ? Color.FromArgb("#f2c94c") : Colors.White,
                FontSize = 10,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
                HorizontalOptions = LayoutOptions.End
            };
            row.Add(valueLabel);

            stack.Add(row);
            node.ParamPortYOffsets[i] = baseY + i * ParamRowHeight + ParamRowHeight / 2;
        }
        return stack;
    }

    private string GetParamDisplayValue(NodeViewModel node, string fieldId)
    {
        if (node.Bundle == null || !node.Bundle.Fields.TryGetValue(fieldId, out var field)) return "-";
        if (field is DynamicEffectParamField df)
        {
            var srcName = GetBoundSourceDisplayName(node, df.BoundProviderId ?? "");
            return $"← {srcName}";
        }
        var raw = field is StaticEffectArgumentField sf ? sf.Value : field.GetGetter()?.Invoke();
        return raw?.ToString() ?? "-";
    }

    private string GetBoundSourceDisplayName(NodeViewModel node, string sourceId)
    {
        if (_clip == null || string.IsNullOrEmpty(sourceId)) return sourceId;
        if (Guid.TryParse(sourceId, out var gid) && _freeFieldNodes.TryGetValue(gid, out var ffNode))
            return ffNode.DisplayName;
        try
        {
            var host = new ClipBindingHost(_clip, node.Bundle ?? throw new ArgumentNullException(), _page);
            return host.GetSourceDisplayName(sourceId) ?? sourceId;
        }
        catch
        {
            return sourceId;
        }
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

                AbsoluteLayout.SetLayoutBounds(node.View, new Rect(node.X, node.Y, -1, AbsoluteLayout.AutoSize));
                ConnectionsLayer.Invalidate();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _isDraggingNodeOrPort = false;
                if (node.Kind == NodeKind.Effect && node?.Bundle is { } b)
                {
                    b.MetaData ??= new Dictionary<string, object>();
                    b.MetaData["__DraftEffectBindingView_InteractiveEditorX__"] = node.X;
                    b.MetaData["__DraftEffectBindingView_InteractiveEditorY__"] = node.Y;
                }
                else if (node.Kind == NodeKind.FreeField && node.FreeFieldGlobalId is { } gid)
                {
                    SaveFreeFieldNodePosition(node, gid);
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

    private void SaveFreeFieldNodePosition(NodeViewModel node, Guid gid)
    {
        if (_clip == null) return;
        _clip.ExtraData ??= new Dictionary<string, object>();
        _clip.ExtraData[$"{ExtraDataFreeFieldXKeyPrefix}{gid}{ExtraDataFreeFieldXKeySuffix}"] = node.X;
        _clip.ExtraData[$"{ExtraDataFreeFieldXKeyPrefix}{gid}{ExtraDataFreeFieldYKeySuffix}"] = node.Y;
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

    // ── Port dragging & hit-testing ──────────────────────────────────────
    private void OnPortPan(NodeViewModel node, PanUpdatedEventArgs e, bool isInput, int portIndex = 0, PortKind portKind = PortKind.AnchorInput)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _isDraggingNodeOrPort = true;
                _drawable.DragSourceNode = node;
                _drawable.IsDraggingFromInput = isInput;
                _drawable.DragSourcePortIndex = portIndex;
                _drawable.DragSourceKind = portKind;
                _drawable.DragFieldType = GetPortFieldType(node, isInput, portIndex, portKind);
                double s = NodesContainer.Scale;
                double startY = GetDragStartPortY(node, isInput, portIndex, portKind);
                if (isInput)
                    _drawable.DragPoint = new Point((node.X * s) + _drawable.PanX, (node.Y * s) + _drawable.PanY + startY * s);
                else
                    _drawable.DragPoint = new Point((node.X * s) + _drawable.PanX + node.Width * s, (node.Y * s) + _drawable.PanY + startY * s);
                break;

            case GestureStatus.Running:
                double scale = NodesContainer.Scale;
                double startPortY = GetDragStartPortY(node, isInput, portIndex, portKind);
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

                    bool srcIsValueOutput = portKind == PortKind.AnchorOutput && IsValueNode(node);
                    bool srcIsParamBind = portKind == PortKind.ParamBind;

                    // Hit Test
                    foreach (var kvp in _nodes)
                    {
                        var candidate = kvp.Value;
                        if (candidate == node) continue;

                        if (srcIsValueOutput || srcIsParamBind)
                        {
                            // Value-binding paths: value output ⇄ param bind dot.
                            if (srcIsValueOutput)
                            {
                                if (candidate.Kind != NodeKind.Effect || candidate.Bundle == null) continue;
                                for (int i = 0; i < candidate.ParamPorts.Count; i++)
                                {
                                    var pp = candidate.ParamPorts[i];
                                    if (!PortTypeHelper.IsPortTypeCompatible(_drawable.DragFieldType, pp.FieldType)) continue;
                                    double targetX = candidate.X * finalScale + _drawable.PanX;
                                    double targetY = (candidate.Y + GetParamPortY(candidate, i)) * finalScale + _drawable.PanY;
                                    if (Math.Abs(targetX - dropPoint.X) < 40 && Math.Abs(targetY - dropPoint.Y) < 40)
                                    {
                                        match = candidate;
                                        matchPortIndex = i;
                                        break;
                                    }
                                }
                            }
                            else // srcIsParamBind
                            {
                                if (candidate == _outputNode) continue;
                                if (!IsValueNode(candidate)) continue;
                                var srcType = candidate.OutputPort?.FieldType ?? EffectArgumentFieldType.Unknown;
                                var paramField = node.Bundle?.Fields?.TryGetValue(node.ParamPorts[portIndex].Key, out var pf) == true ? pf : null;
                                var paramType = paramField?.FieldType ?? EffectArgumentFieldType.Unknown;
                                if (!PortTypeHelper.IsPortTypeCompatible(srcType, paramType)) continue;
                                double targetX = (candidate.X + candidate.Width) * finalScale + _drawable.PanX;
                                double targetY = (candidate.Y + GetOutputPortY(candidate)) * finalScale + _drawable.PanY;
                                if (Math.Abs(targetX - dropPoint.X) < 40 && Math.Abs(targetY - dropPoint.Y) < 40)
                                {
                                    match = candidate;
                                    matchPortIndex = 0;
                                    break;
                                }
                            }
                            if (match != null) break;
                        }

                        if (isInput)
                        {
                            // Dragging FROM Input, looking for Output
                            if (candidate.Kind == NodeKind.Output) continue;
                            if (IsValueNode(candidate)) continue; // value outputs cannot feed picture inputs

                            double targetX = (candidate.X + candidate.Width) * finalScale + _drawable.PanX;
                            double targetY = (candidate.Y + GetOutputPortY(candidate)) * finalScale + _drawable.PanY;

                            if (Math.Abs(targetX - dropPoint.X) < 40 && Math.Abs(targetY - dropPoint.Y) < 40)
                            {
                                match = candidate;
                                matchPortIndex = 0;
                                break;
                            }
                        }
                        else if (portKind == PortKind.AnchorOutput && !IsValueNode(node))
                        {
                            // Dragging FROM Output (picture), looking for Input
                            if (candidate.Kind == NodeKind.Input) continue;

                            if (candidate.InputPorts.Count > 1)
                            {
                                for (int i = 0; i < candidate.InputPorts.Count; i++)
                                {
                                    double thisPortY = GetInputPortY(candidate, i);
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
                                double targetY = (candidate.Y + GetInputPortY(candidate, 0)) * finalScale + _drawable.PanY;
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
                        if (srcIsParamBind)
                        {
                            // Param dot dragged onto a value output → bind node's param to that source.
                            var paramKey = node.ParamPorts[portIndex].Key;
                            var sourceId = GetValueSourceId(match);
                            if (sourceId is not null)
                            {
                                BindFieldToSource(node, paramKey, sourceId);
                                SetStatusText(PPLocalizedResources.EffectBindView_Connected(node?.DisplayName ?? "?", match?.DisplayName ?? "?"));
                            }
                            else
                            {
                                SetStatusText(PPLocalizedResources.EffectBindView_ConnectedFail);
                            }
                        }
                        else if (srcIsValueOutput)
                        {
                            // Value output dragged onto a param dot → bind match's param to this source.
                            var paramKey = match.ParamPorts[matchPortIndex].Key;
                            var sourceId = GetValueSourceId(node);
                            if (sourceId is not null)
                            {
                                BindFieldToSource(match, paramKey, sourceId);
                                SetStatusText(PPLocalizedResources.EffectBindView_Connected(node?.DisplayName ?? "?", match?.DisplayName ?? "?"));
                            }
                            else
                            {
                                SetStatusText(PPLocalizedResources.EffectBindView_ConnectedFail);
                            }
                        }
                        else if (isInput)
                        {
                            // Dragged from Input (Target) -> Found Output (Source)
                            ConnectNodes(match, node, portIndex);
                        }
                        else
                        {
                            // Dragged from Output (Source) -> Found Input (Target)
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

    private bool IsValueNode(NodeViewModel n)
    {
        if (n.Kind == NodeKind.FreeField) return true;
        return n.Kind == NodeKind.Effect && n.Bundle?.Target.HasFlag(EffectTarget.ValueProvider) == true;
    }

    private string? GetValueSourceId(NodeViewModel n)
    {
        if (n.Kind == NodeKind.FreeField && n.FreeFieldGlobalId is { } gid) return gid.ToString();
        if (n.Kind == NodeKind.Effect && n.Bundle != null && n.Bundle.Target.HasFlag(EffectTarget.ValueProvider)) return n.Bundle.Id.ToString();
        return null;
    }

    private static double GetDragStartPortY(NodeViewModel node, bool isInput, int portIndex, PortKind portKind)
    {
        if (portKind == PortKind.ParamBind) return GetParamPortY(node, portIndex);
        if (isInput) return GetInputPortY(node, portIndex);
        return GetOutputPortY(node);
    }

    private static EffectArgumentFieldType GetPortFieldType(NodeViewModel node, bool isInput, int portIndex, PortKind portKind)
    {
        if (portKind == PortKind.ParamBind) return node.ParamPorts.Count > portIndex ? node.ParamPorts[portIndex].FieldType : EffectArgumentFieldType.Unknown;
        if (isInput) return node.InputPorts.Count > portIndex ? node.InputPorts[portIndex].FieldType : EffectArgumentFieldType.Unknown;
        return node.OutputPort?.FieldType ?? EffectArgumentFieldType.Unknown;
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

        if (node.Kind == NodeKind.FreeField)
        {
            // Info panel for free-field reference nodes.
            var idLabel = new Label { Text = $"Free Field: {node.DisplayName}", FontSize = 13, HorizontalOptions = LayoutOptions.Center };
            var typeLabel = new Label { Text = $"Type: {PortTypeHelper.HumanizeTypeName(node.OutputPort?.FieldType ?? EffectArgumentFieldType.Unknown)}", FontSize = 12, TextColor = Colors.Gray, HorizontalOptions = LayoutOptions.Center };
            var removeButton = new Button { Text = "移除引用" };
            removeButton.Clicked += (s, e) => RemoveFreeFieldReference(node);
            PropertiesPanel.Children.Add(idLabel);
            PropertiesPanel.Children.Add(typeLabel);
            PropertiesPanel.Children.Add(removeButton);
            return;
        }

        if (node.Kind != NodeKind.Effect)
        {
            return;
        }

        try
        {
            ArgumentNullException.ThrowIfNull(node.Bundle);
            var ui = EffectServices.GetUIProvider(node.Bundle);
            // Inject the binding host so each field in the property UI can offer a bind action.
            if (ui is IBindingHostHolder bindingHostHolder && _clip is not null)
            {
                bindingHostHolder.BindingHost = new ClipBindingHost(_clip, node.Bundle, _page,
                    onChanged: () => { NotifyEffectBundlesChanged(); RefreshSelectedNode(); });
            }
            var ppb = ui.CreateUI(node.Bundle);
            ArgumentNullException.ThrowIfNull(ppb, $"CreateUI() for {node.Bundle?.TypeName}");
            ppb.PropertyChanged += (s, args) =>
            {
                ArgumentNullException.ThrowIfNull(node.Bundle);
                if (node.Bundle is IEffectProvider p)
                {
                    ui.HandlePropertyPanelChange(node.Bundle, args);
                }
                RefreshNodeParams(node);
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

    /// <summary>
    /// Rebuilds the property panel of the currently selected effect node.
    /// Used by the binding host after a binding is applied / removed so the UI reflects the new state.
    /// </summary>
    private void RefreshSelectedNode()
    {
        if (_selectedNode is not null && _selectedNode.Kind == NodeKind.Effect)
        {
            SelectNode(_selectedNode);
        }
    }

    /// <summary>
    /// Rebuild only the node-embedded parameter strip so it reflects the latest field values.
    /// </summary>
    private void RefreshNodeParams(NodeViewModel node)
    {
        if (node?.Kind != NodeKind.Effect || node.View == null) return;
        RecreateNodeView(node);
    }

    private async Task ShowContextMenu(NodeViewModel node)
    {
        if (node.Kind == NodeKind.FreeField)
        {
            string[] freeCommands = [PPLocalizedResources.EffectBindView_Configure, "移除引用"];
            async Task freeProcess(int command)
            {
                switch (command)
                {
                    case 0:
                        SelectNode(node);
                        RightTabView.SelectedIndex = 0;
                        break;
                    case 1:
                        RemoveFreeFieldReference(node);
                        break;
                }
            }
            await ShowActionSheet(node, freeCommands, freeProcess);
            return;
        }

        if (node.Kind != NodeKind.Effect) return;
        string[] commands = [
            PPLocalizedResources.EffectBindView_Configure,
            PPLocalizedResources.EffectBindView_Disconnect,
            Localized.DraftPage_ContextMenu_Delete,
            "切换参数方向"
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
                case 3:
                    {
                        ToggleParamDirection(node);
                        break;
                    }
                default:
                    break;
            }

        }

        await ShowActionSheet(node, commands, process);
    }

    private async Task ShowActionSheet(NodeViewModel node, string[] commands, Func<int, Task> process)
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

    private void ToggleParamDirection(NodeViewModel node)
    {
        if (node.Bundle == null) return;
        var next = !GetParamDirection(node);
        node.Bundle.MetaData ??= new Dictionary<string, object>();
        node.Bundle.MetaData[ParamDirectionKey] = next;
        RecreateNodeView(node);
        RebuildConnections();
        ConnectionsLayer.Invalidate();
        NotifyEffectBundlesChanged();
    }

    private static bool GetParamDirection(NodeViewModel node)
    {
        if (node.Bundle?.MetaData?.TryGetValue(ParamDirectionKey, out var v) == true)
        {
            if (v is bool b) return b;
            if (v is string s && bool.TryParse(s, out var parsed)) return parsed;
        }
        return true; // top is the default
    }

    private void RemoveEffect(NodeViewModel node)
    {
        if (_clip == null) return;
        if (node.Kind != NodeKind.Effect) return;

        if (_pendingConnectionSource == node) _pendingConnectionSource = null;

        if (_drawable.DragSourceNode == node)
        {
            _drawable.DragSourceNode = null;
            _drawable.DragPoint = null;
        }

        DisconnectNode(node);

        _clip.EffectProviders?.Remove(node.Id);
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
        // Value nodes (free-field references / value providers) don't participate in tap-to-connect
        // picture data flow; their value bindings are made by dragging or tapping the param bind dot.
        if (IsValueNode(node)) return;

        if (_pendingConnectionSource == null)
        {
            if (!isInput)
            {
                _pendingConnectionSource = node;
            }
        }
        else
        {
            if (isInput)
            {
                if (_pendingConnectionSource != node)
                {
                    ConnectNodes(_pendingConnectionSource, node, portIndex);
                }
                _pendingConnectionSource = null;
            }
            else
            {
                _pendingConnectionSource = node;
            }
        }
    }

    private void ConnectNodes(NodeViewModel source, NodeViewModel target, int targetPortIndex = 0)
    {
        if (source == null || target == null) return;
        if (target.InputPorts.Count == 0 || targetPortIndex < 0 || targetPortIndex >= target.InputPorts.Count) return;

        // Enforce one incoming edge: detach any node whose output currently feeds this target's input anchor.
        foreach (var item in _nodes.Values)
        {
            if (item == target || item.OutputAnchorID != target.Id) continue;
            item.OutputAnchorID = IEffectProvider.NoConnectionGUID;
        }

        var port = target.InputPorts[targetPortIndex];
        if (port.Key == EffectProviderAnchorExtensions.InputKey || target.InputPorts.Count <= 1)
            target.InputAnchorID = source.Id;               // single-input (handles Bundle == null too)
        else
        {
            var ids = target.InputAnchorIDs ?? Enumerable.Repeat(IEffectProvider.NoConnectionGUID, target.InputPorts.Count).ToList();
            ids[targetPortIndex] = source.Id;
            target.InputAnchorIDs = ids;                    // multi-input, keyed by port
        }

        // The source's output now points at the target (used by output-chain resolution and opacity).
        if (source.Kind == NodeKind.Effect || source.Kind == NodeKind.Input)
            source.OutputAnchorID = target.Id;

        SetStatusText(PPLocalizedResources.EffectBindView_Connected(source?.DisplayName ?? "Unknown", target?.DisplayName ?? "Unknown"));
        NotifyEffectBundlesChanged();
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
                        if (item.Value.InputAnchorID == IEffectProvider.InputAnchorGUID)
                        {
                            item.Value.InputAnchorID = IEffectProvider.NoConnectionGUID;
                            newList[item.Key] = item.Value;
                        }
                    }

                    break;
                }
            case NodeKind.Output:
                {
                    foreach (var item in _nodes)
                    {
                        if (item.Value.OutputAnchorID == IEffectProvider.OutputAnchorGUID)
                        {
                            item.Value.OutputAnchorID = IEffectProvider.NoConnectionGUID;
                            newList[item.Key] = item.Value;
                        }

                    }

                    break;
                }
            default:
                {
                    node.InputAnchorID = IEffectProvider.NoConnectionGUID;
                    node.OutputAnchorID = IEffectProvider.NoConnectionGUID;
                    if (node.InputAnchorIDs is not null) node.InputAnchorIDs = Enumerable.Repeat(IEffectProvider.NoConnectionGUID, node.InputAnchorIDs.Count).ToList();
                    newList[node.Id] = node;

                    foreach (var item in _nodes)
                    {
                        if (item.Key == node.Id) continue;

                        var other = item.Value;
                        bool changed = false;

                        if (other.InputAnchorID == node.Id)
                        {
                            other.InputAnchorID = IEffectProvider.NoConnectionGUID;
                            changed = true;
                        }

                        if (other.InputAnchorIDs is not null)
                        {
                            for (int i = 0; i < other.InputAnchorIDs.Count; i++)
                            {
                                if (other.InputAnchorIDs[i] == node.Id)
                                {
                                    other.InputAnchorIDs[i] = IEffectProvider.NoConnectionGUID;
                                    changed = true;
                                }
                            }
                        }

                        if (other.OutputAnchorID == node.Id)
                        {
                            other.OutputAnchorID = IEffectProvider.NoConnectionGUID;
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
                _drawable.Connections.Add(new NodeConnection
                {
                    From = sourceNode,
                    To = target,
                    ToPortIndex = targetPortIndex,
                    IsValueBinding = false,
                    FieldType = target.InputPorts.Count > targetPortIndex ? target.InputPorts[targetPortIndex].FieldType : EffectArgumentFieldType.IPicture,
                });
                return true;
            }

            return false;
        }

        // 将可能指向隐藏 Bundle 的 sourceId 沿着链路向上追溯，
        // 找到第一个可见节点（或 InputAnchorGUID / NoConnectionGUID）。
        Guid ResolveVisibleSourceId(Guid sourceId)
        {
            while (sourceId != IEffectProvider.InputAnchorGUID
                   && sourceId != IEffectProvider.NoConnectionGUID
                   && !_nodes.ContainsKey(sourceId))
            {
                if (_clip?.EffectProviders?.TryGetValue(sourceId, out var hiddenBundle) == true)
                    sourceId = hiddenBundle.GetInputAnchor();
                else
                    return IEffectProvider.NoConnectionGUID;
            }
            return sourceId;
        }

        if (!_nodes.Any(n => n.Value.Kind == NodeKind.Effect && n.Value.OutputAnchorID == IEffectProvider.OutputAnchorGUID)
            && !(_clip?.EffectProviders?.Values.Any(b => b.GetOutputAnchor() == IEffectProvider.OutputAnchorGUID
                                                    && !b.Target.HasFlag(EffectTarget.SpeedVariance)
                                                    && !b.Target.HasFlag(EffectTarget.Mixture)) ?? false))
        {
            _drawable.Connections.Add(new NodeConnection
            {
                From = _inputNode ?? throw new NullReferenceException(),
                To = _outputNode ?? throw new NullReferenceException(),
                ToPortIndex = 0,
                IsValueBinding = false,
                FieldType = EffectArgumentFieldType.IPicture,
            });
        }

        // Data-flow edges, one per InputPort (single & multi unified).
        foreach (var kvp in _nodes)
        {
            if (kvp.Key == IEffectProvider.InputAnchorGUID) continue;
            var item = kvp.Value;
            for (int i = 0; i < item.InputPorts.Count; i++)
            {
                var key = item.InputPorts[i].Key;
                Guid inputId = (key == EffectProviderAnchorExtensions.InputKey || item.Bundle == null)
                    ? item.InputAnchorID
                    : (item.InputAnchorIDs is { Count: > 0 } && i < item.InputAnchorIDs.Count ? item.InputAnchorIDs[i] : IEffectProvider.NoConnectionGUID);
                if (inputId == IEffectProvider.NoConnectionGUID) continue;

                var resolvedId = ResolveVisibleSourceId(inputId);
                if (resolvedId == IEffectProvider.NoConnectionGUID
                    || !TryAddConnection(resolvedId, item, i))
                {
                    if (key == EffectProviderAnchorExtensions.InputKey || item.Bundle == null)
                        item.InputAnchorID = IEffectProvider.NoConnectionGUID;
                    else if (item.InputAnchorIDs is not null && i < item.InputAnchorIDs.Count)
                        item.InputAnchorIDs[i] = IEffectProvider.NoConnectionGUID;
                }
            }

            if (item.Kind == NodeKind.Effect)
            {
                // 追踪输出链：如果 output 指向隐藏 Bundle，沿链向下找到 OutputAnchorGUID 或可见节点
                var resolvedOutput = item.OutputAnchorID;
                while (resolvedOutput != IEffectProvider.OutputAnchorGUID
                       && resolvedOutput != IEffectProvider.NoConnectionGUID
                       && !_nodes.ContainsKey(resolvedOutput))
                {
                    if (_clip?.EffectProviders?.TryGetValue(resolvedOutput, out var hiddenBundle) == true)
                        resolvedOutput = hiddenBundle.GetOutputAnchor();
                    else
                        break;
                }
                if (resolvedOutput == IEffectProvider.OutputAnchorGUID)
                {
                    _drawable.Connections.Add(new NodeConnection
                    {
                        From = item,
                        To = _outputNode ?? throw new NullReferenceException(),
                        ToPortIndex = 0,
                        IsValueBinding = false,
                        FieldType = item.OutputPort?.FieldType ?? EffectArgumentFieldType.IPicture,
                    });
                }
            }

            if (kvp.Key == IEffectProvider.OutputAnchorGUID) continue;

            bool hasInput;
            if (item.InputPorts.Count > 0)
            {
                hasInput = false;
                for (int i = 0; i < item.InputPorts.Count; i++)
                {
                    var key = item.InputPorts[i].Key;
                    var g = (key == EffectProviderAnchorExtensions.InputKey || item.Bundle == null)
                        ? item.InputAnchorID
                        : (item.InputAnchorIDs is { Count: > 0 } && i < item.InputAnchorIDs.Count ? item.InputAnchorIDs[i] : IEffectProvider.NoConnectionGUID);
                    if (g != IEffectProvider.NoConnectionGUID) { hasInput = true; break; }
                }
            }
            else
            {
                hasInput = item.InputAnchorID != IEffectProvider.NoConnectionGUID;
            }
            bool hasOutput = item.OutputAnchorID != IEffectProvider.NoConnectionGUID;

            item.View?.Opacity = hasInput && hasOutput ? 1 : 0.8;
        }

        // Value-binding edges, reverse-derived from Fields.
        foreach (var n in _nodes.Values)
        {
            if (n.Kind != NodeKind.Effect || n.Bundle == null) continue;
            for (int pi = 0; pi < n.ParamPorts.Count; pi++)
            {
                var p = n.ParamPorts[pi];
                if (!n.Bundle.Fields.TryGetValue(p.Key, out var f) || f is not DynamicEffectParamField df) continue;
                if (string.IsNullOrEmpty(df.BoundProviderId) || !Guid.TryParse(df.BoundProviderId, out var gid)) continue;

                NodeViewModel? src = null;
                if (_freeFieldNodes.TryGetValue(gid, out var ff)) src = ff;
                else if (_nodes.TryGetValue(gid, out var prov) && prov.Kind == NodeKind.Effect && prov.Bundle?.Target.HasFlag(EffectTarget.ValueProvider) == true) src = prov;
                if (src != null)
                {
                    _drawable.Connections.Add(new NodeConnection
                    {
                        From = src,
                        To = n,
                        ToPortIndex = pi,
                        IsValueBinding = true,
                        FieldType = p.FieldType,
                        TargetParamFieldId = p.Key,
                    });
                }
            }
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
            // BindedEffectProvidingSystemID might not always be correctly configured,
            // so we use multiple strategies to find the right node.
            var effectToNodeMapping = new Dictionary<IEffect, NodeViewModel>();
            if (_clip.EffectProviders != null && _clip.Effects != null)
            {
                foreach (var effect in _clip.Effects.Values)
                {
                    if (!Guid.TryParse(effect.Id, out _)) effect.Id = Guid.NewGuid().ToString();
                    NodeViewModel? node = null;

                    // Strategy 1: Match by BindedEffectProvidingSystemID (the canonical approach)
                    if (effect.BindedEffectProvidingSystemID != null && Guid.TryParse(effect.BindedEffectProvidingSystemID, out var gid))
                    {
                        _nodes.TryGetValue(gid, out node);
                    }

                    // Strategy 2: Match by TypeName between effect and bundle → node
                    if (node == null)
                    {
                        foreach (var bundle in _clip.EffectProviders.Values)
                        {
                            if (string.Equals(bundle.TypeName, effect.TypeName, StringComparison.Ordinal)
                                && _nodes.TryGetValue(bundle.Id, out var n))
                            {
                                node = n;
                                // Fix the BindedEffectProvidingSystemID so future code paths
                                // also see the correct value.
                                effect.BindedEffectProvidingSystemID = bundle.Id.ToString();
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
                        LogDiagnostic($"No UI node for effect '{effect.TypeName}' (BindedEffectProvidingSystemID={effect.BindedEffectProvidingSystemID})");
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
                await Task.Run(() => picture.SaveToPng(stream));
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
        public required IEffectProvider? Bundle;
        public View? View;
        public double X, Y;
        public bool IsBindable => Bundle is not null && (Bundle.TypeOfEffect == EffectType.BindableEffect || Bundle.TypeOfEffect == EffectType.AudioBindableEffect);

        public Guid? FreeFieldGlobalId;

        /// <summary>Input anchor ports, derived from <see cref="IEffectProvider.InFields"/>.</summary>
        public List<NodePort> InputPorts = new();
        /// <summary>Output anchor port, derived from <see cref="IEffectProvider.OutField"/>.</summary>
        public NodePort? OutputPort;
        /// <summary>Visible value-parameter bind ports (non-IPicture, non-NotVisibleInEffectPanel).</summary>
        public List<NodePort> ParamPorts = new();

        /// <summary>Height of the param strip above the frame (0 when below or absent).</summary>
        public double ParamsTopHeight;
        /// <summary>Node-local Y of each param row center (for the param bind ports).</summary>
        public double[] ParamPortYOffsets = [];

        public Guid InputAnchorID { get => Bundle?.GetInputAnchor() ?? field; set { if (Bundle != null) Bundle.SetInputAnchor(value); else field = value; } }
        public Guid OutputAnchorID { get => Bundle?.GetOutputAnchor() ?? field; set { if (Bundle != null) Bundle.SetOutputAnchor(value); else field = value; } }

        public List<Guid>? InputAnchorIDs
        {
            get
            {
                if (Bundle == null) return null;
                var list = new List<Guid>(InputPorts.Count);
                foreach (var p in InputPorts)
                    list.Add(p.Key == EffectProviderAnchorExtensions.InputKey ? Bundle.GetInputAnchor() : Bundle.GetInputAnchorValue(p.Key));
                return list;
            }
            set
            {
                if (Bundle == null || value == null) return;
                for (int i = 0; i < InputPorts.Count && i < value.Count; i++)
                {
                    var key = InputPorts[i].Key;
                    if (key == EffectProviderAnchorExtensions.InputKey) Bundle.SetInputAnchor(value[i]);
                    else Bundle.SetInputAnchorValue(key, value[i]);
                }
            }
        }

        public double DragStartX, DragStartY;
        public double DragTotalX, DragTotalY;

        public double Width => View?.Width > 0 ? View.Width : 150;

        public string DisplayName { get; set; } = "?";

        public ImageSource? PreviewImage { get; set { field = value; PreviewImageChanged?.Invoke(this, value); } }
        public event EventHandler<ImageSource?>? PreviewImageChanged;

        /// <summary>
        /// Derive <see cref="InputPorts"/>, <see cref="OutputPort"/> and <see cref="ParamPorts"/>
        /// from the provider's <see cref="IEffectProvider.InFields"/>, <see cref="IEffectProvider.OutField"/>
        /// and <see cref="IEffectProvider.Fields"/>.
        /// </summary>
        public void BuildPortsFromProvider()
        {
            if (Bundle == null) return;

            InputPorts = Bundle.InFields
                .Select((kv, i) => new NodePort { Kind = PortKind.AnchorInput, Key = kv.Key, FieldType = kv.Value.FieldType, DisplayName = HumanizePortName(kv.Key), Index = i })
                .ToList();

            OutputPort = new NodePort { Kind = PortKind.AnchorOutput, Key = Bundle.OutField?.Id ?? string.Empty, FieldType = Bundle.OutField?.FieldType ?? EffectArgumentFieldType.Unknown, DisplayName = HumanizePortName(Bundle.OutField?.Id ?? "Output"), Index = 0 };

            ParamPorts = Bundle.Fields
                .Where(kv => !kv.Value.FieldType.HasFlag(EffectArgumentFieldType.IPicture)
                          && !kv.Value.FieldType.HasFlag(EffectArgumentFieldType.NotVisibleInEffectPanel))
                .Select((kv, i) => new NodePort { Kind = PortKind.ParamBind, Key = kv.Key, FieldType = kv.Value.FieldType, DisplayName = kv.Key, Index = i })
                .ToList();
        }
    }

    // ── FreeField drawer & reference nodes ───────────────────────────────

    private void OnFreeFieldDrawerToggleClicked(object? sender, EventArgs e)
    {
        _drawerExpanded = !_drawerExpanded;
        FreeFieldDrawer.IsVisible = _drawerExpanded;
        BottomRow.Height = _drawerExpanded ? new GridLength(200) : new GridLength(0);
        FreeFieldDrawerToggle.Text = _drawerExpanded ? "▼ Free Fields" : "Free Fields";
        if (_drawerExpanded) RebuildFreeFieldDrawerContent();
    }

    private void RebuildFreeFieldDrawerContent()
    {
        FreeFieldDrawerContent.Children.Clear();
        foreach (var ff in EffectFieldPool.EnumerateFreeFields())
        {
            if (!ff.Field.IsDynamic && !ff.Field.IsDynamicAtRenderTime) continue;   // non-static only
            var row = new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center };
            var dot = new BoxView { Color = PortTypeHelper.GetTypeColor(ff.Field.FieldType), WidthRequest = 12, HeightRequest = 12, VerticalOptions = LayoutOptions.Center };
            var label = new Label { Text = $"{ff.Field.Id}  ({PortTypeHelper.HumanizeTypeName(ff.Field.FieldType)})", TextColor = Colors.White, FontSize = 13, VerticalOptions = LayoutOptions.Center };
            var add = new Button { Text = "+", WidthRequest = 30, HeightRequest = 30, FontSize = 13 };
            var entry = ff;
            add.Clicked += (s, e) => AddFreeFieldReferenceNode(entry);
            row.Add(dot); row.Add(label); row.Add(add);
            FreeFieldDrawerContent.Add(row);
        }
        if (FreeFieldDrawerContent.Children.Count == 0)
            FreeFieldDrawerContent.Add(new Label { Text = "No free fields available.", TextColor = Colors.Gray });
    }

    private void AddFreeFieldReferenceNode(FreeFieldEntry ff)
    {
        if (_freeFieldNodes.ContainsKey(ff.GlobalId))
        {
            SelectNode(_freeFieldNodes[ff.GlobalId]);
            return;
        }

        var node = new NodeViewModel
        {
            Id = Guid.NewGuid(),
            Kind = NodeKind.FreeField,
            Bundle = null,
            FreeFieldGlobalId = ff.GlobalId,
            DisplayName = ff.Field.Id ?? ff.GlobalId.ToString()[..8],
        };
        node.OutputPort = new NodePort { Kind = PortKind.AnchorOutput, Key = ff.GlobalId.ToString(), FieldType = ff.Field.FieldType, DisplayName = node.DisplayName, Index = 0 };

        double savedX = GetExtraDataDouble($"{ExtraDataFreeFieldXKeyPrefix}{ff.GlobalId}{ExtraDataFreeFieldXKeySuffix}", 250 + _nodes.Count * 40);
        double savedY = GetExtraDataDouble($"{ExtraDataFreeFieldXKeyPrefix}{ff.GlobalId}{ExtraDataFreeFieldYKeySuffix}", 120 + _nodes.Count * 40);
        var pos = FindNonOverlappingPosition(node, savedX, savedY);
        node.X = pos.X;
        node.Y = pos.Y;

        AddNode(node);
        _freeFieldNodes[ff.GlobalId] = node;
        // During LoadClip the output node may not exist yet; the final RebuildConnections() at the
        // end of LoadClip covers drawing. When called interactively (drawer), refresh immediately.
        if (_outputNode != null)
        {
            RebuildConnections();
            ConnectionsLayer.Invalidate();
        }
    }

    private void RestoreFreeFieldReferenceNode(FreeFieldEntry ff)
    {
        AddFreeFieldReferenceNode(ff);
    }

    private void RemoveFreeFieldReference(NodeViewModel node)
    {
        if (node.Kind != NodeKind.FreeField || node.FreeFieldGlobalId is not { } gid) return;

        // Optionally unbind every field bound to this free field.
        if (_clip != null)
        {
            foreach (var n in _nodes.Values)
            {
                if (n.Kind != NodeKind.Effect || n.Bundle == null) continue;
                foreach (var field in n.Bundle.Fields.ToList())
                {
                    if (field.Value is DynamicEffectParamField df && df.BoundProviderId == gid.ToString())
                    {
                        var host = new ClipBindingHost(_clip, n.Bundle, _page, onChanged: () => { });
                        host.Unbind(field.Key);
                    }
                }
            }
        }

        _freeFieldNodes.Remove(gid);
        _nodes.Remove(node.Id);
        if (node.View != null) NodesContainer.Children.Remove(node.View);
        if (_selectedNode == node)
        {
            _selectedNode = null;
            PropertiesPanel.Children.Clear();
            PropertiesPanel.Children.Add(new Label { Text = Localized.DraftPage_PropertyPanel_SelectToContinue });
        }
        RebuildConnections();
        ConnectionsLayer.Invalidate();
        NotifyEffectBundlesChanged();
    }

    // ── Value binding helpers ────────────────────────────────────────────

    private void BindFieldToSource(NodeViewModel effectNode, string fieldId, string sourceId)
    {
        if (effectNode.Bundle == null || _clip == null) return;
        var host = new ClipBindingHost(_clip, effectNode.Bundle, _page, onChanged: () => { NotifyEffectBundlesChanged(); });
        host.ApplyBinding(fieldId, sourceId);
        RebuildConnections();
        ConnectionsLayer.Invalidate();
        RefreshNodeParams(effectNode);
        NotifyEffectBundlesChanged();
    }

    private async Task OnParamPortTapped(NodeViewModel node, string fieldId)
    {
        if (node.Bundle == null || _clip == null) return;
        var host = new ClipBindingHost(_clip, node.Bundle, _page, onChanged: () => { NotifyEffectBundlesChanged(); RefreshSelectedNode(); });
        await host.EditBinding(fieldId);      // picker already lists non-static free fields + value providers + "Unbind"
        RebuildConnections();
        ConnectionsLayer.Invalidate();
        RefreshNodeParams(node);
        NotifyEffectBundlesChanged();
    }

    // ── Add-effects panel ────────────────────────────────────────────────

    private void UpdateAddEffectsPanel()
    {
        AddEffectsPanel.Children.Clear();

        if (_clip is null || _page is null) return;

        AddEffectsPanel.Children.Add(ClipInfoBuilder.BuildAddEffectPanel(
            _clip.ClipType switch { ClipMode.Special => EffectTarget.NotSpecified, ClipMode.MarkingClip => EffectTarget.NotSpecified, ClipMode.AudioClip => EffectTarget.Audio, _ => EffectTarget.Video },
            _page,
            EffectServices.GetAvailableEffectProviders(),
            new(),
            (s, e) =>
            {
                if (e.Id == "AddProvider")
                {
                    var BundleType = e.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(BundleType)) AddBundle(BundleType);
                }
            },
            hideKeyFramedProviders: true
        ));
    }

    private void AddBundle(string bundleTypeName)
    {
        if (_clip == null) return;

        var bundlesFactories = EffectServices.GetAvailableEffectProviders();

        if (bundlesFactories.TryGetValue(bundleTypeName, out var factory))
        {
            var instance = factory();
            instance.Id = Guid.NewGuid();
            instance.SetInputAnchor(IEffectProvider.NoConnectionGUID);
            instance.SetOutputAnchor(IEffectProvider.NoConnectionGUID);
            _clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();
            _clip.EffectProviders[instance.Id] = instance;
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

    // ── Connection data & drawable ───────────────────────────────────────

    class NodeConnection
    {
        public NodeViewModel From = null!;
        public NodeViewModel To = null!;
        public int ToPortIndex;
        public bool IsValueBinding;                 // free-field / value-provider → param bind line
        public EffectArgumentFieldType FieldType;   // drives the line color
        public string? TargetParamFieldId;
    }

    class ConnectionsDrawable : IDrawable
    {
        private List<NodeViewModel> _nodes;
        public List<NodeConnection> Connections = new();
        public double PanX, PanY;
        public double Scale = 1.0;

        // Dragging State
        public NodeViewModel? DragSourceNode;
        public bool IsDraggingFromInput;
        public int DragSourcePortIndex;
        public PortKind DragSourceKind = PortKind.AnchorInput;
        public EffectArgumentFieldType DragFieldType = EffectArgumentFieldType.Unknown;
        public Point? DragPoint;

        public ConnectionsDrawable(Dictionary<Guid, NodeViewModel> nodes)
        {
            _nodes = nodes.Values.ToList();
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.SaveState();

            foreach (var conn in Connections)
            {
                canvas.StrokeColor = PortTypeHelper.GetTypeColor(conn.FieldType);
                canvas.StrokeSize = (float)((conn.IsValueBinding ? 1.5 : 2.5) * Scale);

                var start = Transform(conn.From.X + conn.From.Width, conn.From.Y + GetOutputPortY(conn.From));

                double endY = conn.IsValueBinding
                    ? conn.To.Y + GetParamPortY(conn.To, conn.ToPortIndex)
                    : conn.To.Y + GetInputPortY(conn.To, conn.ToPortIndex);
                var end = Transform(conn.To.X, endY);

                if (conn.IsValueBinding)
                {
                    canvas.StrokeDashPattern = [4, 3];
                    DrawCurve(canvas, start, end);
                    canvas.StrokeDashPattern = null;
                }
                else
                {
                    DrawCurve(canvas, start, end);
                }
            }

            if (DragSourceNode != null && DragPoint.HasValue)
            {
                Point start, end;
                double portY = GetDragStartPortY(DragSourceNode, IsDraggingFromInput, DragSourcePortIndex, DragSourceKind);
                if (IsDraggingFromInput)
                {
                    start = Transform(DragSourceNode.X, DragSourceNode.Y + portY);
                    end = DragPoint.Value;
                }
                else
                {
                    start = Transform(DragSourceNode.X + DragSourceNode.Width, DragSourceNode.Y + portY);
                    end = DragPoint.Value;
                }

                canvas.StrokeColor = PortTypeHelper.GetTypeColor(DragFieldType);
                DrawCurve(canvas, start, end);
            }

            canvas.RestoreState();
        }

        private Point Transform(double x, double y)
        {
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
