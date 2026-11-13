using projectFrameCut.ApplicationAPIBase.Interaction;
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
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Contracts;
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

public enum NodeKind { Effect, Input, Output, ClipArguments, Group, AudioSample }

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
    public EffectArgumentFieldType DeclaredFieldType;
    public EffectPortTypeContract? TypeContract;
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

    private EffectStage _stage = EffectStage.Render;
    private EffectStage[] _availableStages = [];
    private bool _loadingStage;
    private ClipElementUI? _clip;
    private EffectFieldTypeResolution? _fieldTypes;
    private IReadOnlyList<EffectBindingHelper.BindingDiagnostic> _bindingDiagnostics = [];
    private int _fieldTypeVersion;
    private DraftPage? _page;
    private (ClipElementUI Clip, DraftPage Page, EffectTarget Target, EffectStage Stage, CompositionPipeline Pipeline)? _addEffectsContext;
    private Dictionary<Guid, NodeViewModel> _nodes = new();
    private AbsoluteLayout NodesContainer => Graph.NodesContainer;
    private GraphicsView ConnectionsLayer => Graph.ConnectionsLayer;
    private NodeViewModel? _selectedNode;

    private NodeViewModel? _inputNode;
    private static readonly Guid PositionNodeId = new("f151dd63-6821-4f7b-a71a-9bd8a035e762");
    private static readonly Guid AudioSampleNodeId = new("f151dd63-6821-4f7b-a71a-9bd8a035e761");
    private NodeViewModel? _outputNode;

    private bool _isDraggingNodeOrPort => Graph.IsInteracting;
    private const double PanelMinWidth = 230;
    private const double PanelMaxWidth = 800;
    private double _panelStartWidth;
    private double _panelWidthBeforeCollapse = 300;

    public bool PanelCollapsed { get; private set; }

    private bool _showIsNotVisibleInEffectEditorEffect;
    private bool _subscribedToPageEvents;

    private CancellationTokenSource? _fieldTypesCts;
    private CancellationTokenSource? _previewCts;
    private Task _providerAddTask = Task.CompletedTask;
    private bool _closing;

    /// <summary>Raised after pending changes are committed when the editor closes.</summary>
    public event Action? EffectProvidersChanged;

    private const double NodeDefaultWidth = 150;
    private const double NodeDefaultHeight = 80;
    private const double NodeSpacing = 20;
    private const int NodePlacementMaxAttempts = 200;

    // ── Node geometry (shared by view, drag and drawable) ────────────────
    private const double FrameHeight = 80;
    private const double PortSize = 15;
    private const double ParamRowHeight = 24;

    public DraftEffectBindingView()
    {
        BindingContext = this;
        InitializeComponent();
        InitializeGraph();
        InitializeGroupGraph();

        ZoomInButton.Clicked += OnZoomIn;
        ZoomOutButton.Clicked += OnZoomOut;
        ResetButton.Clicked += OnReset;
        InitializeGroups();
        Loaded += (_, _) =>
        {
            if (_closing) return;
            SubscribeToPageEvents();
            RefreshFieldTypes();
        };

#if WINDOWS
        AddEffectsScrollView.HandlerChanged += (_, _) =>
        {
            if (AddEffectsScrollView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ScrollViewer sv)
                sv.BringIntoViewOnFocusChange = false;
        };
#endif

        _panelWidthBeforeCollapse = RightPanelColumn.Width.Value;
        UpdatePanelToggleText();

    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler == null)
        {
            EndPortConnection();
            _fieldTypesCts?.Cancel();
            _previewCts?.Cancel();
            UnsubscribeFromPageEvents();
        }
    }

    private void OnZoomIn(object? sender, EventArgs e) => Graph.Zoom(Graph.Viewport.Scale * 1.2);

    private void OnZoomOut(object? sender, EventArgs e) => Graph.Zoom(Graph.Viewport.Scale / 1.2);

    private void OnReset(object? sender, EventArgs e) => Graph.SetViewport(new GraphViewport(1), true);

    private void UpdateDrawableScale()
    {
        StaticInputTooltip.IsVisible = false;
        Graph.Invalidate();
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
        {
            TogglePanelButton.Text = PanelCollapsed ? "<" : ">";
            ToolTipProperties.SetText(TogglePanelButton, PanelCollapsed ? Localized.Effect_ShowSidePanel : Localized.Effect_HideSidePanel);
        }
    }

    public void LoadClip(ClipElementUI clip, DraftPage? page = null, bool showIsNotVisibleInEffectEditorEffect = false)
        => LoadStage(clip, page, showIsNotVisibleInEffectEditorEffect);

    private void OnStageChanged(object? sender, EventArgs e)
    {
        if (_loadingStage || _clip is null) return;
        if (StagePicker.SelectedIndex < 0 || StagePicker.SelectedIndex >= _availableStages.Length) return;
        SaveStageLayout();
        LoadStage(_clip, _page, _showIsNotVisibleInEffectEditorEffect, _availableStages[StagePicker.SelectedIndex]);
    }

    private static EffectStage[] GetAvailableStages(ClipElementUI clip) => clip.SupportedStages
        .Where(s => s != EffectStage.SourceReplacement || clip.EffectProviders?.Values
            .Any(p => p.Stage == s && p.TypeOfEffect == EffectType.SourceReplacement) == true)
        .Distinct().ToArray();

    public void LoadStage(ClipElementUI clip, DraftPage? page, bool showIsNotVisibleInEffectEditorEffect, EffectStage? stage = null)
    {
        Graph.CancelInteraction();
        CloseMermaidImport();
        CloseGroupGraph();
        EffectStageUI.Resolve(clip, page);
        StopStepDebug();
        _previewCts?.Cancel();
        _fieldTypesCts?.Cancel();
        if (!ReferenceEquals(_page, page)) UnsubscribeFromPageEvents();
        if (stage is { } selectedStage) _stage = selectedStage;
        else if (Enum.TryParse<EffectStage>(clip.ExtraData.GetValueOrDefault("__EffectStageTab__")?.ToString(), out var savedStage)) _stage = savedStage;
        _availableStages = GetAvailableStages(clip);
        if (!_availableStages.Contains(_stage)) _stage = _availableStages.FirstOrDefault(EffectStage.Render);
        clip.ExtraData["__EffectStageTab__"] = _stage.ToString();
        _loadingStage = true;
        StagePicker.IsVisible = _availableStages.Length > 1;
        StagePicker.ItemsSource = _availableStages.Select(EffectStageUI.Name).ToArray();
        StagePicker.SelectedIndex = Array.IndexOf(_availableStages, _stage);
        LoadCompositionPipeline(clip);
        _loadingStage = false;
        _clip = clip;
        _fieldTypes = null;
        _bindingDiagnostics = [];
        _previewDiagnostics = [];
        _fieldTypeVersion++;
        _page = page;
        _showIsNotVisibleInEffectEditorEffect = showIsNotVisibleInEffectEditorEffect;
        EnsureClipArgumentProvider();
        if (_stage == EffectStage.Composition && _clip.ClipType != ClipMode.AudioClip)
        {
            _clip.EffectProviders ??= new();
            MixtureBindings.EnsureSources(_clip.EffectProviders);
        }
        NormalizeBindingConfiguration();
        RefreshCompositionProviders();
        UpdateAddEffectsPanel();
        UpdateClipValuesPanel();
        EndPortConnection();
        _nodes.Clear();
        _inputNode = null;
        _outputNode = null;
        _selectedNode = null;
        Graph.Clear();
        PropertiesPanel.Children.Clear();

        Graph.SetViewport(new GraphViewport());

        if (_clip?.EffectProviders != null)
        {
            foreach (var bundle in _clip.EffectProviders.Values)
            {
                if (!IsCurrentPipelineProvider(bundle)) continue;
                // Skip bundles that are internal/special effects (e.g. Crop, Place, Resize)
                // to keep consistent with ClipInfoBuilder.BuildEffectTab filtering behavior.
                if (bundle is not ClipArgumentProvider && !showIsNotVisibleInEffectEditorEffect && (bundle.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor)
                    || !bundle.Target.HasFlag(EffectTarget.Pipeline) && !bundle.Target.HasFlag(EffectTarget.ValueProvider) && !EffectBindingHelper.AreTargetsCompatible(bundle.Target, _clip.GetEffectSelectionTarget(_stage))))
                    continue;

                if (bundle is not (ClipArgumentProvider or RenderOutputProvider)
                    && (bundle is not MixtureSourceProvider || IsSourceOutputEnabled("content"))) AddProviderNode(bundle);
            }
        }

        AddSystemNodes();

        SubscribeToPageEvents();

        LoadGroups();
        ApplyGroupProjection();
        RefreshFieldTypes();
        RefreshBindingDiagnosticIndicators();
        RebuildConnections();
        ApplySavedViewTransform();
        UpdateSourcesAndOutputsPanel();
        ConnectionsLayer.Invalidate();
        InitializeEditHistory();
    }

    private void EnsureClipArgumentProvider()
    {
        if (_clip is null) return;
        _clip.EffectProviders ??= new();
        RenderOutputProvider.Ensure(_clip.EffectProviders);
        if (_clip.ClipType is ClipMode.TextClip or ClipMode.SubtitleClip) ClipInfoBuilder.EnsureClipArgumentProvider(_clip, _page);
    }

    /// <summary>
    /// Reloads all effect data from the current clip.
    /// Call this when external code (e.g. ClipInfoBuilder) has modified effect bundlesOnPortPan
    /// and the binding view needs to reflect the latest state.
    /// </summary>
    public void Reload()
    {
        if (_clip == null) return;
        CloseGroupGraph();
        if (!_availableStages.SequenceEqual(GetAvailableStages(_clip)))
        {
            SaveStageLayout();
            LoadStage(_clip, _page, _showIsNotVisibleInEffectEditorEffect, _stage);
            return;
        }
        Log($"Reload effect binding view for {_clip.Id}, stage {_stage}, effect selector scroll {AddEffectsScrollView.ScrollY}.", "debug");
        NormalizeBindingConfiguration();
        LoadGroups(resetSelection: false);
        UpdateProviderNodes();
        ApplyGroupProjection();
        UpdateClipValuesPanel();
        RefreshFieldTypes();
        RebuildConnections();
        RefreshSelectedNode();
        ConnectionsLayer.Invalidate();
    }

    private void AddProviderNode(IEffectProvider provider)
    {
        if (provider is ClipArgumentProvider) { AddStageTerminals(); return; }
        var node = new NodeViewModel
        {
            Id = provider.Id,
            Kind = provider is MixtureSourceProvider ? NodeKind.Input : NodeKind.Effect,
            Provider = provider,
            DisplayName = ProviderNodeName(provider),
        };
        node.BuildPortsFromProvider();
        if (provider is MixtureSourceProvider { IsUnder: false }) _inputNode = node;
        if (provider.MetaData.TryGetValue(ProviderPositionKey("X"), out var x)
            && provider.MetaData.TryGetValue(ProviderPositionKey("Y"), out var y)
            && double.TryParse(x?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px)
            && double.TryParse(y?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var py))
        {
            node.X = px;
            node.Y = py;
            AddNode(node);
            return;
        }
        var position = provider is MixtureSourceProvider source
            ? FindNonOverlappingPosition(node, 50, source.IsUnder ? 350 : 150)
            : FindNonOverlappingPosition(node, 250 + _nodes.Count * 50, 150 + _nodes.Count * 20);
        node.X = position.X;
        node.Y = position.Y;
        provider.MetaData[ProviderPositionKey("X")] = node.X;
        provider.MetaData[ProviderPositionKey("Y")] = node.Y;
        AddNode(node);
    }

    private void UpdateProviderNodes()
    {
        if (_clip?.EffectProviders is not { } providers) return;
        RefreshCompositionProviders();
        var visible = providers.Values.Where(p => IsCurrentPipelineProvider(p)
            && (_showIsNotVisibleInEffectEditorEffect || !p.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor)
                && (p.Target.HasFlag(EffectTarget.Pipeline) || p.Target.HasFlag(EffectTarget.ValueProvider) || EffectBindingHelper.AreTargetsCompatible(p.Target, _clip.GetEffectSelectionTarget(_stage)))))
            .ToDictionary(p => p.Id);
        foreach (var node in _nodes.Values.Where(n => n.Provider is not null && n.TerminalField is null
            && (!visible.ContainsKey(n.Provider!.Id) || n.Provider is MixtureSourceProvider && !IsSourceOutputEnabled("content"))).ToArray())
        {
            Graph.RemoveNode(node.Id);
            _nodes.Remove(node.Id);
            if (_inputNode == node) _inputNode = null;
            if (_outputNode == node) _outputNode = null;
        }
        foreach (var provider in visible.Values)
        {
            if (provider is ClipArgumentProvider or RenderOutputProvider
                || provider is MixtureSourceProvider && !IsSourceOutputEnabled("content")) continue;
            if (!_nodes.TryGetValue(provider.Id, out var node))
            {
                AddProviderNode(provider);
                continue;
            }
            node.Provider = provider;
            node.DisplayName = ProviderNodeName(provider);
            node.FieldTypes = _fieldTypes;
            node.BuildPortsFromProvider();
            if (node.VisualSignature != GetNodeVisualSignature(node)) RecreateNodeView(node);
        }
        AddSystemNodes();
        if (_selectedNode is not null && !_nodes.ContainsKey(_selectedNode.Id))
        {
            _selectedNode = null;
            UpdateSourcesAndOutputsPanel();
        }
    }

    private void AddNode(NodeViewModel node)
    {
        PrepareGraphNode(node);
        _nodes.Add(node.Id, node);
        Graph.AddNode(node);
        node.VisualSignature = GetNodeVisualSignature(node);
    }

    /// <summary>
    /// Rebuild the view of a single node (used after param direction toggle or a right-panel edit).
    /// </summary>
    private void RecreateNodeView(NodeViewModel node)
    {
        StaticInputTooltip.IsVisible = false;
        PrepareGraphNode(node);
        Graph.RefreshNode(node.Id);
        node.VisualSignature = GetNodeVisualSignature(node);
        ConnectionsLayer.Invalidate();
        if (_stepDebugEnabled && _stepIndex >= 0 && _stepIndex < _steps.Count)
        {
            try { ApplyStepHighlight(_steps[_stepIndex]); }
            catch (Exception ex) { Log(ex, "Reapply single-step highlight", this); }
        }
    }

    private string GetNodeVisualSignature(NodeViewModel node)
    {
        var signature = new StringBuilder($"{node.DisplayName}|{GetParamDirection(node)}|{GetPortTypeSignature(node)}|{node.BindingDiagnosticSignature}|{node.Group?.Collapsed}");
        IEnumerable<IEffectProvider> providers = node.Provider is { } provider ? new[] { provider }
            : node.Group?.Ports.Select(p => _clip!.EffectProviders![p.ProviderId]).Distinct() ?? [];
        foreach (var p in providers)
        {
            foreach (var binding in p.AnchorsBindingState) signature.Append($"|{binding.Key}:{binding.Value}");
            foreach (var (key, field) in p.Fields)
            {
                object? value = field switch
                {
                    StaticEffectArgumentField f => f.Value,
                    DynamicEffectParamField f => f.StaticFallbackValue,
                    _ => field.DefaultValue,
                };
                signature.Append($"|{key}:{value}");
            }
        }
        return signature.ToString();
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
        var members = node.Group is { } group ? EffectGroupHelper.Members(group, _groups) : null;
        foreach (var other in _nodes.Values)
        {
            if (other == node || other.View?.IsVisible == false) continue;
            if (members is not null && (members.Contains(other.Id)
                || other.Group is not null && ContainsGroup(node.Group!, other.Id))) continue;
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

    private static string HumanizePortName(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return key ?? string.Empty;
        return key switch
        {
            EffectProviderAnchorExtensions.InputKey => "Input",
            EffectProviderAnchorExtensions.OutputKey => "Output",
            RenderOutputProvider.AudioKey => Localized.Effect_RenderFinalAudio,
            RenderOutputProvider.PositionKey => Localized.Effect_RenderFinalPosition,
            RenderOutputProvider.ShouldRenderKey => Localized.Effect_RenderShouldRender,
            MixtureProviderBase.UpperInputKey => Localized.Effect_MixtureUpper,
            MixtureProviderBase.LowerInputKey => Localized.Effect_MixtureLower,
            EffectVariableReadProvider.InitialValueKey => Localized.Effect_VariableInitialValue,
            nameof(IClip.TargetX) => "X",
            nameof(IClip.TargetY) => "Y",
            nameof(IClip.TargetWidth) => Localized.Effect_ClipWidth,
            nameof(IClip.TargetHeight) => Localized.Effect_ClipHeight,
            _ => EffectProviderAnchorExtensions.IsInternalInputKey(key) ? key[2..^2] : key,
        };
    }

    private static bool IsNodeInput(IEffectProvider provider, string key) =>
        provider is not ClipArgumentProvider && provider.InFields.ContainsKey(key);

    private static string ProviderNodeName(IEffectProvider provider)
    {
        if (provider is MixtureSourceProvider source)
            return source.IsUnder ? Localized.Effect_UnderClip : Localized.Effect_CurrentClip;
        if (provider is EffectVariableProvider)
            return $"{provider.Name} ({(provider is EffectVariableReadProvider ? Localized.Effect_VariableRead : Localized.Effect_VariableWrite)})";
        return string.IsNullOrWhiteSpace(provider.Name) || provider.Name.Replace(" ", "") == provider.TypeName
            || provider.Name is "New Effect" or "Int Arithmetic" or "Classic Speed"
            ? EffectServices.GetUIProvider(provider).GetLocalizedEffectName(provider, PluginManager.CurrentLocale) : provider.Name;
    }

    private string PortTooltip(NodeViewModel node, NodePort port)
    {
        var title = $"{port.DisplayName} ({PortTypeHelper.HumanizeTypeName(port.FieldType, port.TypeContract)})";
        if (port.TypeContract is { } contract)
            title += $"\n{contract.Id}" + (string.IsNullOrWhiteSpace(contract.Description) ? "" : $"\n{contract.Description}");
        var provider = node.Group is null ? node.Provider : _clip?.EffectProviders?.GetValueOrDefault(port.Id);
        if (provider is null) return title;
        var description = EffectProviderUIHelper.FieldInfo(provider, port.Key).description;
        return string.IsNullOrWhiteSpace(description) ? title : $"{title}\n{description}";
    }

    private View CreateNodeView(NodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.InputPortViews.Clear();
        node.OutputPortViews.Clear();
        node.ParamPortViews.Clear();
        var bindingDiagnostics = GetBindingDiagnostics(node);
        node.BindingDiagnosticSignature = GetBindingDiagnosticSignature(bindingDiagnostics);
        var container = new Grid
        {
            ColumnSpacing = 0,
            RowSpacing = 0,
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            },
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
        node.Frame = frame;
        node.BorderColor = borderColor;

        var title = node.Kind switch
        {
            NodeKind.Input => node.DisplayName,
            NodeKind.Output => node.DisplayName,
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

        var description = node.Provider is { } nodeProvider
            ? EffectServices.GetUIProvider(nodeProvider).GetLocalizedEffectDescription(nodeProvider, PluginManager.CurrentLocale) : "";
        ToolTipProperties.SetText(label, string.IsNullOrWhiteSpace(description) ? title : $"{title}\n{description}");
        if (bindingDiagnostics.Count > 0)
        {
            ToolTipProperties.SetText(frame,
                string.Join(Environment.NewLine, bindingDiagnostics.Select(FormatBindingDiagnostic)));
        }
        else if (!string.IsNullOrWhiteSpace(description)) ToolTipProperties.SetText(frame, description);

        // ── Parameter display strip (above or below the frame, read-only) ──
        VerticalStackLayout? paramStack = null;
        bool paramsOnTop = true;
        if ((node.Provider is not null || node.Group is not null) && node.ParamPorts.Count > 0)
        {
            paramsOnTop = GetParamDirection(node);
            paramStack = BuildParamStack(node);
        }
        // Only a top strip shifts the frame down; a bottom strip sits below it.
        node.ParamsTopHeight = paramStack is not null && paramsOnTop ? node.ParamPorts.Count * ParamRowHeight : 0;

        if (paramStack is not null && paramsOnTop) container.Add(paramStack, 1, 0);

        // Ports
        var inputPorts = new VerticalStackLayout { Spacing = 0, HeightRequest = node.BodyHeight - 10,
            MinimumWidthRequest = PortSize };
        var staticValues = new VerticalStackLayout
        {
            Spacing = 0,
            HeightRequest = node.BodyHeight - 10
        };
        bool hasStaticValues = false;
        for (int i = 0; i < node.InputPorts.Count; i++)
        {
            var port = node.InputPorts[i];
            var value = GetInputStaticDisplayValue(node, port);
            var row = new Grid { ColumnSpacing = 4, HeightRequest = (node.BodyHeight - 10) / node.InputPorts.Count,
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Star },
                } };
            var box = new BoxView { Color = value is null ? PortTypeHelper.GetTypeColor(port.FieldType) : Colors.Gray,
                WidthRequest = PortSize, HeightRequest = PortSize, VerticalOptions = LayoutOptions.Center };
            int index = i;
            Graph.AttachPortSurface(GraphPort(node, PortKind.AnchorInput, index), box);
            ToolTipProperties.SetText(box, PortTooltip(node, port));

            row.Add(box);
            if ((node.TerminalField is null || node.Provider is not RenderOutputProvider)
                && (node.Provider is not null || node.Group is not null || node.InputPorts.Count > 1))
            {
                var nameLabel = new Label { Text = port.DisplayName, FontSize = 10, TextColor = Colors.LightGray,
                    WidthRequest = 80, HorizontalOptions = LayoutOptions.Start,
                    VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
                ToolTipProperties.SetText(nameLabel, PortTooltip(node, port));
                var pointer = new PointerGestureRecognizer();
                pointer.PointerEntered += (_, _) =>
                {
                    if (_closing || _isDraggingNodeOrPort) return;
                    var provider = _clip?.EffectProviders?.GetValueOrDefault(PortProviderId(node, port));
                    if (!TryGetPortBindingSource(provider, port.Key, out var sourceId)) return;
                    nameLabel.Text = $"← {GetBoundSourceDisplayName(provider, sourceId)}";
                    nameLabel.TextColor = Color.FromArgb("#f2c94c");
                    ToolTipProperties.SetText(nameLabel, $"{nameLabel.Text}\n{EffectProviderHelper.L("Effect_Port_Navigate", "Double-click to go to the source.")}\n{PortTooltip(node, port)}");
                };
                pointer.PointerExited += (_, _) =>
                {
                    nameLabel.Text = port.DisplayName;
                    nameLabel.TextColor = Colors.LightGray;
                    ToolTipProperties.SetText(nameLabel, PortTooltip(node, port));
                };
                nameLabel.GestureRecognizers.Add(pointer);
                var tap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
                tap.Tapped += (_, _) => NavigateToBoundSource(node, port);
                nameLabel.GestureRecognizers.Add(tap);
                row.Add(nameLabel, 1, 0);
            }
            var valueRow = new Grid { HeightRequest = row.HeightRequest };
            if (value is not null)
            {
                hasStaticValues = true;
                valueRow.ColumnSpacing = 4;
                valueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                valueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                valueRow.HorizontalOptions = LayoutOptions.End;
                var valueLabel = new Label
                {
                    Text = value,
                    TextColor = Color.FromArgb("#f2c94c"),
                    FontSize = 10,
                    MaximumWidthRequest = 80,
                    VerticalOptions = LayoutOptions.Center,
                    HorizontalOptions = LayoutOptions.End,
                    LineBreakMode = LineBreakMode.TailTruncation
                };
                valueRow.Add(valueLabel);
                valueRow.Add(new Label
                {
                    Text = "→",
                    TextColor = Color.FromArgb("#f2c94c"),
                    FontSize = 10,
                    VerticalOptions = LayoutOptions.Center
                }, 1, 0);
                var pointer = new PointerGestureRecognizer();
                pointer.PointerEntered += (_, _) => ShowStaticInputTooltip(valueLabel, value);
                pointer.PointerExited += (_, _) => StaticInputTooltip.IsVisible = false;
                valueRow.GestureRecognizers.Add(pointer);
                valueRow.Unloaded += (_, _) => StaticInputTooltip.IsVisible = false;
            }
            staticValues.Add(valueRow);
            node.InputPortViews.Add(box);
            inputPorts.Add(row);
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
            {
                var nameLabel = new Label { Text = port.DisplayName, FontSize = 10, TextColor = Colors.LightGray,
                    MaximumWidthRequest = 80, HorizontalOptions = LayoutOptions.End,
                    VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
                ToolTipProperties.SetText(nameLabel, PortTooltip(node, port));
                row.Add(nameLabel);
            }
            var box = new BoxView { Color = PortTypeHelper.GetTypeColor(port.FieldType), WidthRequest = PortSize,
                HeightRequest = PortSize, VerticalOptions = LayoutOptions.Center };
            ToolTipProperties.SetText(box, PortTooltip(node, port));
            int index = i;
            Graph.AttachPortSurface(GraphPort(node, PortKind.AnchorOutput, index), box);
            row.Add(box, 1, 0);
            node.OutputPortViews.Add(box);
            outputPorts.Add(row);
        }

        // Handle visibility for System Nodes
        if (node.Kind is NodeKind.Input or NodeKind.AudioSample) inputPorts.IsVisible = false;
        if (node.Kind == NodeKind.Output) outputPorts.IsVisible = false;

        var bodyActionContainer = new Grid();
        Graph.AttachNodeSurface(node.Id, frame, bodyActionContainer);

        // Layout
        var layout = new Grid { ColumnSpacing = 6, ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Star }, new ColumnDefinition { Width = GridLength.Auto } } };

        bodyActionContainer.Add(label);

        layout.Add(inputPorts, 0, 0);
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

        container.Add(frame, 1, 1);
        if (hasStaticValues)
            container.Add(new Border
            {
                Stroke = Colors.Transparent,
                StrokeThickness = frame.StrokeThickness,
                Padding = new Thickness(0, 5),
                Margin = new Thickness(0, 0, 6, 0),
                HeightRequest = node.BodyHeight,
                Content = new Grid { Children = { staticValues } }
            }, 0, 1);

        if (paramStack is not null && !paramsOnTop) container.Add(paramStack, 1, 2);

        if (bindingDiagnostics.Count > 0)
        {
            var warning = new Button
            {
                Text = $"⚠ {bindingDiagnostics.Count}",
                TextColor = Colors.OrangeRed,
                FontSize = 11,
                HorizontalOptions = LayoutOptions.Start,
                Margin = new Thickness(4, 3, 4, 0),
            };
            warning.Clicked += (_, _) => ShowNodeDiagnostics(node);
            ToolTipProperties.SetText(warning, string.Join(Environment.NewLine, bindingDiagnostics.Select(FormatBindingDiagnostic)));
            container.Add(warning, 1, 3);
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
            WidthRequest = NodeDefaultWidth,
            HeightRequest = 120,
            HorizontalOptions = LayoutOptions.Center,
            IsVisible = false // Hidden initially
        };
        frame.SizeChanged += (_, _) => previewBorder.WidthRequest = frame.Width;

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
        node.PreviewValueChanged = value =>
        {
            previewValue.Text = value;
            previewImage.IsVisible = string.IsNullOrEmpty(value);
            previewValue.IsVisible = !string.IsNullOrEmpty(value);
            arrow.IsVisible = previewBorder.IsVisible = !string.IsNullOrEmpty(value) || node.PreviewImage is not null;
        };
        previewValue.Text = node.PreviewValue;
        previewValue.IsVisible = !string.IsNullOrEmpty(node.PreviewValue);
        previewImage.IsVisible = !previewValue.IsVisible;

        container.Add(arrow, 1, 4);
        container.Add(previewBorder, 1, 5);

        // Property to update preview
        node.PreviewImageChanged = img =>
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
    private VerticalStackLayout BuildParamStack(NodeViewModel node)
    {
        if (node.ParamPorts.Count == 0) return null!;
        var stack = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(4, 2), MinimumWidthRequest = NodeDefaultWidth };

        for (int i = 0; i < node.ParamPorts.Count; i++)
        {
            var p = node.ParamPorts[i];
            var provider = node.Group is null ? node.Provider! : _clip!.EffectProviders![p.Id];
            var field = provider.Fields.TryGetValue(p.Key, out var f) ? f : null;
            bool isBound = provider.TryGetFieldBinding(p.Key, out var boundSourceId);

            var row = new HorizontalStackLayout { Spacing = 4, HeightRequest = ParamRowHeight, VerticalOptions = LayoutOptions.Center };

            var dot = new BoxView { Color = PortTypeHelper.GetTypeColor(p.FieldType), WidthRequest = 9, HeightRequest = 9,
                VerticalOptions = LayoutOptions.Center, IsVisible = isBound || !p.DeclaredFieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic) };
            int rowIndex = i;
            Graph.AttachPortSurface(GraphPort(node, PortKind.ParamBind, rowIndex), dot);
            node.ParamPortViews.Add(dot);
            ToolTipProperties.SetText(dot, isBound
                ? $"{PortTooltip(node, p)}\n← {GetBoundSourceDisplayName(provider, boundSourceId)}" : PortTooltip(node, p));
            row.Add(dot);

            var nameLabel = new Label
            {
                Text = EffectProviderUIHelper.FieldName(provider, p.Key, true, p.DisplayName),
                TextColor = Colors.LightGray,
                FontSize = 10,
                WidthRequest = 48,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            ToolTipProperties.SetText(nameLabel, PortTooltip(node, p));
            row.Add(nameLabel);

            var value = node.Group is null ? GetParamDisplayValue(node, p.Key) : isBound ? "←" : field is StaticEffectArgumentField sf ? sf.Value?.ToString() : "-";
            var valueLabel = new Label
            {
                Text = !isBound && field?.PresetOptions is { Length: > 0 }
                    ? EffectProviderUIHelper.PresetOptionName($"Effect_{provider.TypeName}_{p.Key.Replace('.', '_')}_", value) : value,
                TextColor = isBound ? Color.FromArgb("#f2c94c") : Colors.White,
                FontSize = 10,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
                HorizontalOptions = LayoutOptions.End
            };
            if (isBound)
            {
                var tap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
                tap.Tapped += (_, _) => NavigateToBoundSource(node, p);
                valueLabel.GestureRecognizers.Add(tap);
            }
            row.Add(valueLabel);

            stack.Add(row);
        }
        return stack;
    }

    private void ShowStaticInputTooltip(View anchor, string value)
    {
        if (_closing || _isDraggingNodeOrPort || anchor.Width <= 0 || anchor.Height <= 0
            || ConnectionsLayer.Width <= 0 || ConnectionsLayer.Height <= 0) return;
        double x = 0, y = 0;
        View? v = anchor;
        for (; v is not null && !ReferenceEquals(v, NodesContainer); v = v.Parent as View)
        {
            x += v.X + v.TranslationX;
            y += v.Y + v.TranslationY;
        }
        if (v is null) return;
        x = NodesContainer.X + NodesContainer.TranslationX + x * NodesContainer.Scale;
        y = NodesContainer.Y + NodesContainer.TranslationY + y * NodesContainer.Scale;
        StaticInputTooltipText.Text = value;
        StaticInputTooltip.MaximumWidthRequest = Math.Min(320, ConnectionsLayer.Width);
        StaticInputTooltip.IsVisible = true;
        var size = ((IView)StaticInputTooltip).Measure(StaticInputTooltip.MaximumWidthRequest, ConnectionsLayer.Height);
        double below = y + anchor.Height * NodesContainer.Scale + 4;
        StaticInputTooltip.TranslationX = Math.Clamp(x, 0, Math.Max(0, ConnectionsLayer.Width - size.Width));
        StaticInputTooltip.TranslationY = Math.Clamp(below + size.Height <= ConnectionsLayer.Height ? below : y - size.Height - 4,
            0, Math.Max(0, ConnectionsLayer.Height - size.Height));
    }

    private string? GetInputStaticDisplayValue(NodeViewModel node, NodePort port)
    {
        var provider = node.Group is null ? node.Provider : _clip?.EffectProviders?.GetValueOrDefault(port.Id);
        if (provider is null || TryGetPortBindingSource(provider, port.Key, out _)
            || provider.Fields.GetValueOrDefault(port.Key) is not StaticEffectArgumentField field) return null;
        var descriptor = (IEffectArgumentField?)provider.InFields.GetValueOrDefault(port.Key) ?? field;
        var value = EffectParamConvert.Normalize(field.Value);
        var defaultValue = EffectFieldTypes.DefaultValue(descriptor);
        value = descriptor.FieldType.BaseType() switch
        {
            EffectArgumentFieldType.Boolean when EffectParamConvert.TryConvertToBool(value, out var b) => b,
            EffectArgumentFieldType.Integer when EffectParamConvert.TryConvertToInt(value, out var i) => i,
            EffectArgumentFieldType.UnsignedInteger when uint.TryParse(value?.ToString(), out var u) => u,
            EffectArgumentFieldType.Long when long.TryParse(value?.ToString(), out var l) => l,
            EffectArgumentFieldType.UnsignedLong when ulong.TryParse(value?.ToString(), out var ul) => ul,
            EffectArgumentFieldType.Numeric when EffectParamConvert.TryConvertToFloat(value, out var f) => f,
            _ => value,
        };
        if (Equals(value, defaultValue)
            || string.Equals(value?.ToString() ?? "", defaultValue?.ToString() ?? "", StringComparison.Ordinal)) return null;
        var text = value?.ToString() ?? "-";
        if (text.Length == 0) text = "\"\"";
        return descriptor.PresetOptions is { Length: > 0 }
            ? EffectProviderUIHelper.PresetOptionName($"Effect_{provider.TypeName}_{port.Key.Replace('.', '_')}_", text) : text;
    }

    private static bool TryGetPortBindingSource(IEffectProvider? provider, string key, out string sourceId)
    {
        sourceId = string.Empty;
        if (provider is null) return false;
        if (key != EffectProviderAnchorExtensions.InputKey)
            return provider.TryGetFieldBinding(key, out sourceId);
        sourceId = provider.GetMainInputSource();
        return !string.IsNullOrWhiteSpace(sourceId) && sourceId != IEffectProvider.NoConnectionGUID.ToString();
    }

    private void NavigateToBoundSource(NodeViewModel node, NodePort port)
    {
        if (_closing || _isDraggingNodeOrPort || ConnectionsLayer.Width <= 0 || ConnectionsLayer.Height <= 0) return;
        var provider = _clip?.EffectProviders?.GetValueOrDefault(PortProviderId(node, port));
        if (provider is null || !TryGetPortBindingSource(provider, port.Key, out var sourceId)) return;
        if (!TryParseBindingSource(sourceId, out var id, out _))
        {
            Log($"Binding source {sourceId} for {provider.Id}.{port.Key} has no editor node.", "debug");
            return;
        }
        var source = VisiblePortNode(id);
        if (source?.View?.IsVisible != true)
        {
            Log($"Cannot locate binding source {sourceId} for {provider.Id}.{port.Key} in the editor.", "debug");
            return;
        }

        NodesContainer.TranslationX = ConnectionsLayer.Width / 2 - (source.X + source.Width / 2) * NodesContainer.Scale;
        NodesContainer.TranslationY = ConnectionsLayer.Height / 2 - (source.Y + source.ParamsTopHeight + source.BodyHeight / 2) * NodesContainer.Scale;
        UpdateDrawableScale();
        SaveViewTransform();
        SelectNode(source);
        RightTabView.SelectedIndex = 0;
        Log($"Navigated from {provider.Id}.{port.Key} to binding source {sourceId}, visible node {source.Id}.", "debug");
    }

    private string GetParamDisplayValue(NodeViewModel node, string fieldId)
    {
        if (node.Provider == null || !node.Provider.Fields.TryGetValue(fieldId, out var field)) return "-";
        if (node.Provider.TryGetFieldBinding(fieldId, out var sourceId))
        {
            var srcName = GetBoundSourceDisplayName(node.Provider, sourceId);
            return $"← {srcName}";
        }
        var raw = field is StaticEffectArgumentField sf ? sf.Value : field.GetGetter()?.Invoke();
        return raw?.ToString() ?? "-";
    }

    private string GetBoundSourceDisplayName(IEffectProvider? provider, string sourceId)
    {
        if (_clip == null || provider is null || string.IsNullOrEmpty(sourceId)) return sourceId;
        try
        {
            var host = new ClipBindingHost(_clip, provider, _page);
            return host.GetSourceDisplayName(sourceId) ?? sourceId;
        }
        catch
        {
            return sourceId;
        }
    }

    private void SetNodePosition(NodeViewModel node, bool commit) =>
        Graph.SetNodePosition(node.Id, new Point(node.X, node.Y), commit);

    private void SaveSystemNodePosition(NodeViewModel node)
    {
        if (_clip == null) return;
        _clip.ExtraData ??= new Dictionary<string, object>();

        switch (node.Kind)
        {
            case NodeKind.AudioSample:
                var key = node.Id == PositionNodeId ? "positionSource" : node.Id == AudioSampleNodeId ? "audioSource" : BindingSourceId(node.Id);
                _clip.ExtraData[StagePositionKey(key, "X")] = node.X;
                _clip.ExtraData[StagePositionKey(key, "Y")] = node.Y;
                break;
            case NodeKind.Input:
                _clip.ExtraData[StagePositionKey("input", "X")] = node.X;
                _clip.ExtraData[StagePositionKey("input", "Y")] = node.Y;
                break;
            case NodeKind.Output:
                _clip.ExtraData[StagePositionKey("output", "X")] = node.X;
                _clip.ExtraData[StagePositionKey("output", "Y")] = node.Y;
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
        _clip.ExtraData[StagePositionKey(ExtraDataViewScaleKey, "View")] = NodesContainer.Scale;
        _clip.ExtraData[StagePositionKey(ExtraDataViewPanXKey, "View")] = NodesContainer.TranslationX;
        _clip.ExtraData[StagePositionKey(ExtraDataViewPanYKey, "View")] = NodesContainer.TranslationY;
    }

    private void ApplySavedViewTransform()
    {
        if (_clip == null) return;
        double scale = GetExtraDataDouble(StagePositionKey(ExtraDataViewScaleKey, "View"), GetExtraDataDouble(ExtraDataViewScaleKey, 1.0));
        double panX = GetExtraDataDouble(StagePositionKey(ExtraDataViewPanXKey, "View"), GetExtraDataDouble(ExtraDataViewPanXKey, 0.0));
        double panY = GetExtraDataDouble(StagePositionKey(ExtraDataViewPanYKey, "View"), GetExtraDataDouble(ExtraDataViewPanYKey, 0.0));

        Graph.SetViewport(new GraphViewport(scale, panX, panY));

        UpdateDrawableScale();
    }

    private void SubscribeToPageEvents()
    {
        if (_page == null || _subscribedToPageEvents) return;
        _page.OnClipChanged += OnPageClipChanged;
        _page.EditCommandRequested += OnEditCommandRequested;
        _subscribedToPageEvents = true;
        Unloaded += OnViewUnloaded;
    }

    private void UnsubscribeFromPageEvents()
    {
        if (_page == null || !_subscribedToPageEvents) return;
        _page.OnClipChanged -= OnPageClipChanged;
        _page.EditCommandRequested -= OnEditCommandRequested;
        _subscribedToPageEvents = false;
        Unloaded -= OnViewUnloaded;
    }

    private void OnViewUnloaded(object? sender, EventArgs e)
    {
        CloseMermaidImport();
        CloseGroupGraph();
        EndPortConnection();
        _fieldTypesCts?.Cancel();
        _previewCts?.Cancel();
        UnsubscribeFromPageEvents();
    }

    private void OnPageClipChanged(object? sender, ClipUpdateEventArgs e)
    {
        if (_clip == null || _closing) return;
        if (e.SourceId != _clip.Id)
        {
            Dispatcher.Dispatch(() =>
            {
                _previewCts?.Cancel();
                UpdateClipValuesPanel();
                RefreshBindingDiagnosticIndicators();
            });
            return;
        }
        if (e.Reason != ClipUpdateReason.PropertyChanged
            || e.DetailInfo == "__EFFECT_BINDING_CHANGED__" && e.Operator != ClipChangeOperatorKind.ExternalRPC) return;

        Dispatcher.Dispatch(() =>
        {
            if (_clip == null || _closing) return;
            // Avoid re-entrant Reload() if we're the one making changes
            if (_isDraggingNodeOrPort && e.Operator != ClipChangeOperatorKind.ExternalRPC) return;
            if (e.Operator == ClipChangeOperatorKind.ExternalRPC)
            {
                EndPortConnection();
            }
            Reload();
            InitializeEditHistory(reset: true);
        });
    }

    private async Task<bool> ConnectPortsAsync(NodeViewModel source, int sourceIndex, NodeViewModel target,
        PortKind targetKind, int targetIndex)
    {
        if (_clip?.EffectProviders is not { } providers) return false;
        var sourcePort = PortOf(source, PortKind.AnchorOutput, sourceIndex);
        var targetPort = PortOf(target, targetKind, targetIndex);
        if (sourcePort is null || targetPort is null
            || !NodePort.IsValidPort(sourcePort) || !NodePort.IsValidPort(targetPort)
            || targetPort.DeclaredFieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic)
            || sourcePort.FieldType.HasFlag(EffectArgumentFieldType.Any) && target.Kind == NodeKind.Output
            || !sourcePort.FieldType.HasFlag(EffectArgumentFieldType.Any)
                && !targetPort.DeclaredFieldType.HasFlag(EffectArgumentFieldType.Any)
                && !PortTypeHelper.IsPortTypeCompatible(sourcePort.FieldType, targetPort.FieldType,
                    sourcePort.TypeContract, targetPort.TypeContract))
        {
            SetStatusText(PPLocalizedResources.EffectBindView_ConnectedFail);
            return false;
        }
        var sourceProviderId = PortProviderId(source, sourcePort);
        var targetProviderId = PortProviderId(target, targetPort);
        var sourceProvider = providers.GetValueOrDefault(sourceProviderId);
        var targetProvider = providers.GetValueOrDefault(targetProviderId);
        string? sourcePortKey = sourceProvider is IMultipleOutputEffectProvider ? sourcePort.Key : null;
        string targetKey = targetPort?.Key ?? EffectProviderAnchorExtensions.InputKey;
        var owner = _clip;
        int version = _fieldTypeVersion;
        int interactionVersion = Graph.InteractionVersion;
        var sourceId = BindingSourceId(sourceProviderId, sourcePortKey);
        EffectFieldTypeResolution types;
        try
        {
            types = await EffectFieldTypeResolution.ResolveAsync(providers, bindingOverrides: targetProvider is null ? null
                : new Dictionary<Guid, IReadOnlyDictionary<string, string>>
                {
                    [targetProviderId] = new Dictionary<string, string>(targetProvider.AnchorsBindingState)
                    {
                        [targetKey] = sourceId,
                    },
                });
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Resolve candidate effect connection", this);
            SetStatusText(ex.Message);
            return false;
        }
        if (_closing || !ReferenceEquals(_clip, owner) || version != _fieldTypeVersion
            || interactionVersion != Graph.InteractionVersion
            || !_nodes.TryGetValue(source.Id, out var currentSource) || currentSource != source
            || !_nodes.TryGetValue(target.Id, out var currentTarget) || currentTarget != target) return false;
        if (DynamicEffectCalculation.HasTypeErrors(types, _clip?.ExtraData) || targetProvider is not null && !types.AreInputsCompatible(targetProvider)
            || target.Kind == NodeKind.Output && target.Provider is null && sourceProvider is not null
                && !EffectFieldTypes.AreCompatible(types.GetOutputType(sourceProvider, sourcePortKey), _stage.GetContentType()))
        {
            SetStatusText(PPLocalizedResources.EffectBindView_ConnectedFail);
            return false;
        }
        if (targetKey == EffectProviderAnchorExtensions.InputKey && sourceProvider is not null && targetProvider is not null
            && !sourceProvider.CanConnectContent(targetProvider, sourcePortKey, types))
        {
            SetStatusText(Localized.Effect_IncompatiblePipeline);
            return false;
        }
        AddUIBinding(targetKey != EffectProviderAnchorExtensions.InputKey
            ? new(UIBindingKind.Value, sourceProviderId, targetProviderId, targetKey, sourcePortKey)
            : new(UIBindingKind.Picture, sourceProviderId, targetProviderId, SourcePortKey: sourcePortKey), types);
        RecreateNodeView(target);
        if (!OnBindingConfigurationChanged()) return false;
        LogDiagnostic($"Connected '{source.DisplayName}' to '{target.DisplayName}', port {targetKind}/{targetIndex}.");
        SetStatusText(PPLocalizedResources.EffectBindView_Connected(source.DisplayName, target.DisplayName));
        RebuildConnections();
        return true;
    }

    /// <summary>
    /// 拖拽到一个无效位置（空白/不兼容端口）时，把拖拽来源端口的现有绑定断开。
    /// 参数绑定点对应断其 <see cref="UIBindingKind.Value"/> 绑定；图片输入/输出对应断图片链路。
    /// </summary>
    private bool TryDisconnectDroppedPort(NodeViewModel node, PortKind portKind, int portIndex)
    {
        if (node.Group is not null)
        {
            var port = PortOf(node, portKind, portIndex);
            if (port is null) return false;
            bool changed = portKind == PortKind.AnchorOutput
                ? DisconnectGroupOutput(node, port)
                : RemoveUIBindingsTo(port.Id, port.Key == EffectProviderAnchorExtensions.InputKey ? null : port.Key,
                    port.Key == EffectProviderAnchorExtensions.InputKey ? UIBindingKind.Picture : UIBindingKind.Value);
            if (changed) OnBindingConfigurationChanged();
            return changed;
        }
        bool removed = false;
        if (portKind is PortKind.ParamBind or PortKind.AnchorInput)
        {
            var port = PortOf(node, portKind, portIndex);
            if (port is null) return false;
            removed = port.Key == EffectProviderAnchorExtensions.InputKey
                ? RemoveUIBindingsTo(BindingIdOf(node), null, UIBindingKind.Picture)
                : RemoveUIBindingsTo(BindingIdOf(node), port.Key, UIBindingKind.Value);
            if (removed)
            {
                RecreateNodeView(node);
            }
        }
        else if (portKind == PortKind.AnchorOutput)
        {
            removed = RemoveUIBindingsFrom(BindingIdOf(node), node.Provider is IMultipleOutputEffectProvider ? node.OutputPorts[portIndex].Key : null,
                currentPipelineOnly: _stage == EffectStage.Composition);
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
            var hadOutput = providers.Values.Any(p => p.Stage == _stage && p.IsFinalOutputSource());
            EffectBindingHelper.SetFinalOutput(providers, null, _stage);
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

    private bool RemoveUIBindingsFrom(Guid sourceId, string? sourcePortKey = null, EffectStage? stage = null, bool currentPipelineOnly = false)
    {
        if (_clip?.EffectProviders is not { } providers) return false;
        var source = BindingSourceId(sourceId, sourcePortKey);
        bool Matches(string value) => value == source || sourcePortKey is null
            && EffectProviderOutputExtensions.TryParseOutputSourceId(value, out var id, out _) && id == sourceId;
        bool removed = false;
        foreach (var provider in providers.Values.Where(p => (stage is null || p.Stage == stage)
            && (!currentPipelineOnly || IsCurrentPipelineProvider(p))))
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
            && (!currentPipelineOnly || _outputNode is not null)
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
        if (node.Group is not null)
        {
            foreach (var port in node.Group.Ports.ToArray())
            {
                if (port.IsInput) RemoveUIBindingsTo(port.ProviderId, port.FieldId == EffectProviderAnchorExtensions.InputKey ? null : port.FieldId,
                    port.FieldId == EffectProviderAnchorExtensions.InputKey ? UIBindingKind.Picture : UIBindingKind.Value);
                else DisconnectGroupOutput(node, new NodePort { Id = port.ProviderId, Key = port.FieldId });
            }
            OnBindingConfigurationChanged();
            RebuildConnections();
            return;
        }
        bool removed;
        if (node.TerminalField is not null)
        {
            removed = false;
            foreach (var field in node.TerminalFields)
                removed |= RemoveUIBindingsTo(BindingIdOf(node), field, UIBindingKind.Value);
        }
        else
        {
            removed = RemoveUIBindingsFrom(BindingIdOf(node), currentPipelineOnly: _stage == EffectStage.Composition);
            removed |= RemoveUIBindingsTo(BindingIdOf(node), null);
        }
        if (!removed) return;

        RecreateNodeView(node);
        RebuildConnections();
        ConnectionsLayer.Invalidate();
        OnBindingConfigurationChanged();
        SetStatusText(Localized._Done);
    }

    private NodeViewModel? GetNodeByBindingId(Guid id)
    {
        if (_nodes.TryGetValue(id, out var n)) return n;
        if (_nodes.Values.FirstOrDefault(n => n.Provider?.Id == id) is { } terminal) return terminal;
        if (_inputNode?.Id == id) return _inputNode;
        if (_outputNode?.Id == id) return _outputNode;
        return null;
    }

    private static Guid BindingIdOf(NodeViewModel node) => node.Kind switch
    {
        NodeKind.Input => node.Provider?.Id ?? IEffectProvider.InputAnchorGUID,
        NodeKind.Output => IEffectProvider.OutputAnchorGUID,
        _ => node.Provider?.Id ?? node.Id,
    };

    private string BindingSourceId(Guid id, string? port = null)
    {
        if (id == AudioSampleNodeId) return ValueProviderFrameContext.ClipAudioProviderId;
        if (id == PositionNodeId) return ValueProviderFrameContext.ClipPositionProviderId;
        foreach (var source in BuiltInSources)
            if (id == source.Id) return source.Source;
        return port is null ? id.ToString() : EffectProviderOutputExtensions.CreateOutputSourceId(id, port);
    }

    private static bool TryParseBindingSource(string? source, out Guid id, out string? port)
    {
        port = null;
        if (source == ValueProviderFrameContext.ClipPositionProviderId) { id = PositionNodeId; return true; }
        if (source == ValueProviderFrameContext.ClipAudioProviderId) { id = AudioSampleNodeId; return true; }
        foreach (var builtIn in BuiltInSources)
            if (source == builtIn.Source) { id = builtIn.Id; return true; }
        return EffectProviderOutputExtensions.TryParseOutputSourceId(source, out id, out port);
    }

    /// <summary>写入一条 UI 层绑定：同一来源到同一目标端口的旧绑定先移除（同端口单连线），再追加新绑定。</summary>
    /// <summary>
    /// 写入一条 UI 层绑定。同一目标端口（图片输入 或 参数字段）只允许一条入边：先移除目标为该端口的
    /// 旧绑定（无论来源），再追加新绑定。值绑定的来源（一个值输出）可同时喂给多个参数，因此不做来源去重。
    /// 图片入边只覆盖图片入边，值入边只覆盖值入边——两者互不干扰。
    /// </summary>
    private void AddUIBinding(UIBinding binding, EffectFieldTypeResolution? types = null)
    {
        if (_clip?.EffectProviders is not { } providers) return;
        var sourceId = BindingSourceId(binding.Source, binding.SourcePortKey);
        if (binding.Kind == UIBindingKind.Value)
        {
            if (!providers.TryGetValue(binding.Target, out var target) || string.IsNullOrWhiteSpace(binding.TargetPortKey)) return;
            target.SetFieldBinding(binding.TargetPortKey, sourceId);
        }
        else if (binding.Target == IEffectProvider.OutputAnchorGUID)
        {
            EffectBindingHelper.SetFinalOutput(providers,
                binding.Source == IEffectProvider.InputAnchorGUID ? null : binding.Source, _stage, binding.SourcePortKey, types ?? _fieldTypes);
        }
        else if (providers.TryGetValue(binding.Target, out var target))
        {
            if (providers.TryGetValue(binding.Source, out var source) && !source.CanConnectContent(target, binding.SourcePortKey, types))
            {
                SetStatusText(Localized.Effect_IncompatiblePipeline);
                return;
            }
            target.SetMainInputSource(sourceId);
        }
        LogDiagnostic($"Provider binding updated: {JsonSerializer.Serialize(binding)}");
    }

    private void SelectNode(NodeViewModel node, bool force = false)
    {
        if (!force)
        {
            Graph.SelectNode(node.Id);
            return;
        }
        if (!_groupMultiSelect) Graph.SetSelection([node.Id], node.Id);
        _selectedNode = node;
        RefreshGroupSelection();

        PropertiesTabHeader.Text = Localized.PropertyPanel;
        SourcesAndOutputsPanel.IsVisible = false;
        PropertiesPanel.IsVisible = true;
        PropertiesPanel.Children.Clear();
        AfterEffectBigPreview.IsVisible = true;
        AfterEffectBigPreview.Source = node.PreviewImage;

        PropertiesPanel.Children.Add(new Label { Text = node.DisplayName, FontAttributes = FontAttributes.Bold, HorizontalOptions = LayoutOptions.Center });
        PropertiesPanel.Children.Add(_selectedNodeDiagnostics);
        UpdateSelectedNodeDiagnostics();

        if (node.Group is not null)
        {
            BuildGroupProperties(node);
            return;
        }
        if (node.Provider is null)
        {
            return;
        }
        var description = EffectServices.GetUIProvider(node.Provider).GetLocalizedEffectDescription(node.Provider, PluginManager.CurrentLocale);
        if (!string.IsNullOrWhiteSpace(description))
            PropertiesPanel.Children.Add(new Label { Text = description, FontSize = 12, TextColor = Colors.Gray });
        if (node.Provider is MixtureSourceProvider) return;

        if (node.Provider is IClipValueEndpointProvider endpoint)
        {
            BuildEndpointProperties(node, endpoint);
            return;
        }
        if (node.Provider is EffectVariableProvider variable)
        {
            BuildVariableProperties(node, variable);
            return;
        }

        try
        {
            ArgumentNullException.ThrowIfNull(node.Provider);
            var ui = node.Provider is ClipArgumentProvider or RenderOutputProvider ? new EffectProviderUI(node.Provider, node.TerminalField is null ? null : node.TerminalFields) : EffectServices.GetUIProvider(node.Provider);
            // Inject the binding host so each field in the property UI can offer a bind action.
            // Note: the property-panel bind channel writes directly into the provider's Fields
            // (via ClipBindingHost), while drag bindings in this view stay in the UI layer only.
            if (ui is IBindingHostHolder bindingHostHolder && _clip is not null)
            {
                bindingHostHolder.BindingHost = new ClipBindingHost(_clip, node.Provider, _page,
                    onChanged: () =>
                    {
                        OnBindingConfigurationChanged();
                        RefreshNodeParams(node);
                        RefreshSelectedNode();
                    });
            }
            var ppb = ui.CreateUI(node.Provider);
            ArgumentNullException.ThrowIfNull(ppb, $"CreateUI() for {node.Provider?.TypeName}");
            if (_clip is not null && _page is not null)
                ClipInfoBuilder.AddContinuousEffectRangeUI(ppb, node.Provider, _clip, _page);
            ppb.PropertyChanged += (s, args) =>
            {
                ArgumentNullException.ThrowIfNull(node.Provider);
                if (!args.Id.StartsWith(ClipInfoBuilder.ContinuousEffectRangePropertyPrefix, StringComparison.Ordinal)
                    && node.Provider is IEffectProvider p)
                {
                    var fieldUpdate = ui.HandlePropertyPanelChange(node.Provider, args);
                    if (fieldUpdate.newFields is not null)
                        p.Fields = fieldUpdate.newFields;
                }
                RefreshNodeParams(node);
                if (node.Provider is AdvancedMathEffectProvider
                    && EffectProviderUIHelper.FieldId(node.Provider, args.Id) == "Operation")
                {
                    foreach (var port in node.OutputPorts.Where(p => !node.Provider.GetOutputFields().ContainsKey(p.Key)))
                        RemoveUIBindingsFrom(node.Provider.Id, port.Key);
                    RefreshSelectedNode();
                }
                // 属性面板改动字段后，重建连线（例如动态绑定字段值变化可能影响值绑定连线）。
                RebuildConnections();
                ConnectionsLayer.Invalidate();
                OnBindingConfigurationChanged();
            };

            PropertiesPanel.Children.Add(ppb.BuildWithScrollView());
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Load effect node properties", this);
            PropertiesPanel.Children.Add(new Label { Text = Localized._ExceptionTemplate(ex) });
        }

    }

    /// <summary>
    /// Rebuilds the property panel of the currently selected effect node.
    /// Used by the binding host after a binding is applied / removed so the UI reflects the new state.
    /// </summary>
    private void RefreshSelectedNode()
    {
        if (_selectedNode is not null) SelectNode(_selectedNode, force: true);
        else UpdateSourcesAndOutputsPanel();
    }

    /// <summary>
    /// Rebuild only the node-embedded parameter strip so it reflects the latest field values.
    /// </summary>
    private void RefreshNodeParams(NodeViewModel node)
    {
        if (node?.Provider is null || node.View == null) return;
        RecreateNodeView(node);
    }

    private async void ShowNodeContextMenu(NodeViewModel node)
    {
        var clip = _clip;
        List<(string Text, Action Run)> actions = [];
        AddGroupActions(node, actions);
        if (GetSourceOutputKey(node) is { } key)
            actions.Add((Localized.DraftPage_ContextMenu_Delete, () => SetSourceOutputEnabled(key, false)));
        if (node.Provider is not null)
        {
            actions.Add((PPLocalizedResources.EffectBindView_Configure, () =>
            {
                SelectNode(node);
                RightTabView.SelectedIndex = 0;
            }));
            if (node.Kind == NodeKind.Effect)
                actions.Add((Localized.DraftPage_ContextMenu_Delete, () => RemoveEffect(node)));
            actions.Add((Localized.Effect_ToggleParamDirection, () => ToggleParamDirection(node)));
        }
        void Run(Action action)
        {
            if (_closing || !ReferenceEquals(_clip, clip) || _nodes.GetValueOrDefault(node.Id) != node) return;
            try { action(); }
            catch (Exception ex)
            {
                Logger.Log(ex, "Effect node context menu action", this);
                SetStatusText(ex.Message);
            }
        }
        try
        {
            if (actions.Count == 0) return;
            if (node.Frame is { } anchor && IContextMenuBuilder.Default?.CreateNewInstance() is { } menu)
            {
                foreach (var action in actions) menu.AddCommand(action.Text, () => Run(action.Run));
                if (menu.TryShow(anchor)) return;
            }
            if (_page is null) return;
            var selected = await _page.DisplayActionSheetAsync(node.DisplayName, Localized._Cancel, null, actions.Select(a => a.Text).ToArray());
            foreach (var action in actions)
                if (action.Text == selected)
                {
                    Run(action.Run);
                    break;
                }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Show effect node context menu", this);
            SetStatusText(ex.Message);
        }
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
        RecordEditHistory();
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

        if (_clip.EffectProviders is { } providers)
            EffectBindingHelper.RemoveProvider(providers, node.Id);
        RemoveEmptyGroups();
        EffectGroupHelper.Write(_clip.ExtraData, _groups);

        _nodes.Remove(node.Id);

        if (node.View != null)
        {
            Graph.RemoveNode(node.Id);
        }

        if (_selectedNode == node) _selectedNode = null;
        UpdateSourcesAndOutputsPanel();

        // 节点删除后清理其涉及的全部 UI 绑定（作为来源或目标的连线），避免悬空引用。
        RemoveUIBindingsFrom(BindingIdOf(node));
        RemoveUIBindingsTo(BindingIdOf(node), null);
        OnBindingConfigurationChanged();
        RebuildConnections();
        ConnectionsLayer.Invalidate();
        UpdateClipValuesPanel();
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
        if (_clip is null || _page is null || _clip.ClipType == ClipMode.MarkingClip) return;
        _previewCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _previewCts = cts;
        _previewDiagnostics = [];
        RefreshBindingDiagnosticIndicators();
        var clip = _clip;
        var stage = _stage;
        bool recordSteps = _stepDebugEnabled;
        if (recordSteps) BeginStepRecording();
        try
        {
            var draft = DraftImportAndExportHelper.ExportFromDraftPage(_page, includeUiOnlyClips: false, rebuildEffects: false);
            draft.Clips = [.. draft.Clips, .. _page.Clips.Values
                .Where(c => c.ClipType == ClipMode.AudioClip && !draft.Clips.Any(d => d.Id == c.Id))
                .Select(c => DraftImportAndExportHelper.ExportClipElementFromDraftPage(_page, c, rebuildEffects: false))];
            var response = await _page.previewer.RenderEffectBindingPreviewAsync(new()
            {
                ClipId = clip.Id,
                TimelineJson = JsonSerializer.Serialize(draft),
                FrameIndex = (uint)Math.Max(0, _page.CurrentFrame),
                Width = _page.previewWidth,
                Height = _page.previewHeight,
                ProjectWidth = Math.Max(1, _page.ProjectInfo.RelativeWidth),
                ProjectHeight = Math.Max(1, _page.ProjectInfo.RelativeHeight),
                Stage = (int)stage,
                RecordSteps = recordSteps,
                Terminals = _nodes.Values.Where(n => n.TerminalField is not null && n.Provider is not null)
                    .Select(n => new EffectBindingPreviewTerminal
                    {
                        NodeId = n.Id, ProviderId = n.Provider!.Id, Fields = n.TerminalFields.ToList(),
                    }).ToList(),
                BuiltInSources = BuiltInSources.Where(s => _nodes.ContainsKey(s.Id)).Select(s => s.Source).ToList(),
            }, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_closing || !ReferenceEquals(_clip, clip) || _stage != stage) return;
            ApplyBackendPreviews(response);
            _previewDiagnostics = response.Diagnostics.Select(d => new EffectBindingHelper.BindingDiagnostic(
                d.ProviderId == Guid.Empty ? null : d.ProviderId, "PreviewFailure", d.Message) { FieldId = d.FieldId }).ToArray();
            if (_previewDiagnostics.Count == 0 && !string.IsNullOrWhiteSpace(response.Error))
                _previewDiagnostics = [new(null, "PreviewFailure", response.Error)];
            RefreshBindingDiagnosticIndicators();
            if (recordSteps && _stepDebugEnabled) FinishStepRecording(response);
            if (!string.IsNullOrWhiteSpace(response.Error)) SetStatusText(response.Error);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log(ex, $"Preview effect bindings for clip {clip.Id}", this);
            if (cts.IsCancellationRequested || _closing || !ReferenceEquals(_clip, clip) || _stage != stage) return;
            var origin = EffectProviderFailure.Read(ex);
            _previewDiagnostics = [new(origin.ProviderId, "PreviewFailure", EffectProviderFailure.Describe(ex)) { FieldId = origin.FieldId }];
            RefreshBindingDiagnosticIndicators();
            if (_stepDebugEnabled)
            {
                _stepError = ex.Message;
                UpdateStepDebugUI();
            }
            SetStatusText(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts)) _previewCts = null;
        }
    }

    private void ApplyBackendPreviews(EffectBindingPreviewResponse response)
    {
        foreach (var node in _nodes.Values)
        {
            node.PreviewImage = null;
            node.PreviewValue = "";
        }
        void Apply(NodeViewModel? node, EffectBindingPreviewValue? value)
        {
            if (node is null || value is null) return;
            var snapshot = ReadStepValue(value);
            node.PreviewImage = snapshot.Image;
            node.PreviewValue = snapshot.Image is null ? snapshot.Text : "";
        }
        foreach (var step in response.Steps)
        {
            var node = _nodes.GetValueOrDefault(step.NodeId == Guid.Empty ? step.ProviderId : step.NodeId);
            if (node is null) continue;
            Apply(node, step.Outputs.Values.FirstOrDefault(v => v.Png.Length > 0) ?? step.Outputs.Values.FirstOrDefault());
            if (node.PreviewImage is null && step.Outputs.Count > 1)
                node.PreviewValue = string.Join(Environment.NewLine, step.Outputs.Select(p => $"{p.Key}: {ReadStepValue(p.Value).Text}"));
        }
        Apply(_inputNode, response.Sources.GetValueOrDefault("input"));
        Apply(_outputNode, response.Sources.GetValueOrDefault("output"));
        Apply(_nodes.Values.FirstOrDefault(n => n.Provider is MixtureSourceProvider { IsUnder: true }), response.Sources.GetValueOrDefault("under"));
        Apply(_nodes.GetValueOrDefault(PositionNodeId), response.Sources.GetValueOrDefault("position"));
        Apply(_nodes.GetValueOrDefault(AudioSampleNodeId), response.Sources.GetValueOrDefault("audio"));
        foreach (var source in BuiltInSources) Apply(_nodes.GetValueOrDefault(source.Id), response.Sources.GetValueOrDefault(source.Source));
        if (response.Sources.GetValueOrDefault("output") is { } output) AfterEffectBigPreview.Source = ReadStepValue(output).Image;
        else AfterEffectBigPreview.Source = null;
    }

    [DebuggerDisplay("{Id}, {Provider?.TypeName}")]
    class NodeViewModel : BindableGraphNode
    {
        public required NodeKind Kind;
        public required IEffectProvider? Provider;
        public EffectGroup? Group;
        public List<View> InputPortViews = new();
        public List<View> OutputPortViews = new();
        public List<View> ParamPortViews = new();
        public string BindingDiagnosticSignature = string.Empty;
        public string VisualSignature = string.Empty;

        /// <summary>Input ports displayed inside the node.</summary>
        public List<NodePort> InputPorts = new();
        /// <summary>Output anchor port, derived from <see cref="IEffectProvider.OutField"/>.</summary>
        public List<NodePort> OutputPorts = new();
        public NodePort? OutputPort
        {
            get => OutputPorts.FirstOrDefault();
            set => OutputPorts = value is null ? [] : [value];
        }
        public double BodyHeight => Math.Max(FrameHeight, Math.Max(InputPorts.Count, OutputPorts.Count) * ParamRowHeight);
        /// <summary>Visible named input ports.</summary>
        public List<NodePort> ParamPorts = new();

        /// <summary>Height of the param strip above the frame (0 when below or absent).</summary>
        public double ParamsTopHeight;

        public double Width => View?.Width > 0 ? View.Width : 150;

        public ImageSource? PreviewImage { get; set { field = value; PreviewImageChanged?.Invoke(value); } }
        public Action<ImageSource?>? PreviewImageChanged;
        public string PreviewValue { get; set { field = value; PreviewValueChanged?.Invoke(value); } } = "";
        public Action<string>? PreviewValueChanged;

        /// <summary>
        /// Derive <see cref="InputPorts"/>, <see cref="OutputPort"/> and <see cref="ParamPorts"/>
        /// from the provider's <see cref="IEffectProvider.InFields"/>, <see cref="IEffectProvider.OutField"/>
        /// and <see cref="IEffectProvider.Fields"/>.
        /// </summary>
        public string? TerminalField;
        public IReadOnlyList<string> TerminalFields = [];
        public bool IsStageTerminal => TerminalField is not null || Provider is { } p
            && !p.Target.HasFlag(EffectTarget.ValueProvider)
            && p.TypeOfEffect is EffectType.SpeedVarianceProvider or EffectType.SourceReplacement or EffectType.Transform;
        public EffectFieldTypeResolution? FieldTypes { get; set; }

        public void BuildPortsFromProvider()
        {
            if (Provider == null) return;

            var inputs = DynamicEffectBindings.InputFields(Provider);
            if (TerminalField is not null) inputs = inputs.Where(p => TerminalFields.Contains(p.Key)).ToDictionary();
            else if (IsStageTerminal) inputs.Remove(EffectProviderAnchorExtensions.InputKey);
            InputPorts = inputs
                .Where(kv => IsStageTerminal || IsNodeInput(Provider, kv.Key))
                .Select((kv, i) => new NodePort
                {
                    Kind = PortKind.AnchorInput,
                    Key = kv.Key,
                    FieldType = FieldTypes?.GetInputType(Provider, kv.Key) ?? kv.Value.FieldType,
                    DeclaredFieldType = kv.Value.FieldType,
                    TypeContract = FieldTypes?.GetInputContract(Provider, kv.Key) ?? kv.Value.TypeContract,
                    DisplayName = EffectProviderUIHelper.FieldName(Provider, kv.Key, true, HumanizePortName(kv.Key)),
                    Index = i
                }).ToList();

            OutputPorts = Provider.TypeOfEffect == EffectType.Transform || !DynamicEffectBindings.CanBindOutput(Provider) ? []
                : Provider.GetOutputFields().Values.Select((p, i) => new NodePort
                {
                    Kind = PortKind.AnchorOutput, Key = p.Id, FieldType = FieldTypes?.GetOutputType(Provider, p.Id) ?? p.FieldType,
                    DeclaredFieldType = p.FieldType,
                    TypeContract = FieldTypes?.GetOutputContract(Provider, p.Id) ?? p.TypeContract,
                    DisplayName = EffectProviderUIHelper.FieldName(Provider, p.Id, false, HumanizePortName(p.Id)), Index = i,
                }).ToList();

            ParamPorts = inputs
                .Where(kv => !kv.Value.FieldType.HasFlag(EffectArgumentFieldType.NotVisibleInEffectPanel)
                          && !IsStageTerminal && !IsNodeInput(Provider, kv.Key))
                .Select((kv, i) => new NodePort { Kind = PortKind.ParamBind, Key = kv.Key,
                    FieldType = FieldTypes?.GetInputType(Provider, kv.Key) ?? kv.Value.FieldType,
                    DeclaredFieldType = kv.Value.FieldType,
                    TypeContract = FieldTypes?.GetInputContract(Provider, kv.Key) ?? kv.Value.TypeContract,
                    DisplayName = EffectProviderUIHelper.FieldName(Provider, kv.Key, true,
                        Provider is EffectVariableProvider ? HumanizePortName(kv.Key) : null), Index = i })
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

        var target = _clip.GetEffectSelectionTarget(_stage);
        var context = (_clip, _page, target, _stage, _compositionPipeline);
        if (_addEffectsContext == context && AddEffectsPanel.Children.Count > 0) return;

        var panel = ClipInfoBuilder.BuildAddEffectPanel(
            target,
            _page,
            EffectServices.GetAvailableEffectProviders()
                .Where(p => p.Key is not ("ClipValueInput" or "ClipValueOutput")
                    && (p.Value().Target.HasFlag(EffectTarget.Pipeline) || p.Value().SupportedStages.Contains(_stage))
                    && CanAddToPipeline(p.Value(), _stage, _compositionPipeline))
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
            hideKeyFramedProviders: true,
            includePipelineProviders: true
        );
        AddEffectsPanel.Children.Clear();
        AddEffectsPanel.Children.Add(panel);
        AddGroupLibraryPanel();
        _addEffectsContext = context;
        Log($"Rebuilt effect selector for {_clip.Id}, target {target}, stage {_stage}.", "debug");
    }

    private void AddProvider(string providerTypeName)
    {
        if (_clip == null || _closing) return;
        _providerAddTask = AddProviderAsync(_providerAddTask, _clip, _stage, _compositionPipeline, providerTypeName);
    }

    private async Task AddProviderAsync(Task previous, ClipElementUI clip, EffectStage stage, CompositionPipeline pipeline, string providerTypeName)
    {
        try
        {
            await previous;
            if (!ReferenceEquals(_clip, clip)) return;
            if (!EffectServices.GetAvailableEffectProviders().TryGetValue(providerTypeName, out var factory)) return;
            var target = clip.GetEffectSelectionTarget(stage);
            var instance = await Task.Run(factory);
            instance.Stage = stage;
            if (!ClipInfoBuilder.CanSelectEffectProvider(instance, target, hideKeyFramedProviders: true, includePipelineProviders: true)
                || !CanAddToPipeline(instance, stage, pipeline))
            {
                Log($"Rejected effect provider {providerTypeName} for clip {clip.Id} ({clip.ClipType}).", "warning");
                return;
            }
            instance.Id = Guid.NewGuid();
            instance.DisconnectMainInput();
            instance.SetFinalOutputSource(false);
            if (stage == EffectStage.Composition) instance.MetaData[CompositionPipelineKey] = pipeline.ToString();
            while (ReferenceEquals(_clip, clip))
            {
                int version = _fieldTypeVersion;
                var providers = new Dictionary<Guid, IEffectProvider>(clip.EffectProviders ?? []) { [instance.Id] = instance };
                bool transformPipeline = stage == EffectStage.Composition && pipeline != CompositionPipeline.Mixture;
                var source = transformPipeline ? IEffectProvider.NoConnectionGUID.ToString()
                    : providers.Values.FirstOrDefault(p => p.Id != instance.Id && p.Stage == stage && p.IsFinalOutputSource())
                    ?.GetFinalOutputSourceId() ?? IEffectProvider.InputAnchorGUID.ToString();
                var types = await EffectFieldTypeResolution.ResolveAsync(providers, bindingOverrides: new Dictionary<Guid, IReadOnlyDictionary<string, string>>
                {
                    [instance.Id] = new Dictionary<string, string>(instance.AnchorsBindingState) { [EffectProviderAnchorExtensions.InputKey] = source },
                });
                if (!ReferenceEquals(_clip, clip)) return;
                if (version != _fieldTypeVersion) continue;
                if (!transformPipeline) EffectBindingHelper.AutoConnectProviderToOutput(providers, instance, clip.GetEffectTarget(), types);
                clip.EffectProviders = providers;
                if (stage == _stage && (stage != EffectStage.Composition || pipeline == _compositionPipeline)) AddProviderNode(instance);
                OnBindingConfigurationChanged();
                RebuildConnections();
                ConnectionsLayer.Invalidate();
                Log($"Added effect node {instance.Id} ({providerTypeName}) on {clip.Id}.", "debug");
                SetStatusText(Localized._Done);
                return;
            }
        }
        catch (Exception ex)
        {
            Log(ex, $"Add effect node {providerTypeName} on {clip.Id}", this);
            SetStatusText(ex.Message);
        }
    }

    private void OnSourcesAndOutputsClicked(object sender, EventArgs e) => ShowSourcesAndOutputs();

    private async void GeneratePreviewButton_Clicked(object sender, EventArgs e)
    {
        if (PreviewBusyIndicator.IsRunning) return;
        PreviewBusyIndicator.IsVisible = PreviewBusyIndicator.IsRunning = true;
        try { await GeneratePreviews(); }
        finally
        {
            if (_previewCts is null) PreviewBusyIndicator.IsVisible = PreviewBusyIndicator.IsRunning = false;
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
        diagnostics.AddRange(GetAllBindingDiagnostics());
        if (diagnostics.Count > 0)
            SetStatusText(string.Join(Environment.NewLine, diagnostics.Select(d => d.Message)));
    }

    private static string GetBindingDiagnosticSignature(IEnumerable<EffectBindingHelper.BindingDiagnostic> diagnostics) =>
        string.Join("\n", diagnostics.Select(d => $"{d.ProviderId}|{d.FieldId}|{d.Code}|{d.Message}"));

    /// <summary>Recreates node visuals so newly added or resolved binding diagnostics are visible immediately.</summary>
    private void RefreshBindingDiagnosticIndicators(
        IReadOnlyList<EffectBindingHelper.BindingDiagnostic>? diagnostics = null)
    {
        if (_clip?.EffectProviders is not { } providers) return;
        diagnostics ??= GetAllBindingDiagnostics();
        _bindingDiagnostics = diagnostics;
        foreach (var node in _nodes.Values.ToList())
        {
            var signature = GetBindingDiagnosticSignature(GetBindingDiagnostics(node, diagnostics));
            if (!string.Equals(signature, node.BindingDiagnosticSignature, StringComparison.Ordinal))
                RecreateNodeView(node);
        }
        UpdateBindingDiagnosticsPanel();
        UpdateSelectedNodeDiagnostics();
        RefreshGroupSelection();
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
            if (provider.Stage != _stage || !_nodes.Values.Any(n => n.Provider?.Id == provider.Id)) continue;
            var input = provider.GetMainInputSource();
            if (TryParseBindingSource(input, out var inputId, out var outputId)
                && inputId != IEffectProvider.NoConnectionGUID
                && GetNodeByBindingId(inputId) is not null)
                bindings.Add(new UIBinding(UIBindingKind.Picture, inputId, provider.Id, SourcePortKey: outputId));

            if (provider.IsFinalOutputSource())
                bindings.Add(new UIBinding(UIBindingKind.Picture, provider.Id, IEffectProvider.OutputAnchorGUID, SourcePortKey: provider.GetFinalOutputFieldId()));

            foreach (var fieldBinding in provider.EnumerateFieldBindings())
            {
                if (TryParseBindingSource(fieldBinding.Value, out var sourceId, out var fieldOutputId) && GetNodeByBindingId(sourceId) is not null)
                    bindings.Add(new UIBinding(UIBindingKind.Value, sourceId, provider.Id, fieldBinding.Key, fieldOutputId));
            }
        }

        if (_inputNode is not null && _outputNode is not null && !providers.Values.Any(p => p.Stage == _stage && p.IsFinalOutputSource()))
            bindings.Add(new UIBinding(UIBindingKind.Picture, BindingIdOf(_inputNode), IEffectProvider.OutputAnchorGUID));
        return bindings;
    }

    public string GenerateUiBindingsMermaidDiagram()
    {
        var providers = _clip?.EffectProviders?.Values.Where(IsCurrentPipelineProvider).ToDictionary(p => p.Id) ?? [];
        var sources = new Dictionary<string, string>();
        foreach (var node in _nodes.Values.Where(n => n.Provider is null && n.Kind is NodeKind.Input or NodeKind.AudioSample))
            sources[BindingSourceId(BindingIdOf(node))] = node.DisplayName;
        return EffectGroupMermaid.SerializeDiagram(providers, _groups, EffectStageUI.Name(_stage), sources,
            _outputNode?.DisplayName, _inputNode is null ? null : BindingSourceId(BindingIdOf(_inputNode)));
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

    private async void ShowUiBindingsGraphButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            await ShowUiBindingsMermaidPopupAsync();
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Show effect bindings graph", this);
            SetStatusText(ex.Message);
        }
    }

    /// <summary>Validates provider-owned configuration and rematerializes runtime fields.</summary>
    private bool OnBindingConfigurationChanged()
    {
        _previewCts?.Cancel();
        if (_clip?.EffectProviders == null) return false;
        var providers = _clip.EffectProviders;
        if (!SynchronizeGroupBindings()) return false;
        _previewDiagnostics = [];
        ClipInfoBuilder.SaveTextStyleClipArguments(_clip);
        EffectBindingHelper.MaterializeFields(providers.Values);
        if (_stage == EffectStage.Composition) UpdateProviderNodes();
        else AddSystemNodes();
        UpdateSourcesAndOutputsPanel();
        _fieldTypes = null;
        if (!_availableStages.SequenceEqual(GetAvailableStages(_clip))) Reload();
        else RefreshFieldTypes();
        RecordEditHistory();
        return true;
    }

    private async void RefreshFieldTypes()
    {
        if (_clip?.EffectProviders is not { } providers) return;
        var owner = _clip;
        int version = ++_fieldTypeVersion;
        _fieldTypesCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _fieldTypesCts = cts;
        try
        {
            await Task.Delay(150, cts.Token);
            var types = await EffectFieldTypeResolution.ResolveAsync(providers, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_clip, owner) || version != _fieldTypeVersion) return;
            _fieldTypes = types;
            _previewDiagnostics = _previewDiagnostics.Where(d => d.Code != "FieldTypeResolutionFailure").ToArray();
            var diagnostics = GetAllBindingDiagnostics();
            _bindingDiagnostics = diagnostics;
            foreach (var node in _nodes.Values.Where(n => n.Provider is not null).ToArray())
            {
                string before = GetPortTypeSignature(node);
                node.FieldTypes = types;
                node.BuildPortsFromProvider();
                if (before != GetPortTypeSignature(node)) RecreateNodeView(node);
            }
            ApplyGroupProjection();
            RefreshBindingDiagnosticIndicators(diagnostics);
            RebuildConnections();
            ConnectionsLayer.Invalidate();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_clip, owner) || version != _fieldTypeVersion) return;
            Logger.Log(ex, "Resolve effect editor field types", this);
            var origin = EffectProviderFailure.Read(ex);
            _previewDiagnostics = [new(origin.ProviderId, "FieldTypeResolutionFailure", EffectProviderFailure.Describe(ex)) { FieldId = origin.FieldId }];
            RefreshBindingDiagnosticIndicators();
            SetStatusText(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_fieldTypesCts, cts)) _fieldTypesCts = null;
        }
    }

    private static string GetPortTypeSignature(NodeViewModel node) =>
        string.Join("|", node.InputPorts.Concat(node.OutputPorts).Concat(node.ParamPorts)
            .Select(p => $"{p.Kind}:{p.Id}:{p.Key}:{p.FieldType}:{p.DisplayName}"));

    public async Task CompleteEditingAsync()
    {
        if (_clip == null) return;
        _closing = true;
        CloseMermaidImport();
        CloseGroupGraph();
        UnsubscribeFromPageEvents();
        EndPortConnection();
        IsEnabled = false;
        await _providerAddTask;
        _fieldTypesCts?.Cancel();
        _fieldTypeVersion++;
        _previewCts?.Cancel();
        var clip = _clip;
        try
        {
            foreach (var node in _nodes.Values)
            {
                SetNodePosition(node, commit: true);
                if (node.TerminalField is null && node.Provider is { } provider)
                {
                    provider.MetaData[ProviderPositionKey("X")] = node.X;
                    provider.MetaData[ProviderPositionKey("Y")] = node.Y;
                }
            }
            SaveStageLayout();
            SaveViewTransform();
            EffectGroupHelper.Write(clip.ExtraData, _groups);
            await Task.Run(() => ClipInfoBuilder.RebuildAllEffects(clip));
            Log($"Committed effect editor changes for {clip.Id}.", "debug");
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
        EffectProvidersChanged?.Invoke();
    }

    /// <summary>
    /// 从 UI 层绑定列表派生连线渲染数据，并把每条连线的端点在当前节点集合中解析为可绘制坐标。
    /// 找不到端点（节点被移除等）的绑定直接跳过。
    /// </summary>
    private void RebuildConnections()
    {
        var connections = new List<GraphConnection>();

        foreach (var b in ReadBindings())
        {
            if (VisiblePortNode(b.Source) is not { } from) continue;
            string inputKey = b.Kind == UIBindingKind.Value ? b.TargetPortKey! : EffectProviderAnchorExtensions.InputKey;
            var terminal = _nodes.Values.FirstOrDefault(n => n.TerminalField is not null && n.TerminalFields.Contains(inputKey) && n.Provider?.Id == b.Target);
            if ((terminal ?? VisiblePortNode(b.Target)) is not { } to) continue;
            if (from == to || from.View?.IsVisible == false || to.View?.IsVisible == false) continue;
            int sourceIndex = from.Group is null
                ? b.SourcePortKey is null ? 0 : from.OutputPorts.FindIndex(p => p.Key == b.SourcePortKey)
                : from.OutputPorts.FindIndex(p => p.Id == b.Source && (b.SourcePortKey is null || p.Key == b.SourcePortKey));
            if (sourceIndex < 0 || sourceIndex >= from.OutputPorts.Count) continue;

            int inputIndex = to.InputPorts.FindIndex(p => p.Key == inputKey && (to.Group is null || p.Id == b.Target));
            var targetKind = PortKind.AnchorInput;
            if (inputIndex < 0)
            {
                inputIndex = to.ParamPorts.FindIndex(p => p.Key == inputKey && (to.Group is null || p.Id == b.Target));
                targetKind = PortKind.ParamBind;
            }
            if (inputIndex < 0) continue;
            var fieldType = b.Kind == UIBindingKind.Value ? PortOf(to, targetKind, inputIndex)!.FieldType : from.OutputPorts[sourceIndex].FieldType;
            bool dashed = b.Kind == UIBindingKind.Value && (to.TerminalField is null || to.Provider is not RenderOutputProvider)
                && (targetKind == PortKind.ParamBind
                    || fieldType.BaseType() is not (EffectArgumentFieldType.AudioSample or EffectArgumentFieldType.SizeAndPosition));
            connections.Add(new GraphConnection(GraphPort(from, PortKind.AnchorOutput, sourceIndex),
                GraphPort(to, targetKind, inputIndex), PortTypeHelper.GetTypeColor(fieldType), dashed, dashed ? 1.5 : 2.5));
        }
        Graph.SetConnections(connections);
    }
}
