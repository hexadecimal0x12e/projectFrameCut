using projectFrameCut.Setting.SettingManager;
using System.Globalization;

namespace projectFrameCut.InteractableEditor;

internal static class DynamicPreviewSettings
{
    public const string TargetPixelsSettingKey = "Edit_DynamicPreviewTargetPixels";

    public static Dictionary<string, long> TargetResolutionOptions => new()
    {
        [SettingsManager.SettingLocalizedResources.Edit_DynamicPreviewTargetResolution_Project] = 0,
        ["720P"] = 1280L * 720,
        ["1080P"] = 1920L * 1080,
        ["2K"] = 2560L * 1440,
        ["4K"] = 3840L * 2160,
        ["8K"] = 7680L * 4320
    };

    public static void SetTargetResolution(long pixels)
    {
        SettingsManager.WriteSetting(TargetPixelsSettingKey, pixels.ToString(CultureInfo.InvariantCulture));
        DynamicPreview.TargetResolutionPixels = pixels;
        LogDiagnostic($"Dynamic preview target resolution changed to {pixels} pixels (0 = project resolution).");
    }
}
