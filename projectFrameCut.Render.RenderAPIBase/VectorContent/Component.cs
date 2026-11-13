using projectFrameCut.Drawing.Vector;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.RenderAPIBase.VectorContent
{
    public interface IVectorComponent : projectFrameCut.Render.RenderAPIBase.Plugins.IExtensibleObject
    {
        /// <summary>
        /// Indicates which plugin this component comes from.
        /// </summary>
        public new string FromPlugin { get; }

        /// <summary>
        /// Define the type name of the component. 
        /// </summary>
        public new string TypeName { get; }

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
        public new Guid Id { get; set; }

        /// <summary>
        /// Parameters of the component.
        /// </summary>
        public Dictionary<string, object> Parameters { get; }

        /// <summary>
        /// The layer index of the component in the component stack.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Compute the target <see cref="VectorCanvasElement"/> for this component based on its parameters and state.
        /// </summary>
        /// <returns>The computed <see cref="VectorCanvasElement"/>.</returns>
        public VectorCanvasElement Compute();

        /// <summary>
        /// Compute all target <see cref="VectorCanvasElement"/>s for this component.
        /// For simple components this returns a single element; for group components it returns the flattened children.
        /// </summary>
        /// <returns>The computed elements.</returns>
        public IEnumerable<VectorCanvasElement> ComputeAll()
        {
            var element = Compute();
            if (element is not null)
            {
                yield return element;
            }
        }
    }
}
