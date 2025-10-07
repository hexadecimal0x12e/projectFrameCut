using Microsoft.Extensions.Logging;
using projectFrameCut.Shared;

#if ANDROID
using projectFrameCut.Render.AndroidOpenGL.Platforms.Android;

#endif
using System;
using System.Diagnostics;

namespace projectFrameCut
{
    public static class MauiProgram
    {
        static StreamWriter LogWriter;

        public static MauiApp CreateMauiApp()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.Combine(FileSystem.AppDataDirectory, "logging"));
                LogWriter = new StreamWriter(System.IO.Path.Combine(FileSystem.AppDataDirectory, "logging",$"log-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.log"), append: true)
                {
                    AutoFlush = true
                };
                //Console.SetOut(LogWriter);
                //Console.SetError(LogWriter);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set up log file: {ex.Message}");
            }
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

#if ANDROID
                MyLoggerExtensions.OnLog += (msg, level) =>
                {
                    switch (level.ToLower())
                    {
                        case "info":
                            Android.Util.Log.Info("projectFrameCut", msg);
                            break;
                        case "warning":
                        case "warn":
                            Android.Util.Log.Warn("projectFrameCut", msg);
                            break;
                        case "error":
                            Android.Util.Log.Error("projectFrameCut", msg);
                            break;
                        case "critical":
                            Android.Util.Log.Wtf("projectFrameCut", msg);
                            break;
                        default:
                            Android.Util.Log.Info($"projectFrameCut/{level}", msg);
                            break;
                    }
                };

#endif

                MyLoggerExtensions.OnLog += MyLoggerExtensions_OnLog;

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

        private static void MyLoggerExtensions_OnLog(string msg, string level)
        {
            LogWriter.WriteLine($"[{DateTime.Now:T} @ {level}] {msg}");
        }
    }

}


