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
- `Set-ProjectClip -ClipId <id> [-Name] [-TrackId] [-StartFrame] [-DurationFrames] [-SourceStartFrame] [-FilePath] [-PassThru]`
- `Copy-ProjectClip -ClipId <id> [-Name] [-TrackId] [-StartFrame] [-PassThru]`
- `Remove-ProjectClip -ClipId <id>`

Clip properties and geometry:

- `Get-ProjectClipProperties -ClipId <id>`
- `Set-ProjectClipProperties -ClipId <id> [-Name] [-ClipColor] [-AllowFreeScaleResize] [-ExtendToWholeDraft] [-TargetDecoder] [-HDRBrightnessOffset] [-AudioEnabled] [-AudioVolume] [-DetachAudio]`
- Solid-color channels (`Red/Green/Blue/Alpha`), mask sources (`MaskSourceType/SourceArguments`), and `AntiAliasMode` also use `Set-ProjectClipProperties`.
- `Get-ProjectClipGeometry -ClipId <id>`
- `Set-ProjectClipGeometry -ClipId <id> [-WidthPixels] [-HeightPixels] [-XPixels] [-YPixels] [-RotationDegrees] [-CropEnabled] [-CropXPixels] [-CropYPixels] [-CropWidthPixels] [-CropHeightPixels] [-CropRotationDegrees]`

Display geometry has moved from `Set-ProjectClip` and `Set-ProjectVectorClip` to `Set-ProjectClipGeometry`. Creation commands still accept initial dimensions. Setting static geometry clears the corresponding Clip-argument overrides and bindings. Crop rectangles use pixels; rotation uses degrees.

Additional editing commands:

- `Add-ProjectSolidColorClip`, `Add-ProjectEmptyVideoClip`, `Get-ProjectMaskSourceType`, `Add-ProjectMaskClip`
- `Get-ProjectSubtitleClip`, `Add-ProjectSubtitleClip`, `Set-ProjectSubtitleClip`
- `Get-ProjectSubtitleCue`, `Add-ProjectSubtitleCue`, `Set-ProjectSubtitleCue`, `Remove-ProjectSubtitleCue`
- `Split-ProjectClip -ClipId <id> -FrameIndex <frame>`, `Join-ProjectClip -ClipIds <ids>`, `Split-ProjectClipGroup -ClipId <id>`
- `Remove-ProjectTrack -TrackId <id>` removes its clips and reindexes tracks; query IDs again afterward.
- `Get-ProjectExternalSource`, `Add-ProjectExternalSource -FilePath <folder-or-manifest>`, `Set-ProjectExternalSource`, `Remove-ProjectExternalSource`
- `Add-ProjectExternalSourceClip -ImportId <id> -SourceId <source> -SourceKind Video|Audio|Mask -TrackId <id>`; RPC clients use `ClientId` instead of `ImportId`.
- `Get-ProjectTransformType [-Audio]`, `Get-ProjectClipTransform`, `Set-ProjectClipTransform`, `Remove-ProjectClipTransform`, `Get-ProjectClipTransformPreview`
- `Get-ProjectSettings`, `Set-ProjectSettings`

Subtitle cue times and offsets use milliseconds relative to the subtitle source, independently of project-frame clip timing. Cue writes validate unique IDs, positive intervals and non-overlapping times. File imports support SRT/VTT/ASS/SSA and optional `EncodingName`; importing into an existing clip preserves its style.

Transitions use `Side Left|Right`, `InputMode OneInput|TwoInput`, `DurationFrames`, `RenderOrder BeforeEffects|AfterEffects`, and provider `Fields`. Neighbor selection and shared ownership follow the GUI. Picture-transition previews return a video path. External-source loading is session state; import/removal updates the project index.

Project settings include frame rate, resolution, HDR brightness, imported covers, cover-as-first-frame and `UserDefinedProperties` (null removes a key). Keep using the standard `ChangeReason/WhatIf/PassThru` workflow and save explicitly when finished.

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

Effect binding graphs:

- `Get-ProjectClipEffectGraph -ClipId <id> [-AsMermaid]`
- `Get-ProjectClipEffectBinding -ClipId <id> [-ProviderId] [-FieldId]`
- `Get-ProjectClipEffectBindingSource -ClipId <id> [-ProviderId] [-FieldId] [-Pipeline Picture|NativeContent]`
- `Set-ProjectClipEffectBinding -ClipId <id> [-ProviderId] -FieldId <field> -SourceId <source>`
- `Remove-ProjectClipEffectBinding -ClipId <id> [-ProviderId] -FieldId <field>`
- `Clear-ProjectClipEffectBinding -ClipId <id> [-ProviderId] [-OutputFieldId] [-Pipeline]`
- `Set-ProjectClipEffectOutput -ClipId <id> [-ProviderId] [-OutputFieldId] [-Pipeline]`
- `Get-ProjectClipArgument -ClipId <id> [-FieldId]`
- `Set-ProjectClipArgument -ClipId <id> [-Fields <hashtable>] [-ResetToDefaults]`

