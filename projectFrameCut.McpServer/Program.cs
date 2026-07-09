using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using projectFrameCut.McpCore;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
public partial class Program
{
    private static async Task Main(string[] args)
    {
        try
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            if (assembly is not null)
            {
                var ProgramConfig = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown config";
                var ProgramCommit = new string((assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Skip(6).ToArray());
                var AssemblyName = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "projectFrameCut McpServer";
                Console.WriteLine($"{AssemblyName} v{assembly.GetName().Version} {ProgramConfig}@{ProgramCommit}");
                Console.WriteLine("Copyright (c) hexadecimal0x12e 2026. https://github.com/hexadecimal0x12e/projectFrameCut/");
                Console.WriteLine();
            }

        }
        catch { }

        if (args.Length == 0 || string.IsNullOrWhiteSpace(args.FirstOrDefault("")) || args.FirstOrDefault("--help") == "help" || args.FirstOrDefault("-h") == "-h")
        {
            Console.WriteLine(
                """
                Usage: projectFrameCut.McpServer http --project <projectRoot>
                   or: projectFrameCut.McpServer stdio --project <projectRoot> [--port <portNumber>]
                """);
            return;
        }


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

        string? projectRoot = GetArgValue("--project");
        bool useHttp = argsList.FirstOrDefault("").Equals("http", StringComparison.InvariantCultureIgnoreCase);
        bool useStdio = argsList.FirstOrDefault("").Equals("stdio", StringComparison.InvariantCultureIgnoreCase);
        if (!useHttp && !useStdio)
        {
            Console.Error.WriteLine($"No mode defined. Add 'http' or 'stdio' in params.");
            return;
        }
        if(string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            Console.Error.WriteLine($"Project root '{projectRoot}' does not exist.");
            return;
        }
        int port = int.TryParse(GetArgValue("--port"), out var parsedPort) ? parsedPort : 32123;

        var workspace = TimelineProjectWorkspace.Load(projectRoot);
        var editor = new TimelineProjectEditor(workspace);
        var clientHub = new ClientLinkHub(jsonOptions);

        Console.WriteLine($"Successfully loaded project from '{workspace.ProjectRoot}'. Name: '{workspace.ProjectInfo.ProjectName}'.");

        if (useHttp)
        {
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.Urls.Add($"http://127.0.0.1:{port}");
            app.UseWebSockets();
            app.MapGet("/health", () => Results.Ok(new { ok = true }));
            app.MapGet("/client/health", () => Results.Ok(new { ok = true, connectedClients = clientHub.ConnectedCount }));
            app.Map("/client", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Expected WebSocket request.");
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await clientHub.HandleClientAsync(socket, context.RequestAborted);
            });
            app.MapPost("/mcp", async (HttpContext context) =>
            {
                var request = await JsonSerializer.DeserializeAsync<McpRequest>(context.Request.Body, jsonOptions);
                if (request is null)
                {
                    return Results.BadRequest();
                }

                var response = Dispatch(request, editor, workspace, clientHub, jsonOptions);
                return Results.Json(response, jsonOptions);
            });
            Console.Error.WriteLine($"HTTP MCP endpoint will be listening on http://127.0.0.1:{port}/mcp");
            Console.Error.WriteLine($"Client WebSocket endpoint will be listening on ws://127.0.0.1:{port}/client");
            var arg = $"pjfc:mcp \"{projectRoot}\" --mcpServer=ws://127.0.0.1:{port}/client";

            if (argsList.Contains("--pullApplication", StringComparer.InvariantCultureIgnoreCase))
            {
                Console.WriteLine($"Pulling up application with arg '{arg}'...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = arg,
                    UseShellExecute = true
                });
            }
            else
            {
                Console.WriteLine($"To connect an editor client, use the following argument: {arg}");
            }
            await app.RunAsync();
            return;
        }
        else if (useStdio)
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

                var response = Dispatch(request, editor, workspace, clientHub, jsonOptions);
                Console.Out.WriteLine(JsonSerializer.Serialize(response, jsonOptions));
                Console.Out.Flush();
            }
        }
        return;



    }

    static McpResponse Dispatch(McpRequest request, TimelineProjectEditor editor, TimelineProjectWorkspace workspace, ClientLinkHub clientHub, JsonSerializerOptions options)
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
                "tools/call" => HandleCall(request, editor, workspace, clientHub, options),
                "ping" => Ok(request.Id, new { pong = true }),
                _ => Error(request.Id, -32601, $"Unknown method '{request.Method}'."),
            };
        }
        catch (Exception ex)
        {
            return Error(request.Id, -32603, ex.Message);
        }
    }

    static McpResponse HandleCall(McpRequest request, TimelineProjectEditor editor, TimelineProjectWorkspace workspace, ClientLinkHub clientHub, JsonSerializerOptions options)
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
            "list_connected_clients" => clientHub.ListClients(),
            "get_client_environment" => clientHub.Request(
                GetRequiredString(toolArgs, "clientId"),
                "get_environment",
                null,
                TryGetIntOrDefault(toolArgs, "timeoutMs", 10000)),
            "render_client_preview" => clientHub.Request(
                GetRequiredString(toolArgs, "clientId"),
                "render_preview_frame",
                new
                {
                    frame = GetRequiredUInt(toolArgs, "frame"),
                    width = TryGetInt(toolArgs, "width"),
                    height = TryGetInt(toolArgs, "height")
                },
                TryGetIntOrDefault(toolArgs, "timeoutMs", 15000)),
            "apply_client_patch" => clientHub.Request(
                GetRequiredString(toolArgs, "clientId"),
                "apply_patch_clip",
                new
                {
                    clipId = GetRequiredString(toolArgs, "clipId"),
                    patch = DeserializeRequired<Dictionary<string, object?>>(toolArgs, "patch", options)
                },
                TryGetIntOrDefault(toolArgs, "timeoutMs", 10000)),
            "move_client_clip" => clientHub.Request(
                GetRequiredString(toolArgs, "clientId"),
                "move_clip",
                new
                {
                    clipId = GetRequiredString(toolArgs, "clipId"),
                    layerIndex = GetRequiredUInt(toolArgs, "layerIndex"),
                    startFrame = GetRequiredUInt(toolArgs, "startFrame")
                },
                TryGetIntOrDefault(toolArgs, "timeoutMs", 10000)),
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
        foreach (var clip in draft.Clips)
        {
            if (clip.LayerIndex > maxLayer)
                maxLayer = clip.LayerIndex;
        }

        uint maxFrame = 0;
        foreach (var clip in draft.Clips)
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

        foreach (var clip in workspace.Draft.Clips)
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

        return new { effects, count = effects.Count };
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
                parameters
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
            projectPath,
            fileSize,
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
    new("list_connected_clients", "List currently connected editor clients.",
        new { type = "object", properties = new { }, required = new string[] { } }),
    new("get_client_environment", "Query one connected client for environment capabilities (effects/mixtures/plugins).",
        new { type = "object", properties = new {
            clientId = new { type = "string", description = "Connected client ID from list_connected_clients" },
            timeoutMs = new { type = "integer", description = "Optional request timeout in milliseconds" }
        }, required = new[] { "clientId" } }),
    new("render_client_preview", "Request one frame preview image from connected client.",
        new { type = "object", properties = new {
            clientId = new { type = "string", description = "Connected client ID" },
            frame = new { type = "integer", description = "Timeline frame index" },
            width = new { type = "integer", description = "Optional output width" },
            height = new { type = "integer", description = "Optional output height" },
            timeoutMs = new { type = "integer", description = "Optional request timeout in milliseconds" }
        }, required = new[] { "clientId", "frame" } }),
    new("apply_client_patch", "Apply clip patch on connected client and sync back UI immediately.",
        new { type = "object", properties = new {
            clientId = new { type = "string", description = "Connected client ID" },
            clipId = new { type = "string", description = "Target clip ID" },
            patch = new { type = "object", description = "Patch object for clip fields" },
            timeoutMs = new { type = "integer", description = "Optional request timeout in milliseconds" }
        }, required = new[] { "clientId", "clipId", "patch" } }),
    new("move_client_clip", "Move a clip on connected client and sync back UI immediately.",
        new { type = "object", properties = new {
            clientId = new { type = "string", description = "Connected client ID" },
            clipId = new { type = "string", description = "Target clip ID" },
            layerIndex = new { type = "integer", description = "Destination layer index" },
            startFrame = new { type = "integer", description = "Destination start frame" },
            timeoutMs = new { type = "integer", description = "Optional request timeout in milliseconds" }
        }, required = new[] { "clientId", "clipId", "layerIndex", "startFrame" } }),
    
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

    static int? TryGetInt(JsonElement json, string name)
        => json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Null ? null : v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.TryParse(v.GetString(), out var parsed) ? parsed : null
            : null;

    static int TryGetIntOrDefault(JsonElement json, string name, int fallback)
        => TryGetInt(json, name) ?? fallback;

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
        => new("2.0", null, new { code, message }, id);

    sealed record McpError(int Code, string Message);

    sealed class ClientLinkHub(JsonSerializerOptions options)
    {
        private readonly JsonSerializerOptions _options = options;
        private readonly ConcurrentDictionary<string, ClientLinkConnection> _connections = new(StringComparer.Ordinal);

        public int ConnectedCount => _connections.Count;

        public async Task HandleClientAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            var connection = new ClientLinkConnection(socket);
            try
            {
                while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var msg = await ReceiveAsync(socket, cancellationToken);
                    if (msg is null)
                    {
                        break;
                    }

                    if (string.Equals(msg.Type, "hello", StringComparison.OrdinalIgnoreCase))
                    {
                        var providedClientId = msg.ClientId;
                        connection.ClientId = string.IsNullOrWhiteSpace(providedClientId) ? $"client-{Guid.NewGuid():N}" : providedClientId;
                        connection.DraftPath = TryGetString(msg.Payload, "draftPath");
                        connection.ProjectName = TryGetString(msg.Payload, "projectName");
                        _connections[connection.ClientId] = connection;

                        await SendAsync(socket, new LinkEnvelope
                        {
                            Type = "event",
                            Action = "registered",
                            ClientId = connection.ClientId,
                            Payload = JsonSerializer.SerializeToElement(new
                            {
                                clientId = connection.ClientId,
                                connectedAt = DateTimeOffset.Now
                            }, _options)
                        }, cancellationToken);
                        continue;
                    }

                    if (string.Equals(msg.Type, "response", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(msg.Type, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(msg.RequestId) && connection.Pending.TryRemove(msg.RequestId, out var tcs))
                        {
                            if (string.Equals(msg.Type, "error", StringComparison.OrdinalIgnoreCase))
                            {
                                tcs.TrySetException(new InvalidOperationException(msg.Error?.Message ?? "Client error response."));
                            }
                            else
                            {
                                tcs.TrySetResult(msg.Payload);
                            }
                        }
                    }
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(connection.ClientId))
                {
                    _connections.TryRemove(connection.ClientId, out _);
                }

                foreach (var pending in connection.Pending.Values)
                {
                    pending.TrySetException(new InvalidOperationException("Client disconnected."));
                }
            }
        }

        public object ListClients()
        {
            return new
            {
                count = _connections.Count,
                clients = _connections.Values.Select(c => new
                {
                    clientId = c.ClientId,
                    draftPath = c.DraftPath,
                    projectName = c.ProjectName
                }).ToList()
            };
        }

        public object? Request(string clientId, string action, object? payload, int timeoutMs)
        {
            if (!_connections.TryGetValue(clientId, out var connection))
            {
                throw new KeyNotFoundException($"Client '{clientId}' is not connected.");
            }

            if (connection.Socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException($"Client '{clientId}' socket is not open.");
            }

            string requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Pending[requestId] = tcs;
            try
            {
                var envelope = new LinkEnvelope
                {
                    Type = "request",
                    RequestId = requestId,
                    ClientId = clientId,
                    Action = action,
                    Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, _options)
                };

                SendAsync(connection.Socket, envelope, CancellationToken.None).GetAwaiter().GetResult();
                if (!tcs.Task.Wait(timeoutMs))
                {
                    throw new TimeoutException($"Client request '{action}' timed out after {timeoutMs} ms.");
                }

                return tcs.Task.GetAwaiter().GetResult();
            }
            finally
            {
                connection.Pending.TryRemove(requestId, out _);
            }
        }

        private async Task SendAsync(WebSocket socket, LinkEnvelope envelope, CancellationToken cancellationToken)
        {
            string text = JsonSerializer.Serialize(envelope, _options);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }

        private async Task<LinkEnvelope?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                ms.Write(buffer.AsSpan(0, result.Count));
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            ms.Position = 0;
            using var doc = JsonDocument.Parse(ms);
            return doc.Deserialize<LinkEnvelope>(_options);
        }

        private static string? TryGetString(JsonElement? payload, string key)
        {
            if (!payload.HasValue || payload.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return payload.Value.TryGetProperty(key, out var value) ? value.GetString() : null;
        }
    }

    sealed class ClientLinkConnection(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public string ClientId { get; set; } = string.Empty;
        public string? DraftPath { get; set; }
        public string? ProjectName { get; set; }
        public ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> Pending { get; } = new(StringComparer.Ordinal);
    }

    sealed class LinkEnvelope
    {
        public string Type { get; set; } = string.Empty;
        public string? RequestId { get; set; }
        public string? ClientId { get; set; }
        public string? Action { get; set; }
        public JsonElement? Payload { get; set; }
        public LinkError? Error { get; set; }
    }

    sealed class LinkError
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}




