using Microsoft.Win32.SafeHandles;
using projectFrameCut.Render.Contracts;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace projectFrameCut.Services;

[SupportedOSPlatform("windows")]
internal static partial class ExternalRpcPipePermissions
{
    internal sealed record Principal(string Name, string Sid, bool IsGroup);

    internal static IReadOnlyList<Principal> EnumerateLocalPrincipals()
    {
        var principals = new List<Principal>();
        var unresolved = new List<string>();
        foreach (var isGroup in new[] { false, true })
        {
            uint userResume = 0;
            nuint groupResume = 0;
            int status;
            do
            {
                IntPtr buffer = IntPtr.Zero;
                try
                {
                    uint count;
                    status = isGroup
                        ? NetLocalGroupEnum(null, 0, out buffer, 65536, out count, out _, ref groupResume)
                        : NetUserEnum(null, 0, 0, out buffer, 65536, out count, out _, ref userResume);
                    if (status is not (0 or 234)) throw new Win32Exception(status);
                    for (uint i = 0; i < count; i++)
                    {
                        var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, checked((int)i * IntPtr.Size)));
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        try
                        {
                            var account = isGroup ? new NTAccount(name) : new NTAccount(Environment.MachineName, name);
                            var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
                            principals.Add(new(((NTAccount)sid.Translate(typeof(NTAccount))).Value, sid.Value, isGroup));
                        }
                        catch (IdentityNotMappedException)
                        {
                            unresolved.Add(name);
                        }
                    }
                }
                finally
                {
                    if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
                }
            } while (status == 234);
        }
        if (unresolved.Count > 0)
            Log($"Could not resolve local RPC pipe accounts: {string.Join(", ", unresolved)}.", "warning");
        return principals.DistinctBy(p => p.Sid).OrderBy(p => p.IsGroup).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static HashSet<string> LoadAllowedSids(string dataRoot)
    {
        return ExternalRpcPipeSettings.Load(dataRoot).AllowedSids
            .Select(s => new SecurityIdentifier(s).Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static void SaveAllowedSids(string dataRoot, IEnumerable<string> sids)
    {
        var configuration = ExternalRpcPipeSettings.Load(dataRoot);
        configuration.AllowedSids = sids.Select(s => new SecurityIdentifier(s).Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        configuration.Save(dataRoot);
    }

    internal static bool LoadSingleConnection(string dataRoot) => ExternalRpcPipeSettings.Load(dataRoot).SingleConnection;

    internal static void SaveSingleConnection(string dataRoot, bool enabled)
    {
        var configuration = ExternalRpcPipeSettings.Load(dataRoot);
        configuration.SingleConnection = enabled;
        configuration.Save(dataRoot);
    }

    internal static NamedPipeServerStream CreatePipe(string name, string dataRoot, SecurityIdentifier? appContainerSid = null, int maxInstances = 1)
    {
        ExternalRpcPipeSettings.EnsureAllowed(dataRoot);
        var allowed = LoadAllowedSids(dataRoot);
        if (appContainerSid is null && allowed.Count == 0)
            return new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var current = WindowsIdentity.GetCurrent();
        var sid = current.User ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var sddl = appContainerSid is null
            ? GetDefaultDacl(current)
            : $"D:P(A;;GA;;;SY)(A;;GA;;;{sid.Value})(A;;GA;;;{appContainerSid.Value})";
        // NamedPipeClientStream opens duplex pipes with GENERIC_READ | GENERIC_WRITE.
        if (sddl.Length > 0)
            foreach (var extraSid in allowed) sddl += $"(A;;GRGW;;;{extraSid})";
        sddl += "S:(ML;;NW;;;LW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
            };
            var handle = CreateNamedPipe($@"\\.\pipe\{name}", 0x00000003u | 0x40000000u | (maxInstances == 1 ? 0x00080000u : 0u), 0x00000008,
                maxInstances == NamedPipeServerStream.MaxAllowedServerInstances ? 255u : checked((uint)maxInstances),
                1024 * 1024, 1024 * 1024, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, safePipeHandle: handle); }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally { LocalFree(descriptor); }
    }

    private static string GetDefaultDacl(WindowsIdentity identity)
    {
        if (!GetTokenInformation(identity.AccessToken, 11, IntPtr.Zero, 0, out var length)
            && Marshal.GetLastWin32Error() != 122)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(identity.AccessToken, 11, buffer, length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var acl = Marshal.ReadIntPtr(buffer);
            if (acl == IntPtr.Zero) return "";
            var bytes = new byte[unchecked((ushort)Marshal.ReadInt16(acl, 2))];
            Marshal.Copy(acl, bytes, 0, bytes.Length);
            return new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
                null, null, null, new RawAcl(bytes, 0)).GetSddlForm(AccessControlSections.Access);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        internal int InheritHandle;
    }

    [LibraryImport("Netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetUserEnum(string? serverName, uint level, uint filter, out IntPtr buffer, uint preferredLength, out uint count, out uint total, ref uint resume);

    [LibraryImport("Netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetLocalGroupEnum(string? serverName, uint level, out IntPtr buffer, uint preferredLength, out uint count, out uint total, ref nuint resume);

    [LibraryImport("Netapi32.dll")]
    private static partial int NetApiBufferFree(IntPtr buffer);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr buffer, int length, out int requiredLength);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes securityAttributes);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
