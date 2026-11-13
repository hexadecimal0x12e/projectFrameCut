using System.Text.Json;

namespace projectFrameCut;

public static partial class MauiProgram
{
    internal static void InitializeHeadless(string[] args)
    {
        CmdlineArgs = args;
        IsStoreMode = false;
        BasicDataPath = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hexadecimal0x12e", AppIdentifier)
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "projectFrameCut")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".projectFrameCut", "AppData");
        var overridePath = Path.Combine(BasicDataPath, "OverrideUserDataPath.txt");
        DataPath = File.Exists(overridePath)
            ? File.ReadAllText(overridePath).Trim()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "projectFrameCut");
        var dataRoot = args.FirstOrDefault(a => a.StartsWith("--dataRoot=", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("-dataRoot=", StringComparison.OrdinalIgnoreCase));
        if (dataRoot is not null) DataPath = Path.GetFullPath(dataRoot[(dataRoot.IndexOf('=') + 1)..]);
        Directory.CreateDirectory(BasicDataPath);
        Directory.CreateDirectory(CachePath);
        foreach (var folder in FoldersNeedInUserdata) Directory.CreateDirectory(Path.Combine(DataPath, folder));
        var settingsPath = Path.Combine(BasicDataPath, "settings.json");
        SettingsManager.Settings = File.Exists(settingsPath)
            ? new(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(settingsPath)) ?? [])
            : new();
        Log($"Headless client initialized: BasicDataPath={BasicDataPath}, DataPath={DataPath}, CachePath={CachePath}.");
    }
}
