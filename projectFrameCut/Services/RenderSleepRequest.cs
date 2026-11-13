using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static projectFrameCut.Shared.Logger;

namespace projectFrameCut.Services;

internal sealed class RenderSleepRequest : IDisposable
{
    private bool _active;
    private int _disposed;
#if WINDOWS
    private SafeFileHandle? _request;
    private const int PowerRequestSystemRequired = 1;
#elif MACCATALYST || MACOS
    private uint _assertionId;
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
#endif

    public static RenderSleepRequest Acquire()
    {
        var request = new RenderSleepRequest();
        try
        {
#if WINDOWS
            var reason = Marshal.StringToHGlobalUni(Localized.RenderPage_ExportTitle(Localized.AppBrand));
            try
            {
                var context = new ReasonContext { Version = 0, Flags = 1, SimpleReasonString = reason };
                request._request = PowerCreateRequest(ref context);
                if (request._request.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!PowerSetRequest(request._request, PowerRequestSystemRequired))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                request._active = true;
            }
            finally
            {
                Marshal.FreeHGlobal(reason);
            }
#elif MACCATALYST || MACOS
            var type = CFStringCreateWithCString(0, "PreventUserIdleSystemSleep", 0x08000100);
            var reason = CFStringCreateWithCString(0, Localized.RenderPage_ExportTitle(Localized.AppBrand), 0x08000100);
            try
            {
                if (type == 0 || reason == 0)
                    throw new InvalidOperationException("Unable to create the render power assertion strings.");
                var result = IOPMAssertionCreateWithName(type, 255, reason, out request._assertionId);
                if (result != 0)
                    throw new InvalidOperationException($"IOPMAssertionCreateWithName failed: 0x{result:X8}.");
                request._active = true;
            }
            finally
            {
                if (type != 0) CFRelease(type);
                if (reason != 0) CFRelease(reason);
            }
#endif
            if (request._active) Log("Render sleep prevention enabled; display sleep remains allowed.");
        }
        catch (Exception ex)
        {
            Log(ex, "Acquire render sleep request");
            request.Dispose();
        }
        return request;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!_active) return;
#if WINDOWS
            if (!PowerClearRequest(_request!, PowerRequestSystemRequired))
                throw new Win32Exception(Marshal.GetLastWin32Error());
#elif MACCATALYST || MACOS
            var result = IOPMAssertionRelease(_assertionId);
            if (result != 0)
                throw new InvalidOperationException($"IOPMAssertionRelease failed: 0x{result:X8}.");
#endif
            Log("Render sleep prevention released.");
        }
        catch (Exception ex)
        {
            Log(ex, "Release render sleep request");
        }
        finally
        {
#if WINDOWS
            _request?.Dispose();
#endif
            _active = false;
        }
    }

#if WINDOWS
    // Reserve the full native union, including its detailed-reason layout.
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
        public uint ReservedReasonId;
        public uint ReservedReasonCount;
        public nint ReservedReasonStrings;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeFileHandle request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafeFileHandle request, int requestType);
#elif MACCATALYST || MACOS
    [DllImport(IOKit)]
    private static extern int IOPMAssertionCreateWithName(nint type, uint level, nint name, out uint assertionId);

    [DllImport(IOKit)]
    private static extern int IOPMAssertionRelease(uint assertionId);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint value);
#endif
}
