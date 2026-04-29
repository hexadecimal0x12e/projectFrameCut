using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.Project;
using System.Collections.Concurrent;

namespace projectFrameCut.ApplicationAPIBase.Project
{
    /// <summary>
    /// Exposes timeline editing operations and host page capabilities to application plugins.
    /// </summary>
    public interface IDraftPage : IContentView, Microsoft.Maui.Controls.ILayout, IPageController, IElementConfiguration<Page>, IPaddingElement, IView, ITitledElement, IToolbarElement, ISafeAreaView
    {
        /// <summary>
        /// Gets the main multi-window host used by the draft editor.
        /// </summary>
        public MultiWindowView MainMultiWindowView { get; }

        /// <summary>
        /// Gets current playhead frame.
        /// </summary>
        double CurrentFrame { get; }
        /// <summary>
        /// Gets or sets the current snapshot ID in the save slot linked list.
        /// </summary>
        Guid CurrentSnapshotID { get; set; }
        /// <summary>
        /// Gets or sets current project metadata.
        /// </summary>
        ProjectJSONStructure ProjectInfo { get; set; }
        double SecondsPerFrame { get; set; }
        bool SelectedAnyClip { get; }
        /// <summary>
        /// Gets currently selected clip if any.
        /// </summary>
        IClipElementUI? SelectedClip { get; }
        public event EventHandler? SelectedClipChanged;
        bool UnNullUseCompactLayout { get; }
        string WorkingPath { get; set; }

        /// <summary>
        /// Get the task system's task list.
        /// </summary>
        ConcurrentDictionary<string, DraftTasks> RunningTasks { get; }

        /// <summary>
        /// Adds a prepared clip instance into timeline visuals.
        /// </summary>
        void AddAClip(IClipElementUI c);
        void AddASubTrack(int trackId);
        void AddATrack(int trackId);
        /// <summary>
        /// Creates and inserts a transform clip between the selected clip and a neighbor.
        /// </summary>
        bool AddTransformBetweenSelected(Func<Guid, Guid, Render.RenderAPIBase.ClipAndTrack.ITransform> transformFactory, IClipElementUI center, bool left, bool right, Action<IClipElementUI>? elementSetter = null);
        void ApplySlot(Guid snapshotId);
        /// <summary>
        /// Starts interactive clip placement mode.
        /// </summary>
        void BeginClipPlacement(Func<int, double, IClipElementUI> clipFactory, Predicate<int>? trackFilter = null, string? name = null);
        void CancelPendingClipPlacement(string? statusText = null, bool restoreKeyboardPreview = true);
        /// <summary>
        /// Creates a clip, registers it, and adds it to track UI.
        /// </summary>
        IClipElementUI CreateAndAddClip(double startX, double width, int trackIndex, string? id = null, string? labelText = null, Brush? background = null, Border? prototype = null, bool resolveOverlap = true, uint relativeStart = 0, uint maxFrames = 0, IClipElementUI? sourceElement = null);
        IClipElementUI CreateFromAsset(AssetItem asset, int trackIndex, string fromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase", string? path = null);
        IClipElementUI CreateFromAsset(AssetItem asset, int trackIndex, double startX, string fromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase", string? path = null);
        /// <summary>
        /// Finds direct adjacent clips for the specified clip on the same track.
        /// </summary>
        (IClipElementUI? left, IClipElementUI? right) FindNeighbors(IClipElementUI? clip);
        double FrameToPixel(uint f);
        Task HidePopup(bool force = false);
        void OnClipPropertiesChanged(object? sender, PropertyPanelPropertyChangedEventArgs e);
        uint PixelToFrame(double px);
        /// <summary>
        /// Rebuilds or updates property panel for a clip.
        /// </summary>
        void RefreshPropertyPanel(IClipElementUI clip);
        /// <summary>
        /// Registers interaction handlers for the clip and resolves overlap if requested.
        /// </summary>
        void RegisterClip(IClipElementUI element, bool resolveOverlap);
        /// <summary>
        /// Persists draft state to history slot and project files.
        /// </summary>
        Task Save(bool noSlot = false, ClipUpdateEventArgs? args = null);
        void SetStateBusy();
        void SetStateBusy(string text);
        void SetStateFail();
        void SetStateOK();
        void SetStateOK(string text);
        /// <summary>
        /// Sets transient status text shown in the editor UI.
        /// </summary>
        void SetStatusText(string text);
        /// <summary>
        /// Shows popup content for clip properties or custom views.
        /// </summary>
        Task ShowAPopup(View? content = null, Border? border = null, IClipElementUI? clip = null, string mode = "");
    }
}