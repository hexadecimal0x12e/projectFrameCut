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
using projectFrameCut.Render.ClipsAndTracks;
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

using Point = Microsoft.Maui.Graphics.Point;
using Rect = Microsoft.Maui.Graphics.Rect;

namespace projectFrameCut.DraftStuff;

public enum NodeKind { Effect, Input, Output, ClipArguments }

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

    private EffectPipeline _pipeline = EffectPipeline.Picture;
    private bool _loadingPipeline;
    private ClipElementUI? _clip;
    private DraftPage? _page;
    private (ClipElementUI Clip, DraftPage Page, EffectTarget Target, EffectPipeline Pipeline)? _addEffectsContext;
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

    /// <summary>合并密集的绑定变更，并避免在 UI 线程执行代价高的 provider.Build()。</summary>
    private readonly SemaphoreSlim _providerRebuildGate = new(1, 1);
    private CancellationTokenSource? _providerRebuildCts;
    private CancellationTokenSource? _previewCts;

    /// <summary>
    /// Raised when effect providers or connections have been modified inside this view.
    /// Subscribers (e.g. ClipInfoBuilder's effect tab) should rebuild effects and refresh UI.
    /// </summary>
    public event Action? EffectProvidersChanged;

    private void NotifyEffectProvidersChanged() => EffectProvidersChanged?.Invoke();

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

#if WINDOWS
        AddEffectsScrollView.HandlerChanged += (_, _) =>
        {
            if (AddEffectsScrollView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ScrollViewer sv)
                sv.BringIntoViewOnFocusChange = false;
        };
