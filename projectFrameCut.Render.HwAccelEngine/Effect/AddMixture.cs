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
    public partial class AddMixture_HwAccel : AddMixture, IDisposable
    {
        public override EffectImplementType ImplementType => EffectImplementType.HwAcceleration;

        public override IEffect WithParameters(Dictionary<string, object> parameters) => new AddMixture_HwAccel
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
            ReleaseNativeResources();
        }

        public override IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                return base.Mix(basePicture, topPicture, targetPPB);
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "Execute AddMixture_HwAccel", "HwAccelEngine");
                throw;
            }
        }

        public override IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB, int topStartX, int topStartY, int targetWidth, int targetHeight)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                return base.Mix(basePicture, topPicture, targetPPB, topStartX, topStartY, targetWidth, targetHeight);
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "Execute AddMixture_HwAccel", "HwAccelEngine");
                throw;
            }
        }

        partial void ReleaseNativeResources();
    }
}
