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

For Windows sandbox pipe access denial, request a new one-time pipe using `Connect-ProjectFrameCut -Name ... -Author ... -Purpose ... -AddSID $sandboxSids`; see [Windows sandbox connection](gui-projects.md#windows-sandbox-connection). If the imported module lacks `-AddSID`, use the documented direct CLI workflow or update the module. This parameter does not apply to persistent requests or repair request-directory access failures.

`Run Connect-ProjectFrameCut first.` means the current runspace has no connection. Reconnect in that same runspace. A connection created in another `pwsh` process or runspace is not available here.

If a new connection fails, the previous connection is intentionally preserved. If the GUI project closed or the runspace became broken, query or reconnect; do not blindly replay a write.

## Timeouts and uncertain mutations

The client cancels the wait, not the already-started GUI mutation. For an ordinary edit timeout, run the narrowest read command (`Get-ProjectClip`, `Get-ProjectAsset`, `Get-ProjectTrack`, or `Get-ProjectInfo`) and compare identifiers/values before retrying. Use `-ChangeReason` so the GUI and history explain the intended operation.

## Batch failures

- Check the imported module with `Get-Command Start-ProjectBatch, Complete-ProjectBatch, Stop-ProjectBatch -Module projectFrameCut.PowerShell`. Cmdlets present in source can still be absent from an older published module; the GUI must support batches too. Do not silently fall back to hundreds of ordinary writes when the task requires one history entry.
- An editing error leaves earlier successful requests staged. Read back the affected data in the same connection, then deliberately continue, commit the successful part, or cancel with `Stop-ProjectBatch`.
- If `Complete-ProjectBatch` times out, retain the same runspace/connection and retry only that command with a larger `-TimeoutSeconds`; it reuses the history node or returns the completed result. Do not restart the batch or replay edits. Allow at most one automatic timeout retry, then resolve or cancel the batch and report uncertainty instead of looping. Reads can be rejected once applying has begun, which does not mean the batch was lost.
- A save failure keeps the batch available for retry or cancellation. After saving succeeds, a refresh failure does not make the data unsaved. Retrying completion finishes the refresh; stopping/disconnecting releases the batch without undoing saved changes. Check `Stop-ProjectBatch` result fields `Cancelled` and `Saved`.
- Closing the runspace, unloading the module, replacing the connection, or disconnecting loses uncommitted staging. A new connection cannot resume it. If the final result is uncertain after cleanup, reconnect to the verified project and inspect committed state/history before deciding on further edits.

See [batch operations](batch-operations.md) for the normal workflow.

## What counts as verification

Importing the module, listing cmdlets, reading source, or constructing a command is not proof that a GUI operation worked. For a real operation verify:

1. `Get-ProjectInfo` succeeds after connection.
2. The mutation returns or is followed by a read-back showing the expected state.
3. `Save-Project` or `Complete-ProjectBatch` succeeds when persistence is required; staged read-back alone does not establish persistence.
4. Reopen or re-read the project when the task depends on disk persistence.

For destructive changes, show `-WhatIf` output first when the target is not already unambiguous. `Remove-Project`, `Remove-ProjectAsset`, `Remove-ProjectClip`, and provider removal can discard state; do not replace confirmation with a fabricated `-Force` parameter.
