# Connected GUI project operations

## Connect

The target project must already be open in the projectFrameCut GUI. The ordinary flow asks the user to approve an external RPC request:

```powershell
Connect-ProjectFrameCut -Name 'Batch editor' -Author 'Agent' `
  -Purpose 'Apply the requested timeline edits' -TimeoutSeconds 300
Get-ProjectInfo
```

Use `-ExecutablePath` if `pjfc` is not on `PATH`, and `-RequestDirectory` when the GUI is configured with a non-default request directory. The approval window determines the project being edited.

For an already issued pipe:

```powershell
Connect-ProjectFrameCut -PipeId $pipeId
Connect-ProjectFrameCut -PipeName $connection.PipeName
```

`PipeId` is a 64-character hexadecimal token. Treat it and the token as secrets.

For persistent authorization:

```powershell
Connect-ProjectFrameCutPersistent `
  -ClientId '<client-guid>' -PrivateKey '.\pjfc-client-private.pem' -Service rpc
```

This requests fresh short-lived pipe information using the client private key, then connects to the currently authorized project. `Service` is routing metadata and is limited to 1–24 letters, digits, `.`, `_`, or `-`; it is not a replacement for public-key authorization.

Always verify the session after connection:

```powershell
$info = Get-ProjectInfo
$info
```

Disconnect explicitly with `Disconnect-ProjectFrameCut`. A closed/broken runspace or closed GUI project invalidates the connection; the module does not silently reconnect.

## Safe edit sequence

```powershell
$info = Get-ProjectInfo
$tracks = Get-ProjectTrack
$track = Add-ProjectTrack -PassThru
$asset = Add-ProjectAsset -FilePath '.\input.mp4' -Name '素材' -PassThru
$clip = Add-ProjectClip -TrackId $track.TrackId -AssetId $asset.AssetId `
  -StartFrame 0 -DurationFrames 120 -PassThru -ChangeReason 'Add opening clip'
Set-ProjectClip -ClipId $clip.ClipId -StartFrame 30 `
  -ChangeReason 'Move opening clip'
Get-ProjectClip -ClipId $clip.ClipId
Save-Project
Disconnect-ProjectFrameCut
```

`Add-ProjectClip` requires an existing `TrackId`. Its source can be `-FilePath`, `-AssetId`, or omitted for an empty clip. An omitted start defaults to frame 0; an empty clip defaults to 300 frames. Relative file paths resolve from the PowerShell current filesystem location. Importing an asset copies it into the project assets directory; removing the asset record does not delete the source file.

## Command catalog

Inspection and persistence:

- `Get-ProjectInfo`, `Save-Project`
- `Get-ProjectTrack [-TrackId]`, `Add-ProjectTrack [-TrackId] [-PassThru]`
- `Get-ProjectAsset [-AssetId] [-Name]`
- `Get-ProjectClip [-ClipId] [-TrackId] [-Name]`
- `Get-ProjectHistory [-TimeoutSeconds]`
- `Undo-ProjectHistory`, `Redo-ProjectHistory`, `Restore-ProjectHistory -SnapshotId`

Clips and assets:

- `Add-ProjectAsset -FilePath <path> [-Name] [-PassThru]`
- `Remove-ProjectAsset -AssetId <id>`
- `Add-ProjectClip -TrackId <id> [-FilePath] [-AssetId] [-Name] [-StartFrame] [-DurationFrames] [-SourceStartFrame] [-WidthPixels] [-HeightPixels] [-PassThru]`
- `Set-ProjectClip -ClipId <id> [-Name] [-TrackId] [-StartFrame] [-DurationFrames] [-SourceStartFrame] [-FilePath] [-WidthPixels] [-HeightPixels] [-XPixels] [-YPixels] [-PassThru]`
- `Copy-ProjectClip -ClipId <id> [-Name] [-TrackId] [-StartFrame] [-PassThru]`
- `Remove-ProjectClip -ClipId <id>`

Text:

- `Get-ProjectTextStyle`
- `Get-ProjectTextStyleField -StyleId <id>`
- `Add-ProjectTextClip -StyleId <id> -Text <text> -TrackId <id> [-StartFrame] [-DurationFrames] [-Fields <hashtable>] [-PassThru]`
- `Set-ProjectTextClipStyle -ClipId <id> -Fields <hashtable> [-PassThru]`

Effects/provider graphs:

- `Get-ProjectEffectProviderType [-Name]`
- `Get-ProjectEffectProviderField -TypeName <type>`
- `Get-ProjectClipEffectProvider -ClipId <id> [-ProviderId] [-TypeName]`
- `Add-ProjectClipEffectProvider -ClipId <id> -TypeName <type> [-Name] [-Enabled] [-Fields <hashtable>] [-InputProviderId] [-IsFinalOutput] [-PassThru]`
- `Set-ProjectClipEffectProvider -ClipId <id> -ProviderId <id> [-Name] [-Enabled] [-Fields <hashtable>] [-ResetToDefaults] [-InputProviderId] [-IsFinalOutput] [-PassThru]`
- `Remove-ProjectClipEffectProvider -ClipId <id> -ProviderId <id>`

All connected project commands accept `-TimeoutSeconds` (default 60). Write commands support `-WhatIf`, `-Confirm`, `-PassThru`, and `-ChangeReason`. By default write commands do not emit a result.

## IDs, fields, and history

Never invent IDs. Use returned `TrackId`, `AssetId`, `ClipId`, `StyleId`, and `ProviderId`. Query provider/style fields first; send only fields supported by the returned schema. Provider graph edits should be read back with `Get-ProjectClipEffectProvider` before saving.

`Copy-ProjectClip` places a copy at the source clip's end by default and creates independent effect instances. `Redo-ProjectHistory` chooses the most recently saved successor when multiple branches exist. History operations do not imply `Save-Project`.
