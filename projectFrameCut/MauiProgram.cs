using Microsoft.Extensions.Logging;
#if ANDROID
using projectFrameCut.Render.AndroidOpenGL.Platforms.Android;
#endif
using System;
using System.Diagnostics;

namespace projectFrameCut
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            Console.WriteLine("Creating MAUI App");
            Debug.WriteLine("Creating MAUI App");
            try
            {
                var builder = MauiApp.CreateBuilder();
                builder
                    .UseMauiApp<App>()
                    .ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_SC_Regular.ttf", "OpenSansRegular");
                        fonts.AddFont("HarmonyOS_Sans_SC_Bold.ttf", "OpenSansSemibold");
                    })
#if ANDROID
                    .ConfigureMauiHandlers(handlers =>
                    {

                        handlers.AddHandler<NativeGLSurfaceView, NativeGLSurfaceViewHandler>();
                    });
#else
; 
#endif


#if DEBUG
                builder.Logging.AddDebug();
                builder.Logging.SetMinimumLevel(LogLevel.Trace);
#endif

                var app = builder.Build();
                Debug.WriteLine("MAUI App built successfully");
                Localized = SimpleLocalizer.Init(); 
                Debug.WriteLine($"Localization initialized to {Localized._LocaleId_}");
                return app;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating MAUI App: {ex.Message}");
                Debug.WriteLine($"Error creating MAUI App: {ex.Message}");
                throw;
            }
        }
    }
}
