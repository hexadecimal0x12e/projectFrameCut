using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using projectFrameCut.IntegratedAPIServer.Headless;
using projectFrameCut.IntegratedAPIServer.MCP;
using projectFrameCut.Render.Contracts;
using WatsonWebserver.Core;
using WatsonWebserver.Lite;
using WatsonHttpMethod = WatsonWebserver.Core.HttpMethod;

namespace projectFrameCut.IntegratedAPIServer;

public sealed class IntegratedApiServer : IAsyncDisposable
{
    private const int MaxMcpRequestBytes = 16 * 1024 * 1024;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Dictionary<string, McpSession> _mcpSessions = new(StringComparer.Ordinal);
    private WebserverLite? _server;
    private ServiceProvider? _services;
    private byte[]? _rpcTokenHash;
    private HeadlessProjectService? _headlessService;
    private IIntegratedApiBackend? _backend;
    private EndpointAuthorizationManager? _authorization;
    private IntegratedApiRequestContextAccessor? _requestContextAccessor;
    private bool _enableMcp;

    public bool IsRunning => _server?.IsListening == true;

    public Uri? ListenUri { get; private set; }

    public async Task StartAsync(IntegratedApiServerOptions options, IIntegratedApiBackend backend, CancellationToken cancellationToken = default)
        => await StartCoreAsync(options, backend, null, cancellationToken).ConfigureAwait(false);

    public async Task StartHeadlessAsync(IntegratedApiServerOptions options, CancellationToken cancellationToken = default)
        => await StartCoreAsync(options, null, null, cancellationToken).ConfigureAwait(false);

    public async Task StartHeadlessAsync(IntegratedApiServerOptions options, HeadlessProjectService headlessService, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(headlessService);
        await StartCoreAsync(options, null, headlessService, cancellationToken).ConfigureAwait(false);
    }

    private async Task StartCoreAsync(IntegratedApiServerOptions options, IIntegratedApiBackend? backend, HeadlessProjectService? headlessServiceOverride, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateListenUri(options.ListenUri);
        bool enableRpc = !string.IsNullOrEmpty(options.RpcToken) || headlessServiceOverride is not null;
        if (backend is null && string.IsNullOrEmpty(options.RpcToken)) throw new ArgumentException("Headless mode requires an RPC token.", nameof(options));
        if (backend is null && headlessServiceOverride is null && string.IsNullOrWhiteSpace(options.ProjectRoot)) throw new ArgumentException("Headless mode requires a project root.", nameof(options));
        if (!string.IsNullOrEmpty(options.RpcToken)) ValidateRpcToken(options.RpcToken);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_server is not null) throw new InvalidOperationException("The integrated API server is already running.");
            if (string.Equals(options.ListenUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !IsLoopbackHost(options.ListenUri.Host))
                options.WarningSink?.Invoke("The integrated API server is listening on an unencrypted LAN HTTP endpoint. Credentials and project data are not protected in transit.");

            _backend = backend;
            _enableMcp = backend is not null && options.EnableMcp;
            _authorization = new EndpointAuthorizationManager();
            _requestContextAccessor = new IntegratedApiRequestContextAccessor();
            _headlessService = headlessServiceOverride ?? (enableRpc ? new HeadlessProjectService(options.GlobalAssetsDatabasePath) : null);
            _rpcTokenHash = string.IsNullOrEmpty(options.RpcToken) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(options.RpcToken));

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSingleton(_requestContextAccessor);
            serviceCollection.AddSingleton(_authorization);
            if (backend is not null) serviceCollection.AddSingleton(backend);
            if (_headlessService is not null) serviceCollection.AddSingleton(_headlessService);
            _services = serviceCollection.BuildServiceProvider();

            if (_headlessService is not null && !_headlessService.IsInitialized && !string.IsNullOrWhiteSpace(options.ProjectRoot))
                await _headlessService.InitializeAsync(options.ProjectRoot, cancellationToken).ConfigureAwait(false);

