using ObjCRuntime;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.Reflection.Metadata;
using UIKit;

namespace projectFrameCut.Platforms.iOS
{
    public class Program
    {
        private static string loggingDir;
        // This is the main entry point of the application.
        static void Main(string[] args)
        {

            loggingDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "logging");
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
                Crash(ex);
                throw;
            }
        }

        private static object locker = new();

        private static void MyLoggerExtensions_OnLog(string msg, string level)
        {
            lock (locker) MauiProgram.LogWriter.WriteLine($"[{DateTime.Now:T} @ {level}] {msg}");
        }

        internal static void Crash(Exception ex)
        {
            try
            {
                Log(ex, "Application", null);

            }
            finally
            {
                string innerExceptionInfo = "None";
                if (ex.InnerException != null)
                {
                    innerExceptionInfo =
$"""
Type: {ex.InnerException.GetType().Name}                        
Message: {ex.InnerException.Message}
StackTrace:
{ex.InnerException.StackTrace}

""";
                }


                var logMessage =
$"""
Sorry, the application has encountered an unhandled exception.
Your works have been saved automatically when you make any change on the UI, so you won't lose your work.
If you want to help the development of this application, please consider to submit an issue or send this report to me:

Exception type: {ex.GetType().Name}
Message: {ex.Message}
StackTrace:
{ex.StackTrace}

From:{(ex.TargetSite is not null ? ex.TargetSite.ToString() : "unknown")}
InnerException:
{innerExceptionInfo}

Exception data:
{string.Join("\r\n", ex.Data.Cast<System.Collections.DictionaryEntry>().Select(k => $"{k.Key} : {k.Value}"))}

Environment:
OS version: {Environment.OSVersion}
CLR Version:{Environment.Version}
Command line: {Environment.CommandLine}
Current directory: {Environment.CurrentDirectory}

(report ended here)
""";
                string logPath;

                try
                {
                    Directory.CreateDirectory(Path.Combine(loggingDir, "crashlog"));
                    logPath = Path.Combine(loggingDir, "crashlog\\", $"Crashlog-{DateTime.Now:yyyy-MM-dd-hh-mm-ss}.log");
                    File.WriteAllText(logPath, logMessage);
                }
                catch (Exception)
                {
                    logPath = Path.Combine(Directory.CreateTempSubdirectory("projectFrameCut_").FullName, "crash.log");
                    File.WriteAllText(logPath, logMessage);
                }
                Thread.Sleep(100);
            }
        }
    }
}
