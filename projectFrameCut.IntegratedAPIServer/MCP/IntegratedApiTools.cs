using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Shared;

namespace projectFrameCut.IntegratedAPIServer.MCP;

internal sealed class IntegratedApiRequestContextAccessor
{
    private readonly AsyncLocal<IntegratedApiRequestContext?> _current = new();

    public IntegratedApiRequestContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    public IDisposable Push(string? remoteAddress)
    {
        var previous = Current;
        Current = new IntegratedApiRequestContext(remoteAddress);
        return new Scope(this, previous);
    }

    private sealed class Scope(IntegratedApiRequestContextAccessor owner, IntegratedApiRequestContext? previous) : IDisposable
    {
        public void Dispose() => owner.Current = previous;
    }
}

internal sealed record IntegratedApiRequestContext(string? RemoteAddress);

internal sealed class EndpointAuthorizationManager
{
    private readonly ConcurrentDictionary<string, AuthorizationState> _endpointStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<object, AuthorizationState> _fallbackSessionStates = new();

    public bool IsAuthorized(object session, string? endpoint)
        => GetState(session, endpoint).Decision == AuthorizationDecision.Approved;

    public async ValueTask<bool> AuthorizeAsync(
        object session,
        string? endpoint,
        Func<CancellationToken, ValueTask<bool>> prompt,
        CancellationToken cancellationToken)
    {
        var state = GetState(session, endpoint);
        if (state.Decision != AuthorizationDecision.Unknown)
        {
            return state.Decision == AuthorizationDecision.Approved;
        }

        await state.PromptLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Decision == AuthorizationDecision.Unknown)
            {
                state.Decision = await prompt(cancellationToken).ConfigureAwait(false)
                    ? AuthorizationDecision.Approved
                    : AuthorizationDecision.Denied;
            }

            return state.Decision == AuthorizationDecision.Approved;
        }
        finally
        {
            state.PromptLock.Release();
        }
    }

    private AuthorizationState GetState(object session, string? endpoint)
        => endpoint is null
            ? _fallbackSessionStates.GetOrCreateValue(session)
            : _endpointStates.GetOrAdd(endpoint, static _ => new AuthorizationState());

    private sealed class AuthorizationState
    {
        public AuthorizationDecision Decision { get; set; }

        public SemaphoreSlim PromptLock { get; } = new(1, 1);
    }

    private enum AuthorizationDecision
    {
        Unknown,
        Approved,
        Denied,
    }
}

internal static class IntegratedApiToolCatalog
{
    private const string EmptySchema = """{"type":"object","properties":{},"required":[]}""";

