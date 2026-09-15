# Server and lifecycle

Read this reference when launching, connecting, securing, or troubleshooting the MCP server supplied by `pjfc mcp`.

## Entrypoint

Use `pjfc mcp`, not the separate `headless`, `rpc_server`, or `render` commands.

```powershell
# Stdio MCP; --quiet is mandatory
pjfc mcp --transport=stdio --quiet --headless --dataRoot="D:\projectFrameCut"

# Start directly in one project
pjfc mcp --transport=stdio --quiet --headless --projectRoot="D:\projectFrameCut\My Drafts\Demo"

# Streamable HTTP MCP
pjfc mcp --transport=http --listen=http://127.0.0.1:32123 --headless --dataRoot="D:\projectFrameCut"
```

The stdio client configuration should launch `pjfc` with an argument array equivalent to:

```text
mcp
--transport=stdio
--quiet
--headless
--dataRoot=<absolute user-data root>
```

Do not wrap the command in a shell unless the MCP client requires it. Protocol messages use stdout; diagnostics and failures use stderr.

## Options

| Option | Contract |
| --- | --- |
| `--transport=stdio\|http` | Defaults to `stdio`. Other values are rejected. |
| `--quiet` | Required for stdio to keep stdout protocol-only. The check is the literal `--quiet` argument. |
| `--listen=<URI>` | Required only for HTTP. MCP is served at `<URI>/mcp`. |
| `--projectRoot=<path>` | Optional existing project directory loaded at startup. `--project=<path>` and one positional path are aliases. |
| `--dataRoot=<path>` | User-data root containing `My Drafts`, `My Templates`, and `My Assets`. |
| `--headless` | Suppresses the graphical MCP client. |
| `--rpcListen=<URI>` | Optionally exposes the protobuf RPC endpoint at `/rpc`. Legacy alias: `--projectServer`. |
| `--rpcToken=<token>` | Required with `--rpcListen`; at least 32 non-whitespace characters. Legacy alias: `--projectServerToken`. |

When `--dataRoot` is omitted, the CLI reads `<AppDataPath>/OverrideUserDataPath.txt` if present; otherwise it uses `<Documents>/projectFrameCut`. The MCP service derives the global asset database as `My Assets/.database/database.json` below that root.

Listen URIs must be absolute HTTP or HTTPS addresses with an explicit port and no path, query, or fragment. `--listen` is invalid for stdio. `--rpcToken` without `--rpcListen` is invalid.

## HTTP and RPC

Streamable HTTP MCP is available at `/mcp`. The CLI-hosted MCP has no MCP authorization gate, so bind to loopback unless the user deliberately provides an appropriately protected network environment. An RPC bearer token protects `/rpc`; it does not add authentication to `/mcp`.

MCP and RPC may use the same scheme, host, and port. The shared listener then serves `/mcp` and `/rpc`, and both operate on the same `HeadlessProjectService` project session.

```powershell
pjfc mcp --transport=http `
  --listen=http://127.0.0.1:32123 `
  --rpcListen=http://127.0.0.1:32123 `
  --rpcToken=<32-or-more-non-whitespace-characters> `
  --projectRoot="D:\projectFrameCut\My Drafts\Demo" `
  --headless
```

Treat tokens placed on a command line as process-visible secrets. Do not print, commit, or repeat a real token in reports.

## GUI client and process lifetime

Without `--headless`, `pjfc mcp` starts a local projectFrameCut GUI client connected through an authenticated random named pipe. With no startup project, the client waits for `enter_project`; with a startup project, the GUI and MCP share the same render backend.

The GUI is a lifecycle owner:

- Exiting it cancels and stops the MCP process.
- Stopping MCP terminates the child GUI process.
- `Ctrl+C` cancels the MCP server on supported desktop platforms.

Use `--headless` for background integrations where closing a window must not stop the MCP server.

## Mode changes

The server starts in no-project mode if no project path is supplied. `enter_project`, `create_empty_project(enterProject=true)`, and `create_project_from_template(enterProject=true)` switch to project tools. `exit_project` switches back. Each switch emits an MCP tools-list-changed notification; reconnecting is not required.

## Failure interpretation

- Exit code `2`: invalid command line or missing project directory.
- Exit code `1`: server startup/runtime failure.
- Exit code `0`: help, normal completion, or cancellation.
- Stdio contamination or immediate exit: confirm the exact `--quiet` argument and that the MCP client did not merge stderr into stdout.
- `Plugin not found: projectFrameCut.Render.Plugins.InternalPluginBase`: render runtime initialization did not complete before project/backend creation.
- A slow `enter_project` can be project opening or whole-timeline frame-hash preparation rather than MCP transport failure. Distinguish whether the wait is in project load, render initialization, hash building, or GUI process startup before changing code.