#endif

        InfoLabel.Text = PPLocalizedResources.EffectBindView_Hint;
        PipelinePicker.ItemsSource = new[] { Localized.Effect_NativePipeline, Localized.Effect_PicturePipeline };
        PipelinePicker.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingPipeline || _clip is null) return;
            _pipeline = PipelinePicker.SelectedIndex == 0 ? EffectPipeline.NativeContent : EffectPipeline.Picture;
            LoadClip(_clip, _page, _showIsNotVisibleInEffectEditorEffect, _pipeline);
        };

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
        if (Handler == null)
        {
            _providerRebuildCts?.Cancel();
            _previewCts?.Cancel();
            UnsubscribeFromPageEvents();
        }
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
        ApplyZoom(targetScale, ConnectionsLayer.Width / 2, ConnectionsLayer.Height / 2);
    }

    private void OnZoomOut(object? sender, EventArgs e)
    {
        double targetScale = Math.Max(NodesContainer.Scale / 1.2, 0.2);
        // Zoom to center of the view
        ApplyZoom(targetScale, ConnectionsLayer.Width / 2, ConnectionsLayer.Height / 2);
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
        SaveViewTransform();
    }

    private void UpdateDrawableScale()
    {
        _drawable.Scale = NodesContainer.Scale;
        _drawable.PanX = NodesContainer.TranslationX;
        _drawable.PanY = NodesContainer.TranslationY;
        ConnectionsLayer.Invalidate();
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
        => LoadClip(clip, page, showIsNotVisibleInEffectEditorEffect, null);

    public void LoadClip(ClipElementUI clip, DraftPage? page, bool showIsNotVisibleInEffectEditorEffect, EffectPipeline? pipeline)
    {
        _previewCts?.Cancel();
        if (pipeline is { } stage) _pipeline = stage;
        if (!clip.SupportsNativeEffects) _pipeline = EffectPipeline.Picture;
        _loadingPipeline = true;
        PipelinePicker.IsVisible = clip.SupportsNativeEffects;
        PipelinePicker.SelectedIndex = _pipeline == EffectPipeline.NativeContent ? 0 : 1;
        _loadingPipeline = false;
        _clip = clip;
        _page = page;
        _showIsNotVisibleInEffectEditorEffect = showIsNotVisibleInEffectEditorEffect;
        EnsureClipArgumentProvider();
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
            DisplayName = _pipeline == EffectPipeline.NativeContent ? Localized.Effect_NativeInput : PPLocalizedResources.EffectBind_SourcePicture,
            OutputPort = new NodePort
            {
                Kind = PortKind.AnchorOutput,
                Key = EffectProviderAnchorExtensions.InputKey,
                FieldType = EffectArgumentFieldType.IPicture,
                DisplayName = _pipeline == EffectPipeline.NativeContent ? Localized.Effect_NativeInput : PPLocalizedResources.EffectBind_SourcePicture,
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
                if (bundle is not ClipArgumentProvider && !bundle.Target.HasFlag(EffectTarget.ValueProvider) && bundle.TypeOfEffect.GetPipeline() != _pipeline) continue;
                // Skip bundles that are internal/special effects (e.g. Crop, Place, Resize)
                // to keep consistent with ClipInfoBuilder.BuildEffectTab filtering behavior.
                if (bundle is not ClipArgumentProvider && !showIsNotVisibleInEffectEditorEffect && (bundle.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor)
                    || !bundle.Target.HasFlag(EffectTarget.ValueProvider) && !EffectBindingHelper.AreTargetsCompatible(bundle.Target, _clip.GetEffectTarget())))
                    continue;

                var node = new NodeViewModel
                {
                    Id = bundle.Id,
                    Kind = bundle is ClipArgumentProvider ? NodeKind.ClipArguments : NodeKind.Effect,
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
            DisplayName = _pipeline == EffectPipeline.NativeContent ? Localized.Effect_NativeOutput : PPLocalizedResources.EffectBind_FinalResult,
            MainInputPort = new NodePort
            {
                Kind = PortKind.AnchorInput,
                Key = EffectProviderAnchorExtensions.InputKey,
                FieldType = EffectArgumentFieldType.IPicture,
                DisplayName = _pipeline == EffectPipeline.NativeContent ? Localized.Effect_NativeOutput : PPLocalizedResources.EffectBind_FinalResult,
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
        RefreshBindingDiagnosticIndicators();
        RebuildConnections();
        ApplySavedViewTransform();
        ConnectionsLayer.Invalidate();
    }

    private void EnsureClipArgumentProvider()
    {
        if (_clip is null) return;
        _clip.EffectProviders ??= new();
        var provider = _clip.EffectProviders.Values.OfType<ClipArgumentProvider>().FirstOrDefault();
        if (provider is null)
        {
            provider = new ClipArgumentProvider { Name = (string)Resources["ClipArgumentsNodeTitle"] };
            _clip.EffectProviders.Add(provider.Id, provider);
            Log($"Created clip argument node for {_clip.Id}.");
        }
        IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> fields = ClipArgumentHandler.DefaultFields;
        var defaults = new Dictionary<string, object>();
        if (_page is not null)
        {
            try
            {
                var dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(_page, _clip, rebuildEffects: false);
                using var owner = PluginManager.CreateClip(JsonSerializer.SerializeToElement(dto));
                fields = owner.ArgumentFields;
                foreach (var (key, field) in fields) defaults[key] = field.ReadValue(owner);
            }
            catch (Exception ex)
            {
                Log(ex, $"Read argument fields for clip {_clip.Id}", this);
            }
        }
        var clip = _clip;
        provider.Attach(fields, key => key switch
        {
            nameof(IClip.TargetX) => clip.TargetX,
            nameof(IClip.TargetY) => clip.TargetY,
            nameof(IClip.TargetWidth) => clip.TargetWidth,
            nameof(IClip.TargetHeight) => clip.TargetHeight,
            nameof(IClip.Rotation) => clip.Rotation,
            _ => defaults.GetValueOrDefault(key) ?? fields[key].DefaultValue,
        });
    }

    /// <summary>
    /// Reloads all effect data from the current clip.
    /// Call this when external code (e.g. ClipInfoBuilder) has modified effect bundlesOnPortPan
    /// and the binding view needs to reflect the latest state.
    /// </summary>
    public void Reload()
    {
        if (_clip == null) return;
        Log($"Reload effect binding view for {_clip.Id}, pipeline {_pipeline}, effect selector scroll {AddEffectsScrollView.ScrollY}.", "debug");
        LoadClip(_clip, _page, _showIsNotVisibleInEffectEditorEffect, _pipeline);
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
    private static double GetOutputPortY(NodeViewModel n, int index = 0) => n.ParamsTopHeight
        + 5 + (index + 0.5) * (n.BodyHeight - 10) / Math.Max(1, n.OutputPorts.Count);

    private static double GetInputPortY(NodeViewModel n) => n.ParamsTopHeight + n.BodyHeight / 2;

    private static double GetParamPortY(NodeViewModel n, int idx) => n.ParamPortYOffsets.Length > idx ? n.ParamPortYOffsets[idx] : n.ParamsTopHeight + FrameHeight / 2;

    private static Point GetPortPosition(NodeViewModel node, PortKind kind, int index = 0)
    {
        View? port = kind switch
        {
            PortKind.AnchorInput => node.MainInputPortView,
            PortKind.AnchorOutput => node.OutputPortViews.ElementAtOrDefault(index),
            _ => node.ParamPortViews.ElementAtOrDefault(index),
        };
        if (port is not null && port.Width > 0 && port.Height > 0 && node.View is not null)
        {
            double x = port.Width / 2, y = port.Height / 2;
            // 使用实际布局坐标，包含卡片边框、内边距和参数行的偏移。
            for (View? v = port; v is not null; v = v.Parent as View)
            {
                if (ReferenceEquals(v, node.View)) return new Point(node.X + x, node.Y + y);
                x += v.X;
                y += v.Y;
            }
        }
        return new Point(node.X + (kind == PortKind.AnchorOutput ? node.Width : 0), node.Y + (kind switch
        {
            PortKind.AnchorInput => GetInputPortY(node),
            PortKind.AnchorOutput => GetOutputPortY(node, index),
            _ => GetParamPortY(node, index),
        }));
    }

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
        node.MainInputPortView = null;
        node.OutputPortViews.Clear();
        node.ParamPortViews.Clear();
        var bindingDiagnostics = GetBindingDiagnostics(node);
        node.BindingDiagnosticSignature = GetBindingDiagnosticSignature(bindingDiagnostics);
        var container = new VerticalStackLayout
        {
            Spacing = 0,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = false
        };

        var borderColor = bindingDiagnostics.Count > 0
            ? Colors.OrangeRed
            : node.Kind switch
            {
                NodeKind.Effect => Colors.Gray,
                _ => Colors.White,
            };

        var frame = new Border
        {
            Stroke = borderColor,
            StrokeThickness = bindingDiagnostics.Count > 0 ? 4 : node.Kind == NodeKind.Effect ? 2 : 4,
            StrokeShape = new RoundRectangle { CornerRadius = 5 },
            BackgroundColor = Color.FromArgb("#2d2d2d"),
            Padding = 5,
            HeightRequest = node.BodyHeight,
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
        if (bindingDiagnostics.Count > 0)
        {
            ToolTipProperties.SetText(frame,
                string.Join(Environment.NewLine, bindingDiagnostics.Select(d => $"[{d.Code}] {d.Message}")));
        }

        // ── Parameter display strip (above or below the frame, read-only) ──
        VerticalStackLayout? paramStack = null;
        bool paramsOnTop = true;
        if (node.Provider is not null && node.ParamPorts.Count > 0)
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
            node.MainInputPortView = box;
        }

        var outputPorts = new VerticalStackLayout { Spacing = 0, HeightRequest = node.BodyHeight - 10 };
        for (int i = 0; i < node.OutputPorts.Count; i++)
        {
            var port = node.OutputPorts[i];
            var row = new Grid { ColumnSpacing = 4, HeightRequest = (node.BodyHeight - 10) / node.OutputPorts.Count,
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto },
                } };
            if (node.OutputPorts.Count > 1)
                row.Add(new Label { Text = port.DisplayName, FontSize = 10, TextColor = Colors.LightGray,
                    MaximumWidthRequest = 80, HorizontalOptions = LayoutOptions.End,
                    VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation });
            var box = new BoxView { Color = PortTypeHelper.GetTypeColor(port.FieldType), WidthRequest = PortSize,
                HeightRequest = PortSize, VerticalOptions = LayoutOptions.Center };
            ToolTipProperties.SetText(box, port.DisplayName);
            int index = i;
            var outputPan = new PanGestureRecognizer();
            outputPan.PanUpdated += (s, e) => OnPortPan(node, e, false, index, PortKind.AnchorOutput);
            box.GestureRecognizers.Add(outputPan);
            row.Add(box, 1, 0);
            node.OutputPortViews.Add(box);
            outputPorts.Add(row);
        }

        // Handle visibility for System Nodes
        if (node.Kind == NodeKind.Input) inputPortView.IsVisible = false; // Hide Input on Input Node
        if (node.Kind == NodeKind.Output) outputPorts.IsVisible = false;

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

        // Layout
        var layout = new Grid { ColumnSpacing = 6, ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Star }, new ColumnDefinition { Width = GridLength.Auto } } };

        bodyActionContainer.Add(label);

        layout.Add(inputPortView, 0, 0);
        layout.Add(bodyActionContainer, 1, 0);
        layout.Add(outputPorts, 2, 0);

        //if (node.OutputAnchorID == IEffectProvider.NoConnectionGUID && node.InputAnchorID != IEffectProvider.NoConnectionGUID)
        //{
        //    frame.Opacity = 0.8;
        //}
        //TODO: Dim of node with no output connection (but has input) is not implemented yet, as the connection logic is being rewritten.

        frame.SizeChanged += (s, e) => ConnectionsLayer.Invalidate();
        container.SizeChanged += (s, e) => ConnectionsLayer.Invalidate();

        frame.Content = layout;

        container.Add(frame);

        if (paramStack is not null && !paramsOnTop) container.Add(paramStack);

        if (bindingDiagnostics.Count > 0)
        {
            container.Add(new Label
            {
                Text = string.Join(Environment.NewLine, bindingDiagnostics.Select(d => $"⚠ [{d.Code}] {d.Message}")),
                TextColor = Colors.OrangeRed,
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.WordWrap,
                MaximumWidthRequest = 320,
                Margin = new Thickness(4, 3, 4, 0)
            });
        }

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

        var previewValue = new Label { TextColor = Colors.White, HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.WordWrap };
        var preview = new Grid();
        preview.Add(previewImage);
        preview.Add(previewValue);
        previewBorder.Content = preview;
        node.PreviewValueChanged += (_, value) =>
        {
            previewValue.Text = value;
            previewImage.IsVisible = string.IsNullOrEmpty(value);
            previewValue.IsVisible = !string.IsNullOrEmpty(value);
            arrow.IsVisible = previewBorder.IsVisible = !string.IsNullOrEmpty(value) || node.PreviewImage is not null;
        };
        previewValue.Text = node.PreviewValue;
        previewValue.IsVisible = !string.IsNullOrEmpty(node.PreviewValue);
        previewImage.IsVisible = !previewValue.IsVisible;

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
        if (node.PreviewImage != null || !string.IsNullOrEmpty(node.PreviewValue))
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
        double baseY = paramsOnTop ? 0 : node.BodyHeight;

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
            node.ParamPortViews.Add(dot);
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
                if (node?.Provider is { } b)
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
                _drawable.DragSourcePortIndex = portIndex;
                _drawable.DragSourceKind = portKind;
                _drawable.DragFieldType = GetPortFieldType(node, isInput, portIndex, portKind);
                var start = GetPortPosition(node, portKind, portIndex);
                _drawable.DragPoint = new Point(start.X * NodesContainer.Scale + _drawable.PanX,
                    start.Y * NodesContainer.Scale + _drawable.PanY);
                ConnectionsLayer.Invalidate();
                break;

            case GestureStatus.Running:
                double scale = NodesContainer.Scale;
                var position = GetPortPosition(node, portKind, portIndex);
                double baseX = position.X * scale + _drawable.PanX;
                double baseY = position.Y * scale + _drawable.PanY;

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
        var hit = FindPortTarget(node, isInput, portKind, portIndex, rawPoint, ConnectionSnapRadius);
        return hit is null ? rawPoint : new Point(hit.X, hit.Y);
    }

    private sealed record PortHit(NodeViewModel Node, PortKind Kind, int Index, double X, double Y);

    private PortHit? FindPortTarget(NodeViewModel node, bool isInput, PortKind kind, int index, Point point, double radius)
    {
        bool input = isInput || kind == PortKind.ParamBind;
        var type = GetPortFieldType(node, input, index, kind);
        PortHit? hit = null;
        double best = radius * radius;
        void Check(NodeViewModel candidate, NodePort? port, PortKind targetKind, int targetIndex)
        {
            if (!NodePort.IsValidPort(port)) return;
            if (!PortTypeHelper.IsPortTypeCompatible(input ? port!.FieldType : type, input ? type : port!.FieldType)) return;
            var position = GetPortPosition(candidate, targetKind, targetIndex);
            double x = position.X * NodesContainer.Scale + _drawable.PanX;
            double y = position.Y * NodesContainer.Scale + _drawable.PanY;
            double distance = (x - point.X) * (x - point.X) + (y - point.Y) * (y - point.Y);
            if (distance >= best) return;
            best = distance;
            hit = new(candidate, targetKind, targetIndex, x, y);
        }
        foreach (var candidate in _nodes.Values)
        {
            if (candidate == node) continue;
            if (input)
            {
                if (candidate.Kind == NodeKind.Output) continue;
                for (int i = 0; i < candidate.OutputPorts.Count; i++)
                    Check(candidate, candidate.OutputPorts[i], PortKind.AnchorOutput, i);
            }
            else
            {
                if (candidate.Kind == NodeKind.Input) continue;
                Check(candidate, candidate.MainInputPort, PortKind.AnchorInput, 0);
                for (int i = 0; i < candidate.ParamPorts.Count; i++)
                    Check(candidate, candidate.ParamPorts[i], PortKind.ParamBind, i);
            }
        }
        return hit;
    }

    private void HandlePortConnect(NodeViewModel node, bool isInput, int portIndex, PortKind portKind, Point dropPoint)
    {
        var hit = FindPortTarget(node, isInput, portKind, portIndex, dropPoint, 40);
        if (hit is null)
        {
            if (TryDisconnectDroppedPort(node, portKind, portIndex)) OnBindingConfigurationChanged();
            SetStatusText(PPLocalizedResources.EffectBindView_ConnectedFail);
            RebuildConnections();
            return;
        }
        bool input = isInput || portKind == PortKind.ParamBind;
        var source = input ? hit.Node : node;
        var target = input ? node : hit.Node;
        var targetKind = input ? portKind : hit.Kind;
        int targetIndex = input ? portIndex : hit.Index;
        int sourceIndex = input ? hit.Index : portIndex;
        string? sourcePortKey = source.Provider is IMultipleOutputEffectProvider ? source.OutputPorts[sourceIndex].Key : null;
        if (targetKind == PortKind.AnchorInput && source.Provider is not null && target.Provider is not null
            && !source.Provider.CanConnectContent(target.Provider, sourcePortKey))
        {
            SetStatusText(Localized.Effect_IncompatiblePipeline);
            return;
        }
        AddUIBinding(targetKind == PortKind.ParamBind
            ? new(UIBindingKind.Value, BindingIdOf(source), BindingIdOf(target), target.ParamPorts[targetIndex].Key, sourcePortKey)
            : new(UIBindingKind.Picture, BindingIdOf(source), BindingIdOf(target), SourcePortKey: sourcePortKey));
        LogDiagnostic($"Connected '{source.DisplayName}' to '{target.DisplayName}', port {targetKind}/{targetIndex}.");
        SetStatusText(PPLocalizedResources.EffectBindView_Connected(source.DisplayName, target.DisplayName));
        RecreateNodeView(target);
        OnBindingConfigurationChanged();
        RebuildConnections();
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
            }
        }
        else if (portKind == PortKind.AnchorInput)
        {
            removed = RemoveUIBindingsTo(BindingIdOf(node), null);
        }
        else if (portKind == PortKind.AnchorOutput)
        {
            removed = RemoveUIBindingsFrom(BindingIdOf(node), node.Provider is IMultipleOutputEffectProvider ? node.OutputPorts[portIndex].Key : null);
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
            var hadOutput = providers.Values.Any(p => p.TypeOfEffect.GetPipeline() == _pipeline && p.IsFinalOutputSource());
            EffectBindingHelper.SetFinalOutput(providers, null, _pipeline);
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

    private bool RemoveUIBindingsFrom(Guid sourceId, string? sourcePortKey = null)
    {
        if (_clip?.EffectProviders is not { } providers) return false;
        var source = sourcePortKey is null ? sourceId.ToString() : EffectProviderOutputExtensions.CreateOutputSourceId(sourceId, sourcePortKey);
        bool Matches(string value) => value == source || sourcePortKey is null
            && EffectProviderOutputExtensions.TryParseOutputSourceId(value, out var id, out _) && id == sourceId;
        bool removed = false;
        foreach (var provider in providers.Values)
        {
            if (Matches(provider.GetMainInputSource()))
            {
                provider.DisconnectMainInput();
                removed = true;
            }
            foreach (var binding in provider.EnumerateFieldBindings().Where(b => Matches(b.Value)).ToList())
            {
                provider.ClearFieldBinding(binding.Key);
                removed = true;
            }
        }
        if (providers.TryGetValue(sourceId, out var sourceProvider) && sourceProvider.IsFinalOutputSource()
            && (sourcePortKey is null || sourceProvider.GetFinalOutputFieldId() == sourcePortKey))
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
        SetStatusText(Localized._Done);
    }

    private static EffectArgumentFieldType GetPortFieldType(NodeViewModel node, bool isInput, int portIndex, PortKind portKind)
    {
        if (portKind == PortKind.ParamBind) return node.ParamPorts.Count > portIndex ? node.ParamPorts[portIndex].FieldType : EffectArgumentFieldType.Unknown;
        if (isInput) return node.MainInputPort?.FieldType ?? EffectArgumentFieldType.Unknown;
        return node.OutputPorts.Count > portIndex ? node.OutputPorts[portIndex].FieldType : EffectArgumentFieldType.Unknown;
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
        var sourceId = binding.SourcePortKey is null ? binding.Source.ToString()
            : EffectProviderOutputExtensions.CreateOutputSourceId(binding.Source, binding.SourcePortKey);
        if (binding.Kind == UIBindingKind.Value)
        {
            if (!providers.TryGetValue(binding.Target, out var target) || string.IsNullOrWhiteSpace(binding.TargetPortKey)) return;
            target.SetFieldBinding(binding.TargetPortKey, sourceId);
        }
        else if (binding.Target == IEffectProvider.OutputAnchorGUID)
        {
            EffectBindingHelper.SetFinalOutput(providers,
                binding.Source == IEffectProvider.InputAnchorGUID ? null : binding.Source, _pipeline, binding.SourcePortKey);
        }
        else if (providers.TryGetValue(binding.Target, out var target))
        {
            if (providers.TryGetValue(binding.Source, out var source) && !source.CanConnectContent(target, binding.SourcePortKey))
            {
                SetStatusText(Localized.Effect_IncompatiblePipeline);
                return;
            }
            target.SetMainInputSource(sourceId);
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

        if (node.Provider is null)
        {
            return;
        }

        try
        {
            ArgumentNullException.ThrowIfNull(node.Provider);
            var ui = node.Provider is ClipArgumentProvider ? new EffectProviderUI(node.Provider) : EffectServices.GetUIProvider(node.Provider);
            // Inject the binding host so each field in the property UI can offer a bind action.
            // Note: the property-panel bind channel writes directly into the provider's Fields
            // (via ClipBindingHost), while drag bindings in this view stay in the UI layer only.
            if (ui is IBindingHostHolder bindingHostHolder && _clip is not null)
            {
                bindingHostHolder.BindingHost = new ClipBindingHost(_clip, node.Provider, _page,
                    onChanged: () =>
                    {
                        OnBindingConfigurationChanged();
                        RefreshSelectedNode();
                    });
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
                OnBindingConfigurationChanged();
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
        if (_selectedNode?.Provider is not null)
        {
            SelectNode(_selectedNode);
        }
    }

    /// <summary>
    /// Rebuild only the node-embedded parameter strip so it reflects the latest field values.
    /// </summary>
    private void RefreshNodeParams(NodeViewModel node)
    {
        if (node?.Provider is null || node.View == null) return;
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

        if (node.Provider is not null)
        {
            AddNodeActionButton(PPLocalizedResources.EffectBindView_Configure, () =>
            {
                SelectNode(node);
                RightTabView.SelectedIndex = 0;
            });
            if (node.Kind == NodeKind.Effect)
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
        NotifyEffectProvidersChanged();
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
        ClipInfoBuilder.RebuildAllEffects(_clip);
        var w = _page.previewWidth;
        var h = _page.previewHeight;
        var projectRelativeWidth = Math.Max(1, _page.ProjectInfo.RelativeWidth);
        var projectRelativeHeight = Math.Max(1, _page.ProjectInfo.RelativeHeight);
        _previewCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _previewCts = cts;

        try
        {
            using var owner = PluginManager.CreateClip(JsonSerializer.SerializeToElement(
                DraftImportAndExportHelper.ExportClipElementFromDraftPage(_page, _clip, rebuildEffects: false)));
            owner.ReInit(8);
            uint targetFrame = (uint)Math.Clamp(_page.CurrentFrame, owner.StartFrame,
                (double)owner.StartFrame + Math.Max(1u, owner.GetEffectiveDuration()) - 1);
            var clip = ClipArgumentBinding.InitializeFrame(owner, targetFrame);
            using var srcFrame = VideoClipRotation.ReadFrame(clip, clip.GetRelativeFrameIndex(targetFrame) ?? 0,
                clip.TargetWidth > 0 ? Math.Max(1, (int)Math.Round((double)clip.TargetWidth * w / projectRelativeWidth)) : w,
                clip.TargetHeight > 0 ? Math.Max(1, (int)Math.Round((double)clip.TargetHeight * h / projectRelativeHeight)) : h, 8);
            if (_inputNode is not null)
            {
                await UpdateNodePreview(_inputNode, srcFrame, cts.Token);
            }
            var frame = new OneFrame(targetFrame, clip, srcFrame, resolveEffects: false);
            var previews = new Dictionary<NodeViewModel, object?>();
            void Capture(IEffect effect, object? value)
            {
                if (!Guid.TryParse(effect.BindedEffectProvidingSystemID, out var id) || !_nodes.TryGetValue(id, out var node)) return;
                if (previews.GetValueOrDefault(node) is IPicture old) old.Dispose();
                previews[node] = value switch
                {
                    IPicture picture => DynamicEffectGraph.CopyPicture(picture),
                    IReadOnlyDictionary<string, object?> outputs => string.Join(Environment.NewLine, outputs.Select(p =>
                        $"{p.Key}: {(p.Value is IPicture pic ? $"{pic.Width}×{pic.Height}" : p.Value?.ToString() ?? "-")}")),
                    _ => value,
                };
            }
            try
            {
                using var result = Timeline.MixtureLayers([frame], targetFrame, w, h, 8,
                    AfterEffectCallback: (effect, picture) => Capture(effect, picture),
                    projectRelativeWidth: projectRelativeWidth, projectRelativeHeight: projectRelativeHeight,
                    disposeIntermediateFrames: true,
                    cancellationToken: cts.Token,
                    afterNodeCallback: (effect, value) => { if (value is not IPicture) Capture(effect, value); });
                foreach (var (node, value) in previews)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (value is IPicture picture) await UpdateNodePreview(node, picture, cts.Token);
                    else node.PreviewValue = value?.ToString() ?? "";
                }
                if (_outputNode is not null) await UpdateNodePreview(_outputNode, result, cts.Token);
                foreach (var node in _nodes.Values.Where(n => n.Kind == NodeKind.ClipArguments))
                    await UpdateNodePreview(node, result, cts.Token);
            }
            finally
            {
                foreach (var picture in previews.Values.OfType<IPicture>()) picture.Dispose();
            }

        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log(ex, $"Failed to render", this);
#if DEBUG
            if (await _page.DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(0, ex), "Throw", Localized._OK)) throw;
#else
            await _page.DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(0, ex), Localized._OK);
#endif
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts)) _previewCts = null;
        }

    }

    private async Task UpdateNodePreview(NodeViewModel node, IPicture picture, CancellationToken token = default)
    {
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                using var stream = new MemoryStream();
                await Task.Run(() => picture.SaveToPng(stream), token);
                token.ThrowIfCancellationRequested();
                stream.Position = 0;
                var imageSource = ImageSource.FromStream(() => new MemoryStream(stream.ToArray()));
                node.PreviewValue = "";
                node.PreviewImage = imageSource;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
        public View? MainInputPortView;
        public List<View> OutputPortViews = new();
        public List<View> ParamPortViews = new();
        public string BindingDiagnosticSignature = string.Empty;
        public double X, Y;

        /// <summary>
        /// The main input anchor port, derived from <see cref="IEffectProvider.InFields"/> with key = <see cref="EffectProviderAnchorExtensions.InputKey"/>.
        /// </summary>
        public NodePort? MainInputPort;
        /// <summary>Output anchor port, derived from <see cref="IEffectProvider.OutField"/>.</summary>
        public List<NodePort> OutputPorts = new();
        public NodePort? OutputPort
        {
            get => OutputPorts.FirstOrDefault();
            set => OutputPorts = value is null ? [] : [value];
        }
        public double BodyHeight => Math.Max(FrameHeight, OutputPorts.Count * ParamRowHeight);
        /// <summary>Visible named input ports.</summary>
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
        public string PreviewValue { get; set { field = value; PreviewValueChanged?.Invoke(this, value); } } = "";
        public event EventHandler<string>? PreviewValueChanged;

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

            OutputPorts = Provider is ClipArgumentProvider || Provider.TypeOfEffect == EffectType.Transform ? []
                : Provider.GetOutputFields().Values.Select((p, i) => new NodePort
                {
                    Kind = PortKind.AnchorOutput, Key = p.Id, FieldType = p.FieldType, DisplayName = HumanizePortName(p.Id), Index = i,
                }).ToList();

            ParamPorts = DynamicEffectBindings.InputFields(Provider)
                .Where(kv => !kv.Value.FieldType.HasFlag(EffectArgumentFieldType.NotVisibleInEffectPanel)
                          && kv.Key != EffectProviderAnchorExtensions.InputKey)
                .Select((kv, i) => new NodePort { Kind = PortKind.ParamBind, Key = kv.Key, FieldType = kv.Value.FieldType, DisplayName = kv.Key, Index = i })
                .ToList();
        }

    }

    // ── Add-effects panel ────────────────────────────────────────────────

    private void UpdateAddEffectsPanel()
    {
        if (_clip is null || _page is null)
        {
            _addEffectsContext = null;
            AddEffectsPanel.Children.Clear();
            return;
        }

        var target = _clip.SupportsNativeEffects ? _clip.GetEffectSelectionTarget(_pipeline) : _clip.GetEffectSelectionTarget();
        var context = (_clip, _page, target, _pipeline);
        if (_addEffectsContext == context && AddEffectsPanel.Children.Count > 0) return;

        var panel = ClipInfoBuilder.BuildAddEffectPanel(
            target,
            _page,
            EffectServices.GetAvailableEffectProviders()
                .Where(p => !_clip.SupportsNativeEffects || p.Value().Target.HasFlag(EffectTarget.ValueProvider) || p.Value().TypeOfEffect.GetPipeline() == _pipeline)
                .ToDictionary(p => p.Key, p => p.Value),
            new(),
            (s, e) =>
            {
                if (e.Id == "AddProvider")
                {
                    var providerTypeName = e.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(providerTypeName)) AddProvider(providerTypeName);
                }
            },
            hideKeyFramedProviders: true
        );
        AddEffectsPanel.Children.Clear();
        AddEffectsPanel.Children.Add(panel);
        _addEffectsContext = context;
        Log($"Rebuilt effect selector for {_clip.Id}, target {target}, pipeline {_pipeline}.", "debug");
    }

    private void AddProvider(string providerTypeName)
    {
        if (_clip == null) return;

        var providerFactories = EffectServices.GetAvailableEffectProviders();

        if (providerFactories.TryGetValue(providerTypeName, out var factory))
        {
            var instance = factory();
            if (!ClipInfoBuilder.CanSelectEffectProvider(instance, _clip.SupportsNativeEffects ? _clip.GetEffectSelectionTarget(_pipeline) : _clip.GetEffectSelectionTarget(), hideKeyFramedProviders: true))
            {
                Log($"Rejected effect provider {providerTypeName} for clip {_clip.Id} ({_clip.ClipType}).", "warning");
                return;
            }
            instance.Id = Guid.NewGuid();
            instance.DisconnectMainInput();
            instance.SetFinalOutputSource(false);
            _clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();
            _clip.EffectProviders[instance.Id] = instance;
            EffectBindingHelper.AutoConnectProviderToOutput(_clip.EffectProviders, instance, _clip.GetEffectTarget());

            LoadClip(_clip, _page, _showIsNotVisibleInEffectEditorEffect, _pipeline);
            OnBindingConfigurationChanged();
        }

        SetStatusText(Localized._Done);
    }

    private async void GeneratePreviewButton_Clicked(object sender, EventArgs e)
    {
        GeneratePreviewButton.IsEnabled = false;
        var busy = new ActivityIndicator { IsRunning = true };
        BottomCommandBar.Children.Insert(0, busy);
        try { await GeneratePreviews(); }
        finally
        {
            GeneratePreviewButton.IsEnabled = true;
            BottomCommandBar.Children.Remove(busy);
        }

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
    public record UIBinding(UIBindingKind Kind, Guid Source, Guid Target, string? TargetPortKey = null, string? SourcePortKey = null);

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
    /// Returns diagnostics owned by an effect node. Diagnostics without a provider id describe
    /// graph-wide output state and are therefore attached to the output system node.
    /// </summary>
    private IReadOnlyList<EffectBindingHelper.BindingDiagnostic> GetBindingDiagnostics(
        NodeViewModel node,
        IReadOnlyList<EffectBindingHelper.BindingDiagnostic>? allDiagnostics = null)
    {
        if (_clip?.EffectProviders is not { } providers) return [];
        var diagnostics = allDiagnostics ?? EffectBindingHelper.ValidateBindings(providers);
        return node.Kind switch
        {
            NodeKind.Effect or NodeKind.ClipArguments => diagnostics.Where(d => d.ProviderId == node.Id).ToList(),
            NodeKind.Output => diagnostics.Where(d => d.ProviderId is null).ToList(),
            _ => []
        };
    }

    private static string GetBindingDiagnosticSignature(IEnumerable<EffectBindingHelper.BindingDiagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d => $"{d.ProviderId}|{d.Code}|{d.Message}"));

    /// <summary>Recreates node visuals so newly added or resolved binding diagnostics are visible immediately.</summary>
    private void RefreshBindingDiagnosticIndicators(
        IReadOnlyList<EffectBindingHelper.BindingDiagnostic>? diagnostics = null)
    {
        if (_clip?.EffectProviders is not { } providers) return;
        diagnostics ??= EffectBindingHelper.ValidateBindings(providers);
        foreach (var node in _nodes.Values.ToList())
        {
            var signature = GetBindingDiagnosticSignature(GetBindingDiagnostics(node, diagnostics));
            if (!string.Equals(signature, node.BindingDiagnosticSignature, StringComparison.Ordinal))
                RecreateNodeView(node);
        }
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
            if (EffectProviderOutputExtensions.TryParseOutputSourceId(input, out var inputId, out var outputId)
                && inputId != IEffectProvider.NoConnectionGUID
                && GetNodeByBindingId(inputId) is not null)
                bindings.Add(new UIBinding(UIBindingKind.Picture, inputId, provider.Id, SourcePortKey: outputId));

            if (provider.IsFinalOutputSource())
                bindings.Add(new UIBinding(UIBindingKind.Picture, provider.Id, IEffectProvider.OutputAnchorGUID, SourcePortKey: provider.GetFinalOutputFieldId()));

            foreach (var fieldBinding in provider.EnumerateFieldBindings())
            {
                if (EffectProviderOutputExtensions.TryParseOutputSourceId(fieldBinding.Value, out var sourceId, out var fieldOutputId) && GetNodeByBindingId(sourceId) is not null)
                    bindings.Add(new UIBinding(UIBindingKind.Value, sourceId, provider.Id, fieldBinding.Key, fieldOutputId));
            }
        }

        if (!providers.Values.Any(p => p.TypeOfEffect.GetPipeline() == _pipeline && p.IsFinalOutputSource()))
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
        _previewCts?.Cancel();
        if (_clip?.EffectProviders == null) return;
        var providers = _clip.EffectProviders;
        EffectBindingHelper.MaterializeFields(providers.Values);
        var diagnostics = EffectBindingHelper.ValidateBindings(providers);
        if (diagnostics.Count > 0)
            SetStatusText(string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)));
        RefreshBindingDiagnosticIndicators(diagnostics);
        ScheduleProviderRebuild();
    }

    /// <summary>
    /// 防抖并串行化完整重建。Provider 构建在工作线程进行，只在完成后
    /// 回到 UI 线程替换 Effects 并通知预览层，避免松开鼠标后整个界面卡住。
    /// </summary>
    private async void ScheduleProviderRebuild()
    {
        if (_clip == null) return;

        _providerRebuildCts?.Cancel();
        _providerRebuildCts?.Dispose();
        var cts = _providerRebuildCts = new CancellationTokenSource();
        var token = cts.Token;

        try
        {
            await Task.Delay(150, token);
            await _providerRebuildGate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                var clip = _clip;
                if (clip == null) return;

                var rebuiltEffects = await Task.Run(
                    () => EffectBindingHelper.RebuildAllEffects(clip.EffectProviders, clip.Effects),
                    token);
                token.ThrowIfCancellationRequested();

                if (ReferenceEquals(_clip, clip) && rebuiltEffects != null)
                {
                    clip.Effects = rebuiltEffects;
                    NotifyEffectProvidersChanged();
                }
            }
            finally
            {
                _providerRebuildGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer graph edit superseded this rebuild.
        }
        catch (InvalidOperationException ex)
        {
            SetStatusText(ex.Message);
            LogDiagnostic($"Effect binding graph is not renderable yet: {ex.Message}");
        }
        catch (Exception ex)
        {
            SetStatusText(ex.Message);
            LogDiagnostic($"Failed to rebuild effect binding graph: {ex}");
        }
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
            int sourceIndex = b.SourcePortKey is null ? 0 : from.OutputPorts.FindIndex(p => p.Key == b.SourcePortKey);
            if (sourceIndex < 0 || sourceIndex >= from.OutputPorts.Count) continue;

            if (b.Kind == UIBindingKind.Value)
            {
                var paramIndex = to.ParamPorts.FindIndex(p => p.Key == b.TargetPortKey);
                if (paramIndex < 0) continue;
                var fieldType = to.ParamPorts[paramIndex].FieldType;
                _drawable.Connections.Add(new NodeConnection
                {
                    From = from,
                    FromPortIndex = sourceIndex,
                    To = to,
                    ToPortIndex = paramIndex,
                    IsValueBinding = true,
                    FieldType = fieldType,
                    TargetParamFieldId = b.TargetPortKey
                });
            }
            else
            {
                var srcType = from.OutputPorts[sourceIndex].FieldType;
                _drawable.Connections.Add(new NodeConnection
                {
                    From = from,
                    FromPortIndex = sourceIndex,
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
        public int FromPortIndex;
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

                var start = Transform(GetPortPosition(conn.From, PortKind.AnchorOutput, conn.FromPortIndex));
                var end = Transform(GetPortPosition(conn.To,
                    conn.IsValueBinding ? PortKind.ParamBind : PortKind.AnchorInput, conn.ToPortIndex));

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
                canvas.StrokeColor = PortTypeHelper.GetTypeColor(DragFieldType);
                DrawCurve(canvas, Transform(GetPortPosition(DragSourceNode, DragSourceKind, DragSourcePortIndex)), DragPoint.Value);
            }

            canvas.RestoreState();
        }

        private Point Transform(Point point)
        {
            return new Point(point.X * Scale + PanX, point.Y * Scale + PanY);
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
