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

#if WINDOWS
    internal static Principal ResolvePrincipal(string input)
    {
        input = input.Trim();
        if (input.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
        {
            var sid = new SecurityIdentifier(input.ToUpperInvariant());
            try { return new(((NTAccount)sid.Translate(typeof(NTAccount))).Value, sid.Value, false); }
            catch (IdentityNotMappedException) { return new(sid.Value, sid.Value, false); }
        }
        try
        {
            var account = new NTAccount(input);
            return new(account.Value, ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value, false);
        }
        catch (IdentityNotMappedException) when (!input.Contains('\\')) { }

        var families = new Windows.Management.Deployment.PackageManager().FindPackagesForUser("")
            .Select(p => p.Id)
            .Where(p => string.Equals(p.Name, input, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.FullName, input, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.FamilyName, input, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.FamilyName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (families.Length > 1)
            throw new InvalidOperationException("Multiple packages match this name. Enter the package family name (PFN) instead.");
        var name = families.SingleOrDefault() ?? input;
        return new(name, projectFrameCut.Platforms.Windows.WindowsPluginIsolationPlatform.DeriveAppContainerSid(name).Value, false);
    }
#endif

    internal static void AddPrincipal(string dataRoot, Principal principal)
    {
        var configuration = ExternalRpcPipeSettings.Load(dataRoot);
        var sid = new SecurityIdentifier(principal.Sid).Value;
        configuration.ManualPrincipals[sid] = principal.Name;
        configuration.AllowedSids = configuration.AllowedSids.Append(sid)
            .Select(s => new SecurityIdentifier(s).Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        configuration.Save(dataRoot);
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

    internal static NamedPipeServerStream CreatePipe(string name, string dataRoot, SecurityIdentifier? appContainerSid = null, int maxInstances = 1, IEnumerable<string>? additionalSids = null)
    {
        var configuration = ExternalRpcPipeSettings.Load(dataRoot);
        ExternalRpcPipeSettings.ThrowIfDisabled(configuration.AllowExternalRPC);
        var allowed = configuration.AllowedSids.Select(s => new SecurityIdentifier(s).Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (additionalSids is not null)
            allowed.UnionWith(additionalSids.Select(s => new SecurityIdentifier(s).Value));
        if (configuration.AllowAnyUser)
        {
            // Everyone, AppContainers, LPACs, restricted code, and anonymous callers.
            allowed.UnionWith(["S-1-1-0", "S-1-15-2-1", "S-1-15-2-2", "S-1-5-12", "S-1-5-7"]);
        }
        if (appContainerSid is null && allowed.Count == 0)
            return new NamedPipeServerStream(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var current = WindowsIdentity.GetCurrent();
        var sid = current.User ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var sddl = configuration.AllowAnyUser || appContainerSid is not null
            ? $"D:P(A;;GA;;;SY)(A;;GA;;;{sid.Value})"
            : GetDefaultDacl(current);
        if (appContainerSid is not null) sddl += $"(A;;GA;;;{appContainerSid.Value})";
        // NamedPipeClientStream opens duplex pipes with GENERIC_READ | GENERIC_WRITE.
        if (sddl.Length > 0)
            foreach (var extraSid in allowed) sddl += $"(A;;GRGW;;;{extraSid})";
        sddl += configuration.AllowAnyUser ? "S:(ML;;NW;;;S-1-16-0)" : "S:(ML;;NW;;;LW)";
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
        if (!GetTokenInformation(identity.AccessToken, TokenInformationClass.TokenDefaultDacl, IntPtr.Zero, 0, out var length)
            && Marshal.GetLastWin32Error() != 122)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length < IntPtr.Size) throw new InvalidOperationException("Invalid token default DACL buffer size.");
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(identity.AccessToken, TokenInformationClass.TokenDefaultDacl, buffer, length, out var actualLength))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (actualLength < IntPtr.Size || actualLength > length)
                throw new InvalidOperationException("Invalid token default DACL buffer size.");
            var acl = Marshal.ReadIntPtr(buffer);
            if (acl == IntPtr.Zero) return "";
            var offset = unchecked((nuint)acl - (nuint)buffer);
            if (offset < (nuint)IntPtr.Size || offset >= (nuint)actualLength)
                throw new InvalidOperationException("The token default DACL is outside the returned buffer.");
            var bytes = new byte[actualLength];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
                null, null, null, new RawAcl(bytes, (int)offset)).GetSddlForm(AccessControlSections.Access);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private enum TokenInformationClass
    {
        TokenDefaultDacl = 6,
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
    private static partial bool GetTokenInformation(SafeAccessTokenHandle token, TokenInformationClass informationClass, IntPtr buffer, int length, out int requiredLength);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, ref SecurityAttributes securityAttributes);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
