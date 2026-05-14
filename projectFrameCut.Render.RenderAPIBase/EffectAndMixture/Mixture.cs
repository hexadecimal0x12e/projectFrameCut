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

        bool IEffect.Enabled { get => false; set { } }
        int IEffect.RelativeWidth { get => -1; set { } }
        int IEffect.RelativeHeight { get => -1; set { } }
        int IEffect.Index { get => int.MaxValue; set { } }
        bool IEffect.YieldProcessStep { get => false; }
        EffectImplementType IEffect.ImplementType => EffectImplementType.HwAcceleration;
        EffectType IEffect.TypeOfEffect => EffectType.MixtureProvider;
    }
}
