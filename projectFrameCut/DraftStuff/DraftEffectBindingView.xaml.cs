using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML.Codeblock;
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
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using Color = Microsoft.Maui.Graphics.Color;
using Image = Microsoft.Maui.Controls.Image;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using Point = Microsoft.Maui.Graphics.Point;
using Rect = Microsoft.Maui.Graphics.Rect;

namespace projectFrameCut.DraftStuff;

public enum NodeKind { Effect, Input, Output }

public enum PortKind { AnchorInput, AnchorOutput, ParamBind }

/// <summary>
/// A single port on a node. <see cref="Key"/> is the anchor id (an <see cref="IEffectProvider.InFields"/> key)
/// for <see cref="PortKind.AnchorInput"/>, the <see cref="IEffectProvider.OutField"/> id for
/// <see cref="PortKind.AnchorOutput"/>, or the field id for <see cref="PortKind.ParamBind"/>.
/// </summary>
record NodePort
{
    public Guid Id;
    public PortKind Kind;
    public string Key;
    public EffectArgumentFieldType FieldType;
    public string DisplayName;
    public int Index;

    public static bool IsValidPort(NodePort? p) => p is not null && !string.IsNullOrWhiteSpace(p.Key);
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

    private const string ParamDirectionKey = "__DraftEffectBindingView_ParamDirection__";

    private ClipElementUI? _clip;
    private DraftPage? _page;
    private Dictionary<Guid, NodeViewModel> _nodes = new();
    private ConnectionsDrawable _drawable;
    private NodeViewModel? _selectedNode;
    private NodeViewModel? _contextMenuNode;

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

