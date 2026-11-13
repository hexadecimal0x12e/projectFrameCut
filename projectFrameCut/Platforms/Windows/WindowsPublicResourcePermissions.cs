#if WINDOWS
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Windows.ApplicationModel;

namespace projectFrameCut.Platforms.Windows;

internal static class WindowsPublicResourcePermissions
{
    private static readonly SecurityIdentifier[] ReadPrincipals =
    [
        new("S-1-1-0"),
        new("S-1-15-2-1"),
        new("S-1-15-2-2"),
    ];

    internal static void Repair()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate | TokenAccessLevels.AdjustPrivileges);
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Public resource access repair requires administrator privileges.");
        if (!WinUI.App.IsPackaged())
            throw new PlatformNotSupportedException("Public resource access repair requires an installed MSIX package.");

        var directory = new DirectoryInfo(Path.Combine(Package.Current.InstalledLocation.Path, "Public"));
        if (!directory.Exists) throw new DirectoryNotFoundException(directory.FullName);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The package Public directory must not be a directory link.");

        // Backup/restore handles allow updating a protected package DACL without taking ownership.
        EnablePrivilege(identity.AccessToken, "SeBackupPrivilege");
        EnablePrivilege(identity.AccessToken, "SeRestorePrivilege");
        GrantReadAndExecute(directory.FullName, true);
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        }))
        {
            GrantReadAndExecute(entry.FullName, (entry.Attributes & FileAttributes.Directory) != 0);
        }
        Log($"Repaired public resource read and execute permissions for '{directory.FullName}'.");
    }

    private static void GrantReadAndExecute(string path, bool isDirectory)
    {
        const uint ReadControl = 0x00020000;
        const uint WriteDac = 0x00040000;
        const uint BackupSemantics = 0x02000000;
        const uint OpenReparsePoint = 0x00200000;
        const uint FileObject = 1;
        const uint DaclSecurityInformation = 4;
        using var handle = CreateFile(path, ReadControl | WriteDac, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, FileMode.Open, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open public resource '{path}'.");
        var error = GetSecurityInfo(handle, FileObject, DaclSecurityInformation,
            out _, out _, out var dacl, out _, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error, $"Failed to read permissions for '{path}'.");
        try
        {
            if (dacl == IntPtr.Zero) return;
            var bytes = new byte[(ushort)Marshal.ReadInt16(dacl, 2)];
            Marshal.Copy(dacl, bytes, 0, bytes.Length);
            var acl = new DiscretionaryAcl(isDirectory, false, new RawAcl(bytes, 0));
            foreach (var sid in ReadPrincipals)
                acl.AddAccess(AccessControlType.Allow, sid, (int)(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize),
                    isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
                    PropagationFlags.None);
            bytes = new byte[acl.BinaryLength];
            acl.GetBinaryForm(bytes, 0);
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                error = SetSecurityInfo(handle, FileObject, DaclSecurityInformation,
                    IntPtr.Zero, IntPtr.Zero, pinned.AddrOfPinnedObject(), IntPtr.Zero);
                if (error != 0) throw new Win32Exception((int)error, $"Failed to repair permissions for '{path}'.");
            }
            finally
            {
                pinned.Free();
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static void EnablePrivilege(SafeAccessTokenHandle token, string name)
    {
        if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
        if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var error = Marshal.GetLastWin32Error();
        if (error != 0)
            throw new Win32Exception(error, $"Failed to enable {name}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr securityAttributes,
        FileMode creationDisposition, uint flags, IntPtr template);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInfo,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
#endif
