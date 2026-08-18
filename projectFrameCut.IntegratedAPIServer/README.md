# projectFrameCut.IntegratedAPIServer

An in-process ASP.NET Core MCP server for the project currently open in projectFrameCut.

On Windows, open a project with an explicit listen address:

```text
pjfc.exe "D:\Projects\example" --mcp=http://127.0.0.1:32123
```

Endpoints:

- MCP Streamable HTTP: `http://127.0.0.1:32123/mcp`
- Health: `http://127.0.0.1:32123/health`
- Echo: `http://127.0.0.1:32123/echo?message=hello`

Before calling project tools, an MCP client must call `authorize_client`. projectFrameCut displays the client identity, remote address, and stated reason. The decision is cached by remote client IP for the lifetime of the current server instance, so reconnecting from the same endpoint does not prompt again. If the remote IP is unavailable, authorization safely falls back to the current MCP session.

The library is referenced by every application TFM. Only the Windows application currently starts the server; other platform startup integrations are intentionally left for later.

## Headless protobuf RPC

The same host can expose the render and project-editing protocol over HTTP:

On Windows it can be started from the CLI:

```text
pjfc-cli backend --listen=http://127.0.0.1:32123 --token=<32-or-more-characters> --dataRoot=<path>
```

`headless` is accepted as an alias for `backend`. Press Ctrl+C to stop the server.

```csharp
const string token = "replace-with-at-least-32-non-whitespace-characters";
await using var server = new IntegratedApiServer();
await server.StartHeadlessAsync(new IntegratedApiServerOptions
{
    ListenUri = new Uri("http://127.0.0.1:32123"),
    RpcToken = token,
    EnableMcp = false,
});

await using var client = new RenderClient(
    new HttpRenderClientTransport(
        new Uri("http://127.0.0.1:32123"),
        token,
        "automation-client"),
    "automation-client");

HeadlessProjectSnapshot snapshot = await client.OpenHeadlessProjectAsync(
    new OpenHeadlessProjectRequest { ProjectRoot = @"D:\Projects\example" });
```

`POST /rpc` accepts `application/x-protobuf` and requires `Authorization: Bearer <token>`.
Preview artifacts returned by render operations can be downloaded from
`GET /artifact/{sessionId}/{artifactId}` with the same bearer token. The editor
stores them under the returned project-relative `thumbs/` path in its local
remote-project cache and validates the advertised size and SHA-256 hash.
Every mutation carries a `HeadlessMutationPrecondition` copied from the latest snapshot. A stale revision or snapshot hash returns `RenderErrorCode.VersionConflict`; fetch a new snapshot before retrying. Changes remain in memory until `SaveHeadlessProjectAsync` is called.
