using projectFrameCut.LivePreview;
using projectFrameCut.Setting.SettingManager;
using System.Globalization;
using Path = System.IO.Path;

namespace projectFrameCut.InteractableEditor;

public sealed partial class DynamicPreview
{
    public static string DiskCacheRoot { get; set { if (Directory.Exists(value)) field = value; } } = Path.Combine(MauiProgram.DataPath, "RenderCache", "clipLocalFallback");
    public static NativePreviewOutputMode DefaultOutputMode { get; set; } = NativePreviewOutputMode.Required;
    public static bool IgnorePreviewWindowSize { get; set; }
    public static long TargetResolutionPixels { get; set; }

    /// <summary>
    /// Retained for settings compatibility. Dynamic preview effects are now always rendered by the
    /// picture pipeline; View-based effect previews are no longer used.
    /// </summary>
    public static bool DisableEffectDynamicPreview { get; set; } = false;

    /// <summary>
    /// Retained for settings compatibility. Vector clips are rasterized by their render source just
    /// like every other clip, so this value no longer changes the dynamic-preview path.
    /// </summary>
    public static bool DisableVectorPreviewPaths { get; set; } = false;

    /// <summary>
    /// When a <see cref="Render.VectorContent.Components.ComponentGroup"/> has more child components
    /// than this threshold, its preview is rasterized to a bitmap instead of being split into
    /// individual MAUI Path objects. This prevents GPU overload or platform path-count limits
    /// when a group contains a very large number of sub-components (e.g. an imported SVG with
    /// hundreds of paths). Set to <c>int.MaxValue</c> to disable early rasterization entirely.
    /// Default is 50.
    /// </summary>
    public static int GroupRasterizationChildThreshold { get; set; } = 128;
}

internal static class DynamicPreviewSettings
{
    public const string TargetPixelsSettingKey = "Edit_DynamicPreviewTargetPixels";
    public const string CacheSourceFramesSettingKey = "Edit_CacheDecodedPreviewFrames";

    public const string DiskCacheSettingKey = "Edit_PreviewDiskCache";
    public const string BufferSecondsSettingKey = "Edit_DynamicPreviewBufferSeconds";
    public static int BufferSeconds => Math.Max(1, SettingsManager.GetSettingAs(BufferSecondsSettingKey, 5, 5));
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
