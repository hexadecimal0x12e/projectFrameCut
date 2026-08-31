# Tool catalog

This catalog describes the tools exposed specifically by `pjfc mcp`. Tool availability changes with project mode. The MCP-provided input schema is authoritative.

## No-project mode

| Tool | Important arguments and result |
| --- | --- |
| `list_projects` | No arguments. Lists valid directories under `<dataRoot>/My Drafts`, newest first. |
| `list_templates` | No arguments. Lists template IDs, metadata, variables, and variable definitions from `<dataRoot>/My Templates`. |
| `read_asset_library` | No arguments. Reads the global `My Assets/.database/database.json`. |
| `enter_project` | Required `projectRoot`. Loads an existing project and switches the tool list. There is no `openClient` argument. |
| `create_empty_project` | Required `projectName`; optional `width=1920`, `height=1080`, `frameRate=60`, `enterProject=true`. Creates a unique directory in `My Drafts`. |
| `create_project_from_template` | Required `templateId`, `projectName`; optional string-or-null `variables`, `width`, `height`, `frameRate`, `enterProject=true`. Template assets are extracted into the new project when present. |

Project creation refuses invalid file-name characters and avoids overwriting an existing project directory by choosing a unique path.

## Project inspection and low-level timeline tools

| Tool | Important arguments |
| --- | --- |
| `get_timeline_info` | None. Returns resolution, frame rate, total frames, layer/clip counts, and timing metadata; shared sessions also return revision/hash. |
| `list_layers` | None. Groups clip summaries by `layerIndex`. |
| `get_project_metadata` | None. Returns name/path/file size and project metadata; shared sessions also return revision/hash. |
| `list_clips` | None. Returns all serialized clips. |
| `get_clip` | Required `clipId`. |
| `upsert_clip` | Required raw `clip` object (`ClipDraftDTO`). Use only when its complete shape is known. |
| `move_clip` | Required `clipId`, `layerIndex`, `startFrame`; optional `subLayerIndex`. |
| `patch_clip` | Required `clipId`, `patch`. Patches selected supported clip fields. |
| `delete_clip` | Required `clipId`. |
| `save_project` | Optional `changeReason`. Persists current state to disk. |
| `exit_project` | Optional `save=true`. Returns to no-project mode and changes the tool list. |

## Assets and common clip creation

Placement tools require non-negative `layerIndex` and `startFrame`.

| Tool | Important arguments |
| --- | --- |
| `list_project_assets` | Optional `scope=all\|project\|global`, `filter`, `assetType=Video\|Audio\|Image\|Font\|Other`. Project entries win duplicate asset IDs. |
| `add_clip_from_asset` | Required `assetId`, `layerIndex`, `startFrame`; optional `subLayerIndex`, `duration`. Supports video, image, or audio assets. |
| `add_text_clip` | Required `text`, `layerIndex`, `startFrame`; optional name/duration, font, position, 16-bit RGBA fill, and target size. |
| `set_text_entries` | Required `clipId`, `entries`. Each entry requires `text` and may contain font/fallbacks/style/size, x/y, 16-bit fill/stroke plus alpha, stroke thickness, character/word/line spacing, rotation, entry layer, alignment, decoration, flow direction, variation axes, and extra data. Replaces all entries. |
| `add_solid_color_clip` | Required placement; optional `color=#RRGGBB` or `#RRGGBBAA`, name, duration. |
| `set_solid_color` | Required `clipId`, `color`. |

## Vector canvas tools

| Tool | Important arguments |
| --- | --- |
| `add_vector_canvas_clip` | Required placement; optional name, duration, target width/height. |
| `list_vector_component_types` | None. Use before selecting a component `typeName`. |
| `list_vector_components` | Required `clipId`. |
| `add_vector_component` | Required `clipId`, `component`. Component requires `typeName`; supports `fromPlugin`, name, index, parameters, and animation frames. |
| `update_vector_component` | Required `clipId`, `componentId`, `component`. Retains the existing component ID. |
| `remove_vector_component` | Required `clipId`, `componentId`. |
| `replace_vector_components` | Required `clipId`, `components`. Atomically replaces the entire list. |
| `set_vector_component_keyframes` | Required `clipId`, `componentId`, `fieldId`, `keyframes`. Each keyframe has normalized `time` in 0..1, numeric `value`, and optional easing. |