            bool ssl = string.Equals(options.ListenUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            var settings = new WebserverSettings(options.ListenUri.Host, options.ListenUri.Port, ssl);
            settings.IO.MaxRequestBodySize = Math.Max(RenderProtocol.MaxPipeFrameBytes, MaxMcpRequestBytes);
            settings.IO.EnableKeepAlive = true;
            settings.Ssl.SslCertificate = options.SslCertificate;
            settings.Ssl.PfxCertificateFile = options.SslCertificateFile;
            settings.Ssl.PfxCertificatePassword = options.SslCertificatePassword;

            var server = new WebserverLite(settings, DispatchRequestAsync);
            try { await server.StartAsync(cancellationToken).ConfigureAwait(false); }
            catch { server.Dispose(); await DisposeRuntimeAsync().ConfigureAwait(false); throw; }
            _server = server;
            ListenUri = options.ListenUri;
        }
        catch
        {
            _server?.Dispose();
            _server = null;
            ListenUri = null;
            await DisposeRuntimeAsync().ConfigureAwait(false);
            throw;
        }
        finally { _lifecycleLock.Release(); }
    }

    private async Task DispatchRequestAsync(HttpContextBase context)
    {
        try
        {
            string path = context.Request.Url.RawWithoutQuery.TrimEnd('/');
            if (path.Length == 0) path = "/";
            switch (context.Request.Method)
            {
                case WatsonHttpMethod.GET when path == "/health":
                    await SendJsonAsync(context, new HealthResponse("ok", true), IntegratedApiJsonContext.Default.HealthResponse).ConfigureAwait(false); return;
#if DEBUG
                case WatsonHttpMethod.GET when path == "/echo": await HandleEchoAsync(context).ConfigureAwait(false); return;
#endif
                case WatsonHttpMethod.POST when path == "/rpc": await HandleRpcAsync(context).ConfigureAwait(false); return;
                case WatsonHttpMethod.GET when path == "/mcp" && _enableMcp: await HandleMcpGetAsync(context).ConfigureAwait(false); return;
                case WatsonHttpMethod.POST when path == "/mcp" && _enableMcp: await HandleMcpPostAsync(context).ConfigureAwait(false); return;
                case WatsonHttpMethod.DELETE when path == "/mcp" && _enableMcp: await HandleMcpDeleteAsync(context).ConfigureAwait(false); return;
                case WatsonHttpMethod.GET when path.StartsWith("/artifact/", StringComparison.Ordinal): await HandleArtifactAsync(context, path).ConfigureAwait(false); return;
                default: context.Response.StatusCode = 404; await context.Response.Send(context.Token).ConfigureAwait(false); return;
            }
        }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested) { return; }
        catch { if (!context.Response.ResponseSent) { context.Response.StatusCode = 500; await context.Response.Send(context.Token).ConfigureAwait(false); } }
    }

#if DEBUG
    private static async Task HandleEchoAsync(HttpContextBase context)
    {
        string message = context.Request.RetrieveQueryValue("message") ?? string.Empty;
        if (message.Length > 1024) { await SendTextAsync(context, "The echo message cannot exceed 1024 characters.", 400).ConfigureAwait(false); return; }
        await SendJsonAsync(context, new EchoResponse(message), IntegratedApiJsonContext.Default.EchoResponse).ConfigureAwait(false);
    }
