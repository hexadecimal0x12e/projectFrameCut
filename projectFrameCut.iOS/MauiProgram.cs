using Microsoft.Extensions.Logging;
using projectFrameCut.iDevicesAPI;
using projectFrameCut.Shared;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using Microsoft.Maui.LifecycleEvents;
#if IOS
using UIKit;
#endif


namespace projectFrameCut
{
    public static class MauiProgram
    {
        public static string DataPath { get; private set; }
        public static StreamWriter LogWriter { get; internal set; }

        public static MauiApp CreateMauiApp()
        {
#if WINDOWS
#elif IOS
            //files->my [iDevices]->projectFrameCut
            DataPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

#elif MACCATALYST
            DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"projectFrameCut");
#endif

            try
            {
                Directory.CreateDirectory(DataPath);
            }
            catch (Exception ex)
            {
                Log(ex, "create userdata dir", CreateMauiApp);
                DataPath = FileSystem.AppDataDirectory;
            }


            Log($"projectFrameCut - v{Assembly.GetExecutingAssembly().GetName().Version} \r\n" +
               $"                  on {DeviceInfo.Platform} in cpu arch {RuntimeInformation.ProcessArchitecture},\r\n" +
               $"                  os version {Environment.OSVersion},\r\n" +
               $"                  clr version {Environment.Version},\r\n" +
               $"                  cmdline: {Environment.CommandLine}");
            Log("Copyright (c) hexadecimal0x12e 2025, and thanks to other open-source code's authors. This project is licensed under GNU GPL V2.");
            Log($"DataPath:{DataPath}");
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
                fonts.AddFont("HarmonyOS_Sans_SC_Regular.ttf", "Font_Regular");
                fonts.AddFont("HarmonyOS_Sans_SC_Bold.ttf", "Font_Semibold");
            });

#if iDevices
            builder.Services.AddSingleton<IDeviceThermalService, DeviceThermalService>();
            builder.Services.AddSingleton<IDeviceMemoryPressureService, DeviceMemoryPressureService>();
#endif

            return builder.Build();
        }


    }
}
