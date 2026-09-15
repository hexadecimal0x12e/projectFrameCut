# Project plugins

Project plugins are Windows-only plugin packages stored below a project's `projectPlugins` directory. They are verified and loaded into the AppContainer worker when the project opens, then unloaded when the project closes. The host process never loads the plugin assembly.

## Create a package

Open the project's **Extensions > Project plugins** page and select a staging directory containing:

- `<PluginID>.dll`
- `metadata.json`
- optional managed dependencies and resources
- optional `project-plugin.json`

The first package creation generates a local RSA publisher identity. Its private key remains in the app's secure storage. Every package receives a short-lived signing certificate issued by that identity; the publisher certificate, package hash, capability declaration, and relative package path are stored with the project.

On another device, the first open asks the user to trust the binding of project ID, plugin ID, and publisher certificate fingerprint. Headless rendering never prompts and only loads a binding already trusted on that device.

## Declaration

`project-plugin.json` is signed as part of the package. Enum values may be written as names, including comma-separated capability flags.

```json
{
  "version": 1,
  "pluginId": "Example.ProjectPlugin",
  "capabilities": "Effects, Tools, Menus, Settings, PropertyPanels",
  "tools": [
    {
      "id": "normalize-title",
      "name": "Normalize title",
      "description": "Normalizes a title for this project.",
      "inputSchemaJson": "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"}},\"required\":[\"title\"]}"
    }
  ],
  "menus": [
    { "id": "normalize-default", "title": "Normalize title", "toolId": "normalize-title" }
  ],
  "settings": [
    { "id": "style", "title": "Style", "kind": "Choice", "defaultValue": "short", "options": ["short", "long"] }
  ],
  "propertyPanels": [
    {
      "id": "title-panel",
      "title": "Title helper",
      "description": "Runs the project-specific title helper.",
      "toolId": "normalize-title",
      "fields": [
        { "id": "title", "title": "Title", "kind": "String", "defaultValue": "" }
      ]
    }
  ]
}
```

To expose tools, the plugin implements `IProjectPluginToolProvider`. Tool handlers receive and return JSON strings and execute inside the AppContainer worker. Effects and video sources use the existing isolation protocol. Menus, settings, effect controls, and project property panels are rendered by generic host UI from the signed declaration.

`TextStyles` and `VectorHandlers` are reserved and rejected until isolated protocols exist for them.
