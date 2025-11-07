using ObjCRuntime;
using projectFrameCut.Shared;
using System.Reflection.Metadata;
using UIKit;

namespace projectFrameCut.iOS
{
    public class Program
    {
        // This is the main entry point of the application.
        static void Main(string[] args)
        {

            var loggingDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "logging");
#if IOS
            //files->my [iDevices]->projectFrameCut
            loggingDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "logging");

#elif MACCATALYST
            loggingDir = Path.Combine(FileSystem.AppDataDirectory, "logging"); // ~/Library/Containers/<bundle>/Data/Library/Application Support/<bundle>）
#endif
            try
            {
                Directory.CreateDirectory(loggingDir);
                
                MauiProgram.LogWriter = new StreamWriter(System.IO.Path.Combine(loggingDir, $"log-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.log"), append: true)
                {
                    AutoFlush = true
                };

                MyLoggerExtensions.OnLog += MyLoggerExtensions_OnLog;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create log file in {loggingDir}: {ex}");
            }

            try
            {
                UIApplication.Main(args, null, typeof(AppDelegate));
            } 
            catch (Exception ex)
            {
                Log("FATAL: Unhandled exception in Main", "Fatal");
                Log(ex, "An unhandled exception occurred inside the application.", typeof(AppDelegate));
                throw;
            }
        }

        private static object locker = new();

        private static void MyLoggerExtensions_OnLog(string msg, string level)
        {
            lock (locker) MauiProgram.LogWriter.WriteLine($"[{DateTime.Now:T} @ {level}] {msg}");
        }
    }
}
