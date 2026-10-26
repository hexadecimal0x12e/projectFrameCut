using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.Contracts;

public sealed class ExternalSourceSettings
{
    public const string AllowRpcSourcesSetting = "Security_ExternalSources_AllowRpcSources";
    public const string AllowAssemblySourcesSetting = "Security_ExternalSources_AllowAssemblySources";

    [JsonPropertyName(AllowRpcSourcesSetting)]
    public bool AllowRpcSources { get; set; } = true;
    [JsonPropertyName(AllowAssemblySourcesSetting)]
    public bool AllowAssemblySources { get; set; } = true;

    public static ExternalSourceSettings Load(string dataRoot)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(dataRoot, "ExternalSourceSettings.json"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<ExternalSourceSettings>(stream) ?? new();
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }

    public void Save(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "ExternalSourceSettings.json");
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static void EnsureRpcSourcesAllowed(string dataRoot)
    {
        if (!Load(dataRoot).AllowRpcSources)
            throw new UnauthorizedAccessException("RPC external sources are disabled by security policy.");
    }

    public static void EnsureAssemblySourcesAllowed(string dataRoot)
    {
        if (!Load(dataRoot).AllowAssemblySources)
            throw new UnauthorizedAccessException("Assembly external sources are disabled by security policy.");
    }
}
