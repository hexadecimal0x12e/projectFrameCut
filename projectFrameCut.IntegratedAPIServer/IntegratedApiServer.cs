using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using projectFrameCut.IntegratedAPIServer.Headless;
using projectFrameCut.IntegratedAPIServer.MCP;
using projectFrameCut.Render.Contracts;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace projectFrameCut.IntegratedAPIServer;

public sealed class IntegratedApiServer : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private WebApplication? _application;

    public bool IsRunning => _application is not null;

    public Uri? ListenUri { get; private set; }

    public async Task StartAsync(
        IntegratedApiServerOptions options,
        IIntegratedApiBackend backend,
        CancellationToken cancellationToken = default)
        => await StartCoreAsync(options, backend, headlessServiceOverride: null, cancellationToken).ConfigureAwait(false);

    public async Task StartHeadlessAsync(
        IntegratedApiServerOptions options,
        CancellationToken cancellationToken = default)
        => await StartCoreAsync(options, backend: null, headlessServiceOverride: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Starts the headless RPC endpoint over a caller-supplied
    /// <see cref="HeadlessProjectService"/>. The service stays owned by the caller
    /// and is not disposed when this server stops. <see cref="IntegratedApiServerOptions.ProjectRoot"/>
    /// may be omitted when the service is already initialized, or to start without a
    /// preloaded project (clients then open projects on demand over RPC).
    /// </summary>
    public async Task StartHeadlessAsync(
        IntegratedApiServerOptions options,
        HeadlessProjectService headlessService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(headlessService);
        await StartCoreAsync(options, backend: null, headlessService, cancellationToken).ConfigureAwait(false);
    }

    private async Task StartCoreAsync(
        IntegratedApiServerOptions options,
        IIntegratedApiBackend? backend,
        HeadlessProjectService? headlessServiceOverride,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateListenUri(options.ListenUri);
        bool enableRpc = !string.IsNullOrEmpty(options.RpcToken) || headlessServiceOverride is not null;
        if (backend is null && string.IsNullOrEmpty(options.RpcToken))
            throw new ArgumentException("Headless mode requires an RPC token.", nameof(options));
        if (backend is null &&
            headlessServiceOverride is null &&
            string.IsNullOrWhiteSpace(options.ProjectRoot))
            throw new ArgumentException("Headless mode requires a project root.", nameof(options));
        if (!string.IsNullOrEmpty(options.RpcToken)) ValidateRpcToken(options.RpcToken);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_application is not null)
            {
                throw new InvalidOperationException("The integrated API server is already running.");
            }

            if (string.Equals(options.ListenUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !IsLoopbackHost(options.ListenUri.Host))
            {
                options.WarningSink?.Invoke(
                    "The integrated API server is listening on an unencrypted LAN HTTP endpoint. Credentials and project data are not protected in transit.");
            }

            var webOptions = new WebApplicationOptions
            {
                Args = [],
            };
            var builder = WebApplication.CreateSlimBuilder(webOptions);
            builder.WebHost.UseUrls(options.ListenUri.AbsoluteUri.TrimEnd('/'));
            builder.Configuration["AllowedHosts"] = options.ListenUri.Host;

            var httpContextAccessor = new HttpContextAccessor();
            var authorization = new EndpointAuthorizationManager();
            HeadlessProjectService? headlessService = headlessServiceOverride ?? (enableRpc
                ? new HeadlessProjectService(options.GlobalAssetsDatabasePath)
                : null);

            builder.Services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);
            if (backend is not null && options.EnableMcp)
            {
                var tools = IntegratedApiToolCatalog.Create(backend, authorization, httpContextAccessor);
                builder.Services.AddSingleton(backend);
                builder.Services.AddSingleton(authorization);
                builder.Services
                    .AddMcpServer(serverOptions =>
                    {
                        serverOptions.ServerInfo = new Implementation
                        {
                            Name = "projectFrameCut.IntegratedAPIServer",
                            Version = "1.0.0",
                        };
                        serverOptions.ToolCollection = [.. tools];
                    })
                    .WithHttpTransport(transportOptions =>
                    {
                        transportOptions.Stateless = false;
                    });
            }

            if (headlessService is not null)
            {
                builder.Services.AddSingleton(headlessService);
            }

            var app = builder.Build();
            app.MapGet("/health", () => Results.Json(
                new HealthResponse("ok", true),
                IntegratedApiJsonContext.Default.HealthResponse));
#if DEBUG
            app.MapGet("/echo", (HttpRequest request) =>
            {
                string message = request.Query["message"].ToString();
                if (message.Length > 1024)
                {
                    return Results.BadRequest("The echo message cannot exceed 1024 characters.");
                }

                return Results.Json(
                    new EchoResponse(message),
                    IntegratedApiJsonContext.Default.EchoResponse);
            });
#endif
            if (backend is not null && options.EnableMcp)
            {
                app.MapMcp("/mcp");
            }
            if (headlessService is not null)
            {
                byte[] expectedTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.RpcToken!));
                app.MapPost("/rpc", async (HttpRequest httpRequest, CancellationToken requestAborted) =>
                {
                    string authorizationHeader = httpRequest.Headers.Authorization.ToString();
                    string suppliedToken = authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? authorizationHeader["Bearer ".Length..]
                        : string.Empty;
                    byte[] suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedToken));

                    if (!CryptographicOperations.FixedTimeEquals(expectedTokenHash, suppliedHash))
                    {
                        return ProtobufResult(new RenderResponseEnvelope
                        {
                            Error = new RenderError
                            {
                                Code = RenderErrorCode.Unauthorized,
                                Message = "A valid bearer token is required.",
                            },
                        }, StatusCodes.Status401Unauthorized);
                    }

                    if (!string.Equals(httpRequest.ContentType, "application/x-protobuf", StringComparison.OrdinalIgnoreCase))
                    {
                        return ProtobufResult(new RenderResponseEnvelope
                        {
                            Error = new RenderError
                            {
                                Code = RenderErrorCode.InvalidRequest,
                                Message = "RPC requests must use Content-Type application/x-protobuf.",
                            },
                        }, StatusCodes.Status415UnsupportedMediaType);
                    }

                    RenderRequestEnvelope rpcRequest;
                    try
                    {
                        using var requestBuffer = new MemoryStream();
                        await httpRequest.Body.CopyToAsync(requestBuffer, requestAborted).ConfigureAwait(false);
                        if (requestBuffer.Length == 0 || requestBuffer.Length > RenderProtocol.MaxPipeFrameBytes)
                            throw new InvalidDataException("The protobuf RPC request body has an invalid length.");
                        rpcRequest = RenderRpcSerializer.Deserialize<RenderRequestEnvelope>(requestBuffer.ToArray());
                    }
                    catch (Exception ex)
                    {
                        return ProtobufResult(new RenderResponseEnvelope
                        {
                            Error = new RenderError
                            {
                                Code = RenderErrorCode.InvalidRequest,
                                Message = "The request body is not a valid protobuf RPC envelope.",
                                Details = ex.Message,
                            },
                        }, StatusCodes.Status400BadRequest);
                    }

                    RenderResponseEnvelope rpcResponse = await headlessService.DispatchAsync(rpcRequest, requestAborted).ConfigureAwait(false);
                    return ProtobufResult(rpcResponse, StatusCodes.Status200OK);
                });

                app.MapGet("/artifact/{sessionId:guid}/{artifactId:guid}", async (
                    Guid sessionId,
                    Guid artifactId,
                    HttpRequest httpRequest,
                    CancellationToken requestAborted) =>
                {
                    string authorizationHeader = httpRequest.Headers.Authorization.ToString();
                    string suppliedToken = authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? authorizationHeader["Bearer ".Length..]
                        : string.Empty;
                    byte[] suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedToken));
                    if (!CryptographicOperations.FixedTimeEquals(expectedTokenHash, suppliedHash))
                        return Results.Unauthorized();

                    try
                    {
                        var artifact = await headlessService.ReadArtifactAsync(sessionId, artifactId, requestAborted).ConfigureAwait(false);
                        return Results.File(artifact.Content, artifact.ContentType, enableRangeProcessing: true);
                    }
                    catch (FileNotFoundException)
                    {
                        return Results.NotFound();
                    }
                    catch (DirectoryNotFoundException)
                    {
                        return Results.NotFound();
                    }
                });
            }

            try
            {
                if (headlessService is not null && !headlessService.IsInitialized && !string.IsNullOrWhiteSpace(options.ProjectRoot))
                {
                    await headlessService.InitializeAsync(options.ProjectRoot, cancellationToken).ConfigureAwait(false);
                }
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _application = app;
            ListenUri = options.ListenUri;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var app = _application;
            _application = null;
            ListenUri = null;
            if (app is null)
            {
                return;
            }

            await app.StopAsync(cancellationToken).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }

    public static void ValidateListenUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The MCP listen address must be an absolute HTTP or HTTPS URL.", nameof(uri));
        }

        if (!string.IsNullOrEmpty(uri.AbsolutePath.Trim('/')) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("The MCP listen address must not include a path, query, or fragment.", nameof(uri));
        }

        if (uri.IsDefaultPort)
        {
            throw new ArgumentException("The MCP listen address must include an explicit port.", nameof(uri));
        }

        string host = uri.Host;
        string unbracketedHost = host.Trim('[', ']');
        if ((IPAddress.TryParse(unbracketedHost, out var address) &&
             (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))) ||
            host.Contains('*', StringComparison.Ordinal) ||
            host.Contains('+', StringComparison.Ordinal))
        {
            throw new ArgumentException("Wildcard MCP listen addresses are not allowed; use a concrete host or IP address.", nameof(uri));
        }
    }

    public static void ValidateRpcToken(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 32 || token.Any(char.IsWhiteSpace))
            throw new ArgumentException("The RPC token must contain at least 32 non-whitespace characters.", nameof(token));
    }

    private static IResult ProtobufResult(RenderResponseEnvelope response, int statusCode)
        => new ProtobufHttpResult(RenderRpcSerializer.Serialize(response), statusCode);

    private sealed class ProtobufHttpResult(byte[] payload, int statusCode) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "application/x-protobuf";
            httpContext.Response.ContentLength = payload.Length;
            await httpContext.Response.Body.WriteAsync(payload, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
           (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
