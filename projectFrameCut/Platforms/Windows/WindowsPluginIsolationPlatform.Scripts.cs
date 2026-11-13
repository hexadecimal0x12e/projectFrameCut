#if WINDOWS
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace projectFrameCut.Platforms.Windows;

internal sealed partial class WindowsPluginIsolationPlatform
{
    internal static void PrepareScriptFileAccess(string path)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();
        if (HasScriptAccess(security, FileSystemRights.Read)) return;
        security.AddAccessRule(new FileSystemAccessRule(AppContainerSid, FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    internal static async Task PrepareScriptRuntimeAsync(string executable, string moduleRoot, string sessionRoot, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            EnsureSandyProfile(PackageFamilyName, ct);
            foreach (var path in new[] { Path.GetDirectoryName(executable)!, moduleRoot })
                if (!HasScriptAccess(new DirectoryInfo(path).GetAccessControl(), FileSystemRights.ReadAndExecute))
                    GrantSandyDirectory(path, FileSystemRights.ReadAndExecute);
            GrantSandyDirectory(sessionRoot, FileSystemRights.Modify | FileSystemRights.ReadAndExecute);
        }, ct);
    }

    private static bool HasScriptAccess(FileSystemSecurity security, FileSystemRights rights) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r =>
            r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & rights) == rights
            && (r.IdentityReference.Equals(AppContainerSid) || r.IdentityReference.Value == "S-1-15-2-1")
            && !r.PropagationFlags.HasFlag(PropagationFlags.InheritOnly));

    // The child waits for acknowledgement before importing the module or executing user code.
    internal static async Task<int> RunScriptProcessAsync(string executable, string sessionRoot, CancellationToken ct,
        Action<string, bool>? output = null)
    {
        var pipeName = "pjfc-template-" + Guid.NewGuid().ToString("N");
        using var pipe = CreateRpcPipe(pipeName, true, MauiProgram.BasicDataPath);
        using var launcher = StartSandyWorker(PackageFamilyName, sessionRoot, executable,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(sessionRoot, "launch.ps1"), pipeName], false, false);
        using var outputLifetime = new CancellationTokenSource();
        async Task ReadOutputAsync(StreamReader reader, bool isError)
        {
            var buffer = new char[2048];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), outputLifetime.Token).ConfigureAwait(false)) > 0)
                output?.Invoke(new string(buffer, 0, count), isError);
        }
        var stdout = ReadOutputAsync(launcher.StandardOutput, false);
        var stderr = ReadOutputAsync(launcher.StandardError, true);
        Process? child = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var connected = pipe.WaitForConnectionAsync(timeout.Token);
            if (await Task.WhenAny(connected, launcher.WaitForExitAsync(timeout.Token)) != connected)
                throw new InvalidOperationException("The isolated PowerShell process exited before initialization.");
            await connected;
            if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            child = Process.GetProcessById(checked((int)pid));
            ValidateSandyChild(child, launcher, executable, AppContainerSid);
            await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
            await child.WaitForExitAsync(ct);
            var exitCode = child.ExitCode;
            await launcher.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5), ct);
            return exitCode;
        }
        finally
        {
            await StopSandyWorkerAsync(child, launcher);
            outputLifetime.CancelAfter(TimeSpan.FromSeconds(2));
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) when (outputLifetime.IsCancellationRequested) { }
            catch (Exception ex) { Log(ex, "Read isolated script output"); }
            child?.Dispose();
        }
    }
}
#endif
