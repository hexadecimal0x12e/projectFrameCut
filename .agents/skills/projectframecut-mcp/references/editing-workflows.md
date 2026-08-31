# Editing workflows

Use these sequences as decision guides. Arguments are JSON objects passed to the named MCP tools.

## Open and inspect a project

```text
list_projects {}
enter_project {"projectRoot":"D:\\projectFrameCut\\My Drafts\\Demo"}
get_timeline_info {}
list_layers {}
list_clips {}
```

After `enter_project`, refresh tool discovery if project tools are not visible. Use the returned UUIDs for all subsequent calls.

If the server started with `--projectRoot`, begin with `get_timeline_info`; `list_projects` will not be present because the server is already in project mode.

## Convert time to frames

Read `frameRate` from `get_timeline_info`. For a user request stated in seconds, calculate the frame position consistently:

```text
startFrame = round(startSeconds * frameRate)
durationFrames = max(1, round(durationSeconds * frameRate))
```

If the user supplied exact timecode or requires a different rounding convention, preserve that convention and report it. Do not convert normalized animation indices (0..1) as seconds or frames.

## Add an asset clip

```text
list_project_assets {"scope":"all","filter":"intro","assetType":"Video"}
add_clip_from_asset {
  "assetId":"<returned asset ID>",
  "layerIndex":0,
  "startFrame":0,
  "duration":300
}
list_clips {}
save_project {"changeReason":"Add intro video"}
```

Resolve ambiguity using asset name, type, and ID. Do not select the first fuzzy match when multiple plausible assets remain.

## Add or replace text

For a simple new title, use `add_text_clip`. For complete typography or multiple entries, create the clip and then use `set_text_entries`.

```text
add_text_clip {
  "text":"Opening title",
  "layerIndex":2,
  "startFrame":0,
  "duration":180,
  "fontName":"Arial",
  "fontSize":96,
  "fillR":65535,
  "fillG":65535,
  "fillB":65535,
  "fillA":1
}
```

`set_text_entries` replaces every text entry, so read the clip first and retain entries the user did not ask to remove.

## Build a vector clip

```text
add_vector_canvas_clip {"layerIndex":1,"startFrame":0,"duration":240}
list_vector_component_types {}
add_vector_component {
  "clipId":"<vector clip UUID>",
  "component":{"typeName":"<returned type>","parameters":{}}
}
list_vector_components {"clipId":"<vector clip UUID>"}
```

Before `update_vector_component`, use `list_vector_components` to obtain the component ID and preserve fields not requested for replacement. Before `replace_vector_components`, preserve the full desired list because the operation is atomic and complete.

## Edit an effect-provider graph

Use the provider-native graph for precise effect work:

1. `list_available_effects {}` and, when useful, `get_effect_info {"effectType":"..."}`.
2. `list_clip_effect_providers {"clipId":"..."}`.
3. Prefer a typed helper such as `set_color_adjustment` or `set_clip_speed` when it matches the request.
4. Otherwise add/update providers, connect picture inputs, select the final output, and bind dynamic fields.
5. Run `validate_effect_provider_graph`.
6. Read the graph again and save only after validation succeeds.

Example typed edit:

```text
set_color_adjustment {
  "clipId":"<clip UUID>",
  "brightness":1.1,
  "contrast":1.15,
  "saturation":0.9
}
validate_effect_provider_graph {"clipId":"<clip UUID>"}
save_project {"changeReason":"Adjust clip color"}
```

Use `replace_effect_provider_graph` only when the user supplied or approved a complete replacement graph.

## Animate a field

- Use `set_linear_effect_animation` for a numeric provider field with a simple start/end transition.
- Use `set_position_keyframes` and `set_crop_keyframes` for their specialized effects.
- Use `set_vector_component_keyframes` for a numeric field on a vector component.
- Bind a custom value provider with `bind_effect_provider_field` only after verifying field compatibility.

Normalized keyframe `index`/`time` values are in the closed interval 0..1 over the clip's duration.

## Save, exit, and verify

After the requested mutation:

1. Read back the changed clip, vector component list, or provider graph.
2. Call `save_project` with a concise change reason.
3. If the user wants another project, call `exit_project {"save":true}`, wait for the tool-list change, and then use no-project tools.

`exit_project {"save":false}` skips the save call and may abandon unsaved work. Do not use it as an error-recovery shortcut.

## Concurrent session conflict

When GUI/RPC and MCP share a backend, the server refreshes its snapshot before operations and protects mutations with revision/hash preconditions. If a mutation reports that the session changed:

1. Re-read the project/timeline and the specific target.
2. Check whether the user's requested edit still applies to the refreshed object.
3. Retry only the still-valid narrow mutation.
4. Do not replay a full graph/list replacement based on stale state.

## Slow `enter_project`

Do not immediately retry, because project entry is serialized and an overlapping retry can compound the wait. Distinguish these stages when diagnosing:

1. render runtime/plugin initialization;
2. project workspace or headless session opening;
3. whole-timeline frame-hash index construction;
4. GUI process startup when the server is not headless;
5. MCP tools-list-changed delivery after the project is ready.

For a code diagnosis, trace and time those boundaries while preserving cancellation. A transport response timeout does not prove the server process is dead.
