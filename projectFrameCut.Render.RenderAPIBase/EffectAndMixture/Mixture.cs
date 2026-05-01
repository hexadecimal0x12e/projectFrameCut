namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface IMixture : IEffect
    {
        /// <summary>
        /// Mix the base picture and the top picture to produce a new picture. The mixture can use the provided computer to perform GPU calculation if needed. The targetPPB parameter indicates the desired pixel format for the output picture, and the Parameters dictionary can contain any additional parameters needed for the mixture calculation. The second Mix method allows specifying a region of the top picture to be mixed with the base picture, which can be useful for effects like cropping or applying a filter to a specific area.
        /// </summary>
        IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer? computer, IPicture.PicturePixelMode targetPPB);
        /// <summary>
        /// Mix the base picture and the top picture to produce a new picture. The mixture can use the provided computer to perform GPU calculation if needed. The targetPPB parameter indicates the desired pixel format for the output picture, and the Parameters dictionary can contain any additional parameters needed for the mixture calculation. The second Mix method allows specifying a region of the top picture to be mixed with the base picture, which can be useful for effects like cropping or applying a filter to a specific area.
        /// </summary>
        IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer? computer, IPicture.PicturePixelMode targetPPB, int topStartX, int topStartY, int targetWidth, int targetHeight);

        bool IEffect.Enabled { get => false; set => Logger.Log("Cannot enable a IMixture. It should be used within the render system.", "warn"); } 
        int IEffect.RelativeWidth { get => -1; set => Logger.Log("Cannot set RelativeWidth for a IMixture. This operation is ignored.", "warn"); }
        int IEffect.RelativeHeight { get => -1; set => Logger.Log("Cannot set RelativeHeight for a IMixture. This operation is ignored.", "warn"); }
        int IEffect.Index { get => int.MaxValue; set => Logger.Log("Cannot set Index for a IMixture. This operation is ignored.", "warn"); }
        bool IEffect.YieldProcessStep { get => false; }
        EffectImplementType IEffect.ImplementType => EffectImplementType.NotSpecified;
        EffectType IEffect.TypeOfEffect => EffectType.MixtureProvider;
    }
}
