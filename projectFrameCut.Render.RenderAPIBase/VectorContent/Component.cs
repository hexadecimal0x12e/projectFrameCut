using projectFrameCut.Drawing.Vector;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.VectorContent
{
    public interface IVectorComponent
    {
        /// <summary>
        /// Indicates which plugin this component comes from.
        /// </summary>
        public string FromPlugin { get; }

        /// <summary>
        /// Define the type name of the component. 
        /// </summary>
        public string TypeName { get; }

        /// <summary>
        /// Indicates all animatable fields of this component, with their current values and animation tracks.
        /// </summary>
        public IReadOnlyDictionary<string, IAnimatableField> AnimatableFields { get; }

        /// <summary>
        /// Name of this component. Most for display purpose.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Get the ID of this specific component instance.
        /// </summary>
        /// <remarks>
        /// DO NOT set this property manually. It will be set when the component is created.
        /// </remarks>
        public Guid Id { get; set; }

        /// <summary>
        /// Parameters of the component.
        /// </summary>
        public Dictionary<string, object> Parameters { get; }

        /// <summary>
        /// The layer index of the component in the component stack.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// The animation frames for this component, defining how its properties change over time.
        /// </summary>
        public List<VectorAnimationKeyFrame> AnimationFrames { get; set; }

        /// <summary>
        /// Compute the target <see cref="VectorCanvasElement"/> for this component based on its parameters and state.
        /// </summary>
        /// <returns>The computed <see cref="VectorCanvasElement"/>.</returns>
        public VectorCanvasElement Compute(float index);
    }
}
