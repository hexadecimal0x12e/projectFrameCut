using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using projectFrameCut.IntegratedAPIServer.MCP;
using System.Net;

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
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        ValidateListenUri(options.ListenUri);

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
                    "The MCP server is listening on an unencrypted LAN HTTP endpoint. Authorization decisions and project data are not protected in transit.");
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
            var tools = IntegratedApiToolCatalog.Create(backend, authorization, httpContextAccessor);

            builder.Services.AddSingleton(backend);
            builder.Services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);
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

            var app = builder.Build();
            app.MapGet("/health", () => Results.Json(
                new HealthResponse("ok", true),
                IntegratedApiJsonContext.Default.HealthResponse));
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
            app.MapMcp("/mcp");

            try
            {
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

    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
           (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
