using System;
using System.Diagnostics;
#if WINDOWS
using System.Security.Principal;
#endif
#if ANDROID

using Android.OS;
using Java.Lang;
#endif
namespace projectFrameCut.Services
{
    public static class AdminHelper
    {
        public static bool IsRunningAsAdministrator()
        {
#if WINDOWS
            var bcdeditProc = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "bcdedit.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                RedirectStandardInput = false,
                CreateNoWindow = true
            });
            bcdeditProc?.WaitForExit();
            return bcdeditProc?.ExitCode == 0; //bcdedit returns 0 when success
#elif ANDROID //if you're rooted we consider you as a admin
            string[] binPaths = {
        "/system/bin/",
        "/system/xbin/",
        "/sbin/",
        "/vendor/bin/",
        "/system/sd/xbin/",
        "/system/bin/failsafe/"
    };
            foreach (var path in binPaths)
            {
                if (System.IO.File.Exists(path + "su"))
                {
                    return true;
                }
            }
            try
            {
                System.Diagnostics.Process.Start("su");
                return true;
            }
            catch //unrooted
            {

            }

            bool isTestKeys = Build.Tags != null && Build.Tags.Contains("test-keys");


            return isTestKeys;

#else
            return false;
#endif
        }
    }
}
