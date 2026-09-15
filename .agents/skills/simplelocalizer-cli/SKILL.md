---
name: simplelocalizer-cli
description: "Use when: managing SimpleLocalizer localization data via CLI, including locale/item CRUD, locate.json updates, and localization code generation (locate.sg.cs). Keywords: SimpleLocalizer, locate.json, localization, add item, update item, delete item, list locales, generate code."
---

# SimpleLocalizer CLI Skill

Use this skill when you need to automate localization operations in projects that use SimpleLocalizer.

## Goal

Perform localization CRUD operations on `Localize/locate.json` through `splc-edit`, then generate localization code with `generate-code`.

## Preconditions

1. CLI command `splc-edit` is available (via a .NET global tool). 
2. Target app has a `.csproj` file.
3. Localization file path is either:
- `<projectDir>/Localize/locate.json`
- or explicitly provided by `--file`.

## Command Pattern

Prefer `--project` to auto-resolve locate.json:

```powershell
splc-edit <command> --project <path-to-target.csproj> [options]
```

Use `--file` when you already know exact file path:

```powershell
splc-edit <command> --file <path-to-locate.json> [options]
```

## Supported Operations

### Locale CRUD

```powershell
# List locales
splc-edit list-locales --project <app.csproj>

# Add locale
splc-edit add-locale --project <app.csproj> --locale zh-CN

# Delete locale
splc-edit delete-locale --project <app.csproj> --locale en-US

# Set default locale *Use with caution, this could affect codegen behavoiur*
splc-edit set-default-locale --project <app.csproj> --locale zh-CN
```

### Item CRUD

```powershell
# List items in locale
splc-edit list-items --project <app.csproj> --locale zh-CN

# Get item
splc-edit get-item --project <app.csproj> --locale zh-CN --id Welcome

# Add item
splc-edit add-item --project <app.csproj> --locale zh-CN --id Welcome --value "欢迎"

# Add interpolation item
splc-edit add-item --project <app.csproj> --locale en-US --id WelcomeUser --mode InterpolationString --args name=string --value "Welcome {name}"

# Update item
splc-edit update-item --project <app.csproj> --locale en-US --id Welcome --value "Welcome!"

# Delete item (all locales)
splc-edit delete-item --project <app.csproj> --id Welcome
```

### Code Generation

```powershell
# Generate to default path: <projectDir>/Localize/locate.sg.cs
splc-edit generate-code --project <app.csproj>

# Generate to custom output path
splc-edit generate-code --project <app.csproj> --output <path-to-output.cs>
```

## Workflow for Agents

1. Confirm target project path (`.csproj`) or `locate.json` path.
2. Run locale/item command requested by user.
3. If changes affect runtime localization, run `generate-code`.
4. Report concise result summary:
- modified file path
- affected locale/item IDs
- generated code output path

## Safety and Validation

1. Do not guess project path; resolve from workspace or ask user.
2. Always quote values containing spaces.
3. After write operations, prefer a read-back verification command:
- `list-locales`
- `get-item`
- or `list-items --locale <id>`
4. If command fails, surface stderr and suggest the exact corrected command.
