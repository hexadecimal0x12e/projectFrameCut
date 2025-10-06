using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Platforms.Windows
{
    public class RenderHelper
    {

        Process _proc = new();

        public event Action<string>? OnLog;

        public event Action<double>? OnProgressChanged;

        public async Task StartRender(string args)
        {
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
#if DEBUG
                    ["pjfc_dbg"] = "1",
#endif
                }
            };

            _proc.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {

                    Log(e.Data,"Render");
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
                        if(int.TryParse(parts[0], out var working) && int.TryParse(parts[1], out var total))
                        {

                            OnProgressChanged?.Invoke((working / (float)total));
                        }
                    }
                    else
                    {
                        Log(e.Data,"Render_err");
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

            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            Log("Render process started.", "Render");

            await _proc.WaitForExitAsync();

        }
    }
}
