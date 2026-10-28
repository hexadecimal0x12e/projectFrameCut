using projectFrameCut.Setting.SettingManager;
using System.Globalization;

namespace projectFrameCut.InteractableEditor;

internal static class DynamicPreviewSettings
{
    public const string TargetPixelsSettingKey = "Edit_DynamicPreviewTargetPixels";
    public const string CacheSourceFramesSettingKey = "Edit_CacheDecodedPreviewFrames";

    public const string DiskCacheSettingKey = "Edit_PreviewDiskCache";
    public static string DiskCacheMode
    {
        get
        {
            var mode = SettingsManager.GetSetting(DiskCacheSettingKey, "");
            return mode is "none" or "preview" or "previewAndSource" ? mode
                : SettingsManager.IsBoolSettingTrueOrDefault(CacheSourceFramesSettingKey, true) ? "previewAndSource" : "preview";
        }
    }
    public static bool MemoryOnly => DiskCacheMode == "none";
    public static bool CacheSourceFrames => DiskCacheMode == "previewAndSource";
    public static Dictionary<string, string> DiskCacheOptions => new()
    {
        [SettingsManager.SettingLocalizedResources.Edit_PreviewDiskCache_None] = "none",
        [SettingsManager.SettingLocalizedResources.Edit_PreviewDiskCache_Preview] = "preview",
        [SettingsManager.SettingLocalizedResources.Edit_PreviewDiskCache_All] = "previewAndSource"
    };

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