    public static IReadOnlyList<McpServerTool> Create(
        IIntegratedApiBackend backend,
        EndpointAuthorizationManager authorization,
        IntegratedApiRequestContextAccessor requestContextAccessor,
        bool requireAuthorization = true,
        bool includeIntegratedClientTools = true)
    {
        List<McpServerTool> tools = [];
        McpServerTool CreateTool(string name, string description, string schema, IntegratedApiOperation operation)
            => Tool(name, description, schema, operation, requireAuthorization);

        if (requireAuthorization)
        {
            tools.Add(new AuthorizationTool(backend, authorization, requestContextAccessor));
        }

        tools.AddRange(
        [
            CreateTool("get_timeline_info", "Get timeline metadata: frame rate, resolution, total frames, layer count.", EmptySchema, IntegratedApiOperation.GetTimelineInfo),
            CreateTool("list_layers", "List all layers/tracks in the timeline with their properties.", EmptySchema, IntegratedApiOperation.ListLayers),
            CreateTool("list_available_effects", "List all available effect types with their parameters and defaults.", EmptySchema, IntegratedApiOperation.ListAvailableEffects),
            CreateTool("get_effect_info", "Get detailed information about a specific effect type.", Schema("effectType", "string", true), IntegratedApiOperation.GetEffectInfo),
            CreateTool("get_project_metadata", "Get project metadata: name, file path, creation/modification times, file size.", EmptySchema, IntegratedApiOperation.GetProjectMetadata),
        ]);

        if (includeIntegratedClientTools)
        {
            tools.AddRange(
            [
                CreateTool("list_connected_clients", "List currently connected editor clients.", EmptySchema, IntegratedApiOperation.ListConnectedClients),
                CreateTool("get_client_environment", "Query the integrated editor environment capabilities.", ObjectSchema("""
                    "clientId":{"type":"string","description":"Integrated editor client ID"},
                    "timeoutMs":{"type":"integer","description":"Optional request timeout in milliseconds"}
                    """, "clientId"), IntegratedApiOperation.GetClientEnvironment),
                CreateTool("render_client_preview", "Render one timeline frame in the integrated editor.", ObjectSchema("""
                    "clientId":{"type":"string"},"frame":{"type":"integer"},
                    "width":{"type":"integer"},"height":{"type":"integer"},"timeoutMs":{"type":"integer"}
                    """, "clientId", "frame"), IntegratedApiOperation.RenderClientPreview),
                CreateTool("apply_client_patch", "Apply a clip patch in the integrated editor.", ObjectSchema("""
                    "clientId":{"type":"string"},"clipId":{"type":"string"},
                    "patch":{"type":"object"},"timeoutMs":{"type":"integer"}
                    """, "clientId", "clipId", "patch"), IntegratedApiOperation.ApplyClientPatch),
                CreateTool("move_client_clip", "Move a clip in the integrated editor.", ObjectSchema("""
                    "clientId":{"type":"string"},"clipId":{"type":"string"},
                    "layerIndex":{"type":"integer"},"startFrame":{"type":"integer"},"timeoutMs":{"type":"integer"}
                    """, "clientId", "clipId", "layerIndex", "startFrame"), IntegratedApiOperation.MoveClientClip),
            ]);
        }

        tools.AddRange(
        [
            CreateTool("list_clips", "List all clips in the current project.", EmptySchema, IntegratedApiOperation.ListClips),
            CreateTool("get_clip", "Get one clip by ID.", Schema("clipId", "string", true), IntegratedApiOperation.GetClip),
            CreateTool("upsert_clip", "Create or replace a clip.", ObjectSchema("\"clip\":{\"type\":\"object\",\"description\":\"ClipDraftDTO\"}", "clip"), IntegratedApiOperation.UpsertClip),
            CreateTool("move_clip", "Move a clip to another track or frame position.", ObjectSchema("""
                "clipId":{"type":"string"},"layerIndex":{"type":"integer"},
                "startFrame":{"type":"integer"},"subLayerIndex":{"type":"integer"}
                """, "clipId", "layerIndex", "startFrame"), IntegratedApiOperation.MoveClip),
            CreateTool("patch_clip", "Patch selected clip fields.", ObjectSchema("""
                "clipId":{"type":"string"},"patch":{"type":"object"}
                """, "clipId", "patch"), IntegratedApiOperation.PatchClip),
            CreateTool("delete_clip", "Delete a clip by ID.", Schema("clipId", "string", true), IntegratedApiOperation.DeleteClip),
            CreateTool("add_effect", "Add or replace one effect on a clip.", ObjectSchema("""
                "clipId":{"type":"string"},"effect":{"type":"object"}
                """, "clipId", "effect"), IntegratedApiOperation.AddEffect),
            CreateTool("remove_effect", "Remove one effect from a clip.", ObjectSchema("""
                "clipId":{"type":"string"},"effectKey":{"type":"string"}
                """, "clipId", "effectKey"), IntegratedApiOperation.RemoveEffect),
            CreateTool("add_effect_bundle", "Add or replace one effect bundle on a clip.", ObjectSchema("""
                "clipId":{"type":"string"},"bundle":{"type":"object"}
                """, "clipId", "bundle"), IntegratedApiOperation.AddEffectBundle),
            CreateTool("remove_effect_bundle", "Remove an effect bundle from a clip.", ObjectSchema("""
                "clipId":{"type":"string"},"bundleId":{"type":"string"}
                """, "clipId", "bundleId"), IntegratedApiOperation.RemoveEffectBundle),
            CreateTool("save_project", "Persist the current project state to disk.", ObjectSchema("""
                "changeReason":{"type":"string"}
                """), IntegratedApiOperation.SaveProject),
        ]);

        return tools;
    }

    private static McpServerTool Tool(
        string name,
        string description,
        string schema,
        IntegratedApiOperation operation,
        bool requireAuthorization)
        => new BackendTool(name, description, schema, operation, requireAuthorization);

    private static string Schema(string propertyName, string type, bool required)
        => ObjectSchema($"\"{propertyName}\":{{\"type\":\"{type}\"}}", required ? [propertyName] : []);

    private static string ObjectSchema(string properties, params string[] required)
    {
        string requiredJson = string.Join(',', required.Select(static item => $"\"{item}\""));
        return $"{{\"type\":\"object\",\"properties\":{{{properties}}},\"required\":[{requiredJson}]}}";
    }

