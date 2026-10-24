using projectFrameCut.Setting.SettingManager;

namespace projectFrameCut.Services;

internal static class RenderSchedulingSettings
{
    public static int MaxParallelWorkers => Math.Max(1, (int)SettingsManager.GetSettingAs<double>("render_defaultMaxParallelWorkers", 8, 8));
    public static bool AllowWorkerOversubscription => SettingsManager.IsBoolSettingTrueOrDefault("render_allowWorkerOversubscription", true);
    public static bool EnableThreadAffinity => SettingsManager.IsBoolSettingTrueOrDefault("render_enableThreadAffinity", true);
    public static int RenderAheadFrames => Math.Max(0, SettingsManager.GetSettingAs("render_renderAheadFrames", 0, 0));
    public static bool EnableMemoryBudget => SettingsManager.IsBoolSettingTrueOrDefault("render_enableMemoryBudget", false);
    public static int[] WorkerCPUCoreIndexs => SettingsManager.GetSetting("render_coreAffinityOverride", "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(c => int.TryParse(c, out var index) && index >= 0).Select(int.Parse).Distinct().ToArray();
    public static long MaxBufferedBytes => Math.Clamp(SettingsManager.GetSettingAs<long>("render_maxBufferedMiB", 0, 0),
        0, long.MaxValue / (1024 * 1024)) * 1024 * 1024;
}
