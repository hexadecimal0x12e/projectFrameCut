# projectFrameCut.IntegratedAPIServer

An in-process cross-platform MCP and protobuf HTTP server for the project currently open in projectFrameCut.

The server uses Watson.Lite for its TCP-based HTTP listener and the ModelContextProtocol
core transport directly. It does not reference ASP.NET Core or Kestrel, which keeps the
library usable from the Android and iOS application targets.

Start a headless HTTP MCP server from the CLI:

```text
pjfc mcp --transport=http --listen=http://127.0.0.1:32123 --projectRoot="D:\Projects\example"
```

Endpoints:

- MCP Streamable HTTP: `http://127.0.0.1:32123/mcp`
- Health: `http://127.0.0.1:32123/health`
- Echo: `http://127.0.0.1:32123/echo?message=hello`

The headless CLI transport exposes the project tool catalog directly. It does
not start the graphical interface unless `--start-client` is supplied.

The HTTP MCP implementation preserves stateful Streamable HTTP sessions and supports
`POST /mcp`, `GET /mcp`, and `DELETE /mcp`. Legacy `/sse` and `/message` endpoints are
not exposed.

## MCP from the CLI

An MCP client can also launch the CLI as a child process and communicate over
standard input/output without opening a network port:

```text
pjfc mcp --transport=stdio --projectRoot="D:\Projects\example" --quiet
```

note that `--quiet` flag was **REQUIRED** to avoid the CLI writing any non-MCP messages to stdout.

The stdio server loads the project directly and exposes the project/timeline
tools from the integrated MCP catalog. Editor-client-only tools are not
advertised in this mode. Mutations remain in memory until the client calls
`save_project`. Standard output is reserved for MCP JSON-RPC messages;
diagnostics are written to standard error.

For a local CLI instance multiplexer, use the raw named-pipe transport:

```text
pjfc mcp --transport=raw_pipe --pipe=projectFrameCut-mcp-instance-1 \
  --parentPid=<routerPid> \
  --projectRoot="D:\Projects\example" --headless
```

The value of `--pipe` is passed to the platform named-pipe implementation as
provided. The server accepts one client connection and uses the same pipe for
MCP input and output. MCP framing is handled by `StreamServerTransport`; the
pipe itself is a byte stream. When the client disconnects, this MCP server
instance exits. The external multiplexer is responsible for routing requests
to multiple CLI instances. `--headless` prevents the normal GUI client from
starting.
Pass the router process ID with `--parentPid=<routerPid>` when the raw-pipe
server should follow the router lifetime. If that process exits, the MCP
server cancels its transport and the normal cleanup path terminates any GUI
client started by the MCP process.

To start a graphical client together with the MCP server, add
`--start-client`:

```text
pjfc mcp --transport=stdio --quiet --start-client
```

The server creates an authenticated local named pipe before launching the GUI.
The GUI runs in MCP mode and shares the MCP server's `HeadlessProjectService`,
render backend, revision, and save lifecycle. Without `--projectRoot`, it shows
a waiting indicator until the MCP caller invokes `enter_project`; subsequent
project switches are reflected in the same GUI. `exit_project` returns it to
the waiting state. The internal pipe name and token are generated per process
and passed only to the child GUI.

To publish the same server-side project over protobuf RPC, add an RPC listener
and token:

```text
pjfc mcp --transport=stdio --projectRoot="D:\Projects\example" --quiet \
  --rpcListen=http://127.0.0.1:32123 \
  --rpcToken=replace-with-at-least-32-characters

pjfc gui --remote=http://127.0.0.1:32123 \
  --remoteToken=replace-with-at-least-32-characters
```

In this mode, MCP and remote RPC clients share one
`HeadlessProjectService` session, including its revision, snapshot hash, render
session, and save lifecycle. The GUI can synchronize MCP changes from the
server, and concurrent saves use the existing optimistic-concurrency checks.

For HTTP MCP, `--rpcListen` can use the same address as `--listen`; the shared
listener exposes `/mcp` and `/rpc`.

## Headless protobuf RPC

The same host can expose the render and project-editing protocol over HTTP:

On Windows it can be started from the CLI:

```text
pjfc-cli headless --listen=http://127.0.0.1:32123 --token=<32-or-more-characters> --dataRoot=<path>
```

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
