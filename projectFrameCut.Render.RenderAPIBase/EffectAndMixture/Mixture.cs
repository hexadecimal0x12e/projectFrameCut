using projectFrameCut.Drawing.Base;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public abstract class MixtureProviderBase : EffectProviderBase
    {
        public const string UpperInputKey = "__Upper__";
        public const string LowerInputKey = "__Lower__";

        protected sealed override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() =>
            new Dictionary<string, EffectArgumentFieldDescriptor>
            {
                [UpperInputKey] = Field(UpperInputKey, EffectArgumentFieldType.IPicture | EffectArgumentFieldType.Mandatory, ""),
                [LowerInputKey] = Field(LowerInputKey, EffectArgumentFieldType.IPicture | EffectArgumentFieldType.Mandatory, ""),
            };

        protected sealed override EffectArgumentFieldDescriptor DefineOutField() =>
            Field(OutputAnchorKey, EffectArgumentFieldType.IPicture, "");
    }

    public interface IMixture : IEffect
    {
        /// <summary>
        /// Mix the base picture and the top picture which in same size to produce a new picture.
        /// </summary>
        IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB);
        /// <summary>
        /// Mix the base picture and the top picture which in the specific position and size to produce a new picture 
        /// </summary>
        IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB, int topStartX, int topStartY, int targetWidth, int targetHeight);

        bool IEffect.Enabled { get => false; set { } }
        int IEffect.RelativeWidth { get => -1; set { } }
        int IEffect.RelativeHeight { get => -1; set { } }
        int IEffect.Index { get => int.MaxValue; set { } }
        EffectType IEffect.TypeOfEffect => EffectType.MixtureProvider;
    }
}
