using projectFrameCut.Render.Contracts;
using FFmpeg.AutoGen;
using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.ReadWriteConvert;
using System.IO.Compression;
using projectFrameCut.Render.Benchmark;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.WindowsRender;
using projectFrameCut.Render.HwAccelEngine;
using projectFrameCut.Shared;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static projectFrameCut.Shared.Logger;
using projectFrameCut.Render.Effect;

using projectFrameCut.Drawing.Base.Picture;

#if DIAGHUB_ENABLE_TRACE_SYSTEM
using Microsoft.DiagnosticsHub;
#endif

namespace projectFrameCut.StandaloneRender
{
    public class Program
    {
        const int PluginAPIVersion = IPluginBase.CurrentPluginAPIVersion;

        static readonly JsonSerializerOptions savingOpts = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        static readonly object consoleLogGate = new();
        static RenderProgressDisplay? renderProgressDisplay;

        private static string FormatEta(TimeSpan eta)
            => $"{eta.Ticks / TimeSpan.TicksPerHour:00}:{eta.Minutes:00}:{eta.Seconds:00}";

        private static void WriteConsoleLine(string message)
        {
            lock (consoleLogGate)
            {
                if (renderProgressDisplay?.TryQueueLog(message) == true) return;
                Console.WriteLine(message);
            }
        }

        private static ConsoleColor GetLogColor(string level)
            => level.Equals("error", StringComparison.InvariantCultureIgnoreCase) ? ConsoleColor.Red :
               level.Equals("stat", StringComparison.InvariantCultureIgnoreCase) ? ConsoleColor.Green :
               (level.Equals("warning", StringComparison.InvariantCultureIgnoreCase) || level.Equals("warn", StringComparison.InvariantCultureIgnoreCase)) ? ConsoleColor.Yellow :
               (level.Equals("debug", StringComparison.InvariantCultureIgnoreCase) || level.Equals("diag", StringComparison.InvariantCultureIgnoreCase)) ? ConsoleColor.Cyan :
               level.StartsWith("FFmpeg", StringComparison.InvariantCultureIgnoreCase) ? ConsoleColor.Magenta : ConsoleColor.Gray;

        private sealed class RenderProgressDisplay : IDisposable
        {
            private readonly Func<Renderer[]> getRenderers;
            private readonly Timer timer;
            private readonly Queue<(string Message, string? Level)> pendingLogs = new();
            private readonly IntPtr outputHandle;
            private readonly uint originalConsoleMode;
            private bool restoreConsoleMode;
            private bool interactive = !Console.IsOutputRedirected;
            private bool disposed, cursorHidden;
            private string[] drawnLines = [];
            private int drawnWidth;
            private long lastSnapshot = Environment.TickCount64 - 5000;
            private string? summary;
            private double projectProgress = -1;

            [DllImport("kernel32.dll")]
            private static extern IntPtr GetStdHandle(int handle);

            [DllImport("kernel32.dll")]
            private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

            [DllImport("kernel32.dll")]
            private static extern bool SetConsoleMode(IntPtr handle, uint mode);

            public RenderProgressDisplay(Func<Renderer[]> getRenderers)
            {
                this.getRenderers = getRenderers;
                lock (consoleLogGate)
                {
                    if (interactive && OperatingSystem.IsWindows())
                    {
                        outputHandle = GetStdHandle(-11);
                        interactive = GetConsoleMode(outputHandle, out var mode) && SetConsoleMode(outputHandle, mode | 0x0004);
                        originalConsoleMode = mode;
                        restoreConsoleMode = interactive && (mode & 0x0004) == 0;
                    }
                    renderProgressDisplay = this;
                }
                timer = new Timer(_ => Refresh(), null, 0, 250);
            }

            public bool TryQueueLog(string message, string? level = null)
            {
                if (!interactive || disposed) return false;
                pendingLogs.Enqueue((message, level));
                return true;
            }

            public void SetProjectProgress(double progress, TimeSpan eta, double fps)
            {
                lock (consoleLogGate)
                {
                    if (progress < projectProgress) return;
                    projectProgress = progress;
                    summary = $"Project: {progress:P1}, ETA: {(fps > 0 || progress >= 1 ? FormatEta(eta) : "--:--:--")}, FPS: {fps:N2}";
                }
            }

            private void Refresh()
            {
                string? projectSummary;
                lock (consoleLogGate)
                {
                    if (disposed) return;
                    projectSummary = summary;
                }
                var lines = new List<string>();
                var compositorLines = new List<string>();
                if (projectSummary is not null) lines.Add(projectSummary);
                foreach (var renderer in getRenderers().OrderBy(r => r.ChunkIndex))
                {
                    if (renderer.ChunkCount > 1)
                        lines.Add($"Chunk {renderer.ChunkIndex + 1}/{renderer.ChunkCount}:");
                    else if (projectSummary is null)
                        lines.Add($"Project: {renderer.Progress:P1}, ETA: {(renderer.CurrentFps > 0 || renderer.Progress >= 1 ? FormatEta(renderer.EstimatedRemaining) : "--:--:--")}, " +
                            $"FPS: {renderer.CurrentFps:N2}, frames: {renderer.CurrentFinished}/{renderer.Duration}");
                    foreach (var p in renderer.GetThreadProgress())
                    {
                        bool cacheWriter = p.Name == "VFD cache writer";
                        string name = p.Name switch
                        {
                            "Render scheduler" => "Scheduler",
                            "VFD cache writer" => "VFD writer",
                            "Render worker/compositor" => "Worker/compositor",
                            _ => p.IsCompositor ? "Compositor" : "Worker"
                        };
                        string state = p.State switch
                        {
                            RenderThreadState.Working => cacheWriter ? "Writing" : p.IsCompositor ? "Working" : "Rendering",
                            RenderThreadState.Paused => p.IsCompositor || cacheWriter ? "Waiting" : "Paused",
                            RenderThreadState.Completed => "Completed",
                            _ => "Stopped"
                        };
                        string line = $"  {$"{name} #{p.ThreadId}",-30} | {state,-9} | {p.Progress:P1} | frame {p.CurrentFrame?.ToString() ?? "-",10} | avg {p.FramesPerSecond:N2} FPS | {p.CompletedFrames} frames";
                        if (p.IsCompositor || cacheWriter) compositorLines.Add(line);
                        else lines.Add(line);
                    }
                }
                lines.AddRange(compositorLines);
                lock (consoleLogGate)
                {
                    if (disposed) return;
                    if (!interactive && Environment.TickCount64 - lastSnapshot < 5000) return;
                    try
                    {
                        if (!interactive)
                        {
                            if (lines.Count > 0) Console.WriteLine(string.Join(Environment.NewLine, lines));
                            lastSnapshot = Environment.TickCount64;
                            return;
                        }

                        int width = Math.Max(1, Console.WindowWidth - 1);
                        var output = new StringBuilder();
                        if (pendingLogs.Count > 0 || width != drawnWidth) AppendClear(output);
                        AppendLogs(output);
                        drawnWidth = width;

                        // Cursor movement cannot reach rows above the visible terminal window.
                        if (lines.Count >= Console.WindowHeight || width == 1)
                        {
                            AppendClear(output);
                            if (cursorHidden) output.Append("\x1b[?25h");
                            cursorHidden = false;
                            if (lines.Count > 0 && Environment.TickCount64 - lastSnapshot >= 5000)
                            {
                                output.AppendJoin("\r\n", lines).Append("\r\n");
                                lastSnapshot = Environment.TickCount64;
                            }
                        }
                        else
                        {
                            if (!cursorHidden && lines.Count > 0)
                            {
                                output.Append("\x1b[?25l");
                                cursorHidden = true;
                            }
                            AppendFrame(output, lines.Select(line => line.Length > width ? line[..width] : line).ToArray());
                        }
                        if (output.Length > 0) Console.Write(output.ToString());
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or PlatformNotSupportedException)
                    {
                        if (!interactive) disposed = true;
                        interactive = false;
                        drawnLines = [];
                    }
                }
            }

            private void AppendFrame(StringBuilder output, string[] lines)
            {
                int row = drawnLines.Length;
                for (int i = 0; i < Math.Max(lines.Length, drawnLines.Length); i++)
                {
                    string line = i < lines.Length ? lines[i] : string.Empty;
                    if (i < drawnLines.Length && line == drawnLines[i]) continue;
                    MoveToRow(output, ref row, i);
                    output.Append('\r').Append(line).Append("\x1b[K");
                    if (i >= drawnLines.Length)
                    {
                        output.Append("\r\n");
                        row++;
                    }
                }
                MoveToRow(output, ref row, lines.Length);
                drawnLines = lines;
            }

            private static void MoveToRow(StringBuilder output, ref int row, int target)
            {
                if (row != target) output.Append("\x1b[").Append(Math.Abs(target - row)).Append(target < row ? 'A' : 'B').Append('\r');
                row = target;
            }

            private void AppendClear(StringBuilder output)
            {
                if (drawnLines.Length == 0) return;
                output.Append("\r\x1b[").Append(drawnLines.Length).Append("A\x1b[J");
                drawnLines = [];
            }

            private void AppendLogs(StringBuilder output)
            {
                while (pendingLogs.TryDequeue(out var log))
                {
                    if (log.Level is not null && !log.Level.Equals("info", StringComparison.InvariantCultureIgnoreCase))
                    {
                        output.Append(GetLogColor(log.Level) switch
                        {
                            ConsoleColor.Red => "\x1b[91m",
                            ConsoleColor.Green => "\x1b[92m",
                            ConsoleColor.Yellow => "\x1b[93m",
                            ConsoleColor.Cyan => "\x1b[96m",
                            ConsoleColor.Magenta => "\x1b[95m",
                            _ => "\x1b[37m"
                        }).Append('[').Append(log.Level).Append("]\x1b[0m ");
                    }
                    output.Append(log.Message.ReplaceLineEndings("\r\n")).Append("\r\n");
                }
            }

            public void Dispose()
            {
                timer.Dispose();
                lock (consoleLogGate)
                {
                    disposed = true;
                    try
                    {
                        var output = new StringBuilder();
                        AppendClear(output);
                        AppendLogs(output);
                        if (cursorHidden) output.Append("\x1b[?25h");
                        cursorHidden = false;
                        if (output.Length > 0) Console.Write(output.ToString());
                    }
                    catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }
                    finally
                    {
                        if (restoreConsoleMode)
                        {
                            SetConsoleMode(outputHandle, originalConsoleMode);
                            restoreConsoleMode = false;
                        }
                        if (renderProgressDisplay == this) renderProgressDisplay = null;
                    }
                }
            }
        }

