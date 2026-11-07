# Offline project management

## Find and import the module

Use PowerShell 7.6+ Core. Windows application packages include the complete module at `<application install directory>/Public/PowerShell/projectFrameCut.PowerShell.psd1`. Prefer that module for the target application; do not publish from source merely because a repository artifacts directory is absent. The module README is beside the manifest; bundled skills are under `<application install directory>/Public/Skills`.

If the install directory is already known, set `$modulePath` to that manifest. Otherwise, look beside the running application first, then query installed MSIX/AppX packages:

```powershell
$modulePaths = @(Get-Process -Name projectFrameCut -ErrorAction SilentlyContinue |
  ForEach-Object {
    if ($_.Path) { Join-Path (Split-Path $_.Path -Parent) 'Public/PowerShell/projectFrameCut.PowerShell.psd1' }
  } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -Unique)
if ($modulePaths.Count -eq 0) {
  $modulePaths = @(Get-AppxPackage -Name '*projectFrameCut*' |
    Where-Object InstallLocation |
    ForEach-Object { Join-Path $_.InstallLocation 'Public/PowerShell/projectFrameCut.PowerShell.psd1' } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -Unique)
}
$modulePaths
```

If there is one candidate, use `$modulePath = $modulePaths[0]`. If there are several, match the target application's process path or package identity before importing; do not arbitrarily choose a different installed version. Windows execution aliases such as `pjfc.exe` may live outside the application directory; use the actual process path or package `InstallLocation`.

```powershell
Import-Module -Name $modulePath
Get-Command -Module projectFrameCut.PowerShell
```

Import the `.psd1` in place with its DLL and dependencies beside it; the source manifest alone is insufficient. Loading the module does not establish or authorize an RPC connection. The module does not install itself or modify the PowerShell profile.

## Publish from source when needed

For module development, an older application without the bundled module, or Linux/macOS, an existing complete publish directory is also usable. If a publish is needed, inspect the actual project target frameworks first. The current module targets .NET 10; on Windows:

```powershell
dotnet publish tools/projectFrameCut.PowerShell/projectFrameCut.PowerShell.csproj `
  -f net10.0-windows10.0.19041.0 -c Release `
  -o ./artifacts/projectFrameCut.PowerShell
Import-Module ./artifacts/projectFrameCut.PowerShell/projectFrameCut.PowerShell.psd1
Get-Command -Module projectFrameCut.PowerShell
```

On Linux or macOS publish `net10.0`. Keep the whole publish directory, including Contracts, RPC protocol, protobuf, and any platform dependencies.

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

