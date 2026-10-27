using projectFrameCut.Drawing.Base;
using projectFrameCut.Shared;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using static projectFrameCut.Shared.Logger;
using System.Diagnostics;

namespace projectFrameCut.Render.HwAccelEngine.Effect
{
    public partial class DifferenceMixture_HwAccel : DifferenceMixture, IDisposable
    {
        public override EffectImplementType ImplementType => EffectImplementType.HwAcceleration;

        public override IEffect WithParameters(Dictionary<string, object> parameters) => new DifferenceMixture_HwAccel
        {
            Parameters = parameters
        };
        private readonly Lock nativeLock = new();
        private bool disposed;
        public void Dispose()
        {
            using var scope = nativeLock.EnterScope();
            if (disposed)
                return;
            disposed = true;
        }

        public override IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB) => ResidentEffectResult.Mix(this, basePicture, topPicture, targetPPB);
        public override IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB, int topStartX, int topStartY, int targetWidth, int targetHeight) => ResidentEffectResult.Download(EffectPictureProcessing.Mix(this, basePicture, topPicture, targetPPB, topStartX, topStartY, targetWidth, targetHeight));
    }
}
