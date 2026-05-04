namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface IMixture : IEffect
    {
        /// <summary>
        /// Mix the base picture and the top picture which in same size to produce a new picture.
        /// </summary>
        IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer? computer, IPicture.PicturePixelMode targetPPB);
        /// <summary>
        /// Mix the base picture and the top picture which in the specific position and size to produce a new picture 
        /// </summary>
        IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer? computer, IPicture.PicturePixelMode targetPPB, int topStartX, int topStartY, int targetWidth, int targetHeight);

        bool IEffect.Enabled { get => false; set => Logger.Log("Cannot enable a IMixture. It should be used within the render system.", "warn"); } 
        int IEffect.RelativeWidth { get => -1; set => Logger.Log("Cannot set RelativeWidth for a IMixture. This operation is ignored.", "warn"); }
        int IEffect.RelativeHeight { get => -1; set => Logger.Log("Cannot set RelativeHeight for a IMixture. This operation is ignored.", "warn"); }
        int IEffect.Index { get => int.MaxValue; set => Logger.Log("Cannot set Index for a IMixture. This operation is ignored.", "warn"); }
        bool IEffect.YieldProcessStep { get => false; }
        EffectImplementType IEffect.ImplementType => EffectImplementType.HwAcceleration;
        EffectType IEffect.TypeOfEffect => EffectType.MixtureProvider;
    }
}
