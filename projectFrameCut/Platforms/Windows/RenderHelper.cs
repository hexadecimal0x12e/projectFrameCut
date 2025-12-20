using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace projectFrameCut.Platforms.Windows
{
    public partial class RenderHelper
    {

        Process _proc = new();

        public event Action<string>? OnLog;

        public event Action<double>? OnProgressChanged;

        public event Action<string>? OnSubProgChanged;

        public async Task<int> StartRender(string args)
        {
            _proc = new();

            var pipeName = ExtractPipeName(args) ?? ("pjfc_plugin_" + Guid.NewGuid().ToString("N"));
            if (!args.Contains("pluginConnectionPipe=", StringComparison.OrdinalIgnoreCase))
                args = args.TrimEnd() + $" -pluginConnectionPipe={pipeName}";

            _proc.StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "projectFrameCut.Render.WindowsRender.exe"),
                WorkingDirectory = Path.Combine(AppContext.BaseDirectory),
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                Environment =
                {
                    ["projectFrameCut"] = $"projectFrameCut/{Assembly.GetExecutingAssembly().GetName().Version}",
                }
            };

            if (SettingsManager.IsBoolSettingTrue("render_ShowBackendConsole"))
            {
                _proc.StartInfo.RedirectStandardOutput = false;
                _proc.StartInfo.CreateNoWindow = false;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await PluginPipeTransport.SendEnabledPluginsAsync(pipeName);
                }
                catch (Exception ex)
                {
                    Log(ex, "send plugins via pipe", nameof(RenderHelper));
                }
            });

            _proc.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {

                    Log(e.Data, "Render");
                    OnLog?.Invoke(e.Data);

                }
            };

            _proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    if (e.Data.StartsWith("@@"))
                    {
                        var parts = e.Data.Trim('@').Split(',', 2, StringSplitOptions.TrimEntries);
                        if (int.TryParse(parts[0], out var working) && int.TryParse(parts[1], out var total))
                        {
                            if (total > 0)
                                OnProgressChanged?.Invoke((working / (float)total));
                        }
                    }
                    else if (e.Data.StartsWith("##"))
                    {
                        var msg = e.Data.Trim('#').Trim();
                        OnSubProgChanged?.Invoke(msg);
                    }
                    else
                    {
                        Log(e.Data, "Render_err");
                        OnLog?.Invoke(e.Data);

                    }
                }
            };

            _proc.EnableRaisingEvents = true;
            _proc.Exited += (s, e) =>
            {
                Log($"Render process exited with code {_proc.ExitCode}", "Render");
            };

            _proc.Start();
            if (!SettingsManager.IsBoolSettingTrue("render_ShowBackendConsole"))
            {
                _proc.BeginOutputReadLine();
            }
            _proc.BeginErrorReadLine();

            Log("Render process started.", "Render");

            await _proc.WaitForExitAsync();

            return _proc.ExitCode;
        }

        private static string? ExtractPipeName(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
                return null;
            try
            {
                var m = PluginConnectionPipeRegex().Match(args);
                if (!m.Success)
                    return null;
                var raw = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                return string.IsNullOrWhiteSpace(raw) ? null : raw;
            }
            catch
            {
                return null;
            }
        }

        internal void Cancel()
        {
            _proc.Kill();
        }

        [GeneratedRegex(@"(?i)(?:^|\s)[-/]pluginConnectionPipe=(?:""([^""]+)""|(\S+))", RegexOptions.None, "zh-CN")]
        private static partial Regex PluginConnectionPipeRegex();
    }

    public class FFmpegStatistics
    {
        public int Frame { get; set; }
        public double Fps { get; set; }
        public double Quality { get; set; }
        public string Size { get; set; } = string.Empty;
        public TimeSpan Time { get; set; }
        public string Bitrate { get; set; } = string.Empty;
        public double Speed { get; set; }
    }

    public partial class ffmpegHelper
    {
        Process _proc;

        public double totalFrames = 1;
        public TimeSpan totalDuration = TimeSpan.Zero;

        public event Action<string>? OnLog;

        public event Action<double>? OnProgressChanged;

        public event Action<FFmpegStatistics>? OnStatisticsUpdate;

        private static readonly Regex StatsRegex = StatsRegexGetter();

        public async Task<int> Run(string args)
        {
            try
            {
                _proc = new();
                _proc.StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(AppContext.BaseDirectory, "FFmpeg", "8.x_internal", "ffmpeg.exe"),
                    WorkingDirectory = Path.Combine(AppContext.BaseDirectory),
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,

                };

                _proc.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {

                        Log(e.Data, "FFmpeg");

                        OnLog?.Invoke(e.Data);

                    }
                };

                _proc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        Log(e.Data, "FFmpeg");
                        OnLog?.Invoke(e.Data);

                        // 尝试解析统计信息
                        var stats = ParseStatistics(e.Data);
                        if (stats != null)
                        {
                            OnStatisticsUpdate?.Invoke(stats);

                            if (totalFrames > 0)
                            {
                                var progress = stats.Frame / totalFrames;
                                OnProgressChanged?.Invoke(progress);
                            }
                            else if (totalDuration > TimeSpan.Zero)
                            {
                                var progress = stats.Time.TotalSeconds / totalDuration.TotalSeconds;
                                OnProgressChanged?.Invoke(Math.Clamp(progress, 0, 1));
                            }
                        }
                    }
                };

                _proc.EnableRaisingEvents = true;
                _proc.Exited += (s, e) =>
                {
                    Log($"Render process exited with code {_proc.ExitCode}", "Render");
                };

                _proc.Start();

                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();

                Log("Render process started.", "Render");

                await _proc.WaitForExitAsync();

                return _proc.ExitCode;
            }
            catch (Exception ex)
            {
                Log(ex, "run ffmpeg", this);
            }

            return -1;



        }

        private FFmpegStatistics? ParseStatistics(string line)
        {
            var match = StatsRegex.Match(line);
            if (!match.Success)
                return null;

            try
            {
                var stats = new FFmpegStatistics();

                if (int.TryParse(match.Groups["frame"].Value, out var frame))
                    stats.Frame = frame;

                if (double.TryParse(match.Groups["fps"].Value, out var fps))
                    stats.Fps = fps;

                if (double.TryParse(match.Groups["q"].Value, out var q))
                    stats.Quality = q;

                stats.Size = match.Groups["size"].Value;

                var timeStr = match.Groups["time"].Value;
                if (TryParseFFmpegTime(timeStr, out var time))
                    stats.Time = time;

                stats.Bitrate = match.Groups["bitrate"].Value;

                if (double.TryParse(match.Groups["speed"].Value, out var speed))
                    stats.Speed = speed;

                return stats;
            }
            catch
            {
                return null;
            }
        }

        private bool TryParseFFmpegTime(string timeStr, out TimeSpan time)
        {
            time = TimeSpan.Zero;

            var parts = timeStr.TrimStart('-').Split(':');
            if (parts.Length != 3)
                return false;

            if (!int.TryParse(parts[0], out var hours))
                return false;
            if (!int.TryParse(parts[1], out var minutes))
                return false;
            if (!double.TryParse(parts[2], out var seconds))
                return false;

            time = new TimeSpan(0, hours, minutes, 0).Add(TimeSpan.FromSeconds(seconds));
            return true;
        }

        internal void Cancel()
        {
            _proc?.Kill();
        }

        [GeneratedRegex(@"frame=\s*(?<frame>\d+)\s+fps=\s*(?<fps>[\d.]+)\s+q=\s*(?<q>[\d.-]+)\s+(?:L?size=\s*(?<size>\S+)\s+)?time=\s*(?<time>-?[\d:.]+)\s+bitrate=\s*(?<bitrate>\S+)\s+(?:dup=\s*(?<dup>\d+)\s+)?(?:drop=\s*(?<drop>\d+)\s+)?speed=\s*(?<speed>[\d.]+)x?", RegexOptions.IgnoreCase | RegexOptions.Compiled, "zh-CN")]
        private static partial Regex StatsRegexGetter();
    }
}
