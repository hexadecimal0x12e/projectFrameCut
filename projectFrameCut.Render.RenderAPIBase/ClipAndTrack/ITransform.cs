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
        /// The name of this clip. Mostly used for display purpose.
        /// </summary>
        public string Name { get; init; }

        /// <summary>
        /// Get the previous clip's ID. This is used for serialization and deserialization.
        /// </summary>
        /// <remarks>
        /// <b>DO NOT</b> set this property manually. It will be set when the transform is created.
        /// </remarks>
        public Guid PreviousClipId { get; init; }
        /// <summary>
        /// Get the next clip's ID. This is used for serialization and deserialization.
        /// </summary>
        /// <remarks>
        /// <b>DO NOT</b> set this property manually. It will be set when the transform is created.
        /// </remarks>
        public Guid NextClipId { get; init; }

        /// <summary>
        /// The previous clip.
        /// </summary>
        /// <remarks>
        /// <b>DO NOT</b> set this property manually. It will be set when the transform is initialized.
        /// </remarks>
        [JsonIgnore]
        public IClip? Previous { get; set; }
        /// <summary>
        /// The next clip.
        /// </summary>
        /// <remarks>
        /// <b>DO NOT</b> set this property manually. It will be set when the transform is initialized.
        /// </remarks>
        [JsonIgnore]
        public IClip? Next { get; set; }

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
        /// Get the transform's frame at the specified progress. 
        /// </summary>
        /// <remarks>
        /// It's pretty similar to <see cref="IContinuousEffect.Render(IPicture, uint, IComputer?, int, int)"/>
        /// </remarks>
        /// <param name="progress">The progress of this render request. 0 for start and 1 for end.</param>
        public IPicture GetFrame(double progress, IComputer? computer, int targetWidth, int targetHeight);

        public virtual void Init() { }

    }

}
