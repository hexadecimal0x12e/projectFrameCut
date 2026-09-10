# Troubleshooting and validation

## Module discovery

```powershell
Get-Command -Module projectFrameCut.PowerShell
Get-Module projectFrameCut.PowerShell
Test-Path .\artifacts\projectFrameCut.PowerShell\projectFrameCut.PowerShell.psd1
```

Import the `.psd1` from the publish directory, not only the DLL. Distribute the whole publish directory so the Contracts, RPC protocol, protobuf, and platform dependencies remain beside the module.

## `pjfc` and request failures

- If offline commands cannot resolve the user-data root, pass `-PjfcExecutablePath` or an absolute executable path and inspect both stdout and stderr from `pjfc user_data_root`.
- If authorization times out, check that the GUI is open, the intended project is active, the request directory matches the GUI configuration, and the user approved the request. Increase `-TimeoutSeconds` only when the approval genuinely needs more time.
- If the CLI reports a non-zero authorization exit code, do not attempt to parse a stale pipe response.
- A persistent connection requires the matching `ClientId`, readable private key, valid public-key registration, and the correct `Service`. Never put private-key contents into the request JSON or logs.

## Connection and session failures

`Run Connect-ProjectFrameCut first.` means the current runspace has no connection. Reconnect in that same runspace. A connection created in another `pwsh` process or runspace is not available here.

If a new connection fails, the previous connection is intentionally preserved. If the GUI project closed or the runspace became broken, query or reconnect; do not blindly replay a write.

## Timeouts and uncertain mutations

The client cancels the wait, not the already-started GUI mutation. After any timeout, run the narrowest read command (`Get-ProjectClip`, `Get-ProjectAsset`, `Get-ProjectTrack`, or `Get-ProjectInfo`) and compare identifiers/values before retrying. Use `-ChangeReason` so the GUI and history explain the intended operation.

## What counts as verification

Importing the module, listing cmdlets, reading source, or constructing a command is not proof that a GUI operation worked. For a real operation verify:

1. `Get-ProjectInfo` succeeds after connection.
2. The mutation returns or is followed by a read-back showing the expected state.
3. `Save-Project` succeeds when persistence is required.
4. Reopen or re-read the project when the task depends on disk persistence.

For destructive changes, show `-WhatIf` output first when the target is not already unambiguous. `Remove-Project`, `Remove-ProjectAsset`, `Remove-ProjectClip`, and provider removal can discard state; do not replace confirmation with a fabricated `-Force` parameter.
