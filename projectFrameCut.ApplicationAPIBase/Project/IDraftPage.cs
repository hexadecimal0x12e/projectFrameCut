using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.Project;
using System.Collections.Concurrent;

namespace projectFrameCut.ApplicationAPIBase.Project
{
    /// <summary>
    /// Exposes timeline editing operations and host page capabilities to application plugins.
    /// Provides the contract for the draft/project editing surface, including clip management,
    /// track manipulation, state persistence, and UI interaction.
    /// </summary>
    public interface IDraftPage : IContentView, Microsoft.Maui.Controls.ILayout, IPageController, IElementConfiguration<Page>, IPaddingElement, IView, ITitledElement, IToolbarElement, ISafeAreaView
    {
        /// <summary>
        /// Gets the main multi-window host used by the draft editor.
        /// </summary>
        public MultiWindowView MainMultiWindowView { get; }

        /// <summary>
        /// Gets current playhead frame position.
        /// </summary>
        double CurrentFrame { get; }

        /// <summary>
        /// Gets or sets the current snapshot ID in the save slot linked list.
        /// </summary>
        Guid CurrentSnapshotID { get; set; }

        /// <summary>
        /// Gets or sets current project metadata including timeline structure and clip configurations.
        /// </summary>
        ProjectJSONStructure ProjectInfo { get; set; }

        /// <summary>
        /// Gets a read-only dictionary of all clip UI elements keyed by their unique identifiers.
        /// </summary>
        IReadOnlyDictionary<string, IClipElementUI> AllClips { get; }

        /// <summary>
        /// Gets or sets the number of seconds represented by each timeline frame.
        /// </summary>
        double SecondsPerFrame { get; set; }

        /// <summary>
        /// Gets whether any clip is currently selected in the timeline.
        /// </summary>
        bool SelectedAnyClip { get; }

        /// <summary>
        /// Gets the currently selected clip if any; otherwise null.
        /// </summary>
        IClipElementUI? SelectedClip { get; }

        /// <summary>
        /// Occurs when the selected clip changes.
        /// </summary>
        public event EventHandler? SelectedClipChanged;

        /// <summary>
        /// Gets whether to use a compact layout for the editor when possible.
        /// </summary>
        bool UnNullUseCompactLayout { get; }

        /// <summary>
        /// Gets or sets the working directory path used for draft file storage.
        /// </summary>
        string WorkingPath { get; set; }

        /// <summary>
        /// Gets the task system's collection of running draft tasks keyed by name.
        /// </summary>
        ConcurrentDictionary<string, DraftTasks> RunningTasks { get; }

        /// <summary>
        /// Adds a prepared clip instance into the timeline visuals at the appropriate track position.
        /// </summary>
        /// <param name="c">The clip element UI to add.</param>
        void AddAClip(IClipElementUI c);

        /// <summary>
        /// Adds a new sub-track to the specified track in the timeline.
        /// </summary>
        /// <param name="trackId">The identifier of the parent track.</param>
        void AddASubTrack(int trackId);

        /// <summary>
        /// Adds a new top-level track to the timeline.
        /// </summary>
        /// <param name="trackId">The identifier for the new track.</param>
        void AddATrack(int trackId);

        /// <summary>
        /// Creates and inserts a transform clip between the selected clip and a neighbor.
        /// </summary>
        /// <param name="transformFactory">Factory function that creates the transform instance.</param>
        /// <param name="center">The center clip around which the transform is placed.</param>
        /// <param name="left">Whether the transform connects to the left neighbor.</param>
        /// <param name="right">Whether the transform connects to the right neighbor.</param>
        /// <param name="elementSetter">Optional action to configure the newly created clip element.</param>
        /// <returns>True if the transform was successfully added; otherwise false.</returns>
        bool AddTransformBetweenSelected(Func<Guid, Guid, Render.RenderAPIBase.ClipAndTrack.ITransform> transformFactory, IClipElementUI center, bool left, bool right, Action<IClipElementUI>? elementSetter = null);

        /// <summary>
        /// Applies the state from the specified snapshot slot to the current draft.
        /// </summary>
        /// <param name="snapshotId">The snapshot identifier to restore from.</param>
        void ApplySlot(Guid snapshotId);

        /// <summary>
        /// Starts interactive clip placement mode, allowing the user to click on a track to place a new clip.
        /// </summary>
        /// <param name="clipFactory">Factory function that creates a clip at the specified track and position.</param>
        /// <param name="trackFilter">Optional predicate to restrict which tracks accept placement.</param>
        /// <param name="name">Optional display name for the placement mode.</param>
        void BeginClipPlacement(Func<int, double, IClipElementUI> clipFactory, Predicate<int>? trackFilter = null, string? name = null);

        /// <summary>
        /// Cancels any pending clip placement operation and restores normal interaction state.
        /// </summary>
        /// <param name="statusText">Optional status text to display upon cancellation.</param>
        /// <param name="restoreKeyboardPreview">Whether to restore keyboard preview state.</param>
        void CancelPendingClipPlacement(string? statusText = null, bool restoreKeyboardPreview = true);