Supported keyframe easing names are `Linear`, `QuadIn`, `QuadOut`, `QuadInOut`, `CubicIn`, `CubicOut`, `CubicInOut`, `SineIn`, `SineOut`, `SineInOut`, `ElasticIn`, `ElasticOut`, and `BounceOut`.

## Effect discovery and legacy effect records

| Tool | Important arguments |
| --- | --- |
| `list_available_effects` | None. Read this before constructing effect/provider data. |
| `get_effect_info` | Required `effectType`. Returns parameters/defaults for one type. |
| `add_effect` | Required `clipId`, raw `effect` object. Adds or replaces one legacy effect record. |
| `remove_effect` | Required `clipId`, `effectKey`. |
| `add_effect_bundle` | Required `clipId`, raw `bundle` object. Adds or replaces a bundle. |
| `remove_effect_bundle` | Required `clipId`, `bundleId`. |

Prefer the provider-native tools below when editing the current effect graph.

## Effect-provider graph

| Tool | Important arguments |
| --- | --- |
| `list_clip_effect_providers` | Required `clipId`. Returns providers, typed fields, ports, metadata, values, and bindings. |
| `add_effect_provider` | Required `clipId`, `typeName`; optional `providerId`, name, enabled, fields, metadata, implement type, and `autoConnect=output\|input\|none`. |
| `update_effect_provider` | Required `clipId`, `providerId`; optional name, enabled, fields, metadata, implement type. |
| `remove_effect_provider` | Required `clipId`, `providerId`; also clears references to it. |
| `connect_effect_provider_input` | Required `clipId`, `providerId`, `source`. Source is `clip-input`, `none`, or another picture-provider UUID. |
| `set_effect_provider_output` | Required `clipId`; optional provider UUID or null to disconnect the final output. |
| `bind_effect_provider_field` | Required `clipId`, `providerId`, `fieldId`, `source`. Source is a compatible value-provider UUID, `builtin://frame`, or `builtin://progress`. |
| `unbind_effect_provider_field` | Required `clipId`, `providerId`, `fieldId`; preserves the static fallback. |
| `validate_effect_provider_graph` | Required `clipId`. Checks picture connections, final output, cycles, value sources, and port compatibility. |
| `replace_effect_provider_graph` | Required `clipId`, complete `providers` array. Atomically replaces the graph. |

The `implementType` enum is `None`, `NotSpecified`, `IPicture`, `HwAcceleration`, or `Custom1` through `Custom5`.

## Typed effect and animation helpers

| Tool | Important arguments |
| --- | --- |
| `set_color_adjustment` | Required `clipId`; optional provider ID and brightness, contrast, saturation, hue, gamma, vibrance, temperature, invert, grayscale, opacity. Respect the live numeric ranges. |
| `set_clip_speed` | Required `clipId`, `ratio` in 0.05..8; optional provider ID. |
| `set_linear_effect_animation` | Required `clipId`, `targetProviderId`, `fieldId`, `fromValue`, `toValue`; optional animation provider ID/name. Creates or updates a numeric value provider and binds it. |
| `set_position_keyframes` | Required `clipId`, `keyframes`; optional provider ID. Each normalized `index` maps to target x/y/width/height and optional `isDelta`. |
| `set_crop_keyframes` | Required `clipId`, `keyframes`; optional provider ID and base crop geometry/angle. Each normalized `index` maps to crop x/y/width/height and optional angle. |

## Deliberately absent from `pjfc mcp`

The underlying integrated API can define `authorize_client`, `list_connected_clients`, `get_client_environment`, `render_client_preview`, `apply_client_patch`, and `move_client_clip`. The CLI MCP host sets authorization off and integrated-client tool exposure off, so these tools are not part of this Skill's callable surface even when the CLI starts its local GUI client.

The current MCP service registers no resource or prompt collections.
