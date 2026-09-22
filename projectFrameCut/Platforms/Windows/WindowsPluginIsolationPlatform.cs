#if WINDOWS
using Microsoft.Win32.SafeHandles;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Windows.ApplicationModel;
using Windows.Storage;

namespace projectFrameCut.Platforms.Windows;

internal sealed partial class WindowsPluginIsolationPlatform : IPluginIsolationPlatform
{
    private static readonly object DirectoryLinkLock = new();

    internal static string PackageFamilyName
    {
        get
        {
            try { return Package.Current.Id.FamilyName; }
            catch (InvalidOperationException ex)
            {
                throw new PlatformNotSupportedException("Plugin isolation requires an installed MSIX package.", ex);
            }
        }
    }

    internal static string SessionDirectory => Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "plugin-isolation");
    internal static string ProjectPluginDirectory => Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "project-plugins");
    internal static SecurityIdentifier AppContainerSid => GetAppContainerSid(PackageFamilyName);
    internal static SecurityIdentifier GetAppContainerSid(string packageFamilyName) => DeriveAppContainerSid(packageFamilyName);
    internal static NamedPipeServerStream CreateRpcPipe(string name, bool isolated) =>
        isolated ? CreatePipe(name, AppContainerSid) : new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    internal static void PrepareCurrentProjectAccess(string projectRoot) =>
        PrepareDirectoryAccess(projectRoot, "CurrentProject");

    internal static void PrepareAssetsLibraryAccess(string assetsRoot) =>
        PrepareDirectoryAccess(assetsRoot, "AssetsLibrary");

    private static void PrepareDirectoryAccess(string targetRoot, string linkName)
    {
        var fullTargetRoot = Path.GetFullPath(targetRoot);
        if (!Directory.Exists(fullTargetRoot)) throw new DirectoryNotFoundException(fullTargetRoot);

        var appContainerSid = DeriveAppContainerSid(PackageFamilyName);
        var directory = new DirectoryInfo(fullTargetRoot);
        var security = directory.GetAccessControl();
        security.SetAccessRule(new FileSystemAccessRule(
            appContainerSid,
            FileSystemRights.Modify | FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);

        lock (DirectoryLinkLock)
        {
            var linkPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, linkName);
            var link = new DirectoryInfo(linkPath);
            var existingTarget = link.LinkTarget;
            if (existingTarget is not null)
            {
                if (string.Equals(Path.GetFullPath(existingTarget, link.Parent!.FullName), fullTargetRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                link.Delete();
            }
            else if (link.Exists || File.Exists(linkPath))
            {
                throw new IOException($"'{linkPath}' already exists and is not a directory link.");
            }

            Directory.CreateSymbolicLink(linkPath, fullTargetRoot);
        }
    }

    internal static void ClearCurrentProjectLink(string projectRoot)
    {
        var fullProjectRoot = Path.GetFullPath(projectRoot);
        lock (DirectoryLinkLock)
        {
            var link = new DirectoryInfo(Path.Combine(ApplicationData.Current.LocalFolder.Path, "CurrentProject"));
            var linkTarget = link.LinkTarget;
            if (linkTarget is null || !string.Equals(Path.GetFullPath(linkTarget, link.Parent!.FullName), fullProjectRoot, StringComparison.OrdinalIgnoreCase)) return;
            link.Delete();
        }
    }

    public static bool IsInAppContainer()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!NativeMethods.GetTokenInformation(identity.AccessToken, 29, out var isAppContainer, sizeof(int), out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return isAppContainer != 0;
    }

    internal static void ValidateWorkerProcess()
    {
        _ = PackageFamilyName;
        using var identity = WindowsIdentity.GetCurrent();
        if (!NativeMethods.GetTokenInformation(identity.AccessToken, 29, out var isAppContainer, sizeof(int), out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (isAppContainer == 0)
            throw new UnauthorizedAccessException("plugin_worker with --appContainer parameter must be activated through InSandboxWorker in an AppContainer.");
        Log($"AppContainer verified for process {Environment.ProcessId}.");
    }

    public async ValueTask<IPluginIsolationSession> StartAsync(PluginIsolationLaunchContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Transport.ControlMode is not (IsolationControlMode.Auto or IsolationControlMode.NamedPipe))
            throw new NotSupportedException($"Windows AppContainer does not support control mode '{context.Transport.ControlMode}'.");
        if (context.Transport.PayloadMode is IsolationPayloadKind.TransferredResource)
            throw new NotSupportedException("TransferredResource cannot be selected as the frame payload mode.");
        var familyName = PackageFamilyName;
        if (!string.Equals(context.InstancePackageName, familyName, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The isolation session must belong to the current package.");
        var sessionRoot = Path.GetFullPath(context.SessionRoot);
        if (!string.Equals(Path.GetDirectoryName(sessionRoot), Path.GetFullPath(SessionDirectory), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(sessionRoot), "N", out _))
            throw new ArgumentException("The isolation session must be a unique directory under the current package LocalState.");
        if (Directory.Exists(sessionRoot)) throw new IOException("The isolation session directory already exists.");
        var appContainerSid = DeriveAppContainerSid(familyName);
        NamedPipeServerStream? pendingPipe = null;
        StreamIsolationControlChannel? channel = null;
        Process? process = null;
        var terminated = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(sessionRoot);
            ApplySessionAcl(sessionRoot, appContainerSid);
            var pluginDirectory = ResolvePluginDirectory(context);
            var pipeName = $"projectFrameCut.PluginIsolation.{Guid.NewGuid():N}";
            pendingPipe = CreatePipe(pipeName, appContainerSid);
            Log($"Created isolation pipe '\\\\.\\pipe\\{pipeName}' for AppContainer SID '{appContainerSid.Value}'.");
            List<string> args = 
                [
                    "plugin_worker",
                    $"--pipe={pipeName}",
                    $"--token={context.AuthenticationToken}",
                    $"--sessionRoot={sessionRoot}",
                    $"--parentPid={Environment.ProcessId}",
                    $"--pluginId={context.PluginId}",
                    $"--pluginRoot={pluginDirectory}",
                    $"--instancePackageName={familyName}",
                    "--forceRouteToCLI",
                    "--appContainer",
                ];
            if (MyLoggerExtensions.LoggingDiagnosticInfo)
            {
                args.Add("--logDiagnostic");
            }
            if (SettingsManager.IsBoolSettingTrue("plugin_IsolationShowConsole"))
            {
                args.Add("--consoleLog");
            }
            var arguments = string.Join(" ", args.Select(Quote));
            var workerEntryPoint = SettingsManager.IsBoolSettingTrue("plugin_IsolationShowConsole")  ? "InSandboxWorkerConsole" : "InSandboxWorkerNoConsole";
            var processId = ActivateRuntime($"{familyName}!{workerEntryPoint}", arguments);
            process = Process.GetProcessById(processId);
            Log($"Activated InSandboxWorker {processId} for plugin '{context.PluginId}'.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(context.Transport.RequestTimeout);
            await pendingPipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!NativeMethods.GetNamedPipeClientProcessId(pendingPipe.SafePipeHandle, out var clientProcessId)
                || clientProcessId != processId)
                throw new UnauthorizedAccessException("The process connected to the plugin isolation pipe does not match the activated runtime.");

            await projectFrameCut.Services.PluginIsolationHostHandshake.AuthorizeRuntimeAsync(pendingPipe, context, timeout.Token).ConfigureAwait(false);
            channel = new StreamIsolationControlChannel(pendingPipe, IsolationControlMode.NamedPipe);
            var request = new RenderRequestEnvelope
            {
                ClientId = $"plugin-host-{Environment.ProcessId}",
                Operation = RenderOperation.IsolationNegotiate,
                Payload = RenderRpcSerializer.Serialize(new IsolationNegotiateRequest
                {
                    AuthenticationToken = context.AuthenticationToken,
                    HostProcessId = Environment.ProcessId,
                    PreferredPayloadKind = context.Transport.PayloadMode ?? IsolationPayloadKind.SharedMemory,
                }),
            };
            var response = await channel.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.Error is not null) response.Error.ThrowAsException();
            var capabilities = RenderRpcSerializer.Deserialize<IsolationChannelCapabilities>(response.Payload);
            if (capabilities.ProtocolVersion != PluginIsolationProtocol.CurrentVersion)
                throw new InvalidDataException("Plugin isolation protocol version mismatch.");

            var payloads = new SessionPayloadExchange(context.SessionRoot, context.Transport.MaximumInlineBytes, context.Transport.MaximumPayloadBytes);
            var resources = new SessionResourceBroker(context.SessionRoot, context.Transport.MaximumPayloadBytes);
            var session = new PluginIsolationSession(context.PluginId, channel, payloads, resources, capabilities, context.Transport, TerminateAsync);
            _ = MonitorWorkerAsync();
            return session;
        }
        catch
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            else if (pendingPipe is not null) await pendingPipe.DisposeAsync().ConfigureAwait(false);
            await TerminateAsync("Worker startup failed.").ConfigureAwait(false);
            throw;
        }

        async ValueTask TerminateAsync(string reason)
        {
            if (Interlocked.Exchange(ref terminated, 1) != 0) return;
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                if (Directory.Exists(sessionRoot)) Directory.Delete(sessionRoot, recursive: true);
            }
            catch (Exception ex) { projectFrameCut.Shared.Logger.Log(ex, "clean up the plugin isolation session", this); }
            finally { process?.Dispose(); }
            projectFrameCut.Shared.Logger.Log($"Plugin isolation worker for '{context.PluginId}' stopped: {reason}");
        }

        async Task MonitorWorkerAsync()
        {
            try
            {
                await process!.WaitForExitAsync().ConfigureAwait(false);
                await TerminateAsync("Worker process exited.").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref terminated) == 0)
                    projectFrameCut.Shared.Logger.Log(ex, "monitor the plugin isolation worker", this);
            }
        }
    }

    private static string ResolvePluginDirectory(PluginIsolationLaunchContext context)
    {
        var sourceRoot = Path.GetFullPath(context.PluginRoot);
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException(sourceRoot);
        ValidatePathSegment(context.InstancePackageName, nameof(context.InstancePackageName));
        ValidatePathSegment(context.PluginId, nameof(context.PluginId));
        var localState = Path.GetFullPath(ApplicationData.Current.LocalFolder.Path);
        var localStatePrefix = localState.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!sourceRoot.StartsWith(localStatePrefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The isolated plugin must be installed below the current package LocalState.");

        var expectedName = context.PluginId;
        if (!string.Equals(Path.GetFileName(sourceRoot), expectedName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The isolated plugin directory does not match the plugin ID.");

        projectFrameCut.Shared.Logger.Log($"Using installed plugin '{context.PluginId}' directly from '{sourceRoot}' for isolation.");
        return sourceRoot;
    }

    private static void ValidatePathSegment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{name}' must be a single path segment.", name);
    }

    private static int ActivateRuntime(string aumid, string arguments)
    {
        var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), throwOnError: true)!;
        var manager = (IApplicationActivationManager)Activator.CreateInstance(type)!;
        try
        {
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(aumid, arguments, 0, out var processId));
            return checked((int)processId);
        }
        finally { Marshal.FinalReleaseComObject(manager); }
    }

    private static NamedPipeServerStream CreatePipe(string name, SecurityIdentifier appContainerSid)
    {
        using var current = WindowsIdentity.GetCurrent();
        var sid = current.User ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;{sid.Value})(A;;GA;;;{appContainerSid.Value})S:(ML;;NW;;;LW)";
        if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new NativeMethods.SecurityAttributes
            {
                Length = Marshal.SizeOf<NativeMethods.SecurityAttributes>(),
                SecurityDescriptor = descriptor,
            };
            var handle = NativeMethods.CreateNamedPipe($@"\\.\pipe\{name}", 0x00000003 | 0x40000000, 0x00000000 | 0x00000008, 1, 1024 * 1024, 1024 * 1024, 0, ref attributes);
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return new NamedPipeServerStream(PipeDirection.InOut, true, true, handle);
        }
        finally { NativeMethods.LocalFree(descriptor); }
    }

    private static SecurityIdentifier DeriveAppContainerSid(string packageFamilyName)
    {
        // Some packaged calls succeed without writing the SID; keep the native output initialized.
        var sid = IntPtr.Zero;
        var hr = NativeMethods.DeriveAppContainerSidFromAppContainerName(packageFamilyName, ref sid);
        if (hr < 0)
        {
            projectFrameCut.Shared.Logger.Log($"Failed to derive AppContainer SID for '{packageFamilyName}': HRESULT 0x{hr:X8}.");
            Marshal.ThrowExceptionForHR(hr);
        }
        if (sid == IntPtr.Zero)
        {
            projectFrameCut.Shared.Logger.LogDiagnostic($"SID derivation returned no SID for '{packageFamilyName}'; looking up the registered AppContainer.");
            return FindRegisteredAppContainerSid(packageFamilyName);
        }
        try
        {
            var result = new SecurityIdentifier(sid);
            projectFrameCut.Shared.Logger.LogDiagnostic($"Derived AppContainer SID '{result.Value}' for '{packageFamilyName}'.");
            return result;
        }
        finally { NativeMethods.FreeSid(sid); }
    }

    private static SecurityIdentifier FindRegisteredAppContainerSid(string packageFamilyName)
    {
        var error = NativeMethods.NetworkIsolationEnumAppContainers(0, out var count, out var containers);
        if (error != 0)
            throw new System.ComponentModel.Win32Exception((int)error, $"Failed to enumerate AppContainers for '{packageFamilyName}'.");
        try
        {
            var size = Marshal.SizeOf<NativeMethods.AppContainer>();
            var entry = containers;
            for (uint i = 0; containers != IntPtr.Zero && i < count; i++, entry = IntPtr.Add(entry, size))
            {
                var container = Marshal.PtrToStructure<NativeMethods.AppContainer>(entry);
                if (container.Sid == IntPtr.Zero
                    || !string.Equals(Marshal.PtrToStringUni(container.Name), packageFamilyName, StringComparison.OrdinalIgnoreCase))
                    continue;
                // Copy the SID before freeing the enumeration buffer that owns it.
                var sid = new SecurityIdentifier(container.Sid);
                projectFrameCut.Shared.Logger.LogDiagnostic($"Found registered AppContainer SID '{sid.Value}' for '{packageFamilyName}'.");
                return sid;
            }
            throw new InvalidOperationException($"No registered AppContainer SID was found for '{packageFamilyName}' among {count} containers.");
        }
        finally
        {
            if (containers != IntPtr.Zero) NativeMethods.NetworkIsolationFreeAppContainers(containers);
        }
    }

    private static void ApplySessionAcl(string path, SecurityIdentifier appContainerSid)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        using var current = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(current.User!, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(appContainerSid, FileSystemRights.Modify | FileSystemRights.ReadAndExecute, inheritance, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static string Quote(string value) => '"' + value + '"';

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig]
        int ActivateForFile(IntPtr appUserModelId, IntPtr itemArray, IntPtr verb, out uint processId);
        [PreserveSig]
        int ActivateForProtocol(IntPtr appUserModelId, IntPtr itemArray, out uint processId);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out int information, int length, out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            internal int Length;
            internal IntPtr SecurityDescriptor;
            internal int InheritHandle;
        }

        [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);

        [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int DeriveAppContainerSidFromAppContainerName(string appContainerName, ref IntPtr sid);

        [LibraryImport("advapi32.dll")]
        internal static partial IntPtr FreeSid(IntPtr sid);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeArray
        {
            internal uint Count;
            internal IntPtr Items;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AppContainer
        {
            internal IntPtr Sid;
            internal IntPtr UserSid;
            internal IntPtr Name;
            internal IntPtr DisplayName;
            internal IntPtr Description;
            internal NativeArray Capabilities;
            internal NativeArray Binaries;
            internal IntPtr WorkingDirectory;
            internal IntPtr PackageFullName;
        }

        [LibraryImport("FirewallAPI.dll")]
        internal static partial uint NetworkIsolationEnumAppContainers(uint flags, out uint count, out IntPtr containers);

        [LibraryImport("FirewallAPI.dll")]
        internal static partial uint NetworkIsolationFreeAppContainers(IntPtr containers);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes securityAttributes);

        [LibraryImport("kernel32.dll")]
        internal static partial IntPtr LocalFree(IntPtr memory);
    }
}
#endif