Provider snapshots include `InFields`, `OutFields`, `AnchorsBindingState`, `MetaData`, and `Pipeline`. `Add/Set-ProjectClipEffectProvider` also accept `-Pipeline` and `-Metadata`; metadata entries set to null are removed. `Add` accepts `-NoAutoConnect` for a disconnected node. Query the graph's `Diagnostics` while constructing it incrementally.

Binding sources can also be selected with `-SourceProviderId [-SourceOutputFieldId]`, `-ClipInput`, or `-BuiltIn Frame|Progress`. Use a named output on multiple-output providers. Omitting `ProviderId` from binding/field-source commands targets Clip arguments; the argument node is created on the first write. `Get-ProjectClipArgument` returns a null `ProviderId` before that node exists. Setting static fields clears their bindings; resetting Clip arguments restores the Clip's own values.

`Clear-ProjectClipEffectBinding` clears every binding when no provider is specified, all incoming/outgoing connections of one provider when `ProviderId` is given, or just outgoing connections when an `OutputFieldId` is given. `Set-ProjectClipEffectOutput` without a provider clears the chosen pipeline's output (default `Picture`). Final outputs are reported separately in the graph's `Outputs` array.

Cross-Clip endpoints:

- `Get-ProjectClipValueEndpoint -ClipId <id> [-EndpointId] [-Available] [-Pipeline]`
- `Add-ProjectClipValueEndpoint -ClipId <id> [-Name] [-ValueType Numeric|Integer|UnsignedInteger|Long|UnsignedLong|Boolean|String|Color|Size|Position|SizeAndPosition|IPicture|VectorPicture] [-Pipeline]`
- `Add-ProjectClipValueEndpoint -ClipId <id> -SourceClipId <id> -SourceEndpointId <id> [-Name] [-Pipeline]`
- `Set-ProjectClipValueEndpoint -ClipId <id> -EndpointId <id> [-Name] [-Enabled] [-ValueType] [-SourceClipId] [-SourceEndpointId] [-Pipeline]`
- `Remove-ProjectClipValueEndpoint -ClipId <id> -EndpointId <id>`

Bind an export endpoint's `__Input__` to its value source, and bind consumer fields to the import endpoint's `ProviderId`. Discover sources with `-Available`, then inspect `Enabled` and `Status`. Cross-Clip sources must precede the consumer in layer order and use the same pipeline. Within one Clip, only non-content values may pass from `NativeContent` to `Picture`.

Effect groups and templates:

- `Get-ProjectClipEffectGroup -ClipId <id> [-GroupId]`
- `Add-ProjectClipEffectGroup -ClipId <id> -Name <name> [-ProviderIds <guid[]>] [-ChildGroupIds <guid[]>] [-Ports <hashtable[]>] [-Collapsed] [-X] [-Y]`
- `Set-ProjectClipEffectGroup -ClipId <id> -GroupId <id> [-Name] [-Ports] [-Collapsed] [-X] [-Y]`
- `Remove-ProjectClipEffectGroup -ClipId <id> -GroupId <id> [-RemoveProviders]`
- `Export-ProjectClipEffectGroup -ClipId <id> -GroupId <id>`
- `Import-ProjectClipEffectGroup -ClipId <id> -TemplateJson <json> [-X] [-Y]`
- `Import-ProjectClipEffectGroup -ClipId <id> -Hash <hash> [-X] [-Y]`
- `Get-ProjectEffectGroupTemplate [-Hash]`
- `Save-ProjectClipEffectGroup -ClipId <id> -GroupId <id> [-ReplaceHash]`

Group ports are ordered hashtables with `ProviderId`, `FieldId`, `Name`, and `IsInput`. Required boundary ports are added automatically. Group selected nodes with the same parent; Clip arguments, endpoints, and transitions cannot be grouped. Removing a group ungroups its members unless `-RemoveProviders` is used. Export returns JSON for `Set-Content`; import assigns independent IDs and omits external project bindings. Saving a group writes the application's shared group library and does not replace `Save-Project`.

All connected project commands accept `-TimeoutSeconds` (default 60). Write commands support `-WhatIf`, `-Confirm`, `-PassThru`, and `-ChangeReason`. By default write commands do not emit a result.

## IDs, fields, and history

Never invent IDs. Use returned `TrackId`, `AssetId`, `ClipId`, `StyleId`, and `ProviderId`. Query provider/style fields first; send only fields supported by the returned schema. Provider graph edits should be read back with `Get-ProjectClipEffectProvider` before saving.

`Copy-ProjectClip` places a copy at the source clip's end by default and creates independent effect instances. `Redo-ProjectHistory` chooses the most recently saved successor when multiple branches exist. History operations do not imply `Save-Project`.
