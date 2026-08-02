using Microsoft.Maui.ApplicationModel;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Services;

public sealed class McpClientLinkService
{
    public static McpClientLinkService Shared { get; } = new();

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private DraftPage? _currentPage;
    private string _clientId = $"pjfc-client-{Guid.NewGuid():N}";
    private string _connectedServer = string.Empty;

    private McpClientLinkService()
    {
    }

    public async Task ConnectAsync(DraftPage page, string draftPath, string serverAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(draftPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverAddress);

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await DisconnectCoreAsync();
            var endpoint = NormalizeServerAddress(serverAddress);

            var socket = new ClientWebSocket();
            await socket.ConnectAsync(endpoint, cancellationToken);

            _socket = socket;
            _currentPage = page;
            _connectedServer = endpoint.ToString();
            _clientId = $"pjfc-client-{Guid.NewGuid():N}";
            _receiveCts = new CancellationTokenSource();
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), _receiveCts.Token);

            var hello = new LinkMessage
            {
                Type = "hello",
                ClientId = _clientId,
                Action = "register",
                Payload = JsonSerializer.SerializeToElement(new
                {
                    draftPath = Path.GetFullPath(draftPath),
                    projectName = page.ProjectName,
                    app = "projectFrameCut",
                    capabilities = new[] { "get_environment", "render_preview_frame", "apply_patch_clip", "move_clip" }
                }, _jsonOptions)
            };
            await SendMessageAsync(hello, cancellationToken);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task DisconnectCoreAsync()
    {
        try
        {
            _receiveCts?.Cancel();
        }
        catch
        {
        }

        if (_socket is not null)
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);
                }
            }
            catch
            {
            }
            _socket.Dispose();
            _socket = null;
        }

        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveTask = null;
        _currentPage = null;
        _connectedServer = string.Empty;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var socket = _socket;
            if (socket is null || socket.State != WebSocketState.Open)
            {
                return;
            }

            var message = await ReceiveMessageAsync(socket, cancellationToken);
            if (message is null)
            {
                return;
            }

            if (!string.Equals(message.Type, "request", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var response = await HandleRequestAsync(message);
            await SendMessageAsync(response, cancellationToken);
        }
    }

    private async Task<LinkMessage> HandleRequestAsync(LinkMessage request)
    {
        try
        {
            var page = _currentPage ?? throw new InvalidOperationException("No active draft page connected.");
            var payload = request.Payload;
            var action = request.Action ?? string.Empty;

            object result = action switch
            {
                "get_environment" => await MainThread.InvokeOnMainThreadAsync(BuildEnvironmentPayload),
                "render_preview_frame" => await MainThread.InvokeOnMainThreadAsync(() => RenderPreviewPayload(page, payload)),
                "apply_patch_clip" => await MainThread.InvokeOnMainThreadAsync(() => ApplyPatchPayload(page, payload)),
                "move_clip" => await MainThread.InvokeOnMainThreadAsync(() => MoveClipPayload(page, payload)),
                _ => throw new InvalidOperationException($"Unknown client action '{action}'.")
            };

            return new LinkMessage
            {
                Type = "response",
                RequestId = request.RequestId,
                ClientId = _clientId,
                Action = action,
                Payload = JsonSerializer.SerializeToElement(result, _jsonOptions)
            };
        }
        catch (Exception ex)
        {
            return new LinkMessage
            {
                Type = "error",
                RequestId = request.RequestId,
                ClientId = _clientId,
                Action = request.Action,
                Error = new LinkError { Code = "client_error", Message = ex.Message }
            };
        }
    }

    private object BuildEnvironmentPayload()
    {
        var effects = new List<object>();
        foreach (var kv in EffectHelper.EffectsProviderEnum)
        {
            try
            {
                var item = kv.Value();
                effects.Add(new
                {
                    typeName = kv.Key,
                    name = item.Name,
                    fromPlugin = item.FromPlugin,
                    effectType = item.TypeOfEffect.ToString()
                });
            }
            catch
            {
            }
        }

        var mixtures = PluginManager.LoadedPlugins.Values
            .OfType<IApplicationPluginBase>()
            .SelectMany(p => p.EffectProviderProvider)
            .Select(kv => new
            {
                typeName = kv.Key,
                provider = "plugin"
            })
            .ToList();

        return new
        {
            connectedServer = _connectedServer,
            effects,
            mixtures,
            loadedPluginCount = PluginManager.LoadedPlugins.Count
        };
    }

    private object RenderPreviewPayload(DraftPage page, JsonElement payload)
    {
        uint frame = ReadUInt(payload, "frame");
        int width = ReadInt(payload, "width", page.ProjectInfo.RelativeWidth);
        int height = ReadInt(payload, "height", page.ProjectInfo.RelativeHeight);

        string imagePath = page.previewer.RenderFrame(frame, width, height);
        byte[] pngBytes = File.ReadAllBytes(imagePath);
        return new
        {
            frame,
            width,
            height,
            mimeType = "image/png",
            imageBase64 = Convert.ToBase64String(pngBytes)
        };
    }

    private object ApplyPatchPayload(DraftPage page, JsonElement payload)
    {
        string clipId = ReadRequiredString(payload, "clipId");
        var patch = payload.TryGetProperty("patch", out var patchEl) && patchEl.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(patchEl.GetRawText(), _jsonOptions) ?? new Dictionary<string, object?>()
            : throw new InvalidOperationException("Missing patch.");

        var updated = TimelineMcpLiveService.ApplyClipPatch(page, clipId, patch);
        return DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, updated, false);
    }

    private object MoveClipPayload(DraftPage page, JsonElement payload)
    {
        string clipId = ReadRequiredString(payload, "clipId");
        uint layerIndex = ReadUInt(payload, "layerIndex");
        uint startFrame = ReadUInt(payload, "startFrame");
        var moved = TimelineMcpLiveService.MoveClip(page, clipId, layerIndex, startFrame);
        return DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, moved, false);
    }

    private async Task SendMessageAsync(LinkMessage message, CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new InvalidOperationException("MCP link socket is not connected.");
        string text = JsonSerializer.Serialize(message, _jsonOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task<LinkMessage?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
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
        return doc.Deserialize<LinkMessage>(_jsonOptions);
    }

    private static Uri NormalizeServerAddress(string serverAddress)
    {
        string trimmed = serverAddress.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "ws://" + trimmed["http://".Length..];
        }
        else if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "wss://" + trimmed["https://".Length..];
        }
        else if (!trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                 !trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "ws://" + trimmed;
        }

        if (!trimmed.EndsWith("/client", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.TrimEnd('/') + "/client";
        }

        return new Uri(trimmed, UriKind.Absolute);
    }

    private static string ReadRequiredString(JsonElement json, string key)
    {
        if (!json.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Missing '{key}'.");
        }

        return value.GetString() ?? throw new InvalidOperationException($"Missing '{key}'.");
    }

    private static uint ReadUInt(JsonElement json, string key)
    {
        if (!json.TryGetProperty(key, out var value))
        {
            throw new InvalidOperationException($"Missing '{key}'.");
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetUInt32(),
            JsonValueKind.String => uint.Parse(value.GetString() ?? throw new InvalidOperationException($"Missing '{key}'.")),
            _ => throw new InvalidOperationException($"Invalid '{key}'.")
        };
    }

    private static int ReadInt(JsonElement json, string key, int fallback)
    {
        if (!json.TryGetProperty(key, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt32(),
            JsonValueKind.String => int.TryParse(value.GetString(), out var parsed) ? parsed : fallback,
            _ => fallback
        };
    }

    private sealed class LinkMessage
    {
        public string Type { get; set; } = string.Empty;
        public string? RequestId { get; set; }
        public string? ClientId { get; set; }
        public string? Action { get; set; }
        public JsonElement Payload { get; set; }
        public LinkError? Error { get; set; }
    }

    private sealed class LinkError
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}