    /// <summary>合并延后的渲染重建：一次变化批处理（如同一拖拽手势的多次连线改动）只触发一次 RebuildAllEffects。</summary>
    private bool _pendingProviderRebuild;

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
            Provider = null,
            DisplayName = PPLocalizedResources.EffectBind_SourcePicture,
            OutputPort = new NodePort
            {
                Kind = PortKind.AnchorOutput,
                Key = EffectProviderAnchorExtensions.InputKey,
                FieldType = EffectArgumentFieldType.IPicture,
                DisplayName = PPLocalizedResources.EffectBind_SourcePicture,
                Index = 0,
                Id = IEffectProvider.InputAnchorGUID
            }
        };
        if (!inputHasPosition)
        {
            var pos = FindNonOverlappingPosition(_inputNode, inputX, inputY);
            _inputNode.X = pos.X;
            _inputNode.Y = pos.Y;
        }
        AddNode(_inputNode);

        if (_clip?.EffectProviders != null)
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
                    Provider = bundle,
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
            Provider = null,
            DisplayName = PPLocalizedResources.EffectBind_FinalResult,
            MainInputPort = new NodePort
            {
                Kind = PortKind.AnchorInput,
                Key = EffectProviderAnchorExtensions.InputKey,
                FieldType = EffectArgumentFieldType.IPicture,
                DisplayName = PPLocalizedResources.EffectBind_FinalResult,
                Index = 0,
                Id = IEffectProvider.OutputAnchorGUID
            }
        };
        if (!outputHasPosition)
        {
            var pos = FindNonOverlappingPosition(_outputNode, outputX, outputY);
            _outputNode.X = pos.X;
            _outputNode.Y = pos.Y;
        }
        AddNode(_outputNode);

        SubscribeToPageEvents();

        // Normalize provider-owned configuration and project it directly into drawable connections.
        NormalizeBindingConfiguration();
        RebuildConnections();
        ApplySavedViewTransform();
        ConnectionsLayer.Invalidate();
    }

    /// <summary>
    /// Reloads all effect data from the current clip.
    /// Call this when external code (e.g. ClipInfoBuilder) has modified effect bundlesOnPortPan
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

    private static double GetInputPortY(NodeViewModel n) => n.ParamsTopHeight + FrameHeight / 2;

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
            _ => Colors.White,
        };

        var frame = new Border
        {
            Stroke = borderColor,
            StrokeThickness = node.Kind == NodeKind.Effect ? 2 : 4,
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
            _ => node?.DisplayName ?? node?.Provider?.TypeName ?? "?"
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

        if (!NodePort.IsValidPort(node.MainInputPort))
        {
            // Explicitly no picture input (e.g. value providers): reserve the column but render no port.
            inputPortView = new BoxView { Color = Colors.Transparent, WidthRequest = PortSize, HeightRequest = PortSize, InputTransparent = true };
        }
        else
        {
            var port = node.MainInputPort;
            var box = new BoxView { Color = PortTypeHelper.GetTypeColor(port.FieldType), WidthRequest = PortSize, HeightRequest = PortSize, VerticalOptions = LayoutOptions.Center };
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
                // 桌面平台（Windows/macOS）双击节点断开其所有连接；移动端短按仅为选中（见下）。
#if MACCATALYST || WINDOWS
                DisconnectAllFromNode(node);
#else
                SelectNode(node);
#endif
            },
            OnContextMenuClick: () => ShowNodeActionOverlay(node)
        );

        var bodyActionContainer = new Grid();

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (s, e) => OnNodePan(node, e);
        bodyActionContainer.GestureRecognizers.Add(pan);

        // Output Port Interaction (Single Output mostly)
        var outputPan = new PanGestureRecognizer();
        outputPan.PanUpdated += (s, e) => OnPortPan(node, e, false, 0, PortKind.AnchorOutput);
        outputPort.GestureRecognizers.Add(outputPan);

        // Layout
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Star }, new ColumnDefinition { Width = 20 } } };

        bodyActionContainer.Add(label);

        layout.Add(inputPortView, 0, 0);
        layout.Add(bodyActionContainer, 1, 0);
        layout.Add(outputPort, 2, 0);

        //if (node.OutputAnchorID == IEffectProvider.NoConnectionGUID && node.InputAnchorID != IEffectProvider.NoConnectionGUID)
        //{
        //    frame.Opacity = 0.8;
        //}
        //TODO: Dim of node with no output connection (but has input) is not implemented yet, as the connection logic is being rewritten.

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
        if (node.Provider == null || node.ParamPorts.Count == 0) return null!;
        var stack = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(4, 2), MinimumWidthRequest = NodeDefaultWidth };
        node.ParamPortYOffsets = new double[node.ParamPorts.Count];
        double baseY = paramsOnTop ? 0 : FrameHeight;

        for (int i = 0; i < node.ParamPorts.Count; i++)
        {
            var p = node.ParamPorts[i];
            var field = node.Provider.Fields.TryGetValue(p.Key, out var f) ? f : null;
            bool isBound = node.Provider.TryGetFieldBinding(p.Key, out var boundSourceId);

            var row = new HorizontalStackLayout { Spacing = 4, HeightRequest = ParamRowHeight, VerticalOptions = LayoutOptions.Center };

            // Bind dot: 拖拽到值输出上建立绑定。
            // TODO(连接逻辑重写): 原实现还支持「点击 bind dot 弹出绑定选择器」——重写后如需恢复，
            // 在此为 dot 重新添加 TapGestureRecognizer 并接入 ClipBindingHost.EditBinding(fieldId)。
            var dot = new BoxView { Color = PortTypeHelper.GetTypeColor(p.FieldType), WidthRequest = 9, HeightRequest = 9, VerticalOptions = LayoutOptions.Center };
            int rowIndex = i;
            var dotPan = new PanGestureRecognizer();
            dotPan.PanUpdated += (s, e) => OnPortPan(node, e, true, rowIndex, PortKind.ParamBind);
            dot.GestureRecognizers.Add(dotPan);
            ToolTipProperties.SetText(dot, isBound ? $"Bound: {GetBoundSourceDisplayName(node, boundSourceId)}" : "Bind");
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
        if (node.Provider == null || !node.Provider.Fields.TryGetValue(fieldId, out var field)) return "-";
        if (node.Provider.TryGetFieldBinding(fieldId, out var sourceId))
        {
            var srcName = GetBoundSourceDisplayName(node, sourceId);
            return $"← {srcName}";
        }
        var raw = field is StaticEffectArgumentField sf ? sf.Value : field.GetGetter()?.Invoke();
        return raw?.ToString() ?? "-";
    }

    private string GetBoundSourceDisplayName(NodeViewModel node, string sourceId)
    {
        if (_clip == null || string.IsNullOrEmpty(sourceId)) return sourceId;
        try
        {
            var host = new ClipBindingHost(_clip, node.Provider ?? throw new ArgumentNullException(), _page);
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
                if (node.Kind == NodeKind.Effect && node?.Provider is { } b)
                {
                    b.MetaData ??= new Dictionary<string, object>();
                    b.MetaData["__DraftEffectBindingView_InteractiveEditorX__"] = node.X;
                    b.MetaData["__DraftEffectBindingView_InteractiveEditorY__"] = node.Y;
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

    // ── Port dragging (rubber-band line only; hit-test / connect logic removed for rewrite) ──
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

                // 吸附：端点接近类型兼容的端口时自动贴到端口坐标，降低手动对齐难度。
                _drawable.DragPoint = SnapDragPoint(node, isInput, portKind, portIndex, new Point(baseX + e.TotalX, baseY + e.TotalY));
                ConnectionsLayer.Invalidate();
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _isDraggingNodeOrPort = false;

                if (_drawable.DragPoint is { } dropPoint)
                {
                    HandlePortConnect(node, isInput, portIndex, portKind, dropPoint);
                }

                _drawable.DragSourceNode = null;
                _drawable.DragPoint = null;
                ConnectionsLayer.Invalidate();
                break;
        }
    }

    private const double ConnectionSnapRadius = 50; // 拖线端点吸附半径（px，画布坐标）

    /// <summary>
    /// 拖线过程中把端点吸附到类型兼容的目标端口坐标（若在 <see cref="ConnectionSnapRadius"/> 内）。
    /// 命中语义与 <see cref="HandlePortConnect"/> 保持一致：同类型端口、兼容性检查、最近优先。
    /// 仅影响绘制位置，实际连接仍在松开时由 HandlePortConnect 判定。
    /// </summary>
    private Point SnapDragPoint(NodeViewModel node, bool isInput, PortKind portKind, int portIndex, Point rawPoint)
    {
        bool srcIsValueOutput = portKind == PortKind.AnchorOutput && IsValueNode(node);
        bool srcIsParamBind = portKind == PortKind.ParamBind;
        double finalScale = NodesContainer.Scale;

        NodeViewModel? snapNode = null;
        int snapIndex = 0;
        bool snapAsParam = false;   // 目标是候选节点的参数行
        double snapRadiusSq = ConnectionSnapRadius * ConnectionSnapRadius;
        double bestDistSq = double.MaxValue;

        foreach (var kvp in _nodes)
        {
            var candidate = kvp.Value;
            if (candidate == node) continue;

            if (srcIsValueOutput)
            {
                // 值输出 → 目标 Effect 的参数行（兼容类型，取最近行）
                if (candidate.Kind != NodeKind.Effect || candidate.Provider == null) continue;
                for (int i = 0; i < candidate.ParamPorts.Count; i++)
                {
                    var pp = candidate.ParamPorts[i];
                    if (!PortTypeHelper.IsPortTypeCompatible(_drawable.DragFieldType, pp.FieldType)) continue;
                    double px = (candidate.X) * finalScale + _drawable.PanX;
                    double py = (candidate.Y + GetParamPortY(candidate, i)) * finalScale + _drawable.PanY;
                    double d = ((px - rawPoint.X) * (px - rawPoint.X)) + ((py - rawPoint.Y) * (py - rawPoint.Y));
                    if (d < snapRadiusSq && d < bestDistSq)
                    {
                        bestDistSq = d;
                        snapNode = candidate;
                        snapIndex = i;
                        snapAsParam = true;
                    }
                }
            }
            else if (srcIsParamBind)
            {
                // 参数 dot → 值输出
                if (candidate == _outputNode) continue;
                if (!IsValueNode(candidate)) continue;
                var srcType = candidate.OutputPort?.FieldType ?? EffectArgumentFieldType.Unknown;
                var paramField = node.Provider?.Fields?.TryGetValue(node.ParamPorts[portIndex].Key, out var pf) == true ? pf : null;
                var paramType = paramField?.FieldType ?? EffectArgumentFieldType.Unknown;
                if (!PortTypeHelper.IsPortTypeCompatible(srcType, paramType)) continue;
                double px = (candidate.X + candidate.Width) * finalScale + _drawable.PanX;
                double py = (candidate.Y + GetOutputPortY(candidate)) * finalScale + _drawable.PanY;
                double d = ((px - rawPoint.X) * (px - rawPoint.X)) + ((py - rawPoint.Y) * (py - rawPoint.Y));
                if (d < snapRadiusSq && d < bestDistSq)
                {
                    bestDistSq = d;
                    snapNode = candidate;
                    snapIndex = 0;
                    snapAsParam = false;
                }
            }
            else if (isInput)
            {
                // 输入口 → 图片来源输出
                if (candidate.Kind == NodeKind.Output) continue;
                if (IsValueNode(candidate)) continue;
                var candidateFieldType = candidate.OutputPort?.FieldType ?? EffectArgumentFieldType.Unknown;
                if (!PortTypeHelper.IsPortTypeCompatible(EffectArgumentFieldType.IPicture, candidateFieldType)) continue;
                double px = (candidate.X + candidate.Width) * finalScale + _drawable.PanX;
                double py = (candidate.Y + GetOutputPortY(candidate)) * finalScale + _drawable.PanY;
                double d = ((px - rawPoint.X) * (px - rawPoint.X)) + ((py - rawPoint.Y) * (py - rawPoint.Y));
                if (d < snapRadiusSq && d < bestDistSq)
                {
                    bestDistSq = d;
                    snapNode = candidate;
                    snapIndex = 0;
                    snapAsParam = false;
                }
            }
            else if (portKind == PortKind.AnchorOutput && !IsValueNode(node))
            {
                // 输出口 → 目标图片输入
                if (candidate.Kind == NodeKind.Input) continue;
                var srcFieldType = node.OutputPort?.FieldType ?? EffectArgumentFieldType.Unknown;
                var targetFieldType = candidate.MainInputPort?.FieldType ?? EffectArgumentFieldType.Unknown;
                if (!PortTypeHelper.IsPortTypeCompatible(srcFieldType, targetFieldType)) continue;
                double px = (candidate.X) * finalScale + _drawable.PanX;
                double py = (candidate.Y + GetInputPortY(candidate)) * finalScale + _drawable.PanY;
                double d = ((px - rawPoint.X) * (px - rawPoint.X)) + ((py - rawPoint.Y) * (py - rawPoint.Y));
                if (d < snapRadiusSq && d < bestDistSq)
                {
                    bestDistSq = d;
                    snapNode = candidate;
                    snapIndex = 0;
                    snapAsParam = false;
                }
            }
        }

        if (snapNode is null) return rawPoint;

        // 吸附到端口中心坐标。
        double resultX, resultY;
        if (snapAsParam)
        {
            resultX = (snapNode.X) * finalScale + _drawable.PanX;
            resultY = (snapNode.Y + GetParamPortY(snapNode, snapIndex)) * finalScale + _drawable.PanY;
        }
        else if (isInput || srcIsValueOutput || srcIsParamBind)
        {
            // 目标是候选节点的输出口（isInput 找来源输出 / srcIsParamBind 找值输出）。
            // 注意：isInput 场景吸附目标是来源节点（snapNode）的输出口。
            resultX = (snapNode.X + snapNode.Width) * finalScale + _drawable.PanX;
            resultY = (snapNode.Y + GetOutputPortY(snapNode)) * finalScale + _drawable.PanY;
        }
        else
        {
            // 输出口 → 目标输入口
            resultX = (snapNode.X) * finalScale + _drawable.PanX;
            resultY = (snapNode.Y + GetInputPortY(snapNode)) * finalScale + _drawable.PanY;
        }
        return new Point(resultX, resultY);
    }

    private void HandlePortConnect(NodeViewModel node, bool isInput, int portIndex, PortKind portKind, Point dropPoint)
    {
        NodeViewModel? match = null;
        int matchPortIndex = 0;
        double finalScale = NodesContainer.Scale;

        bool srcIsValueOutput = portKind == PortKind.AnchorOutput && IsValueNode(node);
        bool srcIsParamBind = portKind == PortKind.ParamBind;

        // ── Hit test: 遍历所有节点，把候选端口换算成画布坐标与落点比较 ──
        foreach (var kvp in _nodes)
        {
            var candidate = kvp.Value;
            if (candidate == node) continue;

            if (srcIsValueOutput || srcIsParamBind)
            {
                // 值绑定路径：值输出 ⇄ 参数绑定点
                if (srcIsValueOutput)
                {
                    if (candidate.Kind != NodeKind.Effect || candidate.Provider == null) continue;
                    // 命中窗口（40px）覆盖多行参数（行距 24px），不能取第一个命中——否则总是连到最上面的参数。
                    // 改为遍历所有兼容参数行，取与落点距离最近的一行。
                    int bestIdx = -1;
                    double bestDist = double.MaxValue;
                    for (int i = 0; i < candidate.ParamPorts.Count; i++)
                    {
                        var pp = candidate.ParamPorts[i];
                        if (!PortTypeHelper.IsPortTypeCompatible(_drawable.DragFieldType, pp.FieldType)) continue;
                        double targetX = candidate.X * finalScale + _drawable.PanX;
                        double targetY = (candidate.Y + GetParamPortY(candidate, i)) * finalScale + _drawable.PanY;
                        double dx = Math.Abs(targetX - dropPoint.X);
                        double dy = Math.Abs(targetY - dropPoint.Y);
                        if (dx < 40 && dy < 40)
                        {
                            double dist = (dx * dx) + (dy * dy);
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                bestIdx = i;
                            }
                        }
                    }
                    if (bestIdx >= 0)
                    {
                        match = candidate;
                        matchPortIndex = bestIdx;
                        LogDiagnostic($"Connection hits: Drag from {portKind} node '{node.DisplayName}' to candidate node '{candidate.DisplayName}' ({candidate.ParamPorts[bestIdx].FieldType})");
                        break;
                    }
                }
                else // srcIsParamBind
                {
                    if (candidate == _outputNode) continue;
                    if (!IsValueNode(candidate)) continue;
                    var srcType = candidate.OutputPort?.FieldType ?? EffectArgumentFieldType.Unknown;
                    var paramField = node.Provider?.Fields?.TryGetValue(node.ParamPorts[portIndex].Key, out var pf) == true ? pf : null;
                    var paramType = paramField?.FieldType ?? EffectArgumentFieldType.Unknown;
                    if (!PortTypeHelper.IsPortTypeCompatible(srcType, paramType)) continue;
                    double targetX = (candidate.X + candidate.Width) * finalScale + _drawable.PanX;
                    double targetY = (candidate.Y + GetOutputPortY(candidate)) * finalScale + _drawable.PanY;
                    if (Math.Abs(targetX - dropPoint.X) < 40 && Math.Abs(targetY - dropPoint.Y) < 40)
                    {
                        match = candidate;
                        matchPortIndex = 0;
                        LogDiagnostic($"Connection hits: Drag from {portKind} node '{node.DisplayName}' to candidate node '{candidate.DisplayName}' ({paramType})");
                        break;
                    }
                }
                if (match != null)
                {
                    LogDiagnostic($"Connection hits: Drag from {portKind} node '{node.DisplayName}' to candidate node '{candidate.DisplayName}'");
                    break;
                }
            }


            if (isInput)
            {
                // 从输入拖出，寻找来源输出
                if (candidate.Kind == NodeKind.Output) continue;
                if (IsValueNode(candidate)) continue; // 值输出不能喂图片输入

                var candidateFieldType = candidate.OutputPort?.FieldType ?? EffectArgumentFieldType.Unknown;
                if (!PortTypeHelper.IsPortTypeCompatible(EffectArgumentFieldType.IPicture, candidateFieldType)) continue;

                double targetX = (candidate.X + candidate.Width) * finalScale + _drawable.PanX;
                double targetY = (candidate.Y + GetOutputPortY(candidate)) * finalScale + _drawable.PanY;
                if (Math.Abs(targetX - dropPoint.X) < 40 && Math.Abs(targetY - dropPoint.Y) < 40)
                {
                    match = candidate;
                    matchPortIndex = 0;
                    LogDiagnostic($"Connection hits: Drag from {portKind} node '{node.DisplayName}' to candidate node '{candidate.DisplayName}'");
                    break;
                }
            }
            else if (portKind == PortKind.AnchorOutput && !IsValueNode(node))
            {
                // 从输出拖出，寻找目标输入
                if (candidate.Kind == NodeKind.Input) continue;

                var srcFieldType = node.OutputPort?.FieldType ?? EffectArgumentFieldType.Unknown;
                var targetFieldType = candidate.MainInputPort?.FieldType ?? EffectArgumentFieldType.Unknown;
                if (!PortTypeHelper.IsPortTypeCompatible(srcFieldType, targetFieldType)) continue;

                double targetX = candidate.X * finalScale + _drawable.PanX;
                double targetY = (candidate.Y + GetInputPortY(candidate)) * finalScale + _drawable.PanY;
                if (Math.Abs(targetX - dropPoint.X) < 40 && Math.Abs(targetY - dropPoint.Y) < 40)
                {
                    match = candidate;
                    matchPortIndex = 0;
                    LogDiagnostic($"Connection hits: Drag from {portKind} node '{node.DisplayName}' to candidate node '{candidate.DisplayName}' ({targetFieldType})");
                    break;
                }
            }
        }

        if (match != null && node != null)
        {
            if (srcIsParamBind)
            {
                // 参数绑定点拖到值输出 → 把节点参数绑定到该来源。
                var paramKey = node.ParamPorts[portIndex].Key;
                var sourceId = GetValueSourceId(match);
                if (sourceId is not null && Guid.TryParse(sourceId, out var sourceGuid))
                {
                    AddUIBinding(new UIBinding(UIBindingKind.Value, sourceGuid, BindingIdOf(node), paramKey));
                    LogDiagnostic($"Binding: Param '{paramKey}' of node '{node.DisplayName}' bound to source '{sourceId}' (from node '{match.DisplayName}')");
                    SetStatusText(PPLocalizedResources.EffectBindView_Connected(node?.DisplayName ?? "?", match?.DisplayName ?? "?"));
                    RecreateNodeView(node);
                    OnBindingConfigurationChanged();
                    NotifyEffectBundlesChanged();
                }
                else
                {
                    SetStatusText(PPLocalizedResources.EffectBindView_ConnectedFail);
                }
            }
            else if (srcIsValueOutput)
            {
                // 值输出拖到参数绑定点 → 把 match 的参数绑定到本来源。
                var paramKey = match.ParamPorts[matchPortIndex].Key;
                var sourceId = GetValueSourceId(node);
                if (sourceId is not null && Guid.TryParse(sourceId, out var sourceGuid))
                {
                    AddUIBinding(new UIBinding(UIBindingKind.Value, sourceGuid, BindingIdOf(match), paramKey));
                    LogDiagnostic($"Binding: Param '{paramKey}' of node '{match.DisplayName}' bound to source '{sourceId}' (from node '{node.DisplayName}')");
                    SetStatusText(PPLocalizedResources.EffectBindView_Connected(node?.DisplayName ?? "?", match?.DisplayName ?? "?"));
                    RecreateNodeView(match);
                    OnBindingConfigurationChanged();
                    NotifyEffectBundlesChanged();
                }
                else
                {
                    SetStatusText(PPLocalizedResources.EffectBindView_ConnectedFail);
                }
            }
            else if (isInput)
            {
                // 从输入（目标）拖出 → 找到来源输出，建立图片链路。
                AddUIBinding(new UIBinding(UIBindingKind.Picture, BindingIdOf(match), BindingIdOf(node)));
                LogDiagnostic($"Binding: Input node '{node.DisplayName}' connected to source node '{match.DisplayName}'");
                SetStatusText(PPLocalizedResources.EffectBindView_Connected(match?.DisplayName ?? "?", node?.DisplayName ?? "?"));
                OnBindingConfigurationChanged();
                NotifyEffectBundlesChanged();
            }
            else
            {
                // 从输出（来源）拖出 → 找到目标输入，建立图片链路。
                AddUIBinding(new UIBinding(UIBindingKind.Picture, BindingIdOf(node), BindingIdOf(match)));
                LogDiagnostic($"Binding: Output node '{node.DisplayName}' connected to target node '{match.DisplayName}'");
                SetStatusText(PPLocalizedResources.EffectBindView_Connected(node?.DisplayName ?? "?", match?.DisplayName ?? "?"));
                OnBindingConfigurationChanged();
                NotifyEffectBundlesChanged();
            }

            // Provider binding configuration is already updated; redraw its projection.
            RebuildConnections();
        }
        else
        {
            SetStatusText(PPLocalizedResources.EffectBindView_ConnectedFail);
        }
    }

    /// <summary>
    /// 拖拽到一个无效位置（空白/不兼容端口）时，把拖拽来源端口的现有绑定断开。
    /// 参数绑定点对应断其 <see cref="UIBindingKind.Value"/> 绑定；图片输入/输出对应断图片链路。
    /// </summary>
    private bool TryDisconnectDroppedPort(NodeViewModel node, PortKind portKind, int portIndex)
    {
        bool removed = false;
        if (portKind == PortKind.ParamBind)
        {
            var paramKey = node.ParamPorts.Count > portIndex ? node.ParamPorts[portIndex].Key : null;
            if (paramKey is null) return false;
            removed = RemoveUIBindingsTo(BindingIdOf(node), paramKey, UIBindingKind.Value);
            if (removed)
            {
                RecreateNodeView(node);
                NotifyEffectBundlesChanged();
            }
        }
        else if (portKind == PortKind.AnchorInput)
        {
            removed = RemoveUIBindingsTo(BindingIdOf(node), null);
        }
        else if (portKind == PortKind.AnchorOutput)
        {
            removed = RemoveUIBindingsFrom(BindingIdOf(node));
        }

        if (removed)
        {
            RebuildConnections();
            LogDiagnostic($"Disconnected port on node '{node.DisplayName}'");
            OnBindingConfigurationChanged();
        }
        return removed;
    }

    private bool RemoveUIBindingsTo(Guid targetId, string? targetPortKey, UIBindingKind? kind = null)
    {
        if (_clip?.EffectProviders is not { } providers) return false;
        if (targetId == IEffectProvider.OutputAnchorGUID && (kind is null || kind == UIBindingKind.Picture))
        {
            var hadOutput = providers.Values.Any(p => p.IsFinalOutputSource());
            EffectBindingHelper.SetFinalOutput(providers, null);
            return hadOutput;
        }
        if (!providers.TryGetValue(targetId, out var target)) return false;

        if (kind is null && targetPortKey is null)
        {
            var changed = target.GetMainInputSource() != IEffectProvider.NoConnectionGUID.ToString();
            target.DisconnectMainInput();
            foreach (var binding in target.EnumerateFieldBindings().ToList())
            {
                target.ClearFieldBinding(binding.Key);
                changed = true;
            }
            return changed;
        }

        if (kind == UIBindingKind.Value || targetPortKey is not null)
        {
            if (targetPortKey is null || !target.TryGetFieldBinding(targetPortKey, out _)) return false;
            target.ClearFieldBinding(targetPortKey);
            return true;
        }

        var hadInput = target.GetMainInputSource() != IEffectProvider.NoConnectionGUID.ToString();
        target.DisconnectMainInput();
        return hadInput;
    }

    private bool RemoveUIBindingsFrom(Guid sourceId)
    {
        if (_clip?.EffectProviders is not { } providers) return false;
        var source = sourceId.ToString();
        bool removed = false;
        foreach (var provider in providers.Values)
        {
            if (provider.GetMainInputSource() == source)
            {
                provider.DisconnectMainInput();
                removed = true;
            }
            foreach (var binding in provider.EnumerateFieldBindings().Where(b => b.Value == source).ToList())
            {
                provider.ClearFieldBinding(binding.Key);
                removed = true;
            }
        }
        if (providers.TryGetValue(sourceId, out var sourceProvider) && sourceProvider.IsFinalOutputSource())
        {
            sourceProvider.SetFinalOutputSource(false);
            removed = true;
        }
        return removed;
    }

    /// <summary>
    /// 断开指定节点的全部 UI 层绑定：作为来源的图片/值输出，以及作为目标的图片/值输入。
    /// 桌面平台（Windows/macOS）由双击节点触发（见 <see cref="CreateNodeView"/> 的 OnClicked 回调）。
    /// Directly updates the provider-owned binding configuration.
    /// </summary>
    private void DisconnectAllFromNode(NodeViewModel node)
    {
        bool removed = false;
        removed |= RemoveUIBindingsFrom(BindingIdOf(node));
        removed |= RemoveUIBindingsTo(BindingIdOf(node), null);
        if (!removed) return;

        RecreateNodeView(node);
        RebuildConnections();
        ConnectionsLayer.Invalidate();
        OnBindingConfigurationChanged();
        NotifyEffectBundlesChanged();
        SetStatusText(Localized._Done);
    }

    private static double GetDragStartPortY(NodeViewModel node, bool isInput, int portIndex, PortKind portKind)
    {
        if (portKind == PortKind.ParamBind) return GetParamPortY(node, portIndex);
        if (isInput) return GetInputPortY(node);
        return GetOutputPortY(node);
    }

    private static EffectArgumentFieldType GetPortFieldType(NodeViewModel node, bool isInput, int portIndex, PortKind portKind)
    {
        if (portKind == PortKind.ParamBind) return node.ParamPorts.Count > portIndex ? node.ParamPorts[portIndex].FieldType : EffectArgumentFieldType.Unknown;
        if (isInput) return node.MainInputPort?.FieldType ?? EffectArgumentFieldType.Unknown;
        return node.OutputPort?.FieldType ?? EffectArgumentFieldType.Unknown;
    }

    /// <summary>是否为 ValueProvider 效果节点（其输出是值而非图片）。</summary>
    private bool IsValueNode(NodeViewModel n)
    {
        return n.Kind == NodeKind.Effect && n.Provider?.Target.HasFlag(EffectTarget.ValueProvider) == true;
    }

    /// <summary>取一个 ValueProvider 节点的来源 id。</summary>
    private string? GetValueSourceId(NodeViewModel n)
    {
        if (n.Kind == NodeKind.Effect && n.Provider != null && n.Provider.Target.HasFlag(EffectTarget.ValueProvider)) return n.Provider.Id.ToString();
        return null;
    }

    /// <summary>通过 UI 绑定中存储的节点 id 找回当前节点。</summary>
    private NodeViewModel? GetNodeByBindingId(Guid id)
    {
        if (_nodes.TryGetValue(id, out var n)) return n;
        if (_inputNode?.Id == id) return _inputNode;
        if (_outputNode?.Id == id) return _outputNode;
        return null;
    }

    private static Guid BindingIdOf(NodeViewModel node) => node.Kind switch
    {
        NodeKind.Input => IEffectProvider.InputAnchorGUID,
        NodeKind.Output => IEffectProvider.OutputAnchorGUID,
        _ => node.Id,
    };

    /// <summary>写入一条 UI 层绑定：同一来源到同一目标端口的旧绑定先移除（同端口单连线），再追加新绑定。</summary>
    /// <summary>
    /// 写入一条 UI 层绑定。同一目标端口（图片输入 或 参数字段）只允许一条入边：先移除目标为该端口的
    /// 旧绑定（无论来源），再追加新绑定。值绑定的来源（一个值输出）可同时喂给多个参数，因此不做来源去重。
    /// 图片入边只覆盖图片入边，值入边只覆盖值入边——两者互不干扰。
    /// </summary>
    private void AddUIBinding(UIBinding binding)
    {
        if (_clip?.EffectProviders is not { } providers) return;
        if (binding.Kind == UIBindingKind.Value)
        {
            if (!providers.TryGetValue(binding.Target, out var target) || string.IsNullOrWhiteSpace(binding.TargetPortKey)) return;
            target.SetFieldBinding(binding.TargetPortKey, binding.Source.ToString());
        }
        else if (binding.Target == IEffectProvider.OutputAnchorGUID)
        {
            EffectBindingHelper.SetFinalOutput(providers,
                binding.Source == IEffectProvider.InputAnchorGUID ? null : binding.Source);
        }
        else if (providers.TryGetValue(binding.Target, out var target))
        {
            target.SetMainInputSource(binding.Source);
        }
        LogDiagnostic($"Provider binding updated: {JsonSerializer.Serialize(binding)}");
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
            ArgumentNullException.ThrowIfNull(node.Provider);
            var ui = EffectServices.GetUIProvider(node.Provider);
            // Inject the binding host so each field in the property UI can offer a bind action.
            // Note: the property-panel bind channel writes directly into the provider's Fields
            // (via ClipBindingHost), while drag bindings in this view stay in the UI layer only.
            if (ui is IBindingHostHolder bindingHostHolder && _clip is not null)
            {
                bindingHostHolder.BindingHost = new ClipBindingHost(_clip, node.Provider, _page,
                    onChanged: () => { NotifyEffectBundlesChanged(); RefreshSelectedNode(); });
            }
            var ppb = ui.CreateUI(node.Provider);
            ArgumentNullException.ThrowIfNull(ppb, $"CreateUI() for {node.Provider?.TypeName}");
            ppb.PropertyChanged += (s, args) =>
            {
                ArgumentNullException.ThrowIfNull(node.Provider);
                if (node.Provider is IEffectProvider p)
                {
                    var fieldUpdate = ui.HandlePropertyPanelChange(node.Provider, args);
                    if (fieldUpdate.newFields is not null)
                        p.Fields = fieldUpdate.newFields;
                }
                RefreshNodeParams(node);
                // 属性面板改动字段后，重建连线（例如动态绑定字段值变化可能影响值绑定连线）。
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

    private void ShowNodeActionOverlay(NodeViewModel node)
    {
        _contextMenuNode = node;
        NodeActionOverlay.IsVisible = true;
        NodeActionButtonContainer.Children.Clear();

        NodeActionButtonContainer.Children.Add(new Label
        {
            Text = node.DisplayName,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 0, 8)
        });

        if (node.Kind == NodeKind.Effect)
        {
            AddNodeActionButton(PPLocalizedResources.EffectBindView_Configure, () =>
            {
                SelectNode(node);
                RightTabView.SelectedIndex = 0;
            });
            AddNodeActionButton(Localized.DraftPage_ContextMenu_Delete, () => RemoveEffect(node));
            AddNodeActionButton("切换参数方向", () => ToggleParamDirection(node));
        }
    }

    private void AddNodeActionButton(string text, Action action)
    {
        var button = new Button
        {
            Text = text,
            BackgroundColor = Color.FromArgb("#3c3c3c"),
            TextColor = Colors.White,
            CornerRadius = 4
        };
        button.Clicked += (s, e) =>
        {
            action();
            HideNodeActionOverlay();
        };
        NodeActionButtonContainer.Children.Add(button);
    }

    private void HideNodeActionOverlay()
    {
        NodeActionOverlay.IsVisible = false;
        _contextMenuNode = null;
    }

    private void OnNodeActionOverlayBackgroundTapped(object? sender, TappedEventArgs e)
    {
        HideNodeActionOverlay();
    }

    private void ToggleParamDirection(NodeViewModel node)
    {
        if (node.Provider == null) return;
        var next = !GetParamDirection(node);
        node.Provider.MetaData ??= new Dictionary<string, object>();
        node.Provider.MetaData[ParamDirectionKey] = next;
        RecreateNodeView(node);
        // 参数条上下切换后节点端口 Y 偏移变化，重建连线让端点刷新。
        RebuildConnections();
        ConnectionsLayer.Invalidate();
        NotifyEffectBundlesChanged();
    }

    private static bool GetParamDirection(NodeViewModel node)
    {
        if (node.Provider?.MetaData?.TryGetValue(ParamDirectionKey, out var v) == true)
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

        if (_drawable.DragSourceNode == node)
        {
            _drawable.DragSourceNode = null;
            _drawable.DragPoint = null;
        }

        if (_clip.EffectProviders is { } providers)
            EffectBindingHelper.RemoveProvider(providers, node.Id);
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

        // 节点删除后清理其涉及的全部 UI 绑定（作为来源或目标的连线），避免悬空引用。
        RemoveUIBindingsFrom(BindingIdOf(node));
        RemoveUIBindingsTo(BindingIdOf(node), null);
        OnBindingConfigurationChanged();
        RebuildConnections();
        ConnectionsLayer.Invalidate();
        SetStatusText(Localized._Done);
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
                                Log($"Successfully mapped Effect {effect.Name}/{effect.Id}/{effect.TypeName} with Provider {bundle.Name}/{bundle.TypeName}/{bundle.Id}");
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

    [DebuggerDisplay("{Id}, {Provider?.TypeName}")]
    class NodeViewModel
    {
        public required Guid Id;
        public required NodeKind Kind;
        public required IEffectProvider? Provider;
        public View? View;
        public double X, Y;

        /// <summary>
        /// The main input anchor port, derived from <see cref="IEffectProvider.InFields"/> with key = <see cref="EffectProviderAnchorExtensions.InputKey"/>.
        /// </summary>
        public NodePort? MainInputPort;
        /// <summary>Output anchor port, derived from <see cref="IEffectProvider.OutField"/>.</summary>
        public NodePort? OutputPort;
        /// <summary>Visible value-parameter bind ports (non-IPicture, non-NotVisibleInEffectPanel).</summary>
        public List<NodePort> ParamPorts = new();

        /// <summary>Height of the param strip above the frame (0 when below or absent).</summary>
        public double ParamsTopHeight;
        /// <summary>Node-local Y of each param row center (for the param bind ports).</summary>
        public double[] ParamPortYOffsets = [];

        public double DragStartX, DragStartY;
        public double DragTotalX, DragTotalY;

        public double Width => View?.Width > 0 ? View.Width : 150;

        public string DisplayName { get; set; } = "?";

        public ImageSource? PreviewImage { get; set { field = value; PreviewImageChanged?.Invoke(this, value); } }
        public event EventHandler<ImageSource?>? PreviewImageChanged;

        /// <summary>
        /// Derive <see cref="MainInputPort"/> (if presents), <see cref="OutputPort"/> and <see cref="ParamPorts"/>
        /// from the provider's <see cref="IEffectProvider.InFields"/>, <see cref="IEffectProvider.OutField"/>
        /// and <see cref="IEffectProvider.Fields"/>.
        /// </summary>
        public void BuildPortsFromProvider()
        {
            if (Provider == null) return;

            if (Provider.InFields.FirstOrDefault(c => c.Key == EffectProviderAnchorExtensions.InputKey).Value is IEffectArgumentField inputField)
            {
                MainInputPort = new NodePort
                {
                    Kind = PortKind.AnchorInput,
                    Key = inputField.Id,
                    FieldType = inputField.FieldType,
                    DisplayName = HumanizePortName(inputField.Id),
                    Index = 0
                };
            }

            OutputPort = new NodePort { Kind = PortKind.AnchorOutput, Key = Provider.OutField.Id, FieldType = Provider.OutField.FieldType, DisplayName = HumanizePortName(Provider.OutField.Id), Index = 0 };

            ParamPorts = Provider.Fields
                .Where(kv => !kv.Value.FieldType.HasFlag(EffectArgumentFieldType.IPicture)
                          && !kv.Value.FieldType.HasFlag(EffectArgumentFieldType.NotVisibleInEffectPanel)
                          && kv.Key != EffectProviderAnchorExtensions.InputKey)
                .Select((kv, i) => new NodePort { Kind = PortKind.ParamBind, Key = kv.Key, FieldType = kv.Value.FieldType, DisplayName = kv.Key, Index = i })
                .ToList();
        }

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
            instance.DisconnectMainInput();
            instance.SetFinalOutputSource(false);
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

    // ── UI-layer binding types ───────────────────────────────────────────

    /// <summary>Kind of a UI-layer binding: a picture chain edge or a value (parameter) binding.</summary>
    public enum UIBindingKind
    {
        /// <summary><see cref="UIBinding.Source"/>'s picture output feeds <see cref="UIBinding.Target"/>'s main picture input.</summary>
        Picture,
        /// <summary><see cref="UIBinding.Source"/>'s value output feeds <see cref="UIBinding.Target"/>'s parameter identified by <see cref="UIBinding.TargetPortKey"/>.</summary>
        Value
    }

    /// <summary>
    /// A UI-layer binding between two nodes. <see cref="Source"/> and <see cref="Target"/> are node ids —
    /// a provider <see cref="IEffectProvider.Id"/> for effect nodes, <see cref="IEffectProvider.InputAnchorGUID"/>
    /// / <see cref="IEffectProvider.OutputAnchorGUID"/> for the input/output system nodes, and a free-field global
    /// id for free-field reference nodes. Instances are read-only projections of provider-owned configuration.
    /// </summary>
    public record UIBinding(UIBindingKind Kind, Guid Source, Guid Target, string? TargetPortKey = null);

    /// <summary>Normalizes persisted configuration before projecting it into editor connections.</summary>
    private void NormalizeBindingConfiguration()
    {
        if (_clip?.EffectProviders == null) return;
        var diagnostics = EffectBindingHelper.NormalizeStoredBindings(_clip.EffectProviders).ToList();
        EffectBindingHelper.MaterializeFields(_clip.EffectProviders.Values);
        diagnostics.AddRange(EffectBindingHelper.ValidateBindings(_clip.EffectProviders));
        if (diagnostics.Count > 0)
            SetStatusText(string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)));
    }

    /// <summary>
    /// 公开读取当前配置的 UI 图快照。效果/系统/自由字段节点以 <see cref="IEffectProvider.Id"/>、锚点 GUID 或
    /// 自由字段 GlobalId 标识；返回值不作为可写配置源。
    /// </summary>
    public IReadOnlyList<UIBinding> ReadBindings()
    {
        var bindings = new List<UIBinding>();
        if (_clip?.EffectProviders is not { } providers) return bindings;

        foreach (var provider in providers.Values)
        {
            if (!_nodes.ContainsKey(provider.Id)) continue;
            var input = provider.GetMainInputSource();
            if (Guid.TryParse(input, out var inputId)
                && inputId != IEffectProvider.NoConnectionGUID
                && GetNodeByBindingId(inputId) is not null)
                bindings.Add(new UIBinding(UIBindingKind.Picture, inputId, provider.Id));

            if (provider.IsFinalOutputSource())
                bindings.Add(new UIBinding(UIBindingKind.Picture, provider.Id, IEffectProvider.OutputAnchorGUID));

            foreach (var fieldBinding in provider.EnumerateFieldBindings())
            {
                if (Guid.TryParse(fieldBinding.Value, out var sourceId) && GetNodeByBindingId(sourceId) is not null)
                    bindings.Add(new UIBinding(UIBindingKind.Value, sourceId, provider.Id, fieldBinding.Key));
            }
        }

        if (!providers.Values.Any(p => p.IsFinalOutputSource()))
            bindings.Add(new UIBinding(UIBindingKind.Picture, IEffectProvider.InputAnchorGUID, IEffectProvider.OutputAnchorGUID));
        return bindings;
    }

    /// <summary>
    /// 从 Provider 配置生成一张反映连线关系的 Mermaid 图。
    /// 节点 id 通过 <see cref="GetNodeByBindingId"/> 解析为显示名（覆盖 Effect / 输入 / 输出节点），
    /// 边按绑定类型标注：图片链路标记为 <c>Picture</c>，值绑定在边上标注目标参数 Id。
    /// </summary>
    public string GenerateUiBindingsMermaidDiagram()
    {
        var currentBindings = ReadBindings();
        if (currentBindings.Count == 0)
            return "graph TD;\n    empty[\"无 UI 绑定数据\"];";

        var sb = new StringBuilder();
        sb.AppendLine("graph TD;");

        // 收集所有被引用的节点 id，解析为显示名。
        var referencedIds = new HashSet<Guid>();
        foreach (var b in currentBindings)
        {
            referencedIds.Add(b.Source);
            referencedIds.Add(b.Target);
        }

        // 显示名与短节点 id。Effect 节点用 TypeName/Name，输入/输出/自由字段用各自的显示名。
        var nodeIdByGuid = new Dictionary<Guid, string>();
        var labelById = new Dictionary<Guid, string>();
        int counter = 0;
        foreach (var id in referencedIds.OrderBy(id => id))
        {
            string shortId = "N" + counter++;
            nodeIdByGuid[id] = shortId;
            labelById[id] = GetNodeLabelForDiagram(id);
            sb.AppendLine($"    {shortId}[\"{EscapeDiagramText(labelById[id])}\"];");
        }

        // 图片链路与值绑定边。
        var edges = new List<string>();
        foreach (var b in currentBindings)
        {
            if (b.Kind == UIBindingKind.Value)
            {
                var targetName = labelById.TryGetValue(b.Target, out var t) ? t : "?";
                var label = string.IsNullOrEmpty(b.TargetPortKey) ? "Value" : $"{b.TargetPortKey}";
                edges.Add($"    {nodeIdByGuid[b.Source]} -->|\"{EscapeDiagramText(label)} (值) → {EscapeDiagramText(targetName)}\"| {nodeIdByGuid[b.Target]};");
            }
            else
            {
                edges.Add($"    {nodeIdByGuid[b.Source]} -->|\"Picture\"| {nodeIdByGuid[b.Target]};");
            }
        }

        foreach (var line in edges.Distinct())
            sb.AppendLine(line);

        return sb.ToString();
    }

    /// <summary>生成 UI 绑定 Mermaid 图并弹窗展示。仿照 <see cref="ClipInfoBuilder"/> 的 Mermaid 弹窗实现。</summary>
    public async Task ShowUiBindingsMermaidPopupAsync()
    {
        if (_page == null) return;
        var graph = GenerateUiBindingsMermaidDiagram();
        await _page.ShowPopupAsync(
            new MermaidCodeBlockRenderer().Render(graph),
            new PopupOptions { CanBeDismissedByTappingOutsideOfPopup = true });
    }

    /// <summary>解析绑定 id 为 Mermaid 节点显示名。</summary>
    private string GetNodeLabelForDiagram(Guid id)
    {
        if (GetNodeByBindingId(id) is { } node)
            return node.DisplayName;
        return id.ToString();
    }

    /// <summary>转义 Mermaid 标签文本中的引号与方括号/大括号。</summary>
    private static string EscapeDiagramText(string text)
    {
        if (text is null) return "";
        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("[", "\\[")
            .Replace("]", "\\]")
            .Replace("{", "\\{")
            .Replace("}", "\\}");
    }

    private async void ShowUiBindingsGraphButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            await ShowUiBindingsMermaidPopupAsync();
        }
        catch (Exception ex)
        {
            LogDiagnostic($"Failed to show UI bindings graph: {ex}");
            SetStatusText("Failed to show bindings graph.");
        }
    }

    /// <summary>Validates provider-owned configuration and rematerializes runtime fields.</summary>
    private void OnBindingConfigurationChanged()
    {
        if (_clip?.EffectProviders == null) return;
        var providers = _clip.EffectProviders;
        EffectBindingHelper.MaterializeFields(providers.Values);
        var diagnostics = EffectBindingHelper.ValidateBindings(providers);
        if (diagnostics.Count > 0)
            SetStatusText(string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)));
        ScheduleProviderRebuild();
    }

    /// <summary>
    /// 合并延后的完整重建：设置挂起标志并经 Dispatcher 派发一次 <see cref="ClipInfoBuilder.RebuildAllEffects"/>，
    /// 使一次变化批处理只触发一次代价较高的重建。
    /// </summary>
    private void ScheduleProviderRebuild()
    {
        if (_clip == null || _pendingProviderRebuild) return;
        _pendingProviderRebuild = true;
        Dispatcher.Dispatch(() =>
        {
            _pendingProviderRebuild = false;
            if (_clip == null) return;
            try
            {
                ClipInfoBuilder.RebuildAllEffects(_clip);
            }
            catch (InvalidOperationException ex)
            {
                SetStatusText(ex.Message);
                LogDiagnostic($"Effect binding graph is not renderable yet: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 从 UI 层绑定列表派生连线渲染数据，并把每条连线的端点在当前节点集合中解析为可绘制坐标。
    /// 找不到端点（节点被移除等）的绑定直接跳过。
    /// </summary>
    private void RebuildConnections()
    {
        _drawable.Connections.Clear();

        foreach (var b in ReadBindings())
        {
            if (GetNodeByBindingId(b.Source) is not { } from) continue;
            if (GetNodeByBindingId(b.Target) is not { } to) continue;

            if (b.Kind == UIBindingKind.Value)
            {
                var paramIndex = to.ParamPorts.FindIndex(p => p.Key == b.TargetPortKey);
                if (paramIndex < 0) continue;
                var fieldType = to.ParamPorts[paramIndex].FieldType;
                _drawable.Connections.Add(new NodeConnection
                {
                    From = from,
                    To = to,
                    ToPortIndex = paramIndex,
                    IsValueBinding = true,
                    FieldType = fieldType,
                    TargetParamFieldId = b.TargetPortKey
                });
            }
            else
            {
                var srcType = from.OutputPort?.FieldType ?? EffectArgumentFieldType.IPicture;
                _drawable.Connections.Add(new NodeConnection
                {
                    From = from,
                    To = to,
                    ToPortIndex = 0,
                    IsValueBinding = false,
                    FieldType = srcType
                });
            }
        }
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
            // Connections are rebuilt from the UI-layer bindings whenever nodes change (see RebuildConnections).
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
                    : conn.To.Y + GetInputPortY(conn.To);
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
