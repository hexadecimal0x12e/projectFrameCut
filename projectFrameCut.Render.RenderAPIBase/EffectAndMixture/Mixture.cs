using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface IMixture
    {
        /// <summary>
        /// Indicates which plugin this mixture comes from.
        /// </summary>
        public string FromPlugin { get; }
        /// <summary>
        /// Indicate the type name of the mixture. 
        /// </summary>
        public string TypeName { get; }
        /// <summary>
        /// Indicates whether this mixer needs a specific computer with the computer which it's ID is <see cref="NeedComputer"/> to run.
        /// Or be null indicates this mixer does not need a specific computer.
        /// </summary>
        public string? ComputerId { get; }
        /// <summary>
        /// The arguments of the mixture.
        /// </summary>
        public Dictionary<string, object> Parameters { get; }

        /// <summary>
        /// Mix the top picture onto the base picture using this mixture.
        /// </summary>
        /// <param name="basePicture"></param>
        /// <param name="topPicture"></param>
        /// <param name="computer"></param>
        /// <returns>the mixed picture</returns>
        public IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer? computer);
    }
}
