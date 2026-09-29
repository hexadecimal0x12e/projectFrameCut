using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack
{
    public interface ITransform : projectFrameCut.Render.RenderAPIBase.Plugins.IExtensibleObject
    {
        /// <summary>
        /// Gets the ID of the plugin that provided this value.
        /// </summary>
        public new string FromPlugin { get; }

        /// <summary>
        /// The type of this transform.
        /// </summary>
        public new string TypeName { get; }

        /// <summary>
        /// Get which kind of ITransform is.
        /// </summary>
        public TransformType TransformType { get; }

        /// <summary>
        /// The name of this clip. Mostly used for display purpose.
        /// </summary>
        public string Name { get; init; }

        public Guid BindedLeftClip { get; set; }
        public Guid BindedRightClip { get; set; }

        /// <summary>
        /// The duration of this transform. 
        /// </summary>
        public uint Duration { get; set; }

        /// <summary>
        /// Override this method to do some init jobs before use.
        /// </summary>
        public virtual void Init() { }

    }

    public interface ISingleFrameTransform : ITransform
    {
        TransformType ITransform.TransformType => TransformType.SingleFrameTransform;

        /// <summary>
        /// Get the transform's frame at the specified progress. 
        /// </summary>
        /// <remarks>
        /// It's similar to an effect render operation, but with two inputs.
        /// </remarks>
        /// <param name="progress">The progress of this render request. 0 for start and 1 for end.</param>
        public IPicture GetFrame(IPicture left, IPicture right, int targetWidth, int targetHeight);

    }
    public interface IOneInputSingleFrameTransform : ITransform
    {
        TransformType ITransform.TransformType => TransformType.SingleFrameTransform;

        /// <summary>
        /// Get the transform's frame at the specified progress. 
        /// </summary>
        /// <remarks>
        /// It's similar to a continuous effect render operation.
        /// </remarks>
        /// <param name="progress">The progress of this render request. 0 for start and 1 for end.</param>
        public IPicture GetFrame(IPicture input, double progress, int targetWidth, int targetHeight);

    }
    public interface IContinuousTransform : ITransform
    {
        TransformType ITransform.TransformType => TransformType.ContinuousTransform;

        /// <summary>
        /// Get the transform's frame at the specified progress. 
        /// </summary>
        /// <remarks>
        /// It's similar to a continuous effect render operation.
        /// </remarks>
        /// <param name="progress">The progress of this render request. 0 for start and 1 for end.</param>
        public IPicture GetFrame(IPicture left, IPicture right, double progress, int targetWidth, int targetHeight);

    }



}