        /// <summary>
        /// Creates a clip, registers it with the draft system, and adds it to the track UI.
        /// </summary>
        /// <param name="startX">The starting X position of the clip in pixels.</param>
        /// <param name="width">The width of the clip in pixels.</param>
        /// <param name="trackIndex">The index of the target track.</param>
        /// <param name="id">Optional unique identifier for the clip.</param>
        /// <param name="labelText">Optional label text displayed on the clip.</param>
        /// <param name="background">Optional background brush for the clip.</param>
        /// <param name="prototype">Optional border prototype for styling.</param>
        /// <param name="resolveOverlap">Whether to automatically resolve overlapping clips.</param>
        /// <param name="relativeStart">The relative start frame within the source.</param>
        /// <param name="maxFrames">The maximum number of frames for the clip.</param>
        /// <param name="sourceElement">Optional source clip element to copy from.</param>
        /// <returns>The newly created clip element UI.</returns>
        IClipElementUI CreateAndAddClip(double startX, double width, int trackIndex, string? id = null, string? labelText = null, Brush? background = null, Border? prototype = null, bool resolveOverlap = true, uint relativeStart = 0, uint maxFrames = 0, IClipElementUI? sourceElement = null);

        /// <summary>
        /// Creates a clip from the specified asset and adds it to the track at a calculated position.
        /// </summary>
        /// <param name="asset">The asset item to create a clip from.</param>
        /// <param name="trackIndex">The index of the target track.</param>
        /// <param name="fromPlugin">The plugin type name used to render this clip.</param>
        /// <param name="path">Optional file path override for the asset source.</param>
        /// <returns>The newly created clip element UI.</returns>
        IClipElementUI CreateFromAsset(AssetItem asset, int trackIndex, string fromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase", string? path = null);

        /// <summary>
        /// Creates a clip from the specified asset and adds it to the track at the specified position.
        /// </summary>
        /// <param name="asset">The asset item to create a clip from.</param>
        /// <param name="trackIndex">The index of the target track.</param>
        /// <param name="startX">The starting X position of the clip in pixels.</param>
        /// <param name="fromPlugin">The plugin type name used to render this clip.</param>
        /// <param name="path">Optional file path override for the asset source.</param>
        /// <returns>The newly created clip element UI.</returns>
        IClipElementUI CreateFromAsset(AssetItem asset, int trackIndex, double startX, string fromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase", string? path = null);

        /// <summary>
        /// Finds direct adjacent clips for the specified clip on the same track.
        /// </summary>
        /// <param name="clip">The clip to find neighbors for.</param>
        /// <returns>A tuple containing the left and right neighbor clips, if any.</returns>
        (IClipElementUI? left, IClipElementUI? right) FindNeighbors(IClipElementUI? clip);

        /// <summary>
        /// Converts a frame number to its corresponding pixel position on the timeline.
        /// </summary>
        /// <param name="f">The frame number to convert.</param>
        /// <returns>The pixel position on the timeline.</returns>
        double FrameToPixel(uint f);

        /// <summary>
        /// Hides any currently displayed popup in the editor.
        /// </summary>
        /// <param name="force">Whether to force hide the popup even if it is currently pinned.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task HidePopup(bool force = false);

        /// <summary>
        /// Called when clip properties are changed through the property panel.
        /// </summary>
        /// <param name="sender">The source of the property change event.</param>
        /// <param name="e">The property panel event data.</param>
        void OnClipPropertiesChanged(object? sender, PropertyPanelPropertyChangedEventArgs e);

        /// <summary>
        /// Converts a pixel position on the timeline to its corresponding frame number.
        /// </summary>
        /// <param name="px">The pixel position to convert.</param>
        /// <returns>The frame number at the specified pixel position.</returns>
        uint PixelToFrame(double px);

        /// <summary>
        /// Rebuilds or updates the property panel for the specified clip.
        /// </summary>
        /// <param name="clip">The clip whose property panel should be refreshed.</param>
        void RefreshPropertyPanel(IClipElementUI clip);

        /// <summary>
        /// Registers interaction handlers for the clip and optionally resolves overlap conflicts.
        /// </summary>
        /// <param name="element">The clip element to register.</param>
        /// <param name="resolveOverlap">Whether to resolve overlapping clips after registration.</param>
        void RegisterClip(IClipElementUI element, bool resolveOverlap);

        /// <summary>
        /// Persists the current draft state to the history slot and project files.
        /// </summary>
        /// <param name="noSlot">Whether to skip saving to a history slot.</param>
        /// <param name="args">Optional clip update event arguments describing what changed.</param>
        /// <returns>A task representing the asynchronous save operation.</returns>
        Task Save(bool noSlot = false, ClipUpdateEventArgs? args = null);

        /// <summary>
        /// Sets the editor state to busy with a default status message.
        /// </summary>
        void SetStateBusy();

        /// <summary>
        /// Sets the editor state to busy with the specified status text.
        /// </summary>
        /// <param name="text">The status text to display.</param>
        void SetStateBusy(string text);

        /// <summary>
        /// Sets the editor state to failed with a default error message.
        /// </summary>
        void SetStateFail();

        /// <summary>
        /// Sets the editor state to OK (normal) with a default status message.
        /// </summary>
        void SetStateOK();

        /// <summary>
        /// Sets the editor state to OK (normal) with the specified status text.
        /// </summary>
        /// <param name="text">The status text to display.</param>
        void SetStateOK(string text);

        /// <summary>
        /// Sets transient status text shown in the editor UI without changing the editor state.
        /// </summary>
        /// <param name="text">The status text to display.</param>
        void SetStatusText(string text);

        /// <summary>
        /// Shows a popup for clip properties or custom views over the editor surface.
        /// </summary>
        /// <param name="content">Optional view content to display in the popup.</param>
        /// <param name="border">Optional anchor view for clip-mode popup positioning.</param>
        /// <param name="clip">Optional clip associated with the popup.</param>
        /// <param name="mode">The popup display mode identifier.</param>
        /// <returns>A task representing the asynchronous popup operation.</returns>
        Task ShowAPopup(View? content = null, View? border = null, IClipElementUI? clip = null, string mode = "");
    }
}
