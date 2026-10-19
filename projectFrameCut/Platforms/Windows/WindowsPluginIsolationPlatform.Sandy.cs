#if WINDOWS
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Storage;

namespace projectFrameCut.Platforms.Windows;

internal sealed partial class WindowsPluginIsolationPlatform
{
    internal const string PluginInternetSetting = "Security_IsolatedPlugins_AllowInternet";
    internal const string PluginServerSetting = "Security_IsolatedPlugins_AllowServer";
    internal const string PluginLoopbackSetting = "Security_IsolatedPlugins_AllowLoopback";
    private static string[]? sandyCapabilities;

    internal static string SandyRuntimeDirectory => Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "sandy-runtime");
    private static string SandyExecutable => Path.Combine(AppContext.BaseDirectory, "sandy.exe");

    internal static string GetProfileName(string familyName) =>
        "pjfc-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(familyName)))[..32].ToLowerInvariant();

    internal static string GetContainerName(string familyName) => "Sandy_" + GetProfileName(familyName);

    internal static void EnsureSandyProfile(string familyName, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SandyExecutable))
            throw new FileNotFoundException($"Sandy is missing for {RuntimeInformation.ProcessArchitecture}. Run tools/Prepare-Sandy.ps1 before packaging.", SandyExecutable);

        using var mutex = new Mutex(false, $"Local\\projectFrameCut-Sandy-{GetProfileName(familyName)}");
        try
        {
            try
            {
                if (WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], TimeSpan.FromSeconds(60)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("Timed out waiting for Sandy profile initialization.");
                }
            }
            catch (AbandonedMutexException) { }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(SandyRuntimeDirectory);
                using var identity = WindowsIdentity.GetCurrent();
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                    security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(SandyRuntimeDirectory).SetAccessControl(security);

                var profile = GetProfileName(familyName);
                // Keep one capability set for this process; settings changes require a restart.
                var capabilities = sandyCapabilities ??= GetSandyCapabilities();
                var capabilityList = "[" + string.Join(", ", capabilities.Select(n => $"'{n}'")) + "]";
                var configPath = Path.Combine(SandyRuntimeDirectory, "profile.toml");
                File.WriteAllText(configPath, $"""
                    [sandbox]
                    token = 'appcontainer'

                    [capabilities]
                    names = {capabilityList}

                    [environment]
                    inherit = true
                    """);
                var info = RunSandyCommand(["--profile-info", profile], cancellationToken);
                if (info.ExitCode != 0)
                {
                    var created = RunSandyCommand(["--create-profile", profile, "-c", configPath], cancellationToken);
                    if (created.ExitCode != 0)
                        throw new InvalidOperationException($"Sandy profile creation failed ({created.ExitCode}): {created.Output}");
                    info = RunSandyCommand(["--profile-info", profile], cancellationToken);
                }
                if (info.ExitCode != 0
                    || !Regex.IsMatch(info.Output, @"(?m)^Type:\s+appcontainer\s*$")
                    || !Regex.IsMatch(info.Output, @"(?m)^SID:\s+" + Regex.Escape(GetAppContainerSid(familyName).Value) + @"\s*$"))
                    throw new InvalidOperationException($"Sandy profile '{profile}' does not match the expected AppContainer identity.");

                if (!Regex.IsMatch(info.Output, @"(?m)^names = " + Regex.Escape(capabilityList) + @"\r?$"))
                {
                    // Sandy v0.9996 saves capabilities separately from the TOML file.
                    using var key = Registry.CurrentUser.OpenSubKey($@"Software\Sandy\Profiles\{profile}", writable: true)
                        ?? throw new InvalidOperationException($"Sandy profile '{profile}' was not found in the registry.");
                    key.SetValue("_capability_count", 0, RegistryValueKind.DWord);
                    for (int i = 0; i < capabilities.Length; i++)
                        key.SetValue($"_capability_{i}", capabilities[i], RegistryValueKind.String);
                    key.SetValue("_capability_count", capabilities.Length, RegistryValueKind.DWord);
                    info = RunSandyCommand(["--profile-info", profile], cancellationToken);
                    if (info.ExitCode != 0
                        || !Regex.IsMatch(info.Output, @"(?m)^names = " + Regex.Escape(capabilityList) + @"\r?$"))
                        throw new InvalidOperationException($"Sandy profile '{profile}' capability update could not be verified: {info.Output}");
                    Log($"Updated Sandy profile '{profile}' capabilities to {capabilityList}.");
                }

                // These folders were implicitly accessible with the MSIX container identity.
                GrantSandyDirectory(ApplicationData.Current.LocalFolder.Path, FileSystemRights.Modify | FileSystemRights.ReadAndExecute);
                GrantSandyDirectory(ApplicationData.Current.LocalCacheFolder.Path, FileSystemRights.ReadAndExecute, recursive: false);
                Directory.CreateDirectory(SessionDirectory);
                GrantSandyDirectory(SessionDirectory, FileSystemRights.ReadAndExecute, recursive: false);
                Directory.CreateDirectory(ProjectPluginDirectory);
                GrantSandyDirectory(ProjectPluginDirectory, FileSystemRights.ReadAndExecute);
                LogDiagnostic($"Sandy profile '{profile}' ({familyName}) ready ({RuntimeInformation.ProcessArchitecture}, SID {GetAppContainerSid(familyName).Value}, capabilities {capabilityList}).");
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            Log(ex, $"initialize the Sandy AppContainer profile ({RuntimeInformation.ProcessArchitecture})");
            throw;
        }
    }

    private static string[] GetSandyCapabilities()
    {
        SettingsManager.InitializeReadOnly(CLIProgram.AppDataPath);
        List<string> names = [];
        if (SettingsManager.IsBoolSettingTrueOrDefault(PluginInternetSetting, true))
            names.Add(SettingsManager.IsBoolSettingTrueOrDefault(PluginServerSetting, true) ? "internetClientServer" : "internetClient");
        if (SettingsManager.IsBoolSettingTrueOrDefault(PluginLoopbackSetting, false))
            names.Add("privateNetworkClientServer");
        return names.ToArray();
    }

    private static void GrantSandyDirectory(string path, FileSystemRights rights, bool recursive = true)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();
        var sid = AppContainerSid;
        var inheritance = recursive ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        if (security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Any(r => r.IdentityReference.Equals(sid) && r.AccessControlType == AccessControlType.Allow
                && (r.FileSystemRights & rights) == rights && r.InheritanceFlags == inheritance && r.PropagationFlags == PropagationFlags.None))
            return;
        security.SetAccessRule(new FileSystemAccessRule(sid, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        LogDiagnostic($"Granted Sandy SID '{sid.Value}' {rights} access to '{path}'.");
    }

    private static (int ExitCode, string Output) RunSandyCommand(string[] args, CancellationToken cancellationToken = default)
    {
        var info = CliProcessLauncher.CreateStartInfo(SandyExecutable, true);
        info.WorkingDirectory = AppContext.BaseDirectory;
        info.ArgumentList.Add("-q");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start Sandy.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) { Log(ex, "stop a Sandy management command"); }
            throw;
        }
        return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }

    private static Process StartSandyWorker(string familyName, string sessionRoot, string executable, List<string> args, bool showConsole)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("The isolated worker executable was not found.", executable);
        var info = CliProcessLauncher.CreateStartInfo(SandyExecutable, !showConsole);
        info.WorkingDirectory = sessionRoot;
        var temp = Directory.CreateDirectory(Path.Combine(sessionRoot, "temp")).FullName;
        info.Environment["TEMP"] = temp;
        info.Environment["TMP"] = temp;
        foreach (var arg in new[] { "-q", "-p", GetProfileName(familyName), "-l",
            Path.Combine(SandyRuntimeDirectory, $"worker-{Path.GetFileName(sessionRoot)}.log"), "-x", executable })
            info.ArgumentList.Add(arg);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start the Sandy worker launcher.");
        if (!showConsole && MyLoggerExtensions.LoggingDiagnosticInfo)
        {
            // Sandy's own log contains the launch command; only forward child output here.
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data, "IsolatedWorker Stdout"); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data, "IsolatedWorker Stderr"); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        Log($"Started Sandy launcher {process.Id}, profile '{GetProfileName(familyName)}', architecture {RuntimeInformation.ProcessArchitecture}.");
        return process;
    }

    private static SecurityIdentifier GetTokenAppContainerSid(SafeAccessTokenHandle token)
    {
        if (!NativeMethods.GetTokenInformation(token, 29, out var isContainer, sizeof(int), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (isContainer == 0) throw new UnauthorizedAccessException("The worker must run in an AppContainer.");
        NativeMethods.GetTokenInformationBuffer(token, 31, IntPtr.Zero, 0, out var length);
        if (length <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!NativeMethods.GetTokenInformationBuffer(token, 31, buffer, length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void ValidateSandyChild(Process process, Process launcher, string executable, SecurityIdentifier sid)
    {
        if (launcher.HasExited || process.HasExited
            || NativeMethods.NtQueryInformationProcess(process.SafeHandle, 0, out var info, Marshal.SizeOf<NativeMethods.ProcessBasicInformation>(), out _) < 0
            || info.ParentProcessId.ToInt64() != launcher.Id
            || !string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The pipe client is not the worker started by this Sandy launcher.");
        if (!NativeMethods.OpenProcessToken(process.SafeHandle, 0x0008, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!GetTokenAppContainerSid(token).Equals(sid))
                throw new UnauthorizedAccessException("The pipe client has an unexpected AppContainer identity.");
        }
    }

    private static async Task StopSandyWorkerAsync(Process? worker, Process? launcher)
    {
        var forceCleanup = false;
        try
        {
            if (worker is not null && !worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Log(ex, "stop the isolated worker"); }
        if (launcher is null) return;
        try
        {
            if (worker is null && !launcher.HasExited)
            {
                forceCleanup = true;
                launcher.Kill(entireProcessTree: true);
            }
            await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (launcher.ExitCode >= 125) Log($"Sandy launcher {launcher.Id} exited with code {launcher.ExitCode}.", "warning");
        }
        catch (Exception ex)
        {
            Log(ex, "wait for Sandy cleanup");
            forceCleanup = true;
            try
            {
                if (!launcher.HasExited) launcher.Kill(entireProcessTree: true);
                await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception killError) { Log(killError, "stop the Sandy launcher"); }
        }
        if (forceCleanup)
        {
            try
            {
                var result = await Task.Run(() => RunSandyCommand(["--cleanup"])).ConfigureAwait(false);
                if (result.ExitCode != 0) Log($"Sandy recovery failed ({result.ExitCode}): {result.Output}", "warning");
            }
            catch (Exception ex) { Log(ex, "recover Sandy state"); }
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetTokenInformationBuffer(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returnLength);

        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessBasicInformation
        {
            internal IntPtr ExitStatus;
            internal IntPtr Peb;
            internal IntPtr AffinityMask;
            internal IntPtr BasePriority;
            internal IntPtr ProcessId;
            internal IntPtr ParentProcessId;
        }

        [LibraryImport("ntdll.dll")]
        internal static partial int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, out ProcessBasicInformation information, int length, out int returnLength);
    }
}
#endif
