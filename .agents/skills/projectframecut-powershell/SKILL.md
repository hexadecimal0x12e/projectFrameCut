---
name: projectframecut-powershell
description: Use the projectFrameCut.PowerShell binary module to discover, create, open, connect to, inspect, edit, save, and disconnect projectFrameCut projects. Applies when the task asks an Agent to operate projectFrameCut through PowerShell rather than MCP or in-process APIs.
---

# projectFrameCut PowerShell

Use this Skill when the requested work should be performed through `tools/projectFrameCut.PowerShell/`. It covers two intentionally separate modes:

- Offline mode manages `.pjfc` project directories without opening the GUI.
- Connected GUI mode operates the project currently open in an authorized projectFrameCut window through RPC.

Do not treat the module as an in-process page API. It returns serialized snapshots, not `$page` or live `InnerClip` objects. Do not use this Skill for `pjfc mcp` workflows; use the projectFrameCut MCP Skill for those.

## Route the task

- For packaging, importing, offline project CRUD, templates, or starting an app, read [references/offline-projects.md](references/offline-projects.md).
- For authorization, named-pipe connection, timeline/assets/text/effects/history operations, read [references/gui-projects.md](references/gui-projects.md).
- For failures, reconnection, timeout, runspace, or security-sensitive handling, read [references/troubleshooting.md](references/troubleshooting.md).

## Common operating workflow

1. Decide whether the request is offline or requires the currently open GUI project.
2. For offline work, resolve the intended `ProjectRoot` explicitly when the default user-data location is not certain.
3. For GUI work, make sure the intended project is open, request authorization, and verify the returned `SessionId` with `Get-ProjectInfo` before mutating anything.
4. Read stable identifiers first: `TrackId`, `AssetId`, `ClipId`, `ProviderId`, `StyleId`, and the project frame rate.
5. Use the narrow cmdlet for the mutation. Use `-PassThru` when the next operation needs the created object, and pass returned properties through the pipeline where supported.
6. Use `-WhatIf` for destructive or uncertain writes, then execute the approved mutation with an explicit `-ChangeReason`.
7. Read back the affected state. Call `Save-Project` explicitly after GUI edits unless the user requested an unsaved preview.
8. Disconnect when the session is no longer needed.

## Non-obvious invariants

- GUI connections are scoped to the current PowerShell runspace. A new successful connection replaces the old one; a failed replacement leaves the old connection intact.
- The authorization approval selects the target GUI project. The module does not choose a project for the user.
- All timeline positions and durations are project frames, not seconds. Convert using the frame rate from `Get-ProjectInfo`.
- Pixel geometry uses `WidthPixels`, `HeightPixels`, `XPixels`, and `YPixels`; do not reuse old pixel/time property names.
- Query `Get-ProjectEffectProviderField` or `Get-ProjectTextStyleField` before constructing `-Fields` hashtables.
- `Undo-ProjectHistory`, `Redo-ProjectHistory`, and `Restore-ProjectHistory` change the current GUI state but do not save it.
- A timeout or cancellation does not roll back a mutation that already started. Re-read the project before deciding whether to retry.
- Do not expose `PipeId`, `Token`, private keys, request files, or pipe names in logs or user-facing output.

## Completion report

Report the mode used, the project path or connected session, the changes made, whether `Save-Project` succeeded, and any runtime operation that was not verified. Do not claim that a static inspection, import, or command construction proves a GUI edit succeeded.