    private sealed class BackendTool : ExplicitTool
    {
        private readonly IntegratedApiOperation _operation;
        private readonly bool _requireAuthorization;

        public BackendTool(
            string name,
            string description,
            string schema,
            IntegratedApiOperation operation,
            bool requireAuthorization)
            : base(name, description, schema)
        {
            _operation = operation;
            _requireAuthorization = requireAuthorization;
        }

        public override async ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var services = request.Services ?? throw new InvalidOperationException("MCP request services are unavailable.");
            var authorization = services.GetRequiredService<EndpointAuthorizationManager>();
            var requestContextAccessor = services.GetRequiredService<IntegratedApiRequestContextAccessor>();
            if (_requireAuthorization &&
                !authorization.IsAuthorized(request.Server, GetRemoteEndpoint(requestContextAccessor)))
            {
                return Error("This client endpoint is not authorized. Call authorize_client first.");
            }

            try
            {
                var backend = services.GetRequiredService<IIntegratedApiBackend>();
                JsonElement arguments = BuildArguments(request.Params?.Arguments);
                JsonElement result = await backend.ExecuteAsync(_operation, arguments, cancellationToken).ConfigureAwait(false);
                return Success(result.GetRawText());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Error("The operation was cancelled.");
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or RenderRpcException or InvalidOperationException or FormatException or JsonException)
            {
                return Error(ex.Message);
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "Process MCP Message");
                return Error("The integrated editor could not complete the requested operation.");
            }
        }
    }

    private sealed class AuthorizationTool : ExplicitTool
    {
        private readonly IIntegratedApiBackend _backend;
        private readonly EndpointAuthorizationManager _authorization;
        private readonly IntegratedApiRequestContextAccessor _requestContextAccessor;

        public AuthorizationTool(
            IIntegratedApiBackend backend,
            EndpointAuthorizationManager authorization,
            IntegratedApiRequestContextAccessor requestContextAccessor)
            : base(
                "authorize_client",
                "Ask the projectFrameCut user to authorize this client endpoint before accessing project resources.",
                ObjectSchema("\"reason\":{\"type\":\"string\",\"description\":\"Why access to this project is needed\"}"))
        {
            _backend = backend;
            _authorization = authorization;
            _requestContextAccessor = requestContextAccessor;
        }

        public override async ValueTask<CallToolResult> InvokeAsync(
            RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            string reason = TryGetString(request.Params?.Arguments, "reason") ?? "No reason was provided.";
            var client = request.Server.ServerOptions.KnownClientInfo;
            string? endpoint = GetRemoteEndpoint(_requestContextAccessor);
            string remote = endpoint ?? "unknown";

            bool approved;
            try
            {
                approved = await _authorization.AuthorizeAsync(
                    request.Server,
                    endpoint,
                    token => _backend.RequestAuthorizationAsync(
                        new IntegratedApiAuthorizationRequest(
                            client?.Name ?? "Unknown MCP client",
                            client?.Version ?? "unknown",
                            remote,
                            reason),
                        token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Error("The authorization request was cancelled.");
            }
            catch
            {
                return Error("The editor could not display the authorization request.");
            }

            return approved
                ? Success("{\"authorized\":true}")
                : Error("The projectFrameCut user denied access for this client endpoint.");
        }
    }

    private static string? GetRemoteEndpoint(IntegratedApiRequestContextAccessor accessor)
        => accessor.Current?.RemoteAddress;

    private abstract class ExplicitTool : McpServerTool
    {
        protected ExplicitTool(string name, string description, string schema)
        {
            using var document = JsonDocument.Parse(schema);
            ProtocolTool = new Tool
            {
                Name = name,
                Description = description,
                InputSchema = document.RootElement.Clone(),
            };
        }

        public override Tool ProtocolTool { get; }

        public override IReadOnlyList<object> Metadata { get; } = [];

        protected static CallToolResult Success(string text)
            => new()
            {
                Content = [new TextContentBlock { Text = text }],
                IsError = false,
            };

        protected static CallToolResult Error(string message)
            => new()
            {
                Content = [new TextContentBlock { Text = message }],
                IsError = true,
            };

        protected static JsonElement BuildArguments(IDictionary<string, JsonElement>? arguments)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                if (arguments is not null)
                {
                    foreach (var (name, value) in arguments)
                    {
                        writer.WritePropertyName(name);
                        value.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            return document.RootElement.Clone();
        }

        protected static string? TryGetString(IDictionary<string, JsonElement>? arguments, string name)
            => arguments is not null && arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
