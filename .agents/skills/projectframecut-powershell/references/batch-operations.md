# Batch GUI editing

Use a batch for many related edits to a writable local GUI project when they should become one saved history entry. Choose boundaries such as a subtitle pass, scene, or animation; prepare media and parameters first so the GUI is not held behind an overlay during unrelated work. Ordinary edits remain appropriate for a single change or an explicitly unsaved preview.

## Before starting

- Verify the target with `Get-ProjectInfo` and discover the three batch cmdlets in the imported module. Both the published module and GUI must support the feature. Offline project management and GUI projects backed by a remote project session do not use this path.
- Import assets and register/load external sources before `Start-ProjectBatch`. The current `Add-ProjectAsset` cmdlet does not expose `CopyToProject`, while batch asset import requires that explicit RPC parameter. Use existing `AssetId` values in the batch rather than inventing a cmdlet parameter. Confirm current command metadata if this interface changes.
- Discover effect/style fields and component types, and calculate project-frame timing before editing. Keep connection, start, edits, completion, and read-back in the same PowerShell runspace. Do not reconnect or nest batches.

## Edit and complete

This example assumes the intended project is already connected. The editing-error handler cancels staging; completion is outside that handler so a commit timeout is handled as a recovery decision rather than automatically cancelling it.

```powershell
$ErrorActionPreference = 'Stop'
$null = Start-ProjectBatch
try {
    $track = Add-ProjectTrack -PassThru
    foreach ($i in 0..9) {
        $clip = Add-ProjectSolidColorClip -TrackId $track.TrackId `
            -StartFrame ($i * 60) -DurationFrames 60 -PassThru
        Set-ProjectClip -ClipId $clip.ClipId -Name "Segment $i"
    }
    Get-ProjectClip -TrackId $track.TrackId
} catch {
    $null = Stop-ProjectBatch
    throw
}
$commit = Complete-ProjectBatch -ChangeReason 'Create the scene' -TimeoutSeconds 300
$commit | Select-Object BatchId, OperationCount, CurrentSnapshotId
Get-ProjectClip -TrackId $track.TrackId
```

Each edit is sent immediately and changes independent in-memory data. Owner queries and `-PassThru` return that staged state, including real IDs for subsequent commands. They do not update the timeline UI, preview, render state, disk project, or history. The GUI overlay blocks interaction and other RPC connections cannot edit; their ordinary queries see committed state.

`Complete-ProjectBatch` waits for one save/history entry and the unified UI/subsystem refresh. Empty batches add no history. Completion already saves: preview or render after it succeeds, and do not immediately call `Save-Project` solely to persist the same edits. Further corrections can form another batch. Lifecycle commands return status without `-PassThru`; use `$null =` when their output should not enter another command's pipeline.

## Restricted operations

Before completing or cancelling, do not call:

- `Save-Project`, `Undo-ProjectHistory`, `Redo-ProjectHistory`, or `Restore-ProjectHistory`.
- `Get-ProjectFramePreview`, `Get-ProjectClipFramePreview`, or `Get-ProjectClipTransformPreview`.
- `Save-ProjectClipEffectGroup` (writes the shared library), or `Add-ProjectExternalSource`, `Set-ProjectExternalSource`, or `Remove-ProjectExternalSource` (changes source registration).

Clip, subtitle, vector, effect graph, and keyframe edits can stay in the batch; schema queries and reading staged values remain available. `-WhatIf` skips the relevant command; `Start-ProjectBatch -WhatIf` does not enter batch mode. Do not turn an unsaved-preview request into a saved batch without the user's intent supporting persistence.

## Errors, cancellation, and timeouts

A failed edit rolls back that request's data, leaving earlier successful operations staged. Continue or explicitly complete those successes only when they are an intended result; otherwise use `Stop-ProjectBatch`. The example above chooses cancellation for an editing error.

If completion times out, retain the same connection and retry only `Complete-ProjectBatch` with a larger timeout, following [batch failure recovery](troubleshooting.md#batch-failures). Do not replay the editing loop. A batch already applying can reject further staged reads or edits. Do not close the runspace or disconnect while trying to retain uncommitted work.

`Stop-ProjectBatch`, disconnection, module removal, or runspace shutdown discards uncommitted data and releases the overlay. Data already saved stays committed; stopping after a refresh failure reports `Saved = true` and `Cancelled = false`. Re-read committed state after cleanup when the outcome was uncertain, and report persistence only after verified completion or a committed-state check.
