using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using projectFrameCut.McpCore;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using System.Reflection;
using System.Text.Json;
try
{
    Assembly assembly = Assembly.GetExecutingAssembly();
    if (assembly is not null)
    {
        var ProgramConfig = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown config";
        var ProgramCommit = new string((assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Skip(6).ToArray());
        var AssemblyName = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "projectFrameCut McpServer";
        Console.WriteLine($"{AssemblyName} v{assembly.GetName().Version} {ProgramConfig}@{ProgramCommit}");
        Console.WriteLine("Copyright (c) hexadecimal0x12e 2026.");
        Console.WriteLine();
    }

}
catch { }


// Define JsonOptions at top level so all methods can access it
var jsonOptions = new JsonSerializerOptions()
{
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
};

var argsList = args.ToList();

// Define GetArgValue as a local function
string? GetArgValue(string name)
{
    int index = argsList.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase) || string.Equals(a, name.TrimStart('-').ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
    if (index < 0 || index + 1 >= argsList.Count)
    {
        return null;
    }

    string next = argsList[index + 1];
    return next.StartsWith("--", StringComparison.Ordinal) ? null : next;
}

string? projectRoot = GetArgValue("--project") ?? Environment.CurrentDirectory;
bool useHttp = !argsList.Contains("--stdio", StringComparer.OrdinalIgnoreCase);
bool useStdio = !argsList.Contains("--http", StringComparer.OrdinalIgnoreCase);
int port = int.TryParse(GetArgValue("--port"), out var parsedPort) ? parsedPort : 32123;

var workspace = TimelineProjectWorkspace.Load(projectRoot);
var editor = new TimelineProjectEditor(workspace);

Console.WriteLine($"Successfully loaded project from '{workspace.ProjectRoot}'. Name: '{workspace.ProjectInfo.ProjectName}'.");

if (useHttp)
{
    var builder = WebApplication.CreateBuilder(args);
    var app = builder.Build();
    app.Urls.Add($"http://127.0.0.1:{port}");
    app.MapGet("/health", () => Results.Ok(new { ok = true }));
    app.MapPost("/mcp", async (HttpContext context) =>
    {
        var request = await JsonSerializer.DeserializeAsync<McpRequest>(context.Request.Body, jsonOptions);
        if (request is null)
        {
            return Results.BadRequest();
        }

        var response = Dispatch(request, editor, workspace, jsonOptions);
        return Results.Json(response, jsonOptions);
    });
    Console.Error.WriteLine($"HTTP MCP endpoint listening on http://127.0.0.1:{port}/mcp");
    if (!useStdio)
    {
        await app.RunAsync();
        return;
    }

    _ = app.RunAsync();
}

if (useStdio)
{
    Console.Error.WriteLine("MCP stdio mode ready.");
    while (true)
    {
        string? line = Console.ReadLine();
        if (line is null)
        {
            break;
        }

        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        McpRequest? request = JsonSerializer.Deserialize<McpRequest>(line, jsonOptions);
        if (request is null)
        {
            continue;
        }

        var response = Dispatch(request, editor, workspace, jsonOptions);
        Console.Out.WriteLine(JsonSerializer.Serialize(response, jsonOptions));
        Console.Out.Flush();
    }
}

static McpResponse Dispatch(McpRequest request, TimelineProjectEditor editor, TimelineProjectWorkspace workspace, JsonSerializerOptions options)
{
    try
    {
        return request.Method switch
        {
            "initialize" => Ok(request.Id, new
            {
                protocolVersion = "2024-11-05",
                serverInfo = new { name = "projectFrameCut", version = "0.1.0" },
                capabilities = new
                {
                    tools = new { },
                }
            }),
            "tools/list" => Ok(request.Id, new { tools = BuildTools() }),
            "tools/call" => HandleCall(request, editor, workspace, options),
            "ping" => Ok(request.Id, new { pong = true }),
            _ => Error(request.Id, -32601, $"Unknown method '{request.Method}'."),
        };
    }
    catch (Exception ex)
    {
        return Error(request.Id, -32603, ex.Message);
    }
}

static McpResponse HandleCall(McpRequest request, TimelineProjectEditor editor, TimelineProjectWorkspace workspace, JsonSerializerOptions options)
{
    JsonElement? args = request.Params;
    string toolName = args.HasValue && args.Value.ValueKind == JsonValueKind.Object && args.Value.TryGetProperty("name", out var nameEl)
        ? nameEl.GetString() ?? string.Empty
        : string.Empty;

    JsonElement toolArgs = args.HasValue && args.Value.ValueKind == JsonValueKind.Object && args.Value.TryGetProperty("arguments", out var argEl)
        ? argEl
        : default;

    object result = toolName switch
    {
        "get_timeline_info" => GetTimelineInfo(workspace),
        "list_layers" => GetLayers(workspace),
        "list_available_effects" => GetAvailableEffects(),
        "get_effect_info" => GetEffectDetail(GetRequiredString(toolArgs, "effectType")),
        "get_project_metadata" => GetProjectMetadata(workspace),
        "list_clips" => new { clips = editor.ListClips() },
        "get_clip" => editor.GetClip(GetRequiredString(toolArgs, "clipId")),
        "upsert_clip" => editor.UpsertClip(DeserializeRequired<ClipDraftDTO>(toolArgs, "clip", options)),
        "move_clip" => editor.MoveClip(
            GetRequiredString(toolArgs, "clipId"),
            GetRequiredUInt(toolArgs, "layerIndex"),
            GetRequiredUInt(toolArgs, "startFrame"),
            TryGetUInt(toolArgs, "subLayerIndex")),
        "patch_clip" => editor.PatchClip(
            GetRequiredString(toolArgs, "clipId"),
            DeserializeRequired<Dictionary<string, object?>>(toolArgs, "patch", options)),
        "delete_clip" => new { deleted = editor.DeleteClip(GetRequiredString(toolArgs, "clipId")) },
        "add_effect" => editor.AddOrReplaceEffect(
            GetRequiredString(toolArgs, "clipId"),
            DeserializeRequired<EffectAndMixtureJSONStructure>(toolArgs, "effect", options)),
        "remove_effect" => new { removed = editor.RemoveEffect(GetRequiredString(toolArgs, "clipId"), GetRequiredString(toolArgs, "effectKey")) },
        "add_effect_bundle" => editor.AddOrReplaceEffectBundle(
            GetRequiredString(toolArgs, "clipId"),
            DeserializeRequired<EffectBundleJSONStructure>(toolArgs, "bundle", options)),
        "remove_effect_bundle" => new { removed = editor.RemoveEffectBundle(GetRequiredString(toolArgs, "clipId"), GetRequiredGuid(toolArgs, "bundleId")) },
        "save_project" => SaveAndSummarize(workspace, editor),
        _ => throw new InvalidOperationException($"Unknown tool '{toolName}'."),
    };

    if (!string.Equals(toolName, "save_project", StringComparison.Ordinal))
    {
        workspace.Save($"MCP tool {toolName}");
    }

    return Ok(request.Id, new
    {
        content = new object[]
        {
            new { type = "text", text = JsonSerializer.Serialize(result, options) }
        },
        isError = false,
        result
    });
}

static object SaveAndSummarize(TimelineProjectWorkspace workspace, TimelineProjectEditor editor)
{
    workspace.Save("MCP save");
    return new
    {
        saved = true,
        projectRoot = workspace.ProjectRoot,
        clipCount = editor.ListClips().Count
    };
}

static object GetTimelineInfo(TimelineProjectWorkspace workspace)
{
    var projectInfo = workspace.ProjectInfo;
    var draft = workspace.Draft;

    uint maxLayer = 0;
    foreach (var clip in draft.Clips.OfType<ClipDraftDTO>())
    {
        if (clip.LayerIndex > maxLayer)
            maxLayer = clip.LayerIndex;
    }

    uint maxFrame = 0;
    foreach (var clip in draft.Clips.OfType<ClipDraftDTO>())
    {
        uint endFrame = clip.StartFrame + clip.Duration;
        if (endFrame > maxFrame)
            maxFrame = endFrame;
    }

    return new
    {
        projectName = projectInfo.ProjectName,
        width = projectInfo.RelativeWidth,
        height = projectInfo.RelativeHeight,
        frameRate = projectInfo.TargetFrameRate,
        totalFrames = maxFrame,
        layerCount = maxLayer + 1,
        clipCount = draft.Clips.Length,
        lastChanged = projectInfo.LastChanged,
        savedAt = draft.SavedAt
    };
}

static object GetLayers(TimelineProjectWorkspace workspace)
{
    var layers = new Dictionary<uint, object>();
    var layerClips = new Dictionary<uint, List<ClipDraftDTO>>();

    foreach (var clip in workspace.Draft.Clips.OfType<ClipDraftDTO>())
    {
        if (!layerClips.ContainsKey(clip.LayerIndex))
            layerClips[clip.LayerIndex] = new();
        layerClips[clip.LayerIndex].Add(clip);
    }

    foreach (var kv in layerClips)
    {
        layers[kv.Key] = new
        {
            layerIndex = kv.Key,
            clipCount = kv.Value.Count,
            clips = kv.Value.OrderBy(c => c.StartFrame).Select(c => new { id = c.Id, name = c.Name, startFrame = c.StartFrame, duration = c.Duration }).ToList()
        };
    }

    return new { layers = layers.Values.ToList() };
}

static object GetAvailableEffects()
{
    var effects = new List<object>();

    try
    {
        if (EffectHelper.EffectsEnum != null)
        {
            foreach (var kv in EffectHelper.EffectsEnum)
            {
                try
                {
                    var effectInstance = kv.Value();
                    effects.Add(new
                    {
                        typeName = kv.Key,
                        name = effectInstance.Name,
                        fromPlugin = effectInstance.FromPlugin,
                        effectType = effectInstance.TypeOfEffect.ToString(),
                        description = effectInstance.GetInfo()?.Description ?? "No description available"
                    });
                }
                catch
                {
                    // Skip effects that fail to instantiate
                }
            }
        }
    }
    catch
    {
        // If EffectHelper is not available, return empty list
    }

    return new { effects = effects, count = effects.Count };
}

static object GetEffectDetail(string effectType)
{
    try
    {
        IEffect? effect = null;

        if (EffectHelper.EffectsEnum != null && EffectHelper.EffectsEnum.TryGetValue(effectType, out var creator))
        {
            effect = creator();
        }

        if (effect == null)
        {
            return new { error = $"Effect type '{effectType}' not found or cannot be instantiated." };
        }

        var info = effect.GetInfo();
        var parameters = new Dictionary<string, object>();

        if (info?.Parameters != null)
        {
            foreach (var param in info.Parameters)
            {
                parameters[param.Key] = new
                {
                    name = param.Value.Name,
                    type = param.Value.ParameterType,
                    defaultValue = param.Value.DefaultValue
                };
            }
        }

        return new
        {
            typeName = effectType,
            name = effect.Name,
            fromPlugin = effect.FromPlugin,
            effectType = effect.TypeOfEffect.ToString(),
            description = info?.Description ?? "No description",
            isEnabled = effect.Enabled,
            index = effect.Index,
            parameters = parameters
        };
    }
    catch (Exception ex)
    {
        return new { error = ex.Message };
    }
}

static object GetProjectMetadata(TimelineProjectWorkspace workspace)
{
    var projectInfo = workspace.ProjectInfo;
    var projectPath = workspace.ProjectRoot;

    long fileSize = 0;
    try
    {
        var projectFile = Path.Combine(projectPath, "project.pjfc");
        if (File.Exists(projectFile))
        {
            fileSize += new FileInfo(projectFile).Length;
        }

        var timelineFile = Path.Combine(projectPath, "timeline.json");
        if (File.Exists(timelineFile))
        {
            fileSize += new FileInfo(timelineFile).Length;
        }
    }
    catch { }

    return new
    {
        projectName = projectInfo.ProjectName,
        projectPath = projectPath,
        fileSize = fileSize,
        createdOrModified = projectInfo.LastChanged,
        lastSnapshotId = projectInfo.LastSnapshotID,
        pluginsUsed = projectInfo.PluginUsed ?? new List<string>(),
        normallyExited = projectInfo.NormallyExited
    };
}

static List<McpToolDefinition> BuildTools() =>
[
    // Query tools - Timeline Info
    new("get_timeline_info", "Get timeline metadata: frame rate, resolution, total frames, layer count.",
        new { type = "object", properties = new { }, required = new string[] { } }),
    new("list_layers", "List all layers/tracks in the timeline with their properties.",
        new { type = "object", properties = new { }, required = new string[] { } }),
    new("list_available_effects", "List all available effect types with their parameters and defaults.",
        new { type = "object", properties = new { }, required = new string[] { } }),
    new("get_effect_info", "Get detailed information about a specific effect type.",
        new { type = "object", properties = new { effectType = new { type = "string", description = "The effect type name (e.g., 'Opacity', 'Scale')" } }, required = new[] { "effectType" } }),
    new("get_project_metadata", "Get project metadata: name, file path, creation/modification times, file size.",
        new { type = "object", properties = new { }, required = new string[] { } }),
    
    // Clip management tools
    new("list_clips", "List all clips in the current project.",
        new { type = "object", properties = new { }, required = new string[] { } }),
    new("get_clip", "Get one clip by id.",
        new { type = "object", properties = new { clipId = new { type = "string", description = "Unique clip identifier" } }, required = new[] { "clipId" } }),
    new("upsert_clip", "Create or replace a clip. Requires valid clip object with at minimum: id, layerIndex, startFrame.",
        new { type = "object", properties = new { clip = new { type = "object", description = "Clip object (ClipDraftDTO)" } }, required = new[] { "clip" } }),
    new("move_clip", "Move a clip to another track (layer) or frame position.",
        new { type = "object", properties = new {
            clipId = new { type = "string", description = "Clip ID to move" },
            layerIndex = new { type = "integer", description = "Target layer/track index" },
            startFrame = new { type = "integer", description = "Target frame position" },
            subLayerIndex = new { type = "integer", description = "Optional: sub-layer index (usually same as layerIndex)" }
        }, required = new[] { "clipId", "layerIndex", "startFrame" } }),
    new("patch_clip", "Patch/update selected clip fields (non-destructive update).",
        new { type = "object", properties = new {
            clipId = new { type = "string", description = "Clip ID to patch" },
            patch = new { type = "object", description = "Object with fields to update (e.g., {name: 'new name', targetWidth: 1920})" }
        }, required = new[] { "clipId", "patch" } }),
    new("delete_clip", "Delete a clip by id.",
        new { type = "object", properties = new { clipId = new { type = "string", description = "Clip ID to delete" } }, required = new[] { "clipId" } }),
    
    // Effect management tools
    new("add_effect", "Add or replace one effect on a clip. Effect must have: typeName, fromPlugin.",
        new { type = "object", properties = new {
            clipId = new { type = "string", description = "Target clip ID" },
            effect = new { type = "object", description = "Effect object (EffectAndMixtureJSONStructure)" }
        }, required = new[] { "clipId", "effect" } }),
    new("remove_effect", "Remove one effect from a clip by name or id.",
        new { type = "object", properties = new {
            clipId = new { type = "string", description = "Target clip ID" },
            effectKey = new { type = "string", description = "Effect name or ID to remove" }
        }, required = new[] { "clipId", "effectKey" } }),
    new("add_effect_bundle", "Add or replace one effect bundle on a clip. Bundle must have: bundleTypeName, id.",
        new { type = "object", properties = new {
            clipId = new { type = "string", description = "Target clip ID" },
            bundle = new { type = "object", description = "Effect bundle object (EffectBundleJSONStructure)" }
        }, required = new[] { "clipId", "bundle" } }),
    new("remove_effect_bundle", "Remove an effect bundle from a clip by bundle id.",
        new { type = "object", properties = new {
            clipId = new { type = "string", description = "Target clip ID" },
            bundleId = new { type = "string", description = "Bundle ID (GUID) to remove" }
        }, required = new[] { "clipId", "bundleId" } }),
    
    // Project management
    new("save_project", "Persist the current project state to disk.",
        new { type = "object", properties = new { changeReason = new { type = "string", description = "Optional: reason for this save" } }, required = new string[] { } }),
];

static string GetRequiredString(JsonElement json, string name)
    => json.TryGetProperty(name, out var v) ? v.GetString() ?? throw new InvalidOperationException($"Missing '{name}'.") : throw new InvalidOperationException($"Missing '{name}'.");

static uint GetRequiredUInt(JsonElement json, string name)
    => json.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetUInt32() : uint.Parse(v.GetString() ?? throw new InvalidOperationException($"Missing '{name}'.")) : throw new InvalidOperationException($"Missing '{name}'.");

static uint? TryGetUInt(JsonElement json, string name)
    => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Null ? null : v.GetUInt32() : null;

static Guid GetRequiredGuid(JsonElement json, string name)
    => Guid.Parse(GetRequiredString(json, name));

static T DeserializeRequired<T>(JsonElement json, string name, JsonSerializerOptions options)
{
    if (!json.TryGetProperty(name, out var v))
    {
        throw new InvalidOperationException($"Missing '{name}'.");
    }

    return v.Deserialize<T>(options) ?? throw new InvalidOperationException($"Failed to deserialize '{name}'.");
}

static McpResponse Ok(JsonElement? id, object result)
    => new("2.0", result, null, id);
static McpResponse Error(JsonElement? id, int code, string message)
    => new("2.0", null, new { code = code, message = message }, id);

file sealed record McpError(int Code, string Message);
