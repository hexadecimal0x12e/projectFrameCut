using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.Render;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.ApplicationAPIBase.Project
{
    /// <summary>
    /// Represents a timeline clip element with both UI handles and render/source metadata.
    /// </summary>
    public interface IClipElementUI
    {
        /// <summary>
        /// Gets or sets the unique clip identifier.
        /// </summary>
        string Id { get; set; }
        [JsonIgnore]
        /// <summary>
        /// Gets or sets the main clip visual container.
        /// </summary>
        Border Clip { get; set; }
        [JsonIgnore]
        /// <summary>
        /// Gets or sets the left resize/transform handle.
        /// </summary>
        Border LeftHandle { get; set; }
        [JsonIgnore]
        /// <summary>
        /// Gets or sets the right resize/transform handle.
        /// </summary>
        Border RightHandle { get; set; }

        /// <summary>
        /// Gets or sets whether this clip should be presented in timeline UI.
        /// </summary>
        bool ShouldDisplayInUI { get; set; }
        /// <summary>
        /// Gets or sets the user-facing clip name.
        /// </summary>
        string DisplayName { get; set; }
        ClipMovingStatus MovingStatus { get; set; }
        double layoutX { get; set; }
        double layoutY { get; set; }
        double ghostLayoutX { get; set; }
        double ghostLayoutY { get; set; }
        double handleLayoutX { get; set; }
        double defaultY { get; set; }
        int? origTrack { get; set; }
        double origLength { get; set; }
        double origX { get; set; }
        uint lengthInFrame { get; set; }
        bool isInfiniteLength { get; set; }
        uint maxFrameCount { get; set; }
        uint relativeStartFrame { get; set; }
        float sourceSecondPerFrame { get; set; }
        float SecondPerFrameRatio { get; }
        ClipMode ClipType { get; set; }
        /// <summary>
        /// Gets or sets source plugin id for this clip.
        /// </summary>
        string FromPlugin { get; set; }
        /// <summary>
        /// Gets or sets concrete source/effect type name for plugin resolution.
        /// </summary>
        string TypeName { get; set; }
        string? SourcePath { get; set; }
        int TargetWidth { get; set; }
        int TargetHeight { get; set; }
        int TargetX { get; set; }
        int TargetY { get; set; }
        int SubLayerIndex { get; set; }
        int SubTrackIndex { get; set; }
        string? ClipColor { get; set; }
        /// <summary>
        /// Gets or sets effect instances attached to this clip.
        /// </summary>
        Dictionary<string, IEffect>? Effects { get; set; }
        /// <summary>
        /// Gets or sets grouped effect bundles attached to this clip.
        /// </summary>
        Dictionary<Guid, IEffectBundle>? EffectBundles { get; set; }
        /// <summary>
        /// Gets or sets extensible metadata for clip-specific custom options.
        /// </summary>
        Dictionary<string, object> ExtraData { get; set; }

        /// <summary>
        /// Applies the current playback speed ratio to visual width.
        /// </summary>
        void ApplySpeedRatio();
        /// <summary>
        /// Applies clip color override or fallback color by clip mode.
        /// </summary>
        void ApplyClipColor();
        /// <summary>
        /// Reads a boolean-like custom option from ExtraData.
        /// </summary>
        bool IsExtraDataOptionIsTrue(string option);
        /// <summary>
        /// Determines whether this clip should be active at the specified frame.
        /// </summary>
        bool IsClipFallInRange(uint targetFrame, IDraftPage workingPage);
    }

    public class ClipUpdateEventArgs : EventArgs
    {
        public ClipUpdateEventArgs() { }

        public static Func<ClipUpdateReason?, string?, string?, string?>? LocalizedChangeReasonBuilder { get; set; }

        public string? SourceId { get; set; }

        public string? SourceName { get; set; }

        public ClipUpdateReason? Reason { get; set; }

        public string? DetailInfo { get; set; }

        public bool NoSave { get; set; } = false;

        public static string BuildChangeReason(ClipUpdateReason? reason, string? sourceName = null, string? details = null)
        {
            try
            {
                var localized = LocalizedChangeReasonBuilder?.Invoke(reason, sourceName, details);
                if (!string.IsNullOrWhiteSpace(localized))
                {
                    return localized;
                }
            }
            catch
            {
                // Ignore localization callback failures and fallback to built-in text.
            }

            return reason switch
            {
                ClipUpdateReason.ClipItselfMove => $"Clip {sourceName} moved",
                ClipUpdateReason.ClipResized => $"Clip {sourceName} resized",
                ClipUpdateReason.TrackAdd => "Track added",
                ClipUpdateReason.ClipAdded => $"Clip {sourceName} added",
                ClipUpdateReason.ClipDeleted => $"Clip {sourceName} deleted",
                ClipUpdateReason.ClipPasted => $"Clip {sourceName} pasted",
                ClipUpdateReason.ClipGrouped => $"Clip {sourceName} grouped",
                ClipUpdateReason.ClipUngrouped => $"Clip {sourceName} ungrouped",
                ClipUpdateReason.PropertyChanged => $"Clip {sourceName} property changed: {details}",
                ClipUpdateReason.ClipPositionMoved => $"Clip {sourceName} position moved",
                ClipUpdateReason.Unknown or null => "Unknown clip change",
                _ => reason?.ToString() ?? "Unknown clip change"
            };
        }

        public override string ToString() => BuildChangeReason(Reason, SourceName, DetailInfo);
    }

    public enum ClipUpdateReason
    {
        Unknown,
        ClipItselfMove,
        ClipResized,
        TrackAdd,
        ClipAdded,
        ClipDeleted,
        ClipPasted,
        ClipGrouped,
        ClipUngrouped,
        PropertyChanged,
        ClipPositionMoved
    }

    public enum ClipMovingStatus
    {
        Free,
        Move,
        Resize
    }
}