#endif

    private async Task HandleRpcAsync(HttpContextBase context)
    {
        if (!ValidateBearerToken(context)) { await SendProtobufAsync(context, UnauthorizedResponse(), 401).ConfigureAwait(false); return; }
        if (!string.Equals(context.Request.ContentType, "application/x-protobuf", StringComparison.OrdinalIgnoreCase))
        { await SendProtobufAsync(context, InvalidRequestResponse("RPC requests must use Content-Type application/x-protobuf."), 415).ConfigureAwait(false); return; }

        byte[]? body = await context.Request.ReadBodyAsync(context.Token).ConfigureAwait(false);
        if (body is null || body.Length == 0 || body.Length > RenderProtocol.MaxPipeFrameBytes)
        { await SendProtobufAsync(context, InvalidRequestResponse("The protobuf RPC request body has an invalid length."), 400).ConfigureAwait(false); return; }

        RenderRequestEnvelope request;
        try { request = RenderRpcSerializer.Deserialize<RenderRequestEnvelope>(body); }
        catch (Exception ex) { await SendProtobufAsync(context, InvalidRequestResponse("The request body is not a valid protobuf RPC envelope.", ex.Message), 400).ConfigureAwait(false); return; }
        RenderResponseEnvelope response = await _headlessService!.DispatchAsync(request, context.Token).ConfigureAwait(false);
        await SendProtobufAsync(context, response, 200).ConfigureAwait(false);
    }

    private async Task HandleArtifactAsync(HttpContextBase context, string path)
    {
        if (!ValidateBearerToken(context)) { context.Response.StatusCode = 401; await context.Response.Send(context.Token).ConfigureAwait(false); return; }
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !Guid.TryParse(parts[1], out Guid sessionId) || !Guid.TryParse(parts[2], out Guid artifactId))
        { context.Response.StatusCode = 404; await context.Response.Send(context.Token).ConfigureAwait(false); return; }

        try
        {
            var artifact = await _headlessService!.ReadArtifactAsync(sessionId, artifactId, context.Token).ConfigureAwait(false);
            byte[] content = artifact.Content;
            long start = 0, end = content.LongLength - 1;
            int statusCode = 200;
            if (TryParseRange(context.Request.RetrieveHeaderValue("Range"), content.LongLength, out long rangeStart, out long rangeEnd))
            { start = rangeStart; end = rangeEnd; statusCode = 206; }
            int length = checked((int)(end - start + 1));
            byte[] payload = new byte[length];
            Buffer.BlockCopy(content, checked((int)start), payload, 0, length);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = artifact.ContentType;
            context.Response.Headers["Accept-Ranges"] = "bytes";
            if (statusCode == 206) context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{content.LongLength}";
            await SendBytesAsync(context, payload).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { context.Response.StatusCode = 404; await context.Response.Send(context.Token).ConfigureAwait(false); }
        catch (DirectoryNotFoundException) { context.Response.StatusCode = 404; await context.Response.Send(context.Token).ConfigureAwait(false); }
    }

    private async Task HandleMcpPostAsync(HttpContextBase context)
    {
        if (!AcceptsMcpResponse(context)) { await SendTextAsync(context, "MCP clients must accept application/json and text/event-stream.", 406).ConfigureAwait(false); return; }
        byte[]? body = await context.Request.ReadBodyAsync(context.Token).ConfigureAwait(false);
        if (body is null || body.Length == 0 || body.Length > MaxMcpRequestBytes) { await SendTextAsync(context, "The MCP request body is invalid.", 400).ConfigureAwait(false); return; }

        JsonRpcMessage? message;
        try { message = JsonSerializer.Deserialize<JsonRpcMessage>(body, McpJsonUtilities.DefaultOptions); }
        catch (JsonException) { await SendTextAsync(context, "The MCP request body is not valid JSON-RPC.", 400).ConfigureAwait(false); return; }
        if (message is null) { await SendTextAsync(context, "The MCP request body is empty.", 400).ConfigureAwait(false); return; }

        string? requestedSessionId = context.Request.RetrieveHeaderValue("Mcp-Session-Id");
        McpSession session;
        if (string.IsNullOrWhiteSpace(requestedSessionId))
        {
            if (message is not JsonRpcRequest { Method: RequestMethods.Initialize }) { await SendTextAsync(context, "A new MCP session must begin with initialize.", 400).ConfigureAwait(false); return; }
            session = CreateMcpSession();
            context.Response.Headers["Mcp-Session-Id"] = session.Id;
        }
        else if (!_mcpSessions.TryGetValue(requestedSessionId, out McpSession? existingSession))
        { await SendTextAsync(context, "The MCP session was not found.", 404).ConfigureAwait(false); return; }
        else
        {
            session = existingSession;
        }

        using var requestScope = _requestContextAccessor!.Push(GetRemoteAddress(context));
        using var response = new MemoryStream();
        bool wroteResponse = await session.Transport.HandlePostRequestAsync(message, response, context.Token).ConfigureAwait(false);
        if (!wroteResponse) { context.Response.StatusCode = 202; await context.Response.Send(context.Token).ConfigureAwait(false); return; }
        byte[] responseBytes = response.ToArray();
        context.Response.ContentType = responseBytes.Length > 0 && responseBytes[0] == (byte)'{' ? "application/json" : "text/event-stream";
        await SendBytesAsync(context, responseBytes).ConfigureAwait(false);
    }

    private async Task HandleMcpGetAsync(HttpContextBase context)
    {
        string? sessionId = context.Request.RetrieveHeaderValue("Mcp-Session-Id");
        if (string.IsNullOrWhiteSpace(sessionId) || !_mcpSessions.TryGetValue(sessionId, out McpSession? session)) { await SendTextAsync(context, "The MCP session was not found.", 404).ConfigureAwait(false); return; }
        using var requestScope = _requestContextAccessor!.Push(GetRemoteAddress(context));
        using var response = new MemoryStream();
        await session.Transport.HandleGetRequestAsync(response, context.Token).ConfigureAwait(false);
        context.Response.ContentType = "text/event-stream";
        await SendBytesAsync(context, response.ToArray()).ConfigureAwait(false);
    }

    private async Task HandleMcpDeleteAsync(HttpContextBase context)
    {
        string? sessionId = context.Request.RetrieveHeaderValue("Mcp-Session-Id");
        if (string.IsNullOrWhiteSpace(sessionId) || !_mcpSessions.Remove(sessionId, out McpSession? session)) { context.Response.StatusCode = 404; await context.Response.Send(context.Token).ConfigureAwait(false); return; }
        await session.DisposeAsync().ConfigureAwait(false);
        await context.Response.Send(context.Token).ConfigureAwait(false);
    }

    private McpSession CreateMcpSession()
    {
        string id = Guid.NewGuid().ToString("N");
        var transport = new StreamableHttpServerTransport(null) { SessionId = id, Stateless = false, FlowExecutionContextFromRequests = true };
        var tools = IntegratedApiToolCatalog.Create(_backend!, _authorization!, _requestContextAccessor!);
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "projectFrameCut.IntegratedAPIServer", Version = "1.0.0" },
            ToolCollection = [.. tools],
        };
