using projectFrameCut.Render.AndroidOpenGL.Platforms.Android;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.AndroidOpenGL
{
    public static class ComputerHelper
    {
        public static Action<NativeGLSurfaceView>? AddGLViewHandler;

        public static void Init()
        {
            AcceleratedComputerBridge.RequireAComputer = new((name) =>
            {
                if(AddGLViewHandler is null)
                {
                    Logger.Log($"AddGLViewHandler is null.", "warn");
                    throw new NullReferenceException($"{nameof(AddGLViewHandler)} is null.");
                }

                switch (name)
                {                      
                    case "Overlay":
                        return new OverlayComputer();
                    case "RemoveColor":
                        return new RemoveColorComputer();
                    default:
                        Logger.Log($"Computer {name} not found.", "Error");
                        return null;

                }
            });
        }
    }
}
