using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace projectFrameCut.Shared
{
    public interface IScreenReaderService
    {
        bool IsScreenReaderEnabled();
    }

    public class ScreenReaderService : IScreenReaderService
    {
        public bool IsScreenReaderEnabled()
        {
#if ANDROID
            try
            {
                var ctx = Android.App.Application.Context;
                var accessibility = ctx.GetSystemService(Android.Content.Context.AccessibilityService) as Android.Views.Accessibility.AccessibilityManager;
                if (accessibility == null) return false;
                // Touch exploration usually indicates a screen reader (TalkBack) is active.
                return accessibility.IsEnabled && accessibility.IsTouchExplorationEnabled;
            }
            catch
            {
                return false;
            }
#elif IOS || MACCATALYST
            try
            {
                return UIKit.UIAccessibility.IsVoiceOverRunning;
            }
            catch
            {
                return false;
            }
#elif WINDOWS
            try
            {
                return IsScreenReaderReportedBySystem() || AreUIAClientsListening();
            }
            catch
            {
                return false;
            }
#else
            return false;
#endif
        }

#if WINDOWS
        // P/Invoke for SystemParametersInfo to check SPI_GETSCREENREADER
        // SPI_GETSCREENREADER = 0x0046
        private const uint SPI_GETSCREENREADER = 0x0046;
        private const uint SPIF_NONE = 0x0;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out int pvParam, uint fWinIni);

        // P/Invoke for UIA clients listening (checks if any UI Automation clients are listening)
        [DllImport("uiautomationcore.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UiaClientsAreListening();

        static bool IsScreenReaderReportedBySystem()
        {
            if (SystemParametersInfo(SPI_GETSCREENREADER, 0, out int enabled, SPIF_NONE))
            {
                return enabled != 0;
            }
            return false;
        }

        static bool AreUIAClientsListening()
        {
            try
            {
                return UiaClientsAreListening();
            }
            catch
            {
                return false;
            }
        }

#endif
    }
}
