using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.Contracts;

public sealed class ExternalRpcPipeSettings
{
    public const string AllowExternalRpcSetting = "Security_ExternalRpcPipe_AllowExternalRPC";
    public const string DisabledMessage = "External RPC clients are disabled by security policy (Security_ExternalRpcPipe_AllowExternalRPC).";

    public string[] AllowedSids { get; set; } = [];
    public bool SingleConnection { get; set; } = true;
    [JsonPropertyName(AllowExternalRpcSetting)]
    public bool AllowExternalRPC { get; set; } = true;

    public static ExternalRpcPipeSettings Load(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "ExternalRpcPipePermissions.json");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? new() { AllowedSids = document.RootElement.Deserialize<string[]>() ?? [] }
                : document.RootElement.Deserialize<ExternalRpcPipeSettings>() ?? new();
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }

    public void Save(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "ExternalRpcPipePermissions.json");
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

    public static void EnsureAllowed(string dataRoot) => ThrowIfDisabled(Load(dataRoot).AllowExternalRPC);

    public static void ThrowIfDisabled(bool allowed)
    {
        if (!allowed) throw new UnauthorizedAccessException(DisabledMessage);
    }
}
