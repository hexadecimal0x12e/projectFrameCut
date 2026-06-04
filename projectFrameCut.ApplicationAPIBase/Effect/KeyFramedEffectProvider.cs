using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.ApplicationAPIBase.Effect
{
    /// <summary>
    /// Describes a single step in a keyframed effect's step list.
    /// </summary>
    public record struct KeyFrameStepInfo(double Progress, string DisplayLabel);

    public interface IKeyFramedEffectProvider
    {
        /// <summary>
        /// The TypeName of the IKeyFramedEffectProvider.
        /// </summary>
        /// <remarks>
        /// it SHOULD equals to <see cref="IEffect.TypeName"/>, <see cref="IEffectFactory.TypeName"/> and so on.
        /// </remarks>
        public string TypeName { get; }

        /// <summary>
        /// Indicate which plugin this effect comes from, which is used to determine which plugin to use when creating the IKeyFramedEffectProvider.
        /// </summary>
        public string FromPlugin { get; }

        public Dictionary<string, object> Parameters { get; }

        /// <summary>
        /// Get the descriptors of all keyframe steps, ordered by progress.
        /// </summary>
        IReadOnlyList<KeyFrameStepInfo> Steps { get; }

        /// <summary>
        /// Create a PropertyPanelBuilder for editing the keyframe step at the given index.
        /// </summary>
        PropertyPanelBuilder CreateStepUI(int index);

        /// <summary>
        /// Handle a property panel change event from a step's UI.
        /// Returns true if the change was handled and the keyframe list should be rebuilt.
        /// </summary>
        bool HandleStepUIChange(int index, PropertyPanelPropertyChangedEventArgs args);

        /// <summary>
        /// Add a new keyframe step with a default position derived from the given context.
        /// </summary>
        void AddStep(ClipPositionTuple defaultPosition);

        /// <summary>
        /// Remove the keyframe step at the given index.
        /// </summary>
        void RemoveStep(int index);

        /// <summary>
        /// Insert or update a keyframe at the given progress with the specified position.
        /// If a keyframe at the same progress already exists, it is replaced; otherwise a new one is added.
        /// </summary>
        void UpsertStep(double progress, ClipPositionTuple position);
    }
}
