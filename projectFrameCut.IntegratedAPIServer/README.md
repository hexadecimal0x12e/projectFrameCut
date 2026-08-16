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
