using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace projectFrameCut.WinUI
{
    /// <summary>
    /// Entry point for commands exposed by pjfc-cli.
    /// Keep the option descriptions in this file in sync with Program and HomePage.
    /// </summary>
    public static class CLIProgram
    {
        private const int SuccessExitCode = 0;
        private const int InvalidCommandExitCode = 2;

        public static int Main(string[] args)
        {
            args ??= Array.Empty<string>();

            if (args.Length == 0 || IsHelpOption(args[0]))
            {
                if (args.Length > 1)
                {
                    return WriteCommandHelp(args[1]);
                }

                WriteGeneralHelp();
                return SuccessExitCode;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "about":
                    WriteAbout();
                    return 0;
            }

            Console.Error.WriteLine($"Unknown command: {args[0]}");
            Console.Error.WriteLine("Run 'pjfc-cli help' to see the available commands.");
            return InvalidCommandExitCode;
        }

        private static int WriteCommandHelp(string command)
        {
            if (command.Equals("gui", StringComparison.OrdinalIgnoreCase))
            {
                WriteGuiHelp();
                return SuccessExitCode;
            }

            Console.Error.WriteLine($"No help topic found for '{command}'.");
            Console.Error.WriteLine("Run 'pjfc-cli help' to see the available topics.");
            return InvalidCommandExitCode;
        }

        private static bool IsHelpOption(string value) =>
            value.Equals("help", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/?", StringComparison.OrdinalIgnoreCase);

        private static void WriteGeneralHelp()
        {
            Console.WriteLine(
@"projectFrameCut command-line interface

Usage:
  pjfc-cli <command> [arguments] [options]
  pjfc-cli help [command]

Commands:
  gui       Launch the projectFrameCut graphical interface.
  tui       Start the projectFrameCut text-based interface (TUI) in the console.
  help      Show general help or detailed help for a command.
  reset     Reset the application to its default state by clearing settings.
  about     Show version and build information.

Global options:
  --elevated  Launch the CLI with elevated privileges (admin rights).
              Note that the main application will cannot be run with 
              elevated even if the GUI is launched from the CLI with this option.
  --quiet     Suppress the pjfc-cli version banner and copyright notice. 
              This option does not affect the GUI or other commands.

Help options:
  -h, --help, /?    Show this help text.
");
        }

        private static void WriteGuiHelp()
        {
            Console.WriteLine(
@"

Launch the projectFrameCut Application UI

Usage:
  pjfc-cli gui [<target>] [options]
  pjfc:[<target>][?<option>[&<option>...]] 

Arguments:
  <target>                         Item to open after the GUI starts. Supported
                                   targets are a .pjfc project/package, a project
                                   directory, or a .pjfcPlugin package. Quote paths
                                   containing spaces.

Launch options:
  --continue                       Open the last project when <target> is omitted.
                                   An explicit target takes precedence.

  --noSplash                       Do not display the startup splash screen.

  --overrideCulture=<culture>      Override the application culture for this run.
                                   <culture> is a .NET culture name such as zh-CN,
                                   en-US, or ja-JP.

  --userData=<path>                Override the user-data directory for one run
                                   (include your projects, assets, templates, 
                                   skills, and render cache).

                                   To change the user-data directory permanently, 
                                   use the Settings in the GUI or edit the config file.

  --basicUserData=<path>           Override the application-data directory used
                                   for settings and other internal state in one run.

  --noSettings                     Do not persist setting changes automatically
                                   made during this run. The setting changes will 
                                   be committed to the file only when this app closes 
                                   normally.

  --disablePlugins                 Disable plugin-engine startup for this run.

  --scripting=disable|enableWithHostingPipe        
                                   Control the scripting engine for this run. 
                                   if this argument is omitted, the scripting engine 
                                   is controlled by the user preferences.

                                   'disable' disables scripting whatever the user preferences are.

                                   'enableWithHostingPipe' is very dangerous and 
                                   should only be used in a secure environment, because 
                                   it allows arbitrary code execution from a remote process.

                                   When scripting is disabled in the user preferences,  
                                   the scripting engine is always disabled and this argument has no effect.

Logging and diagnostics:
  --consoleLog                     Write application log messages to the console.
  --log                            Open the dedicated log window.
  --logDiagnostic                  Include diagnostic-level log messages.


Integration options:
  --mcpServer=<address>            Supply the MCP server address associated with
                                   the project being opened.

Protocol URI:
  pjfc:file:///C:/path/to/project.pjfc[?option[&option...]]

  A file URI can be passed through the registered pjfc: protocol. Each decoded
  query segment becomes one normal command-line argument. For example,
  ?--noSplash supplies --noSplash, while ?--overrideCulture=en-US supplies
  --overrideCulture=en-US.

Examples:
  pjfc-cli gui
  pjfc-cli gui ""D:\Video Projects\demo.pjfc""
  pjfc-cli gui --continue --noSplash
  pjfc-cli gui ""D:\Video Projects\demo.pjfc"" --consoleLog --logDiagnostic
  pjfc-cli gui --overrideCulture=en-US --userData=""D:\pjfc-data""
  projectFrameCut.exe ""D:\Video Projects\demo.pjfc""
  projectFrameCut.exe ""pjfc:file:///D:/Video%20Projects/demo.pjfc?--noSplash""

Notes:
  * Most GUI options are case-sensitive; use the spelling shown above.
    --continue and --mcpServer are accepted case-insensitively.
  * Options that take a value require the --name=value form.
  * When several non-option arguments are supplied, the longest one is selected
    as the launch target. Supplying one target is recommended.
  * `pjfc-cli gui` launches the GUI immediately. Use `pjfc-cli help gui` to view
    this document without starting it.");
        }

        public static void WriteAbout()
        {
            var ProgramConfig = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown config";
            var ProgramCommit = (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Split('+').Last();
            var AssemblyName = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "projectFrameCut";
            var renderType = typeof(Renderer).Assembly;
            var drawingType = typeof(Drawing.Base.IPicture).Assembly;
            string renderHash = "", drawingHash = "", drawingCommit = "unknown", programDate = "?";
            try
            {
#pragma warning disable IL3000 // we have already detected that the assembly is not dynamic, so it's safe to get the location
                renderHash = !renderType.IsDynamic && Path.Exists(renderType.Location) ? HashServices.ComputeFileHash(renderType.Location) : "unknown";
                drawingHash = !drawingType.IsDynamic && Path.Exists(drawingType.Location) ? HashServices.ComputeFileHash(drawingType.Location) : "unknown";
                try
                {
                    var appType = Assembly.GetExecutingAssembly();
                    programDate = !appType.IsDynamic && Path.Exists(appType.Location) ? $"on {File.GetLastWriteTime(appType.Location):yyyy-MM-dd HH:mm:ss}" : "";
                }
                catch
                {
                    programDate = "?";
                }
#pragma warning restore IL3000
                drawingCommit = (drawingType.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Split('+').Last();

            }
            catch { renderHash = "unknown"; }
            Console.WriteLine(
$"""
{ProgramConfig}@{ProgramCommit} build on {programDate}
https://github.com/hexadecimal0x12e/projectFrameCut

.NET CoreCLR version: {Environment.Version}
.NET MAUI version:    {typeof(View).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "10.0.?"}
IPluginBase API:      v{IPluginBase.CurrentPluginAPIVersion} 
IApplicationPluginBase API: v{IApplicationPluginBase.CurrentAppLevelPluginAPIVersion}
{renderType.GetName().Name}: v{renderType.GetName().Version} {renderHash}
{drawingType.GetName().Name}: v{drawingType.GetName().Version} ({drawingCommit})
Package: {WinUI.App.GetPackageFullName() ?? "N/A (Portable)"} 

This project is licensed under the Apache License, Version 2.0 for personal/educational and non-commercial use ONLY.
See the LICENSE and license.md file in the project root for more information.
""");
        }
    }
}
