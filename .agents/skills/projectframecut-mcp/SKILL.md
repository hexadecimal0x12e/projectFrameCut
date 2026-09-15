---
name: projectframecut-mcp
description: Use the projectFrameCut MCP server exposed by `pjfc mcp` to browse, create, enter, inspect, edit, save, or exit video projects, including timeline clips, assets, text, vector graphics, effects, provider graphs, and keyframes. Also use it when configuring or troubleshooting the stdio, Streamable HTTP, optional RPC, or GUI-client lifecycle of this MCP entrypoint. Do not use it for the separate `headless`, `rpc_server`, or `render` CLI commands unless they are only being compared with `pjfc mcp`.
---

# projectFrameCut MCP

Operate the MCP capabilities implemented by `projectFrameCut/CLIProgram.cs` and `projectFrameCut.IntegratedAPIServer/MCP`.

## Route the task

- If the MCP tools are already connected, work through the exposed tools. Do not start a second server.
- For server launch, client configuration, transport, RPC, security, or lifecycle questions, read [references/server-and-lifecycle.md](references/server-and-lifecycle.md).
- For an exact capability or argument lookup, read [references/tool-catalog.md](references/tool-catalog.md). The live MCP input schema is authoritative if it differs from the reference.
- For multi-step project edits and reliable tool ordering, read [references/editing-workflows.md](references/editing-workflows.md).

## Operating workflow

1. Inspect the current MCP tool list. The server has mutually exclusive no-project and project modes.
2. In no-project mode, use `list_projects`, `list_templates`, or `read_asset_library`; then create or enter a project.
3. After `enter_project` or a creating tool with `enterProject=true`, refresh the tool list if the client has not processed the MCP tools-list-changed notification.
4. Before editing, read only the state needed to identify stable targets: usually `get_timeline_info`, `list_layers`, `list_clips`, and then `get_clip` for the selected UUID.
5. Prefer the narrow, typed editing tools over raw replacements:
   - `add_clip_from_asset`, `add_text_clip`, or `add_solid_color_clip` over constructing a raw `ClipDraftDTO`.
   - `set_text_entries` for text content and styling.
   - vector component tools for vector canvases.
   - provider-graph and typed convenience tools for effects and animation.
   - use `upsert_clip`, `patch_clip`, `add_effect`, and bundle tools only when the higher-level tools cannot express the request and the required model shape is known.
6. Read back the affected clip or specialized structure after mutation. For provider graphs, run `validate_effect_provider_graph` before saving.
7. Call `save_project` after completing an authorized edit unless the user explicitly requested an unsaved preview. Changes are not guaranteed to be persisted to disk before that call.

## Invariants and safety

- Timeline placement is frame-based. Read `frameRate` first and convert seconds to frames consistently; do not treat seconds as frames.
- Use IDs returned by MCP. Do not invent clip, asset, template, component, effect-provider, or bundle IDs unless the schema explicitly permits a new optional UUID.
- Read the live tool schema instead of guessing enum spellings, nested DTO fields, or effect/provider type names.
- A delete, complete graph replacement, complete vector replacement, or `exit_project(save=false)` can discard substantial state. Inspect the target first and perform it only when it is within the user's request.
- `exit_project` defaults to saving. Pass `save=false` only when the user explicitly wants to abandon the current unsaved work.
- Shared GUI/RPC sessions use revision and snapshot-hash preconditions internally. On a conflict, refresh the affected state and re-evaluate the edit; do not blindly replay a stale destructive mutation.
- The CLI-hosted MCP intentionally exposes neither `authorize_client` nor integrated-editor client tools. Do not wait for or fabricate them.
- This server currently exposes tools, not MCP resources or prompts.

## Completion report

State which project was affected, summarize the timeline or project changes, say whether `save_project` succeeded, and identify any runtime operation that was not verified. Keep server configuration details out of the report unless they matter to the result.