        public static async Task<int> Main(string[] args)
        {
            if (args.FirstOrDefault() == "external_source_worker")
            {
                try { await projectFrameCut.Render.PluginIsolation.ProjectExternalSourceWorker.RunAsync(args.Skip(1).ToArray()); return 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            }
            projectFrameCut.Render.RPCProtocol.ProjectExternalSourceRuntime.StartWorker = (root, asset, ct) =>
                new projectFrameCut.Render.PluginIsolation.ProjectExternalSourceProcessPlatform([Environment.ProcessPath is { } executable && !Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? executable : typeof(Program).Assembly.Location]).StartAsync(
                    projectFrameCut.Render.RPCProtocol.ProjectExternalSourceRuntime.CreateContext(asset,
                        projectFrameCut.Render.PluginIsolation.ProjectExternalSourceDatabase.ResolveAssetDirectory(root, asset),
                        Path.Combine(Path.GetTempPath(), "projectFrameCut-external-sources", Guid.NewGuid().ToString("N")), $"standalone-{Environment.ProcessId}"), ct);
            if (!args.Contains("--nolog"))
            {
                Console.ForegroundColor = ConsoleColor.White;
                MyLoggerExtensions.OnLog += (m, l) =>
                {
                    lock (consoleLogGate)
                    {
                        if (renderProgressDisplay?.TryQueueLog(m, l) == true) return;
                        if (l.Equals("info", StringComparison.InvariantCultureIgnoreCase))
                        {
                            Console.WriteLine(m);
                            return;
                        }

                        var oldColor = Console.ForegroundColor;
                        try
                        {
                            Console.ForegroundColor = GetLogColor(l);

                            Console.Write($"[{l}]");
                        }
                        finally
                        {
                            Console.ForegroundColor = oldColor;
                        }

                        Console.WriteLine($" {m}");
                    }
                };
                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    if (assembly is not null)
                    {
                        var ProgramConfig = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown config";
                        var ProgramCommit = new string((assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Split('+').Last().ToArray());
                        var AssemblyName = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "projectFrameCut StandaloneRender";
                        Console.WriteLine($"{AssemblyName} v{assembly.GetName().Version} {ProgramConfig}@{ProgramCommit}");
                        Console.WriteLine("Copyright (c) hexadecimal0x12e 2025-2026. https://github.com/hexadecimal0x12e/projectFrameCut/");
                        Console.WriteLine();
                    }

                }
                catch { }

            }
            if (args.Contains("--logDiagnostic"))
            {
                MyLoggerExtensions.LoggingDiagnosticInfo = true;
            }

            if (args.Length == 0 || args.Any(x => x.Equals("-h") || x.Equals("--help")))
            {
                Console.WriteLine(
                    """
                    Help:
                    Usage for render: 
                        projectFrameCut.StandaloneRender 
                            [--nolog | --logDiagnostic] 
                            [--resolveArgsFromEnvironmentVars] 
                            [--externalAssemblyPath=<path1>[;<path2>;<path3>...]] 
                            <mode> <args>

                    Available modes:
                        - render: Render video/audio/all from the given project file.
                        - diagnostic: Render frames serially and log detailed per-frame stage timings.
                        - bench: Benchmark hardware accelerators for rendering.
                        - reencode: Decode an existing video and re-encode it. Useful for codec testing.

                    Arguments:
                    Modes 'render' / 'diagnostic':
                        -project=<project dir>
                        -output=<output file>
                        -output_options=<width>,<height>,<fps>,<pixel format>,<encoder>
                        [-target=<video|audio|all>]
                        [--allowExternalSources=all|<ImportId,ImportId,...>]
                        [-assetDbFile=<path to database.json file>]
                        [-pluginRoot=<path to plugin root>]
                        [-maxParallelThreads=<number>]
                        [-diagnostic=<true|false>]                      (default: false; detailed per-frame timings)
                        [-allowWorkerOversubscription=<true|false>]      (default: true)
                        [-maxPendingWriteFrames=<number>]
                        [-enableThreadAffinity=<true|false>]
                        [-reuseDynamicPreviewCache=<true|false>]
                        [--incrementalRendering=<true|false>]
                        [-vfdCompression=<None|Deflate|GZip|Brotli|Zstandard|Lzma2>] (default: Zstandard)
                        [-vfdCompressionLevel=<level>]
                          Zstandard: -131072..22 (default: 1); Lzma2: 1..10 (default: 6).
                          Deflate/GZip/Brotli: Fastest|Optimal|SmallestSize|NoCompression (default: Fastest).
                        [-renderWorkerAffinity=<cpu0,cpu1,cpu2... | cpuStart-cpuEnd>]
                        [-multiAccelerator=<true|false>]
                        [-acceleratorType=<auto|cuda|opencl|cpu> or -acceleratorDeviceId=<device id> or -acceleratorDeviceIds=<device ids|all>]
                        [-GCOptions=0,1,2]
                        [-outputIntermediatePath=<intermediate output path>]
                        [-FFmpegLibraryPath=<path to FFmpeg libraries>]
                        [-diagReportPath=<path diag report output directory>]
                        [-preferHwAccelDecoder=<true|false>]
                        [-ApproximateMixture=<true|false>]
                        [-PictureResizer=<cpu|hwaccel>]
                        [-renderAheadFrames=<number>] [-enableMemoryBudget=<true|false>] [-maxBufferedBytes=<bytes>]
                        [-enableDiskCacheRouting=<true|false> or -forceUseDiskCache=<true|false>]
                        [-diskCacheThreshold=<0.1-0.95>] [-diskCacheMaxFrameCount=<number>] [-videoBuilderDiskCacheRoot=<path>]
                        [-chunkRender=<true|false>]
                        [-chunkFrames=<number> | -chunkSeconds=<number>] [-chunkParallelism=<number>]
                        [-chunkResume=<true|false>] [-chunkKeepFiles=<true|false>]
                          chunkResume also restores deferred composition when chunkRender=false.
                        [-multiStream=<true|false>]

                    Mode 'diagnostic' uses one render thread, disables chunk rendering and frame lookahead,
                    and waits for GPU stages to complete before recording their timings.

                    Mode 'bench' [sub-modes]:
                        bench render    - Render pipeline benchmark (default)
                        bench encode    - VideoBuilder cache & encode throughput test
                        bench decode    - IVideoSource decode performance test
                        bench schedule  - Full render scheduling search with real video output

                    Sub-mode 'schedule' arguments:
                        [-reportDirectory=<path>] [-encoder=<exact encoder name>]
                        [-pixelFormat=<AVPixelFormat name>]             (default: AV_PIX_FMT_YUV420P)
                        [-scheduleCandidates=<number>] [-scheduleSeed=<number>] (defaults: 120, 42)
                        Uses hardware H.264 with software fallback unless -encoder is specified.
                        Writes complete 300-frame projects; keeps reports only.

                    Common arguments (all sub-modes):
                        [-writeToNull=<true|false>]

                    Sub-mode 'render' arguments:
                        [-multiAccelerator=<true|false>]
                        [-acceleratorType=<auto|cuda|opencl|cpu> or -acceleratorDeviceId=<device id> or -acceleratorDeviceIds=<device ids|all>]
                        [-maxParallelThreads=<number>]
                        [-allowWorkerOversubscription=<true|false>]      (default: true)
                        [-enableThreadAffinity=<true|false>]
                        [-renderWorkerAffinity=<cpu0,cpu1,cpu2... | cpuStart-cpuEnd>]
                        [-GCOptions=0,1,2]
                        [-preferHwAccelDecoder=<true|false>]
                        [-PictureResizer=<cpu|hwaccel>]
                        [-ApproximateMixture=<true|false>]

                    Sub-mode 'encode' arguments:
                        [-output=<output file>]                         (if omitted → BlackHoleWriter, no real file)
                        [-encoder=<codec name>]                         (default: libx264)
                        [-pixelFormat=<AVPixelFormat name>]             (default: yuv420p)
                        [-totalFrames=<number>]                         (default: 600)
                        [-fps=<frame rate>]                             (default: 60)
                        [-enableDiskCacheRouting=<true|false>]          (test disk cache spillover)
                        [-diskCacheThreshold=<0.1-0.95>]                (default: 0.85)
                        [-maxPendingWriteFrames=<number>]

                    Sub-mode 'decode' arguments:
                        -source=<video file path>                   (required)
                        [-maxFrames=<number>]                       (limit decoded frames)
                        [-preferHwAccelDecoder=<true|false>]

                    Mode 'reencode':
                        -source=<input video>                       (required)
                        -output=<output file>                       (required)
                        [-encoder=<codec name>]                     (default: libx264)
                        [-pixelFormat=<AVPixelFormat name>]         (default: AV_PIX_FMT_YUV420P)
                        [-maxFrames=<number>]                       (limit frames to reencode)
                        [-preferHwAccelDecoder=<true|false>]
                        [-preferHwAccelEncoder=<true|false>]
                        [-bitRate=<bitrate in bps>]                 (optional, encoder bitrate)
                        [-finalVideoBitRate=<bitrate in bps>]       (optional, final output only)
                        [-multiStream=<true|false>]

                    ---

                    Other usage:
                        projectFrameCut.StandaloneRender list_accels - List all available accelerator devices. Result will printed in JSON format to stderr.
                        projectFrameCut.StandaloneRender about - Print about info.
                        projectFrameCut.StandaloneRender -h | --help - Show this help message.
                        
                    See more at the document.
                    Press Enter to exit.
                    """);
                Console.ReadLine();
                return 0;
            }

            var runningMode = args[0];

            if (runningMode == "about")
            {
                Console.WriteLine();
                Console.WriteLine(GetInfo(true));
                return 0;
            }

            ConcurrentDictionary<string, string> switches = new(args
               .Skip(1)
               .Select(x => x.Split('=', 2))
               .Where(x => x.Length == 2)
               .Select(x => new KeyValuePair<string, string>(x[0].TrimStart('-', '/'), x[1])));

            if (args.Contains("--resolveArgsFromEnvironmentVars"))
            {
                var envVars = new Dictionary<string, string>();
                foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
                {
                    if (de.Key is string key && de.Value is string value) envVars[key] = value;
                }

                foreach (var item in envVars.Where(kv => kv.Key.StartsWith("projectFrameCut_")))
                {
                    switches[item.Key.Split("projectFrameCut_", 2)[1]] = item.Value;
                }

            }

            Log($"Running mode: {runningMode}");
            Log($"Switches: {Environment.NewLine}{string.Join(Environment.NewLine, switches.Select(kv => $"- {kv.Key}: {kv.Value}"))}");
            Log($"Flags: {Environment.NewLine}{string.Join(Environment.NewLine, args.Where(c => c.StartsWith("--")).Select(c => $"- {c.Substring(2)}"))}");

            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
            {
                var requestedAssembly = new AssemblyName(eventArgs.Name);
                var probingPaths = new List<string>
                {
                    AppContext.BaseDirectory,
                };
                if (switches.TryGetValue("externalAssemblyPath", out string? value))
                {
                    probingPaths.AddRange(value.Split([';'], StringSplitOptions.RemoveEmptyEntries));
                }
                return TryResolveAssembly(requestedAssembly.Name!, probingPaths.ToArray(), keepInMemory: true);
            };

            #region plugin loading

            var plugins = new List<projectFrameCut.Render.RenderAPIBase.Plugins.IPluginBase>
                {
                    new InternalPluginBase(),
                    new ILGPUPlugin(),
                };

            if (switches.TryGetValue("pluginRoot", out var pluginRoot) && !string.IsNullOrWhiteSpace(pluginRoot))
            {
                if (Directory.Exists(pluginRoot))
                {
                    Log($"Loading external plugins from: {pluginRoot}");
                    foreach (var dllPath in Directory.GetFiles(pluginRoot, "*.dll"))
                    {
                        try
                        {
                            var assembly = Assembly.LoadFrom(dllPath);
                            var types = assembly.GetTypes();
                            var ldr = types?.First(a => a.Name == "PluginLoader");
                            if (ldr is null)
                            {
                                Log("No PluginLoader class found in assembly.", "warning");
                                continue;
                            }
                            var ldrMethod = ldr.GetMethod("CreateInstance");
                            var pluginObj = ldrMethod?.Invoke(null, ["stand-Alone", Path.GetDirectoryName(dllPath)]);
                            if (pluginObj is IPluginBase plugin)
                            {
                                if (plugin.PluginAPIVersion != PluginAPIVersion)
                                {
                                    Log($"Plugin {plugin.Name} has a mismatch PluginAPIVersion. Excepted {PluginAPIVersion}, got {plugin.PluginAPIVersion}.", "error");
                                    continue;
                                }
                                plugins.Add(plugin);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Failed to load assembly {Path.GetFileName(dllPath)}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Log($"WARNING: Plugin root directory '{pluginRoot}' does not exist.");
                }
            }

            PluginManager.Init(plugins);
            Log($"{PluginManager.LoadedPlugins.Count} plugins loaded.");
            #endregion

            try
            {
                switch (runningMode)
                {
                    case "render":
                    case "diagnostic":
                        var accelResult = InitAccel(switches);
                        if (accelResult != 0)
                        {
                            return accelResult;
                        }
                        try
                        {
                            return await GoRender(switches, runningMode == "diagnostic");
                        }
                        catch (TaskCanceledException)
                        {
                            Log("Render task was canceled.");
                            return 255;
                        }
                        catch { throw; }
                    case "bench":
                        // ── 解析子模式（render / encode / decode）──────────
                        var benchSubMode = "render";
                        if (args.Length > 1 && !args[1].StartsWith('-') && !args[1].Contains('='))
                        {
                            benchSubMode = args[1].ToLowerInvariant();
                        }
                        else if (switches.TryGetValue("benchMode", out var switchBenchMode))
                        {
                            benchSubMode = switchBenchMode.ToLowerInvariant();
                        }

                        if (benchSubMode != "render" && benchSubMode != "encode" && benchSubMode != "decode" && benchSubMode != "schedule")
                        {
                            Log($"ERROR: Unknown bench sub-mode '{benchSubMode}'. Available: render, encode, decode, schedule.", "error");
                            return 1;
                        }

                        Log($"Bench sub-mode: {benchSubMode}");

                        if (benchSubMode is "render" or "schedule" || switches.ContainsKey("preferHwAccelDecoder"))
                        {
                            var accelResult1 = InitAccel(switches);
                            if (accelResult1 != 0)
                            {
                                return accelResult1;
                            }
                        }

                        try
                        {
                            return benchSubMode switch
                            {
                                "encode" => await GoBenchEncode(switches),
                                "decode" => await GoBenchDecode(switches),
                                "schedule" => await GoBenchSchedule(switches),
                                _ => await GoBenchRender(switches),
                            };
                        }
                        catch (TaskCanceledException)
                        {
                            Log("Benchmark task was canceled.");
                            return 255;
                        }
                    case "reencode":
                        // 初始化 FFmpeg（与 render 和 bench 使用相同的模式）
                        Log("Initializing FFmpeg for reencode...");
                        DynamicallyLoadedBindings.EnableAutoInitialization = false;
                        FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
                        ffmpeg.RootPath = switches.GetOrAdd("FFmpegLibraryPath", AppContext.BaseDirectory);
                        if (FFmpeg.AutoGen.DynamicallyLoadedBindings.TryInitialize())
                        {
                            FFmpegHelper.SetupFFmpegLogging(ffmpeg.AV_LOG_INFO);
                            Log($"FFmpeg library: version {ffmpeg.av_version_info()}, {ffmpeg.avcodec_license()}");
                        }
                        else
                        {
                            Log($"FFmpeg library failed to load.", "error");
                            return 1;
                        }
                        FFmpegHelper.SetupFFmpegLogging();

                        if (switches.ContainsKey("preferHwAccelDecoder"))
                        {
                            var accelResult1 = InitAccel(switches);
                            if (accelResult1 != 0) return accelResult1;
                        }

                        try
                        {
                            return await GoReencode(switches);
                        }
                        catch (TaskCanceledException)
                        {
                            Log("Reencode task was canceled.");
                            return 255;
                        }
                    case "list_accels":
                        Context context = Context.Create(builder => builder.Default().EnableAlgorithms());
                        var devices = context.Devices.ToList();
                        List<AcceleratorInfo> listAccels = new();
                        for (uint i = 0; i < devices.Count; i++)
                        {
                            var item = devices[(int)i];
                            listAccels.Add(new AcceleratorInfo(i, item.Name, item.AcceleratorType.ToString()));
                        }
                        Console.Error.WriteLine(JsonSerializer.Serialize(listAccels, new JsonSerializerOptions { WriteIndented = true }));
                        return 0;
                    default:
                        Log($"ERROR: Mode {runningMode} doesn't defined.");
                        return 16;

                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("*** FATAL ERROR ***");
                Log(ex, "Unhandled exception");
                Console.CursorVisible = true;
                throw;
            }
            finally
            {
                Console.CursorVisible = true;
            }
        }

        private static bool TryParseCpuIndexList(string value, out int[] cpuIndexes, out string error)
        {
            var result = new SortedSet<int>();
            foreach (var token in value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.Contains('-', StringComparison.Ordinal))
                {
                    var parts = token.Split('-', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], out int start)
                        || !int.TryParse(parts[1], out int end))
                    {
                        cpuIndexes = [];
                        error = $"Invalid CPU range '{token}'.";
                        return false;
                    }

                    if (start > end)
                    {
                        (start, end) = (end, start);
                    }

                    if (start < 0)
                    {
                        cpuIndexes = [];
                        error = $"CPU index cannot be negative in range '{token}'.";
                        return false;
                    }

                    for (int i = start; i <= end; i++)
                    {
                        result.Add(i);
                    }
                    continue;
                }

                if (!int.TryParse(token, out int cpuIndex) || cpuIndex < 0)
                {
                    cpuIndexes = [];
                    error = $"Invalid CPU index '{token}'.";
                    return false;
                }

                result.Add(cpuIndex);
            }

            if (result.Count == 0)
            {
                cpuIndexes = [];
                error = "No CPU indexes were provided.";
                return false;
            }

            cpuIndexes = result.ToArray();
            error = string.Empty;
            return true;
        }

        private static int InitAccel(ConcurrentDictionary<string, string> switches)
        {
            if (Enum.TryParse<EffectImplementType>(switches.GetValueOrDefault("ForcePreferToType"), out var type) && type == EffectImplementType.IPicture)
            {
                EffectHelper.ForcePreferToType = type;
                Log("Using CPU picture effects.");
                return 0;
            }
            try
            {

                using var context = Context.Create(builder => builder.Default().EnableAlgorithms());
                var devices = context.Devices.ToList();
                List<Device> picked = new();
                for (int i = 0; i < devices.Count; i++)
                {
                    Log($"Accelerator device #{i}: {devices[i].Name} ({devices[i].AcceleratorType})");
                }

                var multiAccel = bool.TryParse(switches.GetOrAdd("multiAccelerator", "false"), out var ma) ? ma : false;
                if (!multiAccel)
                {
                    var acceleratorId = int.TryParse(switches.GetOrAdd("acceleratorDeviceId", "-1"), out var result1) ? result1 : -1;
                    var accelType = switches.GetOrAdd("acceleratorType", "auto");
                    var acc = ILGPUExecutionHelper.PickOneAccel(accelType, acceleratorId, devices);
                    if (acc is null)
                    {
                        Log($"ERROR: Cannot pick accelerator device. Check the configuration.");
                        return 2;
                    }
                    picked.Add(acc);
                    Log($"Selecting accelerator device #{devices.IndexOf(acc)}: {acc.Name} ({acc.AcceleratorType})");

                }
                else
                {
                    var accelsIdsStr = switches.GetOrAdd("acceleratorDeviceIds", "");
                    if (string.IsNullOrWhiteSpace(accelsIdsStr))
                    {
                        Log("ERROR: multiAccelerator is set to true, but no acceleratorDeviceIds provided.");
                        return 2;
                    }

                    if (accelsIdsStr == "all")
                    {
                        picked = devices.Where(a => a.AcceleratorType != AcceleratorType.CPU).ToList();
                    }
                    else
                    {

                        var accelsIds = accelsIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(s => int.TryParse(s, out var id) ? id : -1)
                            .Where(id => id >= 0)
                            .ToList();
                        picked = accelsIds.Select(id =>
                        {
                            var acc = ILGPUExecutionHelper.PickOneAccel("auto", id, devices);
                            if (acc is null)
                            {
                                Log($"ERROR: Cannot pick accelerator device with id {id}.");
                            }
                            return acc;
                        }).OfType<Device>().ToList();
                    }

                }

                if (picked == null || picked.Count == 0)
                {
                    Log($"ERROR: No accelerator device picked. Check the configuration.");
                    return 2;
                }

                foreach (var item in picked)
                {
                    Log($"Picked accelerator {item.Name} : {item.AcceleratorType}");
                }

                AcceleratorsManager.ConfigureDevices(picked[0].Name, picked.Select(d => d.Name).ToArray(), multiAccel);
                AcceleratorsManager.IsRendering = true;

                if (switches.TryGetValue("PictureResizer", out var c) && c == "hwaccel") Drawing.Processing.Resizing.PictureResizer.Default = new Render.Effect.EffectPictureResizer(EffectImplementType.HwAcceleration);
                return 0;

            }
            catch (Exception ex)
            {
                Log(ex, "Get accels");
                throw;
            }
        }

        private static async Task<int> GoRender(ConcurrentDictionary<string, string> switches, bool diagnosticRender = false)
        {
            if (!bool.TryParse(switches.GetValueOrDefault("diagnostic", "false"), out var logFrameTimings))
            {
                Log("Invalid diagnostic value: expected true or false.", "error");
                return 1;
            }
            logFrameTimings |= diagnosticRender;

            #region init encoder
            bool trace = Environment.GetCommandLineArgs().Contains("--trace");
            Log("Initiliazing FFmpeg...");
            DynamicallyLoadedBindings.EnableAutoInitialization = false;
            FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            ffmpeg.RootPath = switches.GetOrAdd("FFmpegLibraryPath", AppContext.BaseDirectory);
            if (FFmpeg.AutoGen.DynamicallyLoadedBindings.TryInitialize())
            {
                FFmpegHelper.SetupFFmpegLogging(trace ? ffmpeg.AV_LOG_DEBUG : ffmpeg.AV_LOG_INFO);
                Log($"internal FFmpeg library: version {ffmpeg.av_version_info()}, {ffmpeg.avcodec_license()}\r\nconfiguration:{ffmpeg.avcodec_configuration()}");
            }
            else
            {
                Log($"FFmpeg library failed to load. ({ffmpeg.BindingVerificationResult?.Failures?.Aggregate("", (a, b) => $"{a}{Environment.NewLine}{b.FunctionName} failed to load in {b.LibraryName}: {b.Message}")})", "error");
                return 1;
            }
            FFmpegHelper.SetupFFmpegLogging();
            Log($"internal FFmpeg library: version {ffmpeg.av_version_info()}");

            var outputOptions = switches["output_options"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int width = 0, height = 0, fps = 0;
            FFmpeg.AutoGen.AVPixelFormat outputFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            string outputEncoder = "";
            if (outputOptions.Length != 5)
            {
                Log("ERROR: output_options must contain 5 values: width,height,fps,pixel format,encoder");
                return 1;
            }
            width = int.Parse(outputOptions[0]);
            height = int.Parse(outputOptions[1]);
            fps = int.Parse(outputOptions[2]);
            outputEncoder = outputOptions[4];
            if (!Enum.TryParse(outputOptions[3], out outputFormat) || outputFormat == AVPixelFormat.AV_PIX_FMT_NONE)
            {
                Log($"ERROR: Pixel format {outputOptions[3]} not found in AVPixelFormat.");
                return 1;
            }
            var fmpBPP = FFmpegHelper.GetAVPixelFormatBitsPerPixel(outputFormat);
            var use16Bit = fmpBPP > 8;

            if (fmpBPP <= 0)
            {
                Log($"Cannot auto determine bits per pixel for pixel format {outputOptions[3]}, auto-fallback to 16bpp rendering mode.", "warn");
                use16Bit = trace;
            }

            bool multiStream = bool.TryParse(switches.GetOrAdd("multiStream", "false"), out var multiStreamValue) && multiStreamValue;

            var bpp = use16Bit ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture;
            var chunkOptions = diagnosticRender ? new ChunkRenderOptions() : ParseChunkRenderOptions(switches, fps);
            long? requestedBitRate = switches.TryGetValue("bitRate", out var bitRateText)
                && long.TryParse(bitRateText, out var parsedBitRate) && parsedBitRate > 0
                    ? parsedBitRate
                    : null;
            long? finalVideoBitRate = switches.TryGetValue("finalVideoBitRate", out var finalBitRateText)
                && long.TryParse(finalBitRateText, out var parsedFinalBitRate) && parsedFinalBitRate > 0
                    ? parsedFinalBitRate
                    : null;

            if (!switches.ContainsKey("output"))
            {
                Log("ERROR: No output path specified. Use -output=<output file> to specify the output path.");
                return 1;
            }
            var outputPath = switches["output"].Replace("{CurrentTime}", DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            if (multiStream && !outputPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            {
                Log("ERROR: --multiStream=true requires an .mkv output path.", "error");
                return 1;
            }
            if (multiStream && chunkOptions.Enabled)
            {
                Log("ERROR: --multiStream=true cannot be combined with --chunkRender=true.", "error");
                return 1;
            }

            Log($"Output options: {width}x{height} @ {fps} fps, pixel format: {outputFormat}, encoder: {outputEncoder}, 16 bit render:{use16Bit}({fmpBPP} bpp output), multiStream:{multiStream}");

            #endregion

            #region read args
            if (!switches.ContainsKey("project"))
            {
                Log("No project file specified. Use -project=<project root dir> to specify the project file.", "error");
                return 1;
            }
            var workingPath = Path.GetFullPath(switches["project"]);
            if (string.IsNullOrWhiteSpace(workingPath) || !Directory.Exists(workingPath))
            {
                Log("Invalid project path.", "error");
                return 1;
            }

            string? target = "all";
            if (!switches.TryGetValue("target", out target))
            {
                target = "all";
                Log("No target specified, default to 'all'.");
            }

            var GCOption = 0;
            if (switches.TryGetValue("GCOptions", out var gcopt))
            {
                if (!int.TryParse(gcopt, out GCOption) || (GCOption < 0 || GCOption > 2))
                {
                    Log($"Invalid GCOptions option '{gcopt}', must be 0, 1 or 2. Default to 0.", "warn");
                    GCOption = 0;
                }
            }

            if (GCOption == 2)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            }

            Log($"GC Option:{GCOption}");

            int maxPendingWriteFrames = 0;
            if (switches.TryGetValue("maxPendingWriteFrames", out var mpwf))
            {
                if (!int.TryParse(mpwf, out maxPendingWriteFrames) || maxPendingWriteFrames < 0)
                {
                    Log($"Invalid maxPendingWriteFrames value '{mpwf}', must be a non-negative integer. Default will be used.", "warn");
                    maxPendingWriteFrames = 0;
                }
                else
                {
                    Log($"Max pending write frames: {maxPendingWriteFrames}");
                }
            }

            ConcurrentDictionary<string, AssetItem> assets = new();

            if (switches.TryGetValue("assetDbFile", out var assetDbPath))
            {
                var globalAsset = JsonSerializer.Deserialize<ConcurrentDictionary<string, AssetItem>>(File.ReadAllText(assetDbPath))
                    ?? new ConcurrentDictionary<string, AssetItem>();
                DecoderContextPJFCProject.GlobalAssetGetter = new(() => globalAsset);
                assets = new(globalAsset);
                Log($"Read {assets.Count} assets from asset database.");
            }

            string YesNo(bool b) => b ? "Yes" : "No";

            int maxParallelThreads = Environment.ProcessorCount;
            bool enableThreadAffinity = true;
            int[]? renderWorkerAffinityCpuIndexes = null;

            bool reuseDynamicPreviewCache = bool.TryParse(switches.GetOrAdd("reuseDynamicPreviewCache", "false"), out var reusePreview) && reusePreview;
            bool deferredComposition = bool.TryParse(switches.GetValueOrDefault("incrementalRendering",
                switches.GetValueOrDefault("deferredComposition", "false")), out var deferred) && deferred;
            if (!int.TryParse(switches.GetValueOrDefault("renderAheadFrames", "0"), out var renderAheadFrames) || renderAheadFrames < 0
                || !bool.TryParse(switches.GetValueOrDefault("enableMemoryBudget", "false"), out var enableMemoryBudget)
                || !long.TryParse(switches.GetValueOrDefault("maxBufferedBytes", "0"), out var maxBufferedBytes) || maxBufferedBytes < 0)
            {
                Log("Invalid renderAheadFrames, enableMemoryBudget or maxBufferedBytes.", "error");
                return 1;
            }
            var vfdEncoder = ParseVfdEncoder(switches);
            if (deferredComposition && chunkOptions.Enabled)
            {
                Log("Deferred composition and chunk rendering cannot be enabled together.", "error");
                return 1;
            }

            if (!bool.TryParse(switches.GetOrAdd("allowWorkerOversubscription", "true"), out var allowWorkerOversubscription)) allowWorkerOversubscription = true;
            if (!bool.TryParse(switches.GetOrAdd("enableThreadAffinity", "true"), out enableThreadAffinity)) enableThreadAffinity = true;
            if (switches.TryGetValue("renderWorkerAffinity", out var renderWorkerAffinityRaw) && !string.IsNullOrWhiteSpace(renderWorkerAffinityRaw))
            {
                if (!TryParseCpuIndexList(renderWorkerAffinityRaw, out renderWorkerAffinityCpuIndexes, out var renderWorkerAffinityError))
                {
                    Log($"ERROR: Invalid renderWorkerAffinity '{renderWorkerAffinityRaw}': {renderWorkerAffinityError}", "error");
                    return 1;
                }

                try
                {
                    ThreadAffinityHelper.BuildAffinityMask(renderWorkerAffinityCpuIndexes);
                }
                catch (Exception ex)
                {
                    Log($"ERROR: Invalid renderWorkerAffinity '{renderWorkerAffinityRaw}': {ex.Message}", "error");
                    return 1;
                }

                Log($"Manual render worker CPU affinity: {string.Join(", ", renderWorkerAffinityCpuIndexes)}");
            }

            if (enableThreadAffinity || (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0)
            {
                try
                {
                    var group = ThreadAffinityHelper.GetCpuCoreGroups();
                    foreach (var item in group)
                    {
                        Log($"CPU Cores ({string.Join(",", item.CpuIndexes)})'s Priority:{(item.Capacity ?? 0) + (item.EfficiencyClass ?? 0)}");
                    }
                    if (!int.TryParse(switches.TryGetValue("maxParallelThreads", out var p) ? p : "-1", out maxParallelThreads) || maxParallelThreads <= 0)
                        maxParallelThreads = (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0
                            ? renderWorkerAffinityCpuIndexes!.Length
                            : group.OrderBy(c => (c.Capacity ?? 0) + (c.EfficiencyClass ?? 0)).LastOrDefault()?.CpuIndexes?.Count ?? Environment.ProcessorCount;

                }
                catch
                {
                    if (!int.TryParse(switches.TryGetValue("maxParallelThreads", out var p) ? p : "-1", out maxParallelThreads) || maxParallelThreads <= 0)
                        maxParallelThreads = (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0 ? renderWorkerAffinityCpuIndexes!.Length : Environment.ProcessorCount;
                }
            }
            else
            {
                if (!int.TryParse(switches.TryGetValue("maxParallelThreads", out var p) ? p : "8", out maxParallelThreads)) maxParallelThreads = Environment.ProcessorCount;
            }

            var workerAffinityLabel = (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0
                ? $"manual({string.Join(",", renderWorkerAffinityCpuIndexes ?? Array.Empty<int>())})"
                : (enableThreadAffinity ? "auto" : "disabled");
            if (diagnosticRender) maxParallelThreads = 1;
            Log(diagnosticRender
                ? $"Diagnostic rendering: serial frames, no lookahead or chunk rendering, Worker affinity:{workerAffinityLabel}"
                : $"Working in parallel mode, max {maxParallelThreads} threads, Worker affinity:{workerAffinityLabel}");

            bool hwAccelDecode = bool.TryParse(switches.GetOrAdd("preferHwAccelDecoder", "false"), out var hwAccelDecodeValue) && hwAccelDecodeValue;
            bool hwAccelEncode = bool.TryParse(switches.GetOrAdd("preferHwAccelEncoder", "false"), out var hwAccelEncodeValue) && hwAccelEncodeValue;
            InternalPluginBase.HWAccelDecodeOptionGetter = new(() => hwAccelDecode);
            InternalPluginBase.HWAccelEncodeOptionGetter = new(() => hwAccelEncode);

            PictureLifecycleTracker.Enabled = trace && !Renderer.IsProfilerAttached;
            PictureLifecycleTracker.TrackCollection = trace && !Renderer.IsProfilerAttached;

            var diskCacheRoutingEnabled = bool.TryParse(switches.GetOrAdd("enableDiskCacheRouting", "false"), out var useDiskCache) && useDiskCache;
            if (diskCacheRoutingEnabled)
            {
                var threshold = switches.TryGetValue("diskCacheThreshold", out var dctStr)
                    && double.TryParse(dctStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dct)
                    ? Math.Clamp(dct, 0.1, 0.95) : 0.7;
                var cacheRoot = switches.TryGetValue("videoBuilderDiskCacheRoot", out var vcr) && !string.IsNullOrWhiteSpace(vcr) ? vcr : "(default temp)";
                var maxPending = maxPendingWriteFrames > 0 ? maxPendingWriteFrames.ToString() : "(auto)";
                var maxFrames = switches.TryGetValue("diskCacheMaxFrameCount", out var mfcStr) && int.TryParse(mfcStr, out var mfc) ? mfc : 0;
                Log($"VideoBuilder disk cache routing: Enabled, threshold: {threshold:P0} of max pending ({maxPending}), max frames on disk: {(maxFrames > 0 ? maxFrames.ToString() : "unlimited")}, cache root: {cacheRoot}");
            }

            Log($"Video decoding: Prefer HWAccel Decode: {YesNo(hwAccelDecode)} Encode: {YesNo(hwAccelEncode)}, Disk buffer: {YesNo(diskCacheRoutingEnabled)}");

            ClassicOverlayMixture.EnableApproximatePath = bool.TryParse(switches.GetOrAdd("ApproximateMixture", "false"), out var approximateMixture) && approximateMixture;

            Log($"ClassicOverlayMixture approximate path: {YesNo(ClassicOverlayMixture.EnableApproximatePath)}");

            if (Enum.TryParse<EffectImplementType>(switches.GetOrAdd("ForcePreferToType", "InvalidEnumOptionForDefaultFallback"), out var forcePreferToType) && Enum.IsDefined(forcePreferToType))
            {
                EffectHelper.ForcePreferToType = forcePreferToType;
                Log($"Using forced preferred effect implement type: {forcePreferToType}");
            }
            else
            {
                EffectHelper.ForcePreferToType = EffectImplementType.HwAcceleration;
                Log($"Using default preferred effect implement type: {EffectHelper.ForcePreferToType}");
            }

            #endregion

            #region read draft
            ProjectJSONStructure project = new();
            DraftStructureJSON timeline = new();
            if (File.Exists(Path.Combine(workingPath, "project.pjfc")))
            {
                project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(workingPath, "project.pjfc")), savingOpts) ?? new();
            }
            else if (File.Exists(Path.Combine(workingPath, "project.json")))
            {
                project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(workingPath, "project.json")), savingOpts) ?? new();
            }
            else
            {
                Log("ERROR: project.pjfc or project.json not found in project directory.");
                return 1;
            }

            if (File.Exists(Path.Combine(workingPath, "timeline.json")))
            {
                timeline = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(workingPath, "timeline.json")), savingOpts) ?? new();
            }
            else
            {
                Log("ERROR: timeline.json not found in project directory.");
                return 1;
            }

            if (File.Exists(Path.Combine(workingPath, "assets.json")))
            {
                var projAssets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(workingPath, "assets.json")), savingOpts) ?? new();
                ConcurrentDictionary<string, AssetItem> assetDict = new ConcurrentDictionary<string, AssetItem>(projAssets.ToDictionary((AssetItem a) => a.AssetId ?? $"unknown+{Random.Shared.Next()}", (AssetItem a) => a));
                assets = new ConcurrentDictionary<string, AssetItem>(assets.Concat(assetDict));

            }
            else
            {
                Log("ERROR: assets.json not found in project directory.");
                return 1;
            }
            var sourceProjectRoot = Path.GetFullPath(workingPath);
            Environment.CurrentDirectory = workingPath;
            await using var externalSources = await projectFrameCut.Render.RPCProtocol.ProjectExternalSourceRuntime.OpenAsync(sourceProjectRoot,
                projectFrameCut.Render.PluginIsolation.ProjectExternalSourceDatabase.ParseApprovals(sourceProjectRoot, switches.GetValueOrDefault("allowExternalSources")));
            var renderResumeSignature = deferredComposition ? FrameHashHelper.CreateRenderResumeSignature(project, timeline, assets.Values) : null;
            #endregion

            CancellationTokenSource cts = new();
            VideoBuilder builder = null!;
            Renderer renderer = null!;
            ConcurrentDictionary<int, VideoBuilder> activeChunkBuilders = new();
            ConcurrentDictionary<int, Renderer> activeChunkRenderers = new();
            ConcurrentDictionary<int, Action> completedChunkCleanup = new();
            IVideoWriter CreateConfiguredWriter(string path, bool intermediateChunk)
            {
                IVideoWriter writer = multiStream
                    ? new AlphaBrightnessVideoWriter
                    {
                        CodecName = outputEncoder,
                        Channels = AuxiliaryVideoChannels.Alpha | AuxiliaryVideoChannels.Brightness,
                    }
                    : PluginManager.CreateVideoWriter(outputEncoder);
                writer.Width = width;
                writer.Height = height;
                writer.FramePerSecond = fps;
                writer.PixelFormat = outputFormat.ToString();
                writer.OutputPath = path;
                if (string.IsNullOrWhiteSpace(writer.CodecName)) writer.CodecName = outputEncoder;
                if (requestedBitRate.HasValue) writer.BitRate = requestedBitRate.Value;
                if (intermediateChunk && finalVideoBitRate.HasValue) writer.BitRate = finalVideoBitRate.Value;
                return writer;
            }

            var noSigInt = !Environment.GetCommandLineArgs().Contains("--noSigInt");
            var keyboardInterrupt = !Environment.GetCommandLineArgs().Contains("--keyboardInterrupt");
            bool logProgress = !Environment.GetCommandLineArgs().Contains("--nolog");
            async Task composeVideoRange(
                string resultPath,
                uint startFrame,
                uint frameCount,
                int renderThreads,
                int chunkIndex,
                int chunkCount = 1,
                Action<double, TimeSpan, double>? rangeProgress = null,
                bool intermediateChunk = false)
            {
                var clips = JSONToIClips(timeline, assets, bpp);

                switches.TryGetValue("diagReportPath", out var diagReportPath);
                if (PictureLifecycleTracker.Enabled)
                {
                    PictureLifecycleTracker.Clear();
                }

                var localBuilder = new VideoBuilder(CreateConfiguredWriter(resultPath, intermediateChunk))
                {
                    EnablePreview = !chunkOptions.Enabled || chunkOptions.Parallelism == 1,
                    DoGCAfterEachWrite = GCOption > 0,
                    DisposeFrameAfterEachWrite = true,
                    StartFrame = startFrame,
                    Duration = frameCount,
                    BlockWrite = true,
                    EnableDiskCacheRouting = bool.TryParse(switches.GetOrAdd("enableDiskCacheRouting", "false"), out var useDiskCache) && useDiskCache,
                    ForceUseDiskCache = bool.TryParse(switches.GetOrAdd("forceUseDiskCache", "false"), out var forceUseDiskCache) && forceUseDiskCache,
                    DiskCacheMaxPendingFrames = maxPendingWriteFrames > 0 ? maxPendingWriteFrames : 0,
                    DiskCacheMaxFrameCount = switches.TryGetValue("diskCacheMaxFrameCount", out var mfcStr) && int.TryParse(mfcStr, out var mfc) ? Math.Max(mfc, 0) : 0,
                    DiskCacheDirectory = switches.TryGetValue("videoBuilderDiskCacheRoot", out var cacheRoot) && !string.IsNullOrWhiteSpace(cacheRoot) ? cacheRoot : null,
                };

                if (localBuilder.EnableDiskCacheRouting && switches.TryGetValue("diskCacheThreshold", out var dctStr)
                    && double.TryParse(dctStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dct))
                {
                    localBuilder.DiskCacheThreshold = Math.Clamp(dct, 0.1, 0.95);
                }

                var localRenderer = new Renderer
                {
                    builder = localBuilder,
                    Clips = clips,
                    ProjectRelativeWidth = project.RelativeWidth,
                    ProjectRelativeHeight = project.RelativeHeight,
                    StartFrame = startFrame,
                    Duration = frameCount,
                    ChunkIndex = chunkIndex,
                    ChunkCount = Math.Max(1, chunkCount),
                    CompletedFramesBeforeChunk = startFrame,
                    TotalProjectFrames = timeline.Duration,
                    LogProcessStack = !string.IsNullOrWhiteSpace(diagReportPath),
                    LogRenderState = (bool.TryParse(switches.TryGetValue("LogState", out var ls2) ? ls2 : "false", out var lsbool) && lsbool),
                    LogStaticsData = false,
                    DiagnosticRender = diagnosticRender,
                    LogFrameTimings = logFrameTimings,
                    GCOption = GCOption,
                    Use16Bit = use16Bit,
                    MaxThreads = Math.Max(1, renderThreads),
                    AllowWorkerOversubscription = allowWorkerOversubscription,
                    RenderAheadFrames = renderAheadFrames,
                    EnableMemoryBudget = enableMemoryBudget,
                    MaxBufferedBytes = maxBufferedBytes,
                    ReuseDynamicPreviewCache = reuseDynamicPreviewCache,
                    EnableDeferredComposition = deferredComposition,
                    VfdEncoder = vfdEncoder,
                    ResumeInterruptedRender = !bool.TryParse(switches.GetValueOrDefault("chunkResume", "true"), out var resume) || resume,
                    RenderResumeSignature = renderResumeSignature,
                    DynamicPreviewCacheProjectRoot = workingPath,
                    DynamicPreviewCacheProjectWidth = Math.Max(1, project.RelativeWidth),
                    DynamicPreviewCacheProjectHeight = Math.Max(1, project.RelativeHeight),
                    EnableThreadAffinity = enableThreadAffinity,
                    WorkerCPUCoreIndexs = renderWorkerAffinityCpuIndexes,
                };

#if DIAGHUB_ENABLE_TRACE_SYSTEM
                var FrameDoneMark = new UserMarks("ProgressMark");
#endif

                localRenderer.OnProgressChanged += (s, e) =>
                {
                    double currentFps = localRenderer.CurrentFps;
                    rangeProgress?.Invoke(s, e, currentFps);
                    if (!logProgress) return;
#if DIAGHUB_ENABLE_TRACE_SYSTEM
                    FrameDoneMark.Emit($"Chunk {chunkIndex + 1}/{chunkCount}, progress: {s:p1} ({localRenderer.CurrentFinished}/{localRenderer.Duration}), ETA: {FormatEta(e)}, FPS: {currentFps:n2}");
#endif
                };

                builder = localBuilder;
                renderer = localRenderer;
                activeChunkBuilders[chunkIndex] = localBuilder;
                activeChunkRenderers[chunkIndex] = localRenderer;
                long prepareStarted = logFrameTimings ? Stopwatch.GetTimestamp() : 0;
                localRenderer.PrepareRender(cts.Token);
                if (logFrameTimings)
                    Log($"[Render diagnostic] Renderer initialization: {Stopwatch.GetElapsedTime(prepareStarted).TotalMilliseconds:F3} ms", "diag");
                Stopwatch sw1 = new();
                Log("Start render...");
                sw1.Restart();
                try
                {
                    IPicture? cover = null;
                    try
                    {
                        if (project.ThumbPath is string p && !string.IsNullOrWhiteSpace(p))
                        {
                            if (File.Exists(p))
                            {
                                cover = new Picture8bpp(p);
                                Log($"Loaded cover image from {project.ThumbPath}");
                            }
                            else
                            {
                                Log($"Cover image path {project.ThumbPath} set but does not exist.", "warn");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "Load cover");
                    }
                    await localRenderer.GoRender(cts.Token, cover);
                    sw1.Stop();
                    Log($"Render done,total elapsed {sw1.Elapsed}, avg elapsed {localRenderer.EachElapsedForPreparing.DefaultIfEmpty().Average(t => t.TotalSeconds)} spf to prepare and {localRenderer.EachElapsed.DefaultIfEmpty().Average(t => t.TotalSeconds)} spf to render");
                }
                catch (TaskCanceledException)
                {
                    Log("Render was canceled.");
                }
                catch (Exception ex)
                {
                    Log(ex, "Render error");
                    localBuilder.Interrupt();
                    ReleaseLocalResources();
                    throw;
                }

                if (!string.IsNullOrWhiteSpace(diagReportPath))
                {
                    try
                    {
                        Log("Export diag data...");
                        DiagReportExporter.ExportCsv(diagReportPath!, localRenderer);
                        await PictureLifecycleTracker.ExportPictureLifecycleTrackerSnapshots(Path.Combine(diagReportPath!, $"PictureLifeCycle-{Guid.NewGuid()}.csv"));
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "Export diagReportPath CSV");
                    }
                }

                if (cts.IsCancellationRequested)
                {
                    localBuilder.Interrupt();
                    ReleaseLocalResources();
                    cts.Token.ThrowIfCancellationRequested();
                }

                Log("Finish writing video...");
                long finishStarted = logFrameTimings ? Stopwatch.GetTimestamp() : 0;
                try
                {
                    localBuilder.Writer.Finish();
                    localBuilder.Dispose();
                    if (intermediateChunk && localRenderer.EnableDeferredComposition)
                        completedChunkCleanup[chunkIndex] = localRenderer.CleanupCreatedDeferredCache;
                }
                finally
                {
                    if (logFrameTimings)
                        Log($"[Render diagnostic] Encoder finish/dispose: {Stopwatch.GetElapsedTime(finishStarted).TotalMilliseconds:F3} ms", "diag");
                    ReleaseLocalResources();
                }

                void ReleaseLocalResources()
                {
                    Log($"Releasing resources...");
                    foreach (var item in clips) item?.Dispose();
                    localRenderer.builder = null;
                    activeChunkBuilders.TryRemove(chunkIndex, out _);
                    activeChunkRenderers.TryRemove(chunkIndex, out _);
                }
            }

            async Task<ChunkRenderCoordinator?> composeVideo(string resultPath)
            {
                if (!chunkOptions.Enabled)
                {
                    using var progress = logProgress ? new RenderProgressDisplay(() => activeChunkRenderers.Values.ToArray()) : null;
                    await composeVideoRange(resultPath, 0, timeline.Duration, maxParallelThreads, 0, 1).ConfigureAwait(false);
                    return null;
                }

                var coordinator = new ChunkRenderCoordinator(
                    workingPath,
                    timeline.Duration,
                    fps,
                    Path.GetExtension(resultPath),
                    $"{width}x{height}|{fps}|{outputFormat}|{outputEncoder}|16bit={use16Bit}|bitrate={requestedBitRate}|merge=streamcopy|finalBitrate={finalVideoBitRate}|render=clip-cache|reusePreview={reuseDynamicPreviewCache}|approx={ClassicOverlayMixture.EnableApproximatePath}|effect={EffectHelper.ForcePreferToType}|assetDb={GetFileFingerprintPart(switches.GetValueOrDefault("assetDbFile"))}|externalSources={projectFrameCut.Render.RPCProtocol.ProjectExternalSourceRuntime.Current.CacheToken}",
                    maxParallelThreads,
                    chunkOptions);
                await coordinator.InitializeAsync(cts.Token).ConfigureAwait(false);
                coordinator.OnChunkCompleted += segment =>
                {
                    if (completedChunkCleanup.TryRemove(segment.Index, out var cleanup)) cleanup();
                };
                using (var progress = logProgress ? new RenderProgressDisplay(() => activeChunkRenderers.Values.ToArray()) : null)
                {
                    progress?.SetProjectProgress(0, TimeSpan.Zero, 0);
                    await coordinator.RenderPendingChunksAsync(
                        (segment, chunkPath, chunkThreads, report, token) =>
                            composeVideoRange(chunkPath, segment.StartFrame, segment.Duration, chunkThreads, segment.Index, coordinator.ChunkCount, report, intermediateChunk: true),
                        state => progress?.SetProjectProgress(state.GlobalProgress, state.EstimatedRemaining, state.FramesPerSecond),
                        cts.Token).ConfigureAwait(false);
                }
                IClip[] audioClips = [];
                List<ISoundTrack> tracks = [];
                try
                {
                    if (target == "all")
                    {
                        audioClips = JSONToIClips(timeline, assets, bpp);
                        tracks = JSONToISoundTracks(timeline, assets).ToList();
                        SoundTrackMetadata.AddMissingLegacyTracks(audioClips.Where(c => c.ClipType is ClipMode.AudioClip or ClipMode.VideoClip).ToArray(),
                            tracks, message => Log(message, "warn"));
                    }
                    string merged = await coordinator.MergeAsync(
                        (mergeProgress, mergeEta) =>
                        {
                            if (logProgress)
                                Console.Write($"Merging chunks{(target == "all" ? " and composing audio" : "")}: {mergeProgress:P1} complete, ETA {FormatEta(mergeEta)}       \r");
                        },
                        cts.Token,
                        tracks.Count > 0 ? new AudioComposer<float> { Clips = [], SoundTracks = tracks.ToArray() } : null).ConfigureAwait(false);
                    await ChunkRenderCoordinator.PublishAsync(merged, resultPath, cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    foreach (var clip in audioClips) clip.Dispose();
                    foreach (var track in tracks) track.Dispose();
                }
                return coordinator;
            }

            void composeAudio(string resultPath)
            {
                var clips = JSONToIClips(timeline, assets, bpp).Where(c => c.ClipType == ClipMode.AudioClip || c.ClipType == ClipMode.VideoClip).ToArray();
                var tracks = JSONToISoundTracks(timeline, assets).ToList();
                SoundTrackMetadata.AddMissingLegacyTracks(clips, tracks, message => Log(message, "warn"));

                if (tracks.Count == 0)
                {
                    foreach (var clip in clips) clip.Dispose();
                    Log("No sound clips in the whole draft. returning...");
                    return;
                }

                Log($"Found {tracks.Count} soundtracks.");

                var writer = new AudioWriter(outputPath, 96000, 2, "pcm_s16le");

                var composer = new AudioComposer<float>
                {
                    Clips = [],
                    SoundTracks = tracks.ToArray(),
                    Writer = writer
                };
                if (logProgress)
                {
                    composer.OnProgressChanged += (s, e) =>
                    {
                        Console.Write($"Composing finished {s:p1}, ETA:{FormatEta(e)} \r");
                    };
                }

                composer.Compose(fps, 96000, 2, 4096, cts.Token);
                Console.WriteLine();
                Console.WriteLine();
                writer.Finish();
                writer.Dispose();

                foreach (var item in clips)
                {
                    item?.Dispose();
                }
                foreach (var item in tracks)
                {
                    item.Dispose();
                }
                return;
            }
            Task? keyboardTask = null;
            int KeyQPressCount = 0;

            if (switches.TryGetValue("stopAfter", out var stopAfterStr) && int.TryParse(stopAfterStr, out var stopAfter)) //for Instrument testing, to avoid long time cause a lot huge log.
            {
                Log($"stopAfter is set, render will stop after {stopAfter}s.");

                Timer t = new(async (c) =>
                {
                    Log($"Time's up! stopAfter's {stopAfter}s timeout reached, cancelling render...");
                    await cts.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10));
                });

                t.Change(stopAfter * 1000, Timeout.Infinite);

            }
            if (keyboardInterrupt)
            {
                if (!Console.IsInputRedirected)
                {
                    keyboardTask = Task.Run(async () =>
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            if (Console.KeyAvailable)
                            {
                                var key = Console.ReadKey(intercept: true);

                                switch (key.Key)
                                {
                                    case ConsoleKey.Escape:
                                    case ConsoleKey.Q:
                                        KeyQPressCount++;
                                        if (KeyQPressCount > 5)
                                        {
                                            WriteConsoleLine("Cancel signal receive! try cancelling render...");
                                            await cts.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10));
                                        }
                                        else
                                        {
                                            WriteConsoleLine("Hit Escape/Q again to cancel render.");
                                        }
                                        break;

                                    case ConsoleKey.S:
                                        if (activeChunkRenderers.IsEmpty && renderer is null)
                                        {
                                            Log("Render is not initialized yet.");
                                        }
                                        else
                                        {
                                            foreach (var activeRenderer in activeChunkRenderers.OrderBy(pair => pair.Key))
                                                Log($"Chunk {activeRenderer.Key + 1}: {activeRenderer.Value.GetRendererStatusInfo(includeQueueAndWriterStats: true)}", "STAT");
                                        }
                                        break;
                                    case ConsoleKey.P:

                                        break;
                                }
                            }

                            await Task.Delay(50);
                        }
                    });

                    Console.WriteLine($"Render job starts. Hit Escape/Q to cancel, or S to show status.");
                }

            }
            else if (!noSigInt)
            {
                var cancelled = false;
                Console.CancelKeyPress += async (s, e) =>
                {
                    e.Cancel = true;
                    if (cancelled) Process.GetCurrentProcess().Kill();
                    cancelled = true;
                    WriteConsoleLine("You hit Ctrl-C! try cancelling render...");
                    WriteConsoleLine("Hit Ctrl-C again to stop immediately.");

                    await cts.CancelAsync().WaitAsync(TimeSpan.FromSeconds(10));
                };
                Log("Render job starts. Press Ctrl-C to interrupt render process.");
            }
            else
            {
                Log("Render job starts. Press Ctrl-C to stop.");
            }

            switch (target)
            {
                case "video":
                    var videoChunkJob = await composeVideo(outputPath);
                    if (videoChunkJob is null && finalVideoBitRate is long bitRate)
                        VideoResizer.ReencodeToBitRate(outputPath, outputPath, outputEncoder, bitRate, cts.Token);
                    if (videoChunkJob is not null && !chunkOptions.KeepChunkFiles) videoChunkJob.Cleanup();
                    break;
                case "audio":
                    composeAudio(outputPath);
                    break;
                case "all":
                    if (chunkOptions.Enabled)
                    {
                        var job = await composeVideo(outputPath);
                        if (job is not null && !chunkOptions.KeepChunkFiles) job.Cleanup();
                        break;
                    }
                    var outputDir = switches.TryGetValue("outputIntermediatePath", out var iPath) ? iPath : Path.GetDirectoryName(outputPath);
                    outputDir ??= Environment.CurrentDirectory;
                    var ext = Path.GetExtension(outputPath);
                    string vidOutputPath = Path.Combine(outputDir, $"{project.ProjectName}_Intermediate_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                    string audOutputPath = Path.Combine(outputDir, $"{project.ProjectName}_Intermediate_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                    await composeVideo(vidOutputPath);
                    composeAudio(audOutputPath);
                    Console.WriteLine("Composing audio and video... this may take a few seconds.");
                    if (!File.Exists(audOutputPath) && !finalVideoBitRate.HasValue)
                    {
                        File.Move(vidOutputPath, outputPath, overwrite: true);
                    }
                    else
                    {
                        VideoAudioMuxer.MuxFromFiles(vidOutputPath, audOutputPath, outputPath, true,
                            finalVideoBitRate: finalVideoBitRate, videoEncoder: outputEncoder, cancellationToken: cts.Token);
                    }
                    try
                    {
                        File.Delete(vidOutputPath);
                        File.Delete(audOutputPath);
                    }
                    catch { }
                    break;
                default:
                    Log($"ERROR: Unknown target '{target}'.");
                    return 1;
            }

            Log($"All done! Your result file is here:{Environment.NewLine}{outputPath}");
            Environment.SetEnvironmentVariable("projectFrameCut_LastOutput", outputPath, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("projectFrameCut_RenderFinished", "1", EnvironmentVariableTarget.Process);
            return 0;
        }

        private static VfdV2PictureEncoder ParseVfdEncoder(ConcurrentDictionary<string, string> switches)
        {
            if (!Enum.TryParse<VfdCompression>(switches.GetValueOrDefault("vfdCompression", "Zstandard"), true, out var compression)
                || !Enum.IsDefined(compression))
                throw new ArgumentException("-vfdCompression must be None, Deflate, GZip, Brotli, Zstandard or Lzma2.");
            string? level = switches.GetValueOrDefault("vfdCompressionLevel");
            VfdV2PictureEncoder encoder;
            switch (compression)
            {
                case VfdCompression.Zstandard:
                    level ??= "1";
                    if (!int.TryParse(level, out var zstd) || zstd is < -131072 or > 22)
                        throw new ArgumentException("-vfdCompressionLevel for Zstandard must be an integer from -131072 to 22.");
                    encoder = new(compression, zstandardLevel: zstd);
                    break;
                case VfdCompression.Lzma2:
                    level ??= "6";
                    if (!int.TryParse(level, out var lzma) || lzma is < 1 or > 10)
                        throw new ArgumentException("-vfdCompressionLevel for Lzma2 must be an integer from 1 to 10.");
                    encoder = new(compression, lzma2Level: lzma);
                    break;
                case VfdCompression.None:
                    if (level is not null)
                        throw new ArgumentException("-vfdCompressionLevel is not supported when -vfdCompression=None.");
                    encoder = new(compression);
                    break;
                default:
                    level ??= "Fastest";
                    if (!Enum.TryParse<CompressionLevel>(level, true, out var codecLevel) || !Enum.IsDefined(codecLevel))
                        throw new ArgumentException("-vfdCompressionLevel for Deflate/GZip/Brotli must be Fastest, Optimal, SmallestSize or NoCompression.");
                    encoder = new(compression, compressionLevel: codecLevel);
                    break;
            }
            if (bool.TryParse(switches.GetValueOrDefault("incrementalRendering",
                switches.GetValueOrDefault("deferredComposition", "false")), out var deferred) && deferred)
                Log($"[Render] VFD compression={compression}, level={level ?? "none"}.");
            return encoder;
        }

        private static ChunkRenderOptions ParseChunkRenderOptions(ConcurrentDictionary<string, string> switches, int frameRate)
        {
            bool enabled = bool.TryParse(switches.GetValueOrDefault("chunkRender", "false"), out var chunkRender) && chunkRender;
            bool resume = !bool.TryParse(switches.GetValueOrDefault("chunkResume", "true"), out var chunkResume) || chunkResume;
            bool keepFiles = bool.TryParse(switches.GetValueOrDefault("chunkKeepFiles", "false"), out var chunkKeepFiles) && chunkKeepFiles;
            int parallelism = int.TryParse(switches.GetValueOrDefault("chunkParallelism", "1"), out var parsedParallelism)
                ? Math.Max(1, parsedParallelism)
                : 1;
            uint? frames = null;
            double? seconds = null;
            if (switches.TryGetValue("chunkFrames", out var chunkFramesText))
            {
                if (!uint.TryParse(chunkFramesText, out var parsedFrames) || parsedFrames == 0)
                    throw new ArgumentException("-chunkFrames must be a positive integer.");
                frames = parsedFrames;
            }
            if (switches.TryGetValue("chunkSeconds", out var chunkSecondsText))
            {
                if (!double.TryParse(chunkSecondsText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedSeconds)
                    || parsedSeconds <= 0 || double.IsNaN(parsedSeconds) || double.IsInfinity(parsedSeconds))
                    throw new ArgumentException("-chunkSeconds must be a positive finite number.");
                seconds = parsedSeconds;
            }
            if (frames.HasValue && seconds.HasValue)
                throw new ArgumentException("Specify only one of -chunkFrames and -chunkSeconds.");
            if (enabled && !frames.HasValue && !seconds.HasValue)
                frames = checked((uint)Math.Max(1, frameRate) * 60u);
            return new ChunkRenderOptions
            {
                Enabled = enabled,
                ChunkFrames = frames,
                ChunkSeconds = seconds,
                Parallelism = parallelism,
                Resume = resume,
                KeepChunkFiles = keepFiles
            };
        }

        private static string GetFileFingerprintPart(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
            var info = new FileInfo(path);
            return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }

        private static async Task<int> GoBenchSchedule(ConcurrentDictionary<string, string> switches)
        {

            if (!int.TryParse(switches.GetValueOrDefault("scheduleCandidates", "120"), out var candidates) || candidates < 1
                || !int.TryParse(switches.GetValueOrDefault("scheduleSeed", "42"), out var seed))
            {
                Log("Invalid scheduleCandidates or scheduleSeed.", "error");
                return 1;
            }
            if (switches.ContainsKey("writeToNull"))
            {
                Log("bench schedule always writes real video; omit writeToNull.", "error");
                return 1;
            }
            DynamicallyLoadedBindings.EnableAutoInitialization = false;
            DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            ffmpeg.RootPath = switches.GetOrAdd("FFmpegLibraryPath", AppContext.BaseDirectory);
            if (!DynamicallyLoadedBindings.TryInitialize())
            {
                Log("FFmpeg library failed to load.", "error");
                return 1;
            }
            FFmpegHelper.SetupFFmpegLogging();
            if (Enum.TryParse<EffectImplementType>(switches.GetValueOrDefault("ForcePreferToType"), out var type) && Enum.IsDefined(type))
                EffectHelper.ForcePreferToType = type;
            else EffectHelper.ForcePreferToType = EffectImplementType.HwAcceleration;

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                await RenderScheduleBenchmark.RunAsync(new()
                {
                    OutputDirectory = switches.GetValueOrDefault("reportDirectory", Path.Combine(Environment.CurrentDirectory, "benchmark-reports")),
                    Encoder = switches.TryGetValue("encoder", out var encoder) ? encoder : null,
                    PixelFormat = switches.GetValueOrDefault("pixelFormat", "AV_PIX_FMT_YUV420P"),
                    FFmpegVersion = ffmpeg.av_version_info(),
                    Accelerators = EffectHelper.ForcePreferToType == EffectImplementType.IPicture
                        ? [] : AcceleratorsManager.AcceleratorsForRendering.Select(a => a.Name).ToArray(),
                    CandidateCount = candidates,
                    Seed = seed,
                }, cts.Token, renderer => !Environment.GetCommandLineArgs().Contains("--nolog")
                    ? new RenderProgressDisplay(() => [renderer]) : null);
                return 0;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { return 255; }
            catch (Exception ex)
            {
                Log(ex, "Render scheduling benchmark failed");
                return 1;
            }
            finally { Console.CancelKeyPress -= cancel; }
        }

        /// <summary>
        /// 运行内置基准测试：使用 <see cref="BenchmarkSourceGenerator.GetDraftStructure"/>
        /// 生成的测试项目进行渲染管线性能测试，输出详细的帧时间统计。
        /// </summary>
        private static async Task<int> GoBenchRender(ConcurrentDictionary<string, string> switches)
        {
            // ── 解析基准测试参数 ──────────────────────────────
            const int width = 1920;
            const int height = 1080;
            const int fps = 60;

            // ── 生成测试结构 ──────────────────────────────────
            var clips = BenchmarkSourceGenerator.GetDraftStructure();
            if (clips is null || clips.Length == 0)
            {
                Log("ERROR: Benchmark structure is empty.", "error");
                return 1;
            }

            uint duration = 0;
            if(!switches.TryGetValue("duration", out var d) || !uint.TryParse(d, out duration) || duration == 0)
            {
                foreach (var clip in clips)
                {
                    duration = Math.Max(clip.StartFrame + clip.Duration, duration);
                }
            }

            Log("Initializing...");
            foreach (var clip in clips)
            {
                clip.ExtraData ??= new Dictionary<string, object>();
                clip.ReInit(IPicture.PicturePixelMode.BytePicture);
                //Log($"Clip {clip.Name} initialized: {clip.ClipType}, StartFrame: {clip.StartFrame}, Duration: {clip.Duration}");
            }
            VideoBuilder? builder = null;

            if (switches.TryGetValue("writeToNull", out var writeToStr) && bool.TryParse(writeToStr, out var writeToNull) && writeToNull)
            {
                builder = new VideoBuilder("/dev/null", width, height, fps, "BlackHoleWriter", "")
                {
                    Duration = duration,
                };
            }
            
            if(!switches.TryGetValue("startFrame", out var startFrameStr) || !uint.TryParse(startFrameStr, out var startFrame) || startFrame == 0)
            {
                startFrame = 0;
            }

            Log($"Running benchmark with {clips.Length} test clips, start frame: {startFrame}, {duration} frames, resolution: {width}x{height}, fps: {fps}, use builder: {builder != null}.");

            var cts = new CancellationTokenSource();

            #region read args
            var GCOption = 0;
            if (switches.TryGetValue("GCOptions", out var gcopt))
            {
                if (!int.TryParse(gcopt, out GCOption) || (GCOption < 0 || GCOption > 2))
                {
                    Log($"Invalid GCOptions option '{gcopt}', must be 0, 1 or 2. Default to 0.", "warn");
                    GCOption = 0;
                }
            }

            if (GCOption == 2)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            }

            string YesNo(bool b) => b ? "Yes" : "No";

            int maxParallelThreads = Environment.ProcessorCount;
            bool enableThreadAffinity = true;
            int[]? renderWorkerAffinityCpuIndexes = null;
            if (!bool.TryParse(switches.GetOrAdd("allowWorkerOversubscription", "true"), out var allowWorkerOversubscription)) allowWorkerOversubscription = true;
            if (!bool.TryParse(switches.GetOrAdd("enableThreadAffinity", "true"), out enableThreadAffinity)) enableThreadAffinity = true;
            if (switches.TryGetValue("renderWorkerAffinity", out var renderWorkerAffinityRaw) && !string.IsNullOrWhiteSpace(renderWorkerAffinityRaw))
            {
                if (!TryParseCpuIndexList(renderWorkerAffinityRaw, out renderWorkerAffinityCpuIndexes, out var renderWorkerAffinityError))
                {
                    Log($"ERROR: Invalid renderWorkerAffinity '{renderWorkerAffinityRaw}': {renderWorkerAffinityError}", "error");
                    return 1;
                }

                try
                {
                    ThreadAffinityHelper.BuildAffinityMask(renderWorkerAffinityCpuIndexes);
                }
                catch (Exception ex)
                {
                    Log($"ERROR: Invalid renderWorkerAffinity '{renderWorkerAffinityRaw}': {ex.Message}", "error");
                    return 1;
                }

                Log($"Manual render worker CPU affinity: {string.Join(", ", renderWorkerAffinityCpuIndexes)}");
            }

            if (enableThreadAffinity || (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0)
            {
                try
                {
                    var group = ThreadAffinityHelper.GetCpuCoreGroups();
                    foreach (var item in group)
                    {
                        Log($"CPU Cores ({string.Join(",", item.CpuIndexes)})'s Priority:{(item.Capacity ?? 0) + (item.EfficiencyClass ?? 0)}");
                    }
                    if (!int.TryParse(switches.TryGetValue("maxParallelThreads", out var p) ? p : "-1", out maxParallelThreads) || maxParallelThreads <= 0)
                        maxParallelThreads = (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0
                            ? renderWorkerAffinityCpuIndexes!.Length
                            : group.OrderBy(c => (c.Capacity ?? 0) + (c.EfficiencyClass ?? 0)).LastOrDefault()?.CpuIndexes?.Count ?? Environment.ProcessorCount;

                }
                catch
                {
                    if (!int.TryParse(switches.TryGetValue("maxParallelThreads", out var p) ? p : "-1", out maxParallelThreads) || maxParallelThreads <= 0)
                        maxParallelThreads = (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0 ? renderWorkerAffinityCpuIndexes!.Length : Environment.ProcessorCount;
                }
            }
            else
            {
                if (!int.TryParse(switches.TryGetValue("maxParallelThreads", out var p) ? p : "8", out maxParallelThreads)) maxParallelThreads = Environment.ProcessorCount;
            }

            var workerAffinityLabel = (renderWorkerAffinityCpuIndexes?.Length ?? 0) > 0
                ? $"manual({string.Join(",", renderWorkerAffinityCpuIndexes ?? Array.Empty<int>())})"
                : (enableThreadAffinity ? "auto" : "disabled");

            bool hwAccelDecode = bool.TryParse(switches.GetOrAdd("preferHwAccelDecoder", "false"), out var hwAccelDecodeValue) && hwAccelDecodeValue;
            bool hwAccelEncode = bool.TryParse(switches.GetOrAdd("preferHwAccelEncoding", "false"), out var hwAccelEncodeValue) && hwAccelEncodeValue;
            InternalPluginBase.HWAccelEncodeOptionGetter = new(() => hwAccelEncode);
            InternalPluginBase.HWAccelDecodeOptionGetter = new(() => hwAccelDecode);
            ClassicOverlayMixture.EnableApproximatePath = bool.TryParse(switches.GetOrAdd("ApproximateMixture", "false"), out var approximateMixture) && approximateMixture;
            Log($"Working with max {maxParallelThreads} threads, Worker affinity:{workerAffinityLabel}");
            Log($"ClassicOverlayMixture approximate path: {YesNo(ClassicOverlayMixture.EnableApproximatePath)}");
            Log($"Prefer to HwAccel decode: {YesNo(hwAccelDecode)}");
            Log($"GC Option: {GCOption}");
            Log($"Drawing.Processing.Resizing.PictureResizer: {Drawing.Processing.Resizing.PictureResizer.Default.GetType().Name}");
            if (Enum.TryParse<EffectImplementType>(switches.GetOrAdd("ForcePreferToType", "InvalidEnumOptionForDefaultFallback"), out var forcePreferToType) && Enum.IsDefined(forcePreferToType))
            {
                EffectHelper.ForcePreferToType = forcePreferToType;
                Log($"Using forced preferred effect implement type: {forcePreferToType}");
            }
            else
            {
                EffectHelper.ForcePreferToType = EffectImplementType.HwAcceleration;
                Log($"Using default preferred effect implement type: {EffectHelper.ForcePreferToType}");
            }

            #endregion

            var renderer = new Renderer
            {
                builder = builder,
                Clips = clips,
                Duration = duration,
                LogProcessStack = true,
                LogRenderState = false,
                LogStaticsData = false,
                GCOption = GCOption,
                Use16Bit = false,
                MaxThreads = Math.Max(1, maxParallelThreads),
                AllowWorkerOversubscription = allowWorkerOversubscription,
                EnableThreadAffinity = enableThreadAffinity,
                WorkerCPUCoreIndexs = renderWorkerAffinityCpuIndexes,
                TargetHeight = height,
                TargetWidth = width,
                ProjectRelativeWidth = width,
                ProjectRelativeHeight = height,
                AutoSetupRenderContext = false,
                UseHDR = false,
                StartFrame = 0
            };

            // ── Ctrl+C 处理 ──────────────────────────────────
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Log("\nCancelling benchmark...");
                cts.Cancel();
            };

            // ── 执行渲染 ─────────────────────────────────────
            Log("Starting benchmark render...");
            Console.CursorVisible = false;
            var sw = Stopwatch.StartNew();

            try
            {
                renderer.PrepareRender(cts.Token);
                if (Environment.GetCommandLineArgs().Contains("--wait"))
                {
                    Console.WriteLine("Press Enter to start benchmark render...");
                    Console.ReadLine();
                }
                using (var progress = !Environment.GetCommandLineArgs().Contains("--nolog") ? new RenderProgressDisplay(() => [renderer]) : null)
                    await renderer.GoRender(cts.Token);
                sw.Stop();
                Log($"Render done,total elapsed {sw.Elapsed}, avg elapsed {renderer.EachElapsedForPreparing.Average(t => t.TotalSeconds)} spf to prepare and {renderer.EachElapsed.Average(t => t.TotalSeconds)} spf to render");
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
                Console.CursorVisible = true;
                Log("Benchmark was cancelled by user.");
                return 255;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.CursorVisible = true;
                Log(ex, "Benchmark render failed");
                return 1;
            }

            Console.CursorVisible = true;
            Console.WriteLine(); // 换行

            // ── 统计 ProcessStack 各步骤耗时 ─────────────────
            List<List<PictureProcessStack>> stacksSnapshot = renderer.FrameProcessStacks.Select(s => s.Value).ToList();

            static IEnumerable<PictureProcessStack> FlattenStacks(IEnumerable<PictureProcessStack>? steps)
            {
                if (steps is null) yield break;
                foreach (var step in steps)
                {
                    if (step is null) continue;
                    yield return step;
                    if (step is OverlayedPictureProcessStack overlay)
                    {
                        foreach (var s in FlattenStacks(overlay.TopSteps)) yield return s;
                        foreach (var s in FlattenStacks(overlay.BaseSteps)) yield return s;
                    }
                }
            }

            static string GetStepKey(PictureProcessStack step)
            {
                var name = step.OperationDisplayName;
                if (string.IsNullOrWhiteSpace(name)) name = step.Operator?.Name;
                return string.IsNullOrWhiteSpace(name) ? "(unknown)" : name;
            }

            var sumTicksByKey = new Dictionary<string, long>(StringComparer.Ordinal);
            var countByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            var orderedKeys = new List<string>();

            foreach (var frameStack in stacksSnapshot)
            {
                foreach (var step in FlattenStacks(frameStack))
                {
                    if (step?.Elapsed is not TimeSpan elapsed) continue;
                    var key = GetStepKey(step);
                    if (!orderedKeys.Contains(key)) orderedKeys.Add(key);
                    sumTicksByKey[key] = sumTicksByKey.GetValueOrDefault(key) + elapsed.Ticks;
                    countByKey[key] = countByKey.GetValueOrDefault(key) + 1;
                }
            }

            // ── 输出结果 ─────────────────────────────────────
            var avgTime = renderer.EachElapsed.Count > 0
                ? renderer.EachElapsed.Average(ts => ts.TotalMilliseconds)
                : 0;
            var avgPrepTime = renderer.EachElapsedForPreparing.Count > 0
                ? renderer.EachElapsedForPreparing.Average(ts => ts.TotalMilliseconds)
                : 0;
            var totalTime = sw.Elapsed;
            var renderedFrames = renderer.EachElapsed.Count;

            Log("");
            Log("========================================", "stat");
            Log("  Benchmark Results", "stat");
            Log("========================================", "stat");
            Log($"  Total frames rendered : {renderedFrames}", "stat");
            Log($"  Total time            : {totalTime.TotalSeconds:F2}s", "stat");
            Log($"  Overall FPS           : {renderedFrames / Math.Max(totalTime.TotalSeconds, 0.001):F2}", "stat");
            Log($"  Avg frame render time : {avgTime:F3}ms ({1000.0 / Math.Max(avgTime + avgPrepTime, 0.001):F1} FPS)", "stat");
            Log($"  Avg prepare time      : {avgPrepTime:F3}ms", "stat");
            Log($"  Avg total per frame   : {avgTime + avgPrepTime:F3}ms", "stat");

            if (orderedKeys.Count > 0)
            {
                Log("");
                Log("  Per-step breakdown:", "stat");
                for (int i = 0; i < orderedKeys.Count; i++)
                {
                    var key = orderedKeys[i];
                    var count = countByKey.GetValueOrDefault(key);
                    if (count <= 0) continue;
                    var avg = TimeSpan.FromTicks(sumTicksByKey.GetValueOrDefault(key) / count);
                    Log($"    Step #{i + 1}: {key}", "stat");
                    Log($"      Avg: {avg.TotalMilliseconds:F3}ms  (n={count})", "stat");
                }
            }

            Log("========================================", "stat");

            // ── 资源清理 ─────────────────────────────────────
            foreach (var clip in clips)
            {
                clip.Dispose();
            }
            renderer.builder = null;

            return 0;
        }

        /// <summary>
        /// 编码性能基准测试：使用 <see cref="VideoBuilder"/>，
        /// 生成随机帧并以乱序追加，以模拟真实渲染场景中的 Cache 访问模式。
        /// 指定 -output 时可写入实际视频文件（否则使用 BlackHoleWriter）。
        /// </summary>
        private static async Task<int> GoBenchEncode(ConcurrentDictionary<string, string> switches)
        {
            // ── 参数 ────────────────────────────────────────────
            const int width = 1920;
            const int height = 1080;
            const int defaultFps = 60;

            var totalFrames = uint.TryParse(switches.GetOrAdd("totalFrames", "600"), out var tf) ? tf : 600u;
            var fpsSetting = int.TryParse(switches.GetOrAdd("fps", defaultFps.ToString()), out var fps) ? fps : defaultFps;

            // 磁盘缓存路由
            var diskCacheThreshold = switches.TryGetValue("diskCacheThreshold", out var dctStr)
                && double.TryParse(dctStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dct)
                ? Math.Clamp(dct, 0.1, 0.95) : 0.85;
            var enableDiskCache = bool.TryParse(switches.GetOrAdd("enableDiskCacheRouting", "false"), out var edc) && edc;
            var maxPendingFrames = int.TryParse(switches.GetOrAdd("maxPendingWriteFrames", "0"), out var mpf) ? mpf : 0;

            // ── 输出路径 ────────────────────────────────────────
            var outputPath = switches.TryGetValue("output", out var outPath) && !string.IsNullOrWhiteSpace(outPath)
                ? outPath.Replace("{CurrentTime}", DateTime.Now.ToString("yyyyMMdd_HHmmss"))
                : null;

            var useRealEncoder = outputPath is not null;
            string encoder, pixelFormat;
            string builderLabel;

            if (useRealEncoder)
            {
                encoder = switches.GetOrAdd("encoder", "libx264");
                pixelFormat = switches.GetOrAdd("pixelFormat", "AV_PIX_FMT_YUV420P");
                builderLabel = $"{encoder} → {outputPath}";
                Log($"Output: {outputPath}, encoder: {encoder}, pixel format: {pixelFormat}");
            }
            else
            {
                encoder = "BlackHoleWriter";
                pixelFormat = "";
                builderLabel = "BlackHoleWriter (null output)";
                Log("No -output specified; using BlackHoleWriter (no real file written).");
            }

            string YesNo(bool b) => b ? "Yes" : "No";

            Log($"Encode bench: {totalFrames} frames, {width}x{height} @ {fps}fps");
            Log($"Disk cache: {YesNo(enableDiskCache)} (threshold: {diskCacheThreshold:P0})");
            // ── 初始化 FFmpeg ────────────────────────────────
            Log("Initializing FFmpeg for encode bench...");
            DynamicallyLoadedBindings.EnableAutoInitialization = false;
            FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            ffmpeg.RootPath = switches.GetOrAdd("FFmpegLibraryPath", AppContext.BaseDirectory);
            if (FFmpeg.AutoGen.DynamicallyLoadedBindings.TryInitialize())
            {
                FFmpegHelper.SetupFFmpegLogging(ffmpeg.AV_LOG_INFO);
                Log($"FFmpeg library: version {ffmpeg.av_version_info()}, {ffmpeg.avcodec_license()}");
            }
            else
            {
                Log($"FFmpeg library failed to load. ({ffmpeg.BindingVerificationResult?.Failures?.Aggregate("", (a, b) => $"{a}{Environment.NewLine}{b.FunctionName} failed to load in {b.LibraryName}: {b.Message}")})", "error");
                return 1;
            }
            FFmpegHelper.SetupFFmpegLogging();
            Log($"FFmpeg library path: {ffmpeg.RootPath}");

            // ── 创建 VideoBuilder ──────────────────────────────
            var builder = new VideoBuilder(outputPath ?? "/dev/null", width, height, fps, encoder, pixelFormat)
            {
                Duration = totalFrames,
                // 允许乱序追加
                StrictMode = false,
                AllowDuplicatedFrameWrite = false,
                DisposeFrameAfterEachWrite = true,
                EnableDiskCacheRouting = enableDiskCache,
                DiskCacheThreshold = diskCacheThreshold,
                DiskCacheMaxPendingFrames = maxPendingFrames,
                // 关闭自动预览日志避免干扰
                EnablePreview = false,
            };

            Log($"VideoBuilder initialized with {builderLabel}.");

            // ── 生成随机帧索引（Fisher-Yates 洗牌）─────────────
            var frameIndices = new uint[totalFrames];
            for (uint i = 0; i < totalFrames; i++) frameIndices[i] = i;
            var rng = Random.Shared;
            for (int i = frameIndices.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (frameIndices[i], frameIndices[j]) = (frameIndices[j], frameIndices[i]);
            }

            // ── 预生成帧数据 ──────────────────────────────────
            Log("Pre-generating frame data...");
            var frames = new IPicture[totalFrames];
            for (uint i = 0; i < totalFrames; i++)
            {
                var r = (byte)rng.Next(256);
                var g = (byte)rng.Next(256);
                var b = (byte)rng.Next(256);
                frames[i] = Picture8bpp.GenerateSolidColor(width, height, r, g, b, a: null);
                frames[i].Tag = $"generated frame #{i}";
            }
            Log($"Generated {totalFrames} frames.");

            // ── 启动写入线程 ──────────────────────────────────
            builder.Build()?.Start();
            Log("Writer thread started.");

            // ── 乱序追加 ──────────────────────────────────────
            Log("Appending frames in random order...");
            var swAppend = Stopwatch.StartNew();
            long peakPending = 0;

            for (int i = 0; i < frameIndices.Length; i++)
            {
                var idx = frameIndices[i];
                builder.Append(idx, frames[idx]);

                var pending = builder.PendingWriteCount;
                if (pending > peakPending) peakPending = pending;

                if ((i + 1) % Math.Max(totalFrames / 10, 1) == 0)
                {
                    var pct = (double)(i + 1) / totalFrames;
                    Console.Write($"\rAppending: {pct:P0} ({i + 1}/{totalFrames}), pending: {builder.PendingWriteCount}, disk: {builder.FramesOnDisk}         ");
                }
            }
            swAppend.Stop();
            Console.WriteLine();
            Log($"Append done in {swAppend.Elapsed.TotalSeconds:F3}s, peak pending: {peakPending}, final pending: {builder.PendingWriteCount}, disk: {builder.FramesOnDisk}");

            var appendFps = totalFrames / swAppend.Elapsed.TotalSeconds;

            // ── 等待写入/编码完成 ─────────────────────────────
            Log("Waiting for writer to drain (encode remaining frames)...");
            var swFlush = Stopwatch.StartNew();
            builder.Finish(
                regenerator: idx => Picture8bpp.GenerateSolidColor(width, height, 0, 0, 0, null),
                totalFrames: totalFrames,
                onWritingProgressUpdate: (c, p) => Console.Write($"\rFlushing: frame #{c} / {totalFrames} ({p:P2})    ")
            );
            swFlush.Stop();
            Console.WriteLine();

            var totalTime = swFlush.Elapsed;
            var encodeFps = totalFrames / Math.Max(totalTime.TotalSeconds, 0.001);

            // ── 结果统计 ──────────────────────────────────────
            Log("");
            Log("========================================", "stat");
            if (useRealEncoder)
                Log("  Encode Benchmark Results (real video)", "stat");
            else
                Log("  Encode Benchmark Results (null device)", "stat");
            Log("========================================", "stat");
            Log($"  Total frames       : {totalFrames}", "stat");
            Log($"  Resolution         : {width}x{height}", "stat");
            Log($"  FPS setting        : {fps}", "stat");
            if (useRealEncoder)
            {
                Log($"  Encoder            : {encoder}", "stat");
                Log($"  Pixel format       : {pixelFormat}", "stat");
                Log($"  Output file        : {outputPath}", "stat");
            }
            Log($"  Disk cache         : {YesNo(enableDiskCache)} (@ {diskCacheThreshold:P0} threshold)", "stat");
            Log($"  Append FPS         : {appendFps:F2}", "stat");
            Log($"  Encode FPS         : {encodeFps:F2}", "stat");
            if (useRealEncoder)
            {
                var bitsPerFrame = (long)width * height * 3 * 8; // approx YUV 4:2:0 → 12 bpp → 12 bits
                var bitrate = (long)(bitsPerFrame * fps);
                Log($"  Est. bitrate       : {bitrate / 1000.0 / 1000.0:F1} Mbps (raw, uncompressed)", "stat");
            }
            Log($"  Total time         : {totalTime.TotalSeconds:F3}s", "stat");
            Log($"  Peak pending count : {peakPending}", "stat");
            Log($"  Final cache cleared: {builder.WrittenFramesCount}/{builder.TotalFramesCount}", "stat");
            Log("========================================", "stat");

            builder.Dispose();
            return 0;
        }

        /// <summary>
        /// 解码性能基准测试：创建 <see cref="IVideoSource"/> 并顺序解码所有帧，
        /// 测量解码吞吐量（FPS）和速度倍率。
        /// </summary>
        private static async Task<int> GoBenchDecode(ConcurrentDictionary<string, string> switches)
        {
            // ── 参数 ────────────────────────────────────────────
            if (!switches.TryGetValue("source", out var sourcePath) || string.IsNullOrWhiteSpace(sourcePath))
            {
                Log("ERROR: Decode benchmark requires -source=<video file path>.", "error");
                return 1;
            }

            if (!File.Exists(sourcePath))
            {
                Log($"ERROR: Source file '{sourcePath}' not found.", "error");
                return 1;
            }

            var maxFrames = int.TryParse(switches.TryGetValue("maxFrames", out var mf) ? mf : "0", out var parsedMf) && parsedMf > 0 ? parsedMf : 0;
            Log($"Decode bench source: {sourcePath}");

            // ── 创建解码器 ──────────────────────────────────────
            Log("Creating video source...");
            IVideoSource? source = null;
            try
            {
                source = PluginManager.CreateVideoSource(sourcePath);
            }
            catch (Exception ex)
            {
                Log(ex, "Failed to create video source");
                return 1;
            }

            if (source is null)
            {
                Log("ERROR: Failed to create video source - no plugin supports this file.", "error");
                return 1;
            }

            source.Initialize();
            source.StrictMode = false;

            var totalFrames = source.TotalFrames > 0 ? source.TotalFrames : 0;
            if (totalFrames <= 0)
            {
                Log("WARN: Cannot determine total frame count, estimating via probing...", "warn");
                // 尝试探测帧数
                totalFrames = 0;
                for (uint probe = 0; probe < 100000; probe += 100)
                {
                    try
                    {
                        using var probeFrame = source.GetFrame(probe);
                        if (probeFrame is null) break;
                        totalFrames = probe + 1;
                    }
                    catch { break; }
                }
                if (totalFrames <= 0)
                {
                    Log("ERROR: Cannot determine total frame count, aborting.", "error");
                    source.Dispose();
                    return 1;
                }
            }

            var decodeTotal = maxFrames > 0 ? Math.Min(maxFrames, totalFrames) : totalFrames;

            Log($"Source: {source.Width}x{source.Height}, {source.Fps:F2}fps, {decodeTotal}/{totalFrames} frames, decoder: {source.TypeName}");

            // ── 热启动（丢弃前几帧加载开销）────────────────────
            var warmupCount = (int)Math.Min(30, decodeTotal / 10);
            Log($"Warming up ({warmupCount} frames)...");
            for (uint i = 0; i < warmupCount; i++)
            {
                using var warmupFrame = source.GetFrame(i);
            }
            Log("Warm-up done.");

            // ── 解码测试 ──────────────────────────────────────
            Log("Starting decode benchmark...");
            Console.CursorVisible = false;
            var sw = Stopwatch.StartNew();
            long decodedBytes = 0;
            int decodedCount = 0;

            for (uint i = 0; i < decodeTotal; i++)
            {
                using var frame = source.GetFrame(i);
                if (frame is not null)
                {
                    decodedCount++;
                    decodedBytes += frame.Width * frame.Height * (frame.BitPerPixel / 8);
                }

                if ((i + 1) % (Math.Max(decodeTotal / 50, 1)) == 0 || i == decodeTotal - 1)
                {
                    var pct = (double)(i + 1) / decodeTotal;
                    var elapsed = sw.Elapsed.TotalSeconds;
                    var currentFps = (i + 1) / Math.Max(elapsed, 0.001);
                    Console.Write($"\rDecoding: {pct:P1} ({i + 1}/{decodeTotal}), {currentFps:F1} FPS, elapsed: {elapsed:F1}s      ");
                }
            }
            sw.Stop();
            Console.WriteLine();
            Console.CursorVisible = true;

            // ── 结果统计 ──────────────────────────────────────
            var totalTime = sw.Elapsed;
            var decodeFps = decodedCount / Math.Max(totalTime.TotalSeconds, 0.001);
            var speedVsRealtime = decodeFps / Math.Max(source.Fps, 1.0);
            var bandwidthMbps = (decodedBytes / Math.Max(totalTime.TotalSeconds, 0.001)) / (1024.0 * 1024.0);

            Log("");
            Log("========================================", "stat");
            Log("  Decode Benchmark Results", "stat");
            Log("========================================", "stat");
            Log($"  Source file        : {Path.GetFileName(sourcePath)}", "stat");
            Log($"  Resolution         : {source.Width}x{source.Height}", "stat");
            Log($"  Source FPS         : {source.Fps:F2}", "stat");
            Log($"  Decoder            : {source.TypeName}", "stat");
            Log($"  Frames decoded     : {decodedCount}/{decodeTotal}", "stat");
            Log($"  Total time         : {totalTime.TotalSeconds:F3}s", "stat");
            Log($"  Decode FPS         : {decodeFps:F2}", "stat");
            Log($"  Speed vs realtime  : {speedVsRealtime:F2}x", "stat");
            Log($"  Bandwidth          : {bandwidthMbps:F1} MB/s (raw pixel)", "stat");
            Log($"  Avg per frame      : {(totalTime.TotalMilliseconds / Math.Max(decodedCount, 1)):F3}ms", "stat");
            Log("========================================", "stat");

            source.Dispose();
            return 0;
        }

        /// <summary>
        /// 重新编码模式：解码输入视频，然后使用指定编码器重新编码输出。
        /// 用于测试编解码器的正确性和性能。
        /// </summary>
        private static async Task<int> GoReencode(ConcurrentDictionary<string, string> switches)
        {
            // ── 参数 ────────────────────────────────────────────
            if (!switches.TryGetValue("source", out var sourcePath) || string.IsNullOrWhiteSpace(sourcePath))
            {
                Log("ERROR: Reencode requires -source=<input video>.", "error");
                return 1;
            }
            if (!switches.TryGetValue("output", out var outputPath) || string.IsNullOrWhiteSpace(outputPath))
            {
                Log("ERROR: Reencode requires -output=<output file>.", "error");
                return 1;
            }
            if (!File.Exists(sourcePath))
            {
                Log($"ERROR: Source file '{sourcePath}' not found.", "error");
                return 1;
            }

            bool hwAccelDecode = bool.TryParse(switches.GetOrAdd("preferHwAccelDecoder", "false"), out var hwAccelDecodeValue) && hwAccelDecodeValue;
            bool hwAccelEncode = bool.TryParse(switches.GetOrAdd("preferHwAccelEncoder", "false"), out var hwAccelEncodeValue) && hwAccelEncodeValue;
            bool multiStream = bool.TryParse(switches.GetOrAdd("multiStream", "false"), out var multiStreamValue) && multiStreamValue;
            InternalPluginBase.HWAccelDecodeOptionGetter = new(() => hwAccelDecode);
            InternalPluginBase.HWAccelEncodeOptionGetter = new(() => hwAccelEncode);
            Log($"Use hardware acceleration for decoding: {hwAccelDecode}, encoding: {hwAccelEncode}");

            outputPath = outputPath.Replace("{CurrentTime}", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            if (multiStream && !outputPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            {
                Log("ERROR: -multiStream=true requires an .mkv output path.", "error");
                return 1;
            }

            var encoder = switches.GetOrAdd("encoder", "libx264");
            var pixelFormatStr = switches.GetOrAdd("pixelFormat", "AV_PIX_FMT_YUV420P");
            var maxFrames = int.TryParse(switches.TryGetValue("maxFrames", out var mf) ? mf : "0", out var parsedMf) && parsedMf > 0 ? parsedMf : 0;
            var bitRate = long.TryParse(switches.TryGetValue("bitRate", out var br) ? br : "0", out var parsedBr) && parsedBr > 0 ? parsedBr : 4_000_000L;

            // ── 创建解码器 ──────────────────────────────────────
            Log($"Creating decoder for: {sourcePath}");
            IVideoSource? source = null;
            try
            {
                source = multiStream && HDRDecoderContext.IsHdrVideo(sourcePath)
                    ? new HDRDecoderContext(sourcePath)
                    : PluginManager.CreateVideoSource(sourcePath);
                Log($"Created decoder: {source?.TypeName ?? "(null)"}");
            }
            catch (Exception ex)
            {
                Log(ex, "Failed to create video source");
                return 1;
            }

            if (source is null)
            {
                Log("ERROR: Failed to create video source - no plugin supports this file.", "error");
                return 1;
            }

            source.Initialize();
            source.StrictMode = false;

            var totalFrames = source.TotalFrames > 0 ? source.TotalFrames : 0;
            if (totalFrames <= 0)
            {
                Log("ERROR: Cannot determine total frame count.", "error");
                source.Dispose();
                return 1;
            }

            var decodeCount = maxFrames > 0 ? Math.Min(maxFrames, (int)totalFrames) : (int)totalFrames;
            var srcWidth = source.Width;
            var srcHeight = source.Height;
            var srcFps = source.Fps;

            if (srcWidth <= 0 || srcHeight <= 0 || srcFps <= 0)
            {
                Log($"ERROR: Invalid source dimensions ({srcWidth}x{srcHeight}) or FPS ({srcFps}).", "error");
                source.Dispose();
                return 1;
            }

            Log($"Source: {srcWidth}x{srcHeight}, {srcFps:F2}fps, {decodeCount}/{totalFrames} frames, decoder: {source.TypeName}");

            Log($"Creating encoder: {encoder}, pixel format: {pixelFormatStr}, {srcWidth}x{srcHeight} @ {srcFps:F0}fps");

            IVideoWriter writer = multiStream
                ? new AlphaBrightnessVideoWriter
                {
                    Width = srcWidth,
                    Height = srcHeight,
                    FramePerSecond = (int)Math.Round(srcFps),
                    CodecName = encoder,
                    PixelFormat = pixelFormatStr,
                    OutputPath = outputPath,
                    BitRate = bitRate,
                    Channels = AuxiliaryVideoChannels.Alpha | AuxiliaryVideoChannels.Brightness,
                }
                : hwAccelEncode
                    ? new VideoWriterHWAccel
                    {
                        Width = srcWidth,
                        Height = srcHeight,
                        FramePerSecond = (int)Math.Round(srcFps),
                        CodecName = encoder,
                        PixelFormat = pixelFormatStr,
                        OutputPath = outputPath,
                        BitRate = bitRate,
                    }
                    : new VideoWriter
                    {
                        Width = srcWidth,
                        Height = srcHeight,
                        FramePerSecond = (int)Math.Round(srcFps),
                        CodecName = encoder,
                        PixelFormat = pixelFormatStr,
                        OutputPath = outputPath,
                        BitRate = bitRate,
                    };

            if (multiStream && hwAccelEncode)
            {
                Log("WARNING: -preferHwAccelEncoder is not supported by multistream output; using the software writer.", "warn");
            }

            try
            {
                writer.Initialize();
            }
            catch (Exception ex)
            {
                Log(ex, "Failed to initialize encoder");
                source.Dispose();
                return 1;
            }

            Log($"Encoder initialized: {outputPath}");

            // ── 逐帧转码 ──────────────────────────────────────
            Log("Starting reencode...");
            var sw = Stopwatch.StartNew();
            int encodedCount = 0;

            for (uint i = 0; i < decodeCount; i++)
            {
                try
                {
                    using (var frame = multiStream && source is IHDRVideoSource hdrSource
                        ? hdrSource.GetHDRFrame(i, hasAlpha: true)
                        : source.GetFrame(i))
                    {
                        if (multiStream && frame.BitPerPixel != IPicture.PicturePixelMode.UShortPicture)
                        {
                            using var converted = frame.ToBitPerPixel(IPicture.PicturePixelMode.UShortPicture);
                            writer.Append(converted);
                        }
                        else
                        {
                            writer.Append(frame);
                        }
                        encodedCount++;
                    }

                    var elapsed = sw.Elapsed.TotalSeconds;
                    var fps = (i + 1) / Math.Max(elapsed, 0.001);
                    Console.Write($"\rReencoding: {((double)(i + 1) / decodeCount):P1}, {fps:F1} FPS， ETA: {FormatEta(TimeSpan.FromSeconds((decodeCount - (i + 1)) / Math.Max(fps, 0.001)))} ");
                }
                catch (Exception ex)
                {
                    Log(ex, $"Failed to encode frame #{i}");
                }

            }
            sw.Stop();
            Console.WriteLine();

            // ── 完成编码 ──────────────────────────────────────
            Log("Finishing encoding...");
            try
            {
                writer.Finish();
            }
            catch (Exception ex)
            {
                Log(ex, "Error during encoder finish");
            }

            // ── 结果统计 ──────────────────────────────────────
            var totalTime = sw.Elapsed;
            var overallFps = encodedCount / Math.Max(totalTime.TotalSeconds, 0.001);

            Log("");
            Log("========================================", "stat");
            Log("  Reencode Results", "stat");
            Log("========================================", "stat");
            Log($"  Source file       : {Path.GetFileName(sourcePath)}", "stat");
            Log($"  Output file       : {outputPath}", "stat");
            Log($"  Resolution        : {srcWidth}x{srcHeight}", "stat");
            Log($"  FPS               : {srcFps:F2}", "stat");
            Log($"  Decoder           : {source.TypeName}", "stat");
            Log($"  Encoder           : {encoder}", "stat");
            Log($"  Pixel format      : {pixelFormatStr}", "stat");
            Log($"  Multi-stream      : {multiStream}", "stat");
            Log($"  Bitrate           : {bitRate / 1000.0 / 1000.0:F1} Mbps", "stat");
            Log($"  Frames encoded    : {encodedCount}", "stat");
            Log($"  Total time        : {totalTime.TotalSeconds:F3}s", "stat");
            Log($"  Overall FPS       : {overallFps:F2}", "stat");
            if (srcFps > 0)
                Log($"  Speed vs realtime : {(overallFps / srcFps):F2}x", "stat");
            Log($"  Avg per frame     : {(totalTime.TotalMilliseconds / Math.Max(encodedCount, 1)):F3}ms", "stat");
            Log("========================================", "stat");

            source.Dispose();
            writer.Dispose();
            return 0;
        }

        public static IClip[] JSONToIClips(DraftStructureJSON json, IDictionary<string, AssetItem> assets, IPicture.PicturePixelMode bpp)
        {
            var clips = json.Clips;
            if (clips is null || clips.Length == 0)
            {
                return Array.Empty<IClip>();
            }

            var clipsList = new List<IClip>();

            foreach (var clip in clips)
            {
                if (clip.ClipType == ClipMode.MarkingClip)
                {
                    continue;
                }

                var clipJson = JsonSerializer.SerializeToElement(clip);
                var clipInstance = PluginManager.CreateClip(clipJson);
                if (clipInstance.FilePath?.StartsWith('$') ?? false)
                {
                    try
                    {
                        var id = clipInstance.FilePath.Substring(1);
                        if (!assets.TryGetValue(id, out var value))
                        {
                            throw new FileNotFoundException($"Asset for clip {clipInstance.Name} ({clipInstance.TypeName}) '{id}' is not exist.");
                        }
                        clipInstance.FilePath = value.Path;
                    }
                    catch (InvalidOperationException)
                    {
                        //safe to ignore
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
                else if (string.IsNullOrEmpty(clipInstance.FilePath) && !string.IsNullOrEmpty(clip.FilePath) && clipInstance.NeedFilePath)
                {
                    try
                    {
                        clipInstance.FilePath = clip.FilePath;
                    }
                    catch (InvalidOperationException)
                    {
                        //safe to ignore
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
                if (clipInstance.ClipType != ClipMode.AudioClip) clipInstance.ReInit(bpp);
                clipsList.Add(clipInstance);

            }
            return clipsList.ToArray();
        }

        public static ISoundTrack[] JSONToISoundTracks(DraftStructureJSON json, IDictionary<string, AssetItem> assets)
        {
            var tracks = json.SoundTracks;
            if (tracks is null || tracks.Length == 0)
            {
                return Array.Empty<ISoundTrack>();
            }

            var tracksList = new List<ISoundTrack>();

            foreach (var track in tracks)
            {
                var trackJson = JsonSerializer.SerializeToElement(track);
                var trackInstance = PluginManager.CreateSoundTrack(trackJson);
                trackInstance.ExtraData = track.MetaData ?? new();
                trackInstance.Ratio = track.SecondPerFrameRatio > 0 ? track.SecondPerFrameRatio : 1f;

                if (trackInstance.ExtraData.TryGetValue("Volume", out var trackVolObj))
                {
                    trackInstance.Volume = trackVolObj switch
                    {
                        double d => (float)d,
                        float f => f,
                        System.Text.Json.JsonElement je when je.TryGetDouble(out var jd) => (float)jd,
                        _ when float.TryParse(trackVolObj?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pv) => pv,
                        _ => 1f
                    };
                }

                if (trackInstance.FilePath?.StartsWith('$') ?? false)
                {
                    try
                    {
                        var id = trackInstance.FilePath.Substring(1);
                        if (!assets.TryGetValue(id, out var value))
                        {
                            throw new FileNotFoundException($"Asset for clip {trackInstance.Name} ({trackInstance.TypeName}) '{id}' is not exist.");
                        }
                        trackInstance.FilePath = value.Path;
                    }
                    catch (InvalidOperationException)
                    {
                        //safe to ignore
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
                else if (string.IsNullOrEmpty(trackInstance.FilePath) && !string.IsNullOrEmpty(track.FilePath) && trackInstance.NeedFilePath)
                {
                    try
                    {
                        trackInstance.FilePath = track.FilePath;
                    }
                    catch (InvalidOperationException)
                    {
                        //safe to ignore
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }

                if (SoundTrackMetadata.ReadBool(trackInstance.ExtraData, SoundTrackMetadata.EnabledKey, true)) SoundTrackMetadata.ReInit(trackInstance);
                tracksList.Add(trackInstance);
            }

            return tracksList.ToArray();
        }

        public static string GetInfo(bool all = false)
        {
            StringBuilder builder = new StringBuilder();
            var renderType = typeof(Renderer).Assembly;
            var baseType = typeof(IPluginBase).Assembly;
            string renderHash = "";
            try
            {
                renderHash = !renderType.IsDynamic && Path.Exists(renderType.Location) ? HashServices.ComputeFileHash(renderType.Location) : "unknown";
            }
            catch { renderHash = "unknown"; }
            string baseHash = "";
            try
            {
                baseHash = !baseType.IsDynamic && Path.Exists(baseType.Location) ? HashServices.ComputeFileHash(baseType.Location) : "unknown";
            }
            catch { baseHash = "unknown"; }
            builder.AppendLine($"BaseAPI Definition: V{IPluginBase.CurrentPluginAPIVersion}({baseType.GetName().Version}), hash:{baseHash}");
            builder.AppendLine($"Core render library: v{renderType.GetName().Version}, hash:{renderHash}");
            if (all)
            {
                List<string> printedAsb = new();
                foreach (var asb in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (printedAsb.Contains(asb.FullName))
                        {
                            continue;
                        }
                        printedAsb.Add(asb.FullName);
                        var guid = asb.GetCustomAttribute<GuidAttribute>()?.Value ?? "unknown";
                        string asbHash = "";
                        try
                        {
                            asbHash = !asb.IsDynamic && Path.Exists(asb.Location) ? HashServices.ComputeFileHash(asb.Location) : "unknown";
                        }
                        catch { asbHash = "unknown"; }

                        builder.AppendLine($"Assembly {asb.FullName}, {asb.GetName().Version} GUID:{guid} hash:{asbHash}");
                    }
                    catch
                    {
                        builder.AppendLine($"{asb.FullName}, cannot get assembly info.");
                    }
                    finally
                    {
                        builder.AppendLine();
                    }
                }

                builder.AppendLine(
                    """
                    The standalone render is the part of projectFrameCut. To use this standalone render, you MUST have a license/permission to use the projectFrameCut itself.
                    Most of the project, and this standalone render is licensed under Apache License.
                    The use of other open-source project can be found in readme of the repository:
                    https://github.com/hexadecimal0x12e/projectFrameCut/

                    """
                    );
            }
            return builder.ToString();
        }

        private static Assembly? TryResolveAssembly(string name, string[] paths, bool keepInMemory)
        {
            Log($"Try loading assembly {name}...");
            foreach (var item in paths)
            {
                var p = Path.Combine(item, name + ".dll");
                if (!File.Exists(p)) continue;
                Log($"Found assembly {name} in {p}.");
                if (keepInMemory)
                {
                    var fs = File.OpenRead(p);
                    var buf = new byte[fs.Length];
                    fs.ReadExactly(buf);
                    fs.Dispose();
                    return Assembly.Load(buf);
                }
                else
                {
                    return Assembly.LoadFile(p);
                }

            }
            Log($"Assembly {name} not found.");
            return null;
        }
    }
}
