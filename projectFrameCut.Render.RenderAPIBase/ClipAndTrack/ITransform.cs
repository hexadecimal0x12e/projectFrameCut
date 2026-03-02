using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack
{
    public interface ITransform
    {
        /// <summary>
        /// Gets the ID of the plugin that provided this value.
        /// </summary>
        public string FromPlugin { get; }

        /// <summary>
        /// The type of this transform.
        /// </summary>
        public string TypeName { get; }

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
        /// Indicates whether this transform needs a specific computer with the computer which it's ID is <see cref="NeedComputer"/> to run.
        /// Or be null indicates this effect does not need a specific computer.
        /// </summary>
        [JsonIgnore]
        public string? NeedComputer { get; }


        /// <summary>
        /// Parameters of the transform.
        /// </summary>
        public Dictionary<string, object> Parameters { get; set; }

        /// <summary>
        /// Indicates which parameters are needed for this transform.
        /// </summary>
        [JsonIgnore]
        public List<string> ParametersNeeded { get; }
        /// <summary>
        /// Indicates the type of each parameter.
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, string> ParametersType { get; }

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
        /// It's pretty similar to <see cref="IEffect.Render(IPicture, IComputer?, int, int)"/>, but with 2 input.
        /// </remarks>
        /// <param name="progress">The progress of this render request. 0 for start and 1 for end.</param>
        public IPicture GetFrame(IPicture left, IPicture right, IComputer? computer, int targetWidth, int targetHeight);

    }
    public interface IOneInputSingleFrameTransform : ITransform
    {
        TransformType ITransform.TransformType => TransformType.SingleFrameTransform;

        /// <summary>
        /// Get the transform's frame at the specified progress. 
        /// </summary>
        /// <remarks>
        /// It's pretty similar to <see cref="IContinuousEffect.Render(IPicture, uint, IComputer?, int, int)"/>
        /// </remarks>
        /// <param name="progress">The progress of this render request. 0 for start and 1 for end.</param>
        public IPicture GetFrame(IPicture input, double progress, IComputer? computer, int targetWidth, int targetHeight);

    }
    public interface IContinuousTransform : ITransform
    {
        TransformType ITransform.TransformType => TransformType.ContinuousTransform;

        /// <summary>
        /// Get the transform's frame at the specified progress. 
        /// </summary>
        /// <remarks>
        /// It's pretty similar to <see cref="IContinuousEffect.Render(IPicture, uint, IComputer?, int, int)"/>
        /// </remarks>
        /// <param name="progress">The progress of this render request. 0 for start and 1 for end.</param>
        public IPicture GetFrame(IPicture left, IPicture right, double progress, IComputer? computer, int targetWidth, int targetHeight);

    }



}