#pragma warning disable MCPEXP002
        var server = McpServer.Create(transport, options, loggerFactory: null, _services);
#pragma warning restore MCPEXP002
        var session = new McpSession(id, transport, server);
        _mcpSessions.Add(id, session);
        session.RunTask = server.RunAsync();
        return session;
    }

    private bool ValidateBearerToken(HttpContextBase context)
    {
        if (_rpcTokenHash is null) return false;
        string authorization = context.Request.Authorization?.BearerToken ?? string.Empty;
        string supplied = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization["Bearer ".Length..] : string.Empty;
        return CryptographicOperations.FixedTimeEquals(_rpcTokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    }

    private static bool AcceptsMcpResponse(HttpContextBase context)
    {
        string accept = context.Request.RetrieveHeaderValue("Accept") ?? string.Empty;
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase) && accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetRemoteAddress(HttpContextBase context)
    {
        return string.IsNullOrWhiteSpace(context.Request.Source?.IpAddress) ? null : context.Request.Source.IpAddress;
    }

    private static bool TryParseRange(string? value, long length, out long start, out long end)
    {
        start = 0; end = 0;
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        string[] range = value[6..].Split('-', 2);
        if (range.Length != 2) return false;
        if (!long.TryParse(range[0], out start))
        {
            if (!long.TryParse(range[1], out long suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix); end = length - 1;
        }
        else if (!string.IsNullOrWhiteSpace(range[1]) && !long.TryParse(range[1], out end)) return false;
        else if (string.IsNullOrWhiteSpace(range[1])) end = length - 1;
        return start >= 0 && start < length && end >= start && end < length;
    }

    private static RenderResponseEnvelope UnauthorizedResponse() => new() { Error = new RenderError { Code = RenderErrorCode.Unauthorized, Message = "A valid bearer token is required." } };
    private static RenderResponseEnvelope InvalidRequestResponse(string message, string details = "") => new() { Error = new RenderError { Code = RenderErrorCode.InvalidRequest, Message = message, Details = details } };

    private static async Task SendJsonAsync<T>(HttpContextBase context, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    { context.Response.ContentType = "application/json"; await SendBytesAsync(context, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo)).ConfigureAwait(false); }

    private static async Task SendTextAsync(HttpContextBase context, string text, int statusCode)
    { context.Response.StatusCode = statusCode; context.Response.ContentType = "text/plain; charset=utf-8"; await SendBytesAsync(context, Encoding.UTF8.GetBytes(text)).ConfigureAwait(false); }

    private static async Task SendProtobufAsync(HttpContextBase context, RenderResponseEnvelope response, int statusCode)
    { context.Response.StatusCode = statusCode; context.Response.ContentType = "application/x-protobuf"; await SendBytesAsync(context, RenderRpcSerializer.Serialize(response)).ConfigureAwait(false); }

    private static async Task SendBytesAsync(HttpContextBase context, byte[] payload)
    { context.Response.ContentLength = payload.LongLength; await context.Response.Send(payload, context.Token).ConfigureAwait(false); }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WebserverLite? server = _server; _server = null; ListenUri = null;
            server?.Stop(); server?.Dispose();
            foreach (McpSession session in _mcpSessions.Values.ToArray()) await session.DisposeAsync().ConfigureAwait(false);
            _mcpSessions.Clear();
            await DisposeRuntimeAsync().ConfigureAwait(false);
        }
        finally { _lifecycleLock.Release(); }
    }

    private async ValueTask DisposeRuntimeAsync()
    {
        if (_services is not null) await _services.DisposeAsync().ConfigureAwait(false);
        _services = null; _headlessService = null; _backend = null; _authorization = null; _requestContextAccessor = null; _rpcTokenHash = null; _enableMcp = false;
    }

    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _lifecycleLock.Dispose(); }

    public static void ValidateListenUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The MCP listen address must be an absolute HTTP or HTTPS URL.", nameof(uri));
        if (!string.IsNullOrEmpty(uri.AbsolutePath.Trim('/')) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new ArgumentException("The MCP listen address must not include a path, query, or fragment.", nameof(uri));
        if (uri.IsDefaultPort) throw new ArgumentException("The MCP listen address must include an explicit port.", nameof(uri));
        string host = uri.Host; string unbracketedHost = host.Trim('[', ']');
        if ((IPAddress.TryParse(unbracketedHost, out var address) && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))) || host.Contains('*', StringComparison.Ordinal) || host.Contains('+', StringComparison.Ordinal))
            throw new ArgumentException("Wildcard MCP listen addresses are not allowed; use a concrete host or IP address.", nameof(uri));
    }

    public static void ValidateRpcToken(string token)
    { if (string.IsNullOrEmpty(token) || token.Length < 32 || token.Any(char.IsWhiteSpace)) throw new ArgumentException("The RPC token must contain at least 32 non-whitespace characters.", nameof(token)); }

    private static bool IsLoopbackHost(string host) => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private sealed class McpSession(string id, StreamableHttpServerTransport transport, McpServer server) : IAsyncDisposable
    {
        public string Id { get; } = id;
        public StreamableHttpServerTransport Transport { get; } = transport;
        public McpServer Server { get; } = server;
        public Task? RunTask { get; set; }
        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync().ConfigureAwait(false);
            if (RunTask is not null) await RunTask.ConfigureAwait(false);
            await Transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
