# Offline project management

## Package and import

The module targets PowerShell 7.6+ Core and .NET 10. Publish the whole output directory; its Contracts, RPC protocol, protobuf, and Windows SDK dependencies are required.

```powershell
dotnet publish tools/projectFrameCut.PowerShell/projectFrameCut.PowerShell.csproj `
  -f net10.0-windows10.0.19041.0 -c Release `
  -o ./artifacts/projectFrameCut.PowerShell
Import-Module ./artifacts/projectFrameCut.PowerShell/projectFrameCut.PowerShell.psd1
Get-Command -Module projectFrameCut.PowerShell
```

On Linux or macOS publish `net10.0`. The module does not install itself or modify the PowerShell profile.

## Project root and CRUD

Without `-ProjectRoot`, the module invokes `pjfc user_data_root` and uses `<user data root>/My Drafts`. Every offline root cmdlet also accepts `-PjfcExecutablePath` when `pjfc` is not on `PATH`.

```powershell
Get-Project
$p = New-Project -Name Demo -Width 1920 -Height 1080 -FrameRate 60 -PassThru
Get-Project -Project $p.Path
Rename-Project -Project $p.Path -NewName Demo2 -PassThru
Remove-Project -Project $p.Path -WhatIf
```

`Rename-Project` and `Remove-Project` require the project to be under the selected project root. `Remove-Project` deletes the project directory and has high confirmation impact; keep `-WhatIf` or `-Confirm` in automation until the exact path is verified.

## Templates

Templates are JSON objects. `Get-ProjectTemplateVariable` reports declared variables, types, defaults, display names, and descriptions. `New-Project -Variables` accepts a PowerShell hashtable. Missing variables without defaults are prompted for in an interactive host; non-interactive Agents must provide them all.

```powershell
Get-ProjectTemplateVariable .\template.json
New-Project -Name Opening -Template .\template.json `
  -Variables @{ title = 'Opening'; width = 1920; height = 1080 } -PassThru
```

Template placeholders use `{{ name }}`. Typed definitions can convert Boolean, Integer, Number, File, and Json values. A missing required value should be fixed by supplying `-Variables`, not by guessing a default.

## Starting a project instance

```powershell
Get-ProjectInstance
Start-Project -Project 'D:\Video Projects\demo.pjfc'
Start-Project -Project 'D:\Video Projects\demo.pjfc' -Instance 'someAuthor.SomeApp'
```

On Windows, instances are discovered from compatible installed AppX packages and the project `LastOpenAppIdentifier`; no instance picker is started. On Linux/macOS, `-Instance` must be the executable path:

```powershell
Start-Project -Project '/data/demo.pjfc' -Instance '/opt/projectFrameCut/pjfc'
```

Starting the project does not itself authorize a GUI RPC connection. After the window opens, use the connected workflow.

## Offline command catalog

- `Get-Project [-Project] [-ProjectRoot] [-PjfcExecutablePath]`
- `New-Project -Name <name> [-Width] [-Height] [-FrameRate] [-Template] [-Variables <hashtable>] [-PassThru]`
- `Rename-Project -Project <path> -NewName <name> [-PassThru]`
- `Remove-Project -Project <path> [-PassThru] [-WhatIf] [-Confirm]`
- `Get-ProjectTemplateVariable -Template <path>`
- `Get-ProjectInstance`
- `Start-Project -Project <path> [-Instance <AppUserModelId-or-executable>]`

