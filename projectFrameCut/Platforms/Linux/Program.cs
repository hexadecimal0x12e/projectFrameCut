using Microsoft.Maui.Hosting;
using Microsoft.Maui.Platforms.Linux.Gtk4.Platform;
using projectFrameCut.AIAssistance;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.ScriptEngine;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace projectFrameCut.Platforms.Linux;

public class Program : GtkMauiApplication
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    public static string? BasicDataPathOverride { get; private set; } = null;
    public static string? UserDataPathOverride { get; private set; } = null;
    public static int Main(string[] args)
    {
        System.Threading.Thread.CurrentThread.Name = "App Main thread";

        if (!args.Contains("--quiet"))
        {
            Console.WriteLine($"{Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "projectFrameCut"} {Assembly.GetExecutingAssembly().GetName().Version}");
            Console.WriteLine($"Copyright (c) hexadecimal0x12e 2025-2026.");
        }
        if (args.Contains("--waitDebugger"))
        {
            Console.WriteLine("Waiting for debugger to attach...");
            while (!Debugger.IsAttached)
            {
                Thread.Sleep(100);
            }
            Debugger.Break();
        }
        if (args.Contains("--consoleLog"))
        {
            MyLoggerExtensions.OnLog += (msg, level) =>
            {
                Console.WriteLine($"[{level}] {msg}");
            };
        }
        if (args.Contains("--logDiagnostic"))
        {
            MyLoggerExtensions.LoggingDiagnosticInfo = true;
        }

        if (args.Any() && args.First() == "gui")
        {
            args = args.Skip(1).ToArray();
            Console.WriteLine($"Launching GUI with parameters: '{string.Join(" ", args)}', press Ctrl+C to exit.");
        }
        else
        {
            return CLIProgram.CLIMain(args);
        }
        if (args.Length > 0 && args[0].StartsWith("pjfc:", StringComparison.OrdinalIgnoreCase))
        {
            args = ParseProtocolArgs(args);
        }
        if (args.Any(c => c.StartsWith("--overrideCulture")))
        {
            var overrideCulture = args.First(c => c.StartsWith("--overrideCulture")).Split('=')[1];
            var culture = new System.Globalization.CultureInfo(overrideCulture);
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
            System.Threading.Thread.CurrentThread.CurrentCulture = culture;
            System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
            MauiProgram.NoOverrideCulture = true;
        }
        if (args.Any(c => c.StartsWith("--basicUserData")))
        {
            var userDataPath = args.First(c => c.StartsWith("--basicUserData")).Split('=', 2)[1];
            BasicDataPathOverride = userDataPath;
        }
        if (args.Any(c => c.StartsWith("--userData")))
        {
            var userDataPath = args.First(c => c.StartsWith("--userData")).Split('=', 2)[1];
            UserDataPathOverride = userDataPath;
        }
        if (args.FirstOrDefault(c => c.StartsWith("--scripting")) == "--scripting=enableWithHostingPipe")
        {
            Console.WriteLine($"WARNNING: Enable scripting with hosting pipe is a dangerous option, it it allows arbitrary code execution from a remote process.");
            Console.WriteLine($"Only use this option if you know what you are doing, and you trust everything on this computer.");
            Console.WriteLine($"Press Y to continue:");
            if (Console.ReadKey().Key != ConsoleKey.Y)
            {
                Console.WriteLine($"Aborted.");
                return 1;
            }
            Environment.SetEnvironmentVariable(
                "POWERSHELL_DISABLE_NAMED_PIPE",
                "false",
                EnvironmentVariableTarget.Process
            );
            Console.WriteLine($"Connect to the hosting pipe by using the PowerShell command: Enter-PSHostProcess -Id {Environment.ProcessId}");
        }
        else
        {
            if (args.FirstOrDefault(c => c.StartsWith("--scripting")) == "--scripting=disable")
            {
                ScriptCore.Enabled = false;
            }
            Environment.SetEnvironmentVariable(
                "POWERSHELL_DISABLE_NAMED_PIPE",
                "true",
                EnvironmentVariableTarget.Process
            );
        }
        MauiProgram.CmdlineArgs = args;
        try
        {
            var app = new Program();
#pragma warning disable CA1416 // can safely ignore this warning because this is a Linux-specific entry point
            app.Run(args.Where(c => c.StartsWith("gtkArg:")).Select(c => c.Substring("gtkArg:".Length)).ToArray()); // avoid GTK parses the command line arguments
#pragma warning restore CA1416
            Environment.Exit(0);
            return 0;
        }
        catch (Exception ex)
        {
            MauiProgram.Crash(ex);
            return 1;
        }
    }

    [DoesNotReturn]
    public static void Crash(Exception ex)
    {
        MauiProgram.LogWriter?.Flush();

        try
        {
            MauiProgram.LogWriter?.WriteLine("*** FATAL: CRASH");
            Log(ex, "", "Application");
            Log($"Crash() is invoked by:{Environment.StackTrace}.", "fatal");
            MauiProgram.LogWriter?.Flush();

        }
        catch (Exception) { }

#if DEBUG
        if (Debugger.IsAttached)
        {
            Debugger.BreakForUserUnhandledException(ex);
            Environment.Exit(ex.HResult);
        }
#endif


        string innerExceptionInfo = "None";
        if (ex.InnerException != null)
        {
            innerExceptionInfo =
$"""
ClipType: {ex.InnerException.GetType().Name}                        
Message: {ex.InnerException.Message}
StackTrace:
{ex.InnerException.StackTrace}

""";
        }

        string header =
"""
Sorry, the application has encountered an unhandled exception and needs to close now.
Your works have been saved automatically when you make any change on the UI, so you won't lose your work.
If you want to help the development of this application, please consider to submit an issue or send this report to me.
""";
        try
        {
            if (Localized is not null) header = Localized.AppCrashed;
        }
        catch { }
        string appInfo = $"Application: {Assembly.GetExecutingAssembly().GetName().FullName}";
        try
        {
            appInfo = Setting.SettingPages.DiagnosticSettingPage.GetAppInfo(false, false);
        }
        catch
        {
            appInfo =
$"""
{Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "unknown"}: {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown config"}@{new string((Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Skip(6).ToArray())}  
IPluginBase API: v{IPluginBase.CurrentPluginAPIVersion} | IApplicationPluginBase API: v{IApplicationPluginBase.CurrentAppLevelPluginAPIVersion}

OS version: {Environment.OSVersion}
CLR Version:{Environment.Version}
Command line: {Environment.CommandLine}
Current directory: {Environment.CurrentDirectory}
""";
        }
        var content =
$"""
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
{appInfo}

(report ended here)
""";
        string logPath, logMessage;

        try
        {
            Directory.CreateDirectory(Path.Combine(MauiProgram.DataPath, "Crashlogs"));
            logPath = Path.Combine(MauiProgram.DataPath, "Crashlogs", $"Crashlog-{DateTime.Now:yyyy-MM-dd-hh-mm-ss}.log");
            logMessage = $"{header}\r\nthis log is in: {logPath}\r\n\r\n{content}";
            File.WriteAllText(logPath, logMessage);
        }
        catch (Exception)
        {
            logPath = Path.Combine(Directory.CreateTempSubdirectory("projectFrameCut_").FullName, "crash.log");
            logMessage = $"{header}\r\nthis log is in: {logPath}\r\n\r\n{content}";
            File.WriteAllText(logPath, logMessage);
        }
        Thread.Sleep(100);

        Console.Error.WriteLine("********************");
        Console.Error.WriteLine("*** FATAL: CRASH ***");
        Console.Error.WriteLine("********************");
        Console.Error.WriteLine();
        Console.Error.WriteLine(logMessage);
        Environment.Exit(ex.HResult);

    }

    private static string[] ParseProtocolArgs(string[] args)
    {
        var protocolPayload = args[0].Substring(5);

        if (TryParseFileProtocolPayload(protocolPayload, out var filePath, out var queryArgs))
        {
            var parsedArgs = new List<string> { filePath };
            parsedArgs.AddRange(queryArgs);
            parsedArgs.AddRange(args.Skip(1));

            Log("Params read from protocol URI: " + string.Join(' ', parsedArgs.Select(c => $"'{c}'")));
            return parsedArgs.ToArray();
        }

        // Keep legacy behavior for protocol payloads like "pjfc:--log --noSplash".
        var fallbackArgs = protocolPayload
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Concat(args.Skip(1))
            .ToArray();
        Log("Params read from protocol: " + string.Join(' ', fallbackArgs.Select(c => $"'{c}'")));
        return fallbackArgs;
    }

    private static bool TryParseFileProtocolPayload(string payload, out string filePath, out string[] queryArgs)
    {
        filePath = string.Empty;
        queryArgs = Array.Empty<string>();

        if (!payload.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var queryStartIndex = payload.IndexOf('?');
        var fileUriText = queryStartIndex >= 0 ? payload[..queryStartIndex] : payload;
        var query = queryStartIndex >= 0 ? payload[(queryStartIndex + 1)..] : string.Empty;

        if (!Uri.TryCreate(fileUriText, UriKind.Absolute, out var fileUri) ||
            !string.Equals(fileUri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        filePath = fileUri.LocalPath;
        if (OperatingSystem.IsWindows() && filePath.Length >= 3 && filePath[0] == '/' && char.IsLetter(filePath[1]) && filePath[2] == ':')
        {
            filePath = filePath[1..];
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        queryArgs = ParseQueryArguments(query).ToArray();
        return true;
    }

    private static IEnumerable<string> ParseQueryArguments(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equalsIndex = segment.IndexOf('=');
            if (equalsIndex < 0)
            {
                var decodedSingle = Uri.UnescapeDataString(segment.Replace('+', ' '));
                if (!string.IsNullOrWhiteSpace(decodedSingle))
                {
                    yield return decodedSingle;
                }
                continue;
            }

            var keyPart = segment[..equalsIndex];
            if (string.IsNullOrWhiteSpace(keyPart))
            {
                continue;
            }

            var valuePart = segment[(equalsIndex + 1)..];
            var decodedKey = Uri.UnescapeDataString(keyPart.Replace('+', ' '));
            var decodedValue = Uri.UnescapeDataString(valuePart.Replace('+', ' '));
            yield return $"{decodedKey}={decodedValue}";
        }
    }
}
