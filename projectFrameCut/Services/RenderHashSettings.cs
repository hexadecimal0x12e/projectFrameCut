using projectFrameCut.Render.Contracts;
using projectFrameCut.Setting.SettingManager;

namespace projectFrameCut.Services;

internal static class RenderHashSettings
{
    public static FrameHashMode Mode => Enum.TryParse<FrameHashMode>(SettingsManager.GetSetting("render_FrameHashMode", "Dynamic"), true, out var mode)
        && Enum.IsDefined(mode) ? mode : FrameHashMode.Dynamic;
}
