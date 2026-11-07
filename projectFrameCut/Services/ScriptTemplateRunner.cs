#if !HEADLESS
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Asset;
using projectFrameCut.Template;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace projectFrameCut.Services;

internal sealed class ScriptTemplateRunner : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<DraftPage, CancellationTokenSource> Running = new();
    private readonly string root;
    private readonly string executable;
    private bool disposed;

    private ScriptTemplateRunner(string root, string executable)
    {
        this.root = root;
        this.executable = executable;
    }

    internal static bool IsRunning(DraftPage page) => Running.ContainsKey(page);
    internal static void Cancel(DraftPage page)
    {
        if (page.IsClosed) page.ScriptTemplateLifetime.Cancel();
        if (Running.TryGetValue(page, out var cts)) cts.Cancel();
    }

    internal static async Task<ScriptTemplateRunner?> PrepareWithUiAsync(DraftPage page, ScriptBasedTemplateStructure template,
        IReadOnlyDictionary<string, string?> values)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(page.ScriptTemplateLifetime.Token);
        if (!Running.TryAdd(page, cts)) return null;
        var cancel = new Button { Text = Localized._Cancel };
        cancel.Clicked += (_, _) => cts.Cancel();
        var closable = page.IsPopupClosableByTapBackground;
        try
        {
            page.IsPopupClosableByTapBackground = false;
            await page.ShowAPopup(new VerticalStackLayout
            {
                Padding = 24, Spacing = 12, MinimumWidthRequest = 280,
                Children = { new Label { Text = Localized.ScriptTemplate_Running }, new ActivityIndicator { IsRunning = true }, cancel }
            }, mode: "dialog");
            return await PrepareAsync(template, values, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return null; }
        finally
        {
            Running.TryRemove(page, out _);
            page.IsPopupClosableByTapBackground = closable;
            try { await page.HidePopup(true); }
            catch (Exception ex) { Log(ex, "Close script template preparation", typeof(ScriptTemplateRunner)); }
        }
    }

    internal static async Task<ScriptTemplateRunner> PrepareAsync(ScriptBasedTemplateStructure template,
        IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
#if WINDOWS
        ExternalRpcPipeSettings.EnsureAllowed(MauiProgram.BasicDataPath);
        if (!RenderRpcBootstrap.SupportsCliRenderProcess)
            throw new NotSupportedException("Script templates require the RPC render backend.");
        if (string.IsNullOrWhiteSpace(template.ScriptPath) || !File.Exists(template.ScriptPath))
            throw new FileNotFoundException(Localized.ScriptTemplateCreatePage_NoScript);
        var script = await File.ReadAllTextAsync(template.ScriptPath, ct);
        if (string.IsNullOrWhiteSpace(template.ScriptHash) || !string.Equals(template.ScriptHash,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The template script hash does not match its package.");
        var module = Path.Combine(AppContext.BaseDirectory, "Public", "PowerShell", "projectFrameCut.PowerShell.psd1");
        if (!File.Exists(module)) throw new FileNotFoundException("The bundled PowerShell module is missing.", module);
        var executable = await FindPowerShellAsync(ct);
        var runner = new ScriptTemplateRunner(Path.Combine(Platforms.Windows.WindowsPluginIsolationPlatform.SessionDirectory,
            "script-" + Guid.NewGuid().ToString("N")), executable);
        try
        {
            Directory.CreateDirectory(runner.root);
            await File.WriteAllTextAsync(Path.Combine(runner.root, "script.ps1"), script, ct);
            static string Key(string key) => key.Trim().Trim('{', '}').Trim();
            var defaults = (template.Variables ?? new()).ToDictionary(kv => Key(kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            var definitions = (template.VariableDefinitions ?? new()).ToDictionary(kv => Key(kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            var inputs = values.ToDictionary(kv => Key(kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            var parameters = new JsonObject();
            foreach (var key in defaults.Keys.Concat(definitions.Keys).Concat(inputs.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!TemplateCreatePage.TryResolveVariable(key, inputs, defaults, definitions,
                    out var value, out var type)) continue;
                if (type == TemplateVariableType.File && !string.IsNullOrWhiteSpace(value))
                {
                    if (value.StartsWith('$'))
                    {
                        var id = value[1..];
                        if (template.AssetHashTable?.TryGetValue(id, out var path) == true) value = path;
                        else if (AssetDatabase.Assets.TryGetValue(id, out var asset)) value = asset.Path;
                        else throw new FileNotFoundException("The template parameter asset was not found.");
                    }
                    value = Path.GetFullPath(value);
                    if (!File.Exists(value)) throw new FileNotFoundException("The template parameter file was not found.", value);
                    Platforms.Windows.WindowsPluginIsolationPlatform.PrepareScriptFileAccess(value);
                }
                try { parameters[key] = TemplateCreatePage.ConvertResolvedValue(value, type); }
                catch (Exception ex) when (ex is FormatException or JsonException)
                {
                    throw new FormatException($"Invalid {type} template parameter '{key}'.");
                }
            }
            await File.WriteAllTextAsync(Path.Combine(runner.root, "parameters.json"), parameters.ToJsonString(), ct);
            await File.WriteAllTextAsync(Path.Combine(runner.root, "runtime.json"),
                JsonSerializer.Serialize(new { Module = module }), ct);
            await File.WriteAllTextAsync(Path.Combine(runner.root, "launch.ps1"), Launcher, ct);
            await File.WriteAllTextAsync(Path.Combine(runner.root, "run.ps1"), Driver, ct);
            await Platforms.Windows.WindowsPluginIsolationPlatform.PrepareScriptRuntimeAsync(executable, Path.GetDirectoryName(module)!, runner.root, ct);
            // No connection file means probe: import the module without executing the template.
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            if (await Platforms.Windows.WindowsPluginIsolationPlatform.RunScriptProcessAsync(executable, runner.root, probeTimeout.Token) != 0)
                throw new InvalidOperationException("PowerShell could not load the bundled module in the isolation environment.");
            return runner;
        }
        catch
        {
            await runner.DisposeAsync();
            throw;
        }
#else
        throw new PlatformNotSupportedException("Script templates currently require Windows and Sandy isolation.");
#endif
    }

    internal async Task RunWithUiAsync(DraftPage page)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(page.ScriptTemplateLifetime.Token);
        if (!Running.TryAdd(page, cts))
        {
            await page.DisplayAlertAsync(Localized._Info, Localized.ScriptTemplate_Running, Localized._OK);
            return;
        }
        using var popup = new ScriptTemplateOutputView(cts);
        var closable = page.IsPopupClosableByTapBackground;
        var status = Localized.ScriptTemplate_Failed;
        var shown = false;
        Task? popupTask = null;
        try
        {
            try
            {
                if (disposed) throw new ObjectDisposedException(nameof(ScriptTemplateRunner));
                if (page.IsClosed || page.IsReadonly || page.IsRemoteProject)
                    throw new InvalidOperationException("Script templates require an open, writable local project.");
                page.IsPopupClosableByTapBackground = false;
                popupTask = page.ShowAPopup(popup, mode: "dialog", disableScrollWrapping: true);
                shown = true;
                if (!page.UseCommunityToolkitPopupInsteadOfOverlayLayer) await popupTask;
                page.HasScriptTemplateOutput = true;
#if WINDOWS
                var pipe = await page.CreateProjectClientPipeAsync(Localized.ScriptTemplateCreatePage_Title, true);
                cts.Token.ThrowIfCancellationRequested();
                await File.WriteAllTextAsync(Path.Combine(root, "connection.json"), JsonSerializer.Serialize(new { Pipe = pipe }), cts.Token);
                Log("Executing isolated script template.");
                if (await Platforms.Windows.WindowsPluginIsolationPlatform.RunScriptProcessAsync(executable, root, cts.Token, popup.Append) == 0)
                    status = Localized._Done;
                else
                {
                    Log("Isolated script template failed.", "warning");
                }
#endif
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                status = Localized.DraftPage_Tasks_Status_Canceled;
                page.SetStatusText(status);
            }
            catch (Exception ex)
            {
                Log(ex, "Execute isolated script template", typeof(ScriptTemplateRunner));
                status = Localized.ScriptTemplate_Failed;
                popup.Append(Localized._ExceptionTemplate(ex) + Environment.NewLine, true);
            }
            popup.Complete(status);
            if (shown && !page.IsClosed) await popup.WaitForCloseAsync(page.ScriptTemplateLifetime.Token);
        }
        catch (OperationCanceledException) when (page.ScriptTemplateLifetime.IsCancellationRequested) { }
        finally
        {
            Running.TryRemove(page, out _);
            page.HasScriptTemplateOutput = false;
            page.IsPopupClosableByTapBackground = closable;
            try
            {
                if (shown) await page.HidePopup(true);
                if (popupTask is not null) await popupTask;
            }
            catch (Exception ex) { Log(ex, "Close script template progress", typeof(ScriptTemplateRunner)); }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception ex) { Log(ex, "Clean script template session", typeof(ScriptTemplateRunner)); }
        return ValueTask.CompletedTask;
    }

    private static async Task<string> FindPowerShellAsync(CancellationToken ct)
    {
        var candidates = new List<string>();
        foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("ProgramW6432") }.OfType<string>().Distinct())
        {
            var root = Path.Combine(programFiles, "PowerShell");
            if (Directory.Exists(root))
                candidates.AddRange(Directory.GetDirectories(root).OrderDescending().Select(d => Path.Combine(d, "pwsh.exe")));
        }
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), "pwsh.exe")));
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
        {
            try
            {
                var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()" }) info.ArgumentList.Add(arg);
                using var process = Process.Start(info)!;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var registration = timeout.Token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(true); }
                    catch (Exception ex) { Log(ex, "Stop PowerShell version probe", typeof(ScriptTemplateRunner)); }
                });
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await stderr;
                if (process.ExitCode == 0 && Version.TryParse((await output).Trim(), out var version) && version >= new Version(7, 6))
                    return Path.GetFullPath(path);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log(ex, "Probe installed PowerShell Core", typeof(ScriptTemplateRunner));
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException(Localized.ScriptTemplate_PowerShellRequired);
    }

    private const string Launcher = """
        param([string]$HandshakePipe)
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $OutputEncoding = [Console]::OutputEncoding
        $PSStyle.OutputRendering = 'PlainText'
        $ErrorActionPreference = 'Stop'
        $p = [System.IO.Pipes.NamedPipeClientStream]::new('.', $HandshakePipe, [System.IO.Pipes.PipeDirection]::InOut)
        try {
            $p.Connect(30000)
            if ($p.ReadByte() -ne 1) { throw 'Isolation handshake failed.' }
        } finally { $p.Dispose() }
        $global:LASTEXITCODE = 1
        & "$PSScriptRoot/run.ps1"
        exit $LASTEXITCODE
        """;

    private const string Driver = """
        $ErrorActionPreference = 'Stop'
        $PSNativeCommandUseErrorActionPreference = $true
        $connected = $false
        $exitCode = 1
        try {
            $runtime = Get-Content -LiteralPath "$PSScriptRoot/runtime.json" -Raw | ConvertFrom-Json
            Import-Module -Name $runtime.Module -ErrorAction Stop
            if (Test-Path -LiteralPath "$PSScriptRoot/connection.json") {
                $connection = Get-Content -LiteralPath "$PSScriptRoot/connection.json" -Raw | ConvertFrom-Json
                Connect-ProjectFrameCut -PipeName $connection.Pipe | Out-Null
                $connected = $true
                $TemplateParameters = Get-Content -LiteralPath "$PSScriptRoot/parameters.json" -Raw | ConvertFrom-Json -AsHashtable
                $global:LASTEXITCODE = 0
                $scriptFailed = $false
                & "$PSScriptRoot/script.ps1" 2>&1 | ForEach-Object {
                    if ($_ -is [System.Management.Automation.ErrorRecord]) {
                        if ($_.FullyQualifiedErrorId -notin @('NativeCommandError', 'NativeCommandErrorMessage')) {
                            $scriptFailed = $true
                        }
                        [Console]::Error.WriteLine(($_ | Out-String).TrimEnd())
                    } else { $_ }
                }
                if (!$? -or $scriptFailed -or $LASTEXITCODE -ne 0) { throw "Template script failed (exit code $LASTEXITCODE)." }
            }
            $exitCode = 0
        } catch {
            $message = $_.ToString()
            [Console]::Error.WriteLine(($_ | Out-String))
            [Console]::Error.WriteLine($_.ScriptStackTrace)
            [IO.File]::WriteAllText("$PSScriptRoot/error.txt", $message)
        } finally {
            if ($connected) { Disconnect-ProjectFrameCut }
        }
        exit $exitCode
        """;
}
#endif
