using Microsoft.Extensions.Logging;
using projectFrameCut.Shared;

#if ANDROID
using projectFrameCut.Render.AndroidOpenGL.Platforms.Android;
using projectFrameCut.Platforms.Android;
using Java.Lang;

#endif
using System;
using System.Diagnostics;
using FFmpeg.AutoGen;
using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Native;
using Exception = System.Exception;

namespace projectFrameCut
{
    public static class MauiProgram
    {
        static StreamWriter LogWriter;

        public static MauiApp CreateMauiApp()
        {
            try
            {
                string loggingDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "logging");
#if ANDROID
                try
                {
                    var pfn = Android.App.Application.Context.PackageName;
                    var userAccessblePath = $"/sdcard/Android/data/{pfn}/";
                    if (Path.Exists(userAccessblePath))
                    {
                        loggingDir = System.IO.Path.Combine(userAccessblePath, "logging");
                    }

                }
                catch //use the default path (/data/data/...)           
                {
                    
                }


#elif IOS
                loggingDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "logging"); //files->my [iDevices]->projectFrameCut
                
#endif

                Directory.CreateDirectory(loggingDir);
                LogWriter = new StreamWriter(System.IO.Path.Combine(loggingDir, $"log-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.log"), append: true)
                {
                    AutoFlush = true
                };

                MyLoggerExtensions.OnLog += MyLoggerExtensions_OnLog;

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set up log file: {ex.Message}");
            }

            Log($"projectFrameCut - v{Assembly.GetExecutingAssembly().GetName().Version} on {DeviceInfo.Platform} in cpu arch {RuntimeInformation.ProcessArchitecture}, os version {Environment.OSVersion}, clr version {Environment.Version}, cmdline: {Environment.CommandLine} ");
            Log("Copright hexadecimal0x12e 2025, and thanks to other open-source code's author.");
            try
            {
                var builder = MauiApp.CreateBuilder();
                builder.UseMauiApp<App>();
#if DEBUG
                builder.Logging.SetMinimumLevel(LogLevel.Trace);
#else
                builder.Logging.SetMinimumLevel(LogLevel.Information);
#endif
                builder.Logging.AddProvider(new MyLoggerProvider());
                builder.ConfigureFonts(fonts =>
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

                try
                {
                    var nativeLibDir = Android.App.Application.Context.ApplicationInfo.NativeLibraryDir;
                    Log($"Native library dir: {nativeLibDir}");
                    ffmpeg.RootPath = nativeLibDir;
                    JavaSystem.LoadLibrary("c"); //准备加载库
                    FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
                    FFmpeg.AutoGen.DynamicallyLoadedBindings.Initialize();
                }
                catch (Exception ex)
                {
                    Log(ex);
                    //throw; 
                }

#endif

#if WINDOWS
                if (Directory.GetFiles(AppContext.BaseDirectory, "av*.dll").Length == 0)
                {
                    Log("ERROR: ffmpeg binaries not found. Please reinstall projectFrameCut.");
                    Environment.FailFast("ERROR: ffmpeg binaries not found. Please reinstall projectFrameCut.",new DllNotFoundException("ERROR: ffmpeg binaries not found. Please reinstall projectFrameCut."));
                }

                
#endif
                //try
                //{
                //    var ver = ffmpeg.av_version_info();
                //    Log($"Internal FFmpeg version:{ver}");
                //}
                //catch (PlatformNotSupportedException _)
                //{
                //    Log("Unknown internal ffmpeg version");
                //}
                Localized = SimpleLocalizer.Init();
                Log($"Localization initialized to {Localized._LocaleId_}, {Localized.WelcomeMessage}");
                Log("Everything ready!");
                var app = builder.Build();
                Log("App is ready!");
                return app;
            }
            catch (Exception ex)
            {
                Log($"Error creating MAUI App: {ex.Message}");
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



