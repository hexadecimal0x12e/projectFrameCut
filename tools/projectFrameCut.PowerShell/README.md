# projectFrameCut.PowerShell

PowerShell 7.6.0+（Core）/ .NET 11 模块。既可以离线管理本地项目，也可以操作授权窗口中已打开的 GUI 项目。

## 打包和导入

```powershell
dotnet publish projectFrameCut.PowerShell.csproj -f net11.0-windows10.0.19041.0 -c Release -o ./artifacts/projectFrameCut.PowerShell
Import-Module ./artifacts/projectFrameCut.PowerShell/projectFrameCut.PowerShell.psd1
Get-Command -Module projectFrameCut.PowerShell
```

在 Linux 或 macOS 上将目标框架改为 `net11.0`。

分发整个输出目录，保留 Contracts、RpcClient 和 protobuf 依赖。调用方自行安装 PowerShell；不需要安装模块到系统目录，也不会自动修改 Profile。

## 离线项目管理

离线命令不需要启动主 App。默认通过 `pjfc user_data_root` 获取用户数据根目录，并管理其中的 `My Drafts`；可以用 `-ProjectRoot` 指定其他项目集合目录。

```powershell
Get-Project
$project = New-Project -Name Demo -Width 1920 -Height 1080 -FrameRate 60 -PassThru
Rename-Project -Project $project.Path -NewName Demo2 -PassThru
Remove-Project -Project $project.Path -WhatIf
```

从 JSON 模板创建前可以查看变量，并通过 Hashtable 填充。缺少且没有默认值的变量会在交互式终端中询问；非交互宿主应完整提供 `-Variables`。

```powershell
Get-ProjectTemplateVariable ./template.json
New-Project -Name Opening -Template ./template.json -Variables @{ title = 'Opening'; width = 1920 }
```

Windows 会直接枚举兼容的已安装 AppX 包并激活项目记录的 `LastOpenAppIdentifier`，也可以用 `-Instance` 覆盖。不会启动实例选择器。

```powershell
Get-ProjectInstance
Start-Project -Project 'D:\Video Projects\demo.pjfc'
Start-Project -Project 'D:\Video Projects\demo.pjfc' -Instance 'someAuthor.SomeApp'
```

Linux 和 macOS 不枚举 AppX；此时 `-Instance` 必须是目标 `pjfc` 可执行文件路径。

```powershell
Start-Project -Project '/data/demo.pjfc' -Instance '/opt/projectFrameCut/pjfc'
```

## 连接

在目标窗口打开项目，然后请求授权：

```powershell
Connect-ProjectFrameCut -Name 'Batch editor' -Author 'Me' -Purpose '批量编辑当前项目'
```

默认执行 `pjfc rpc_request --wait`。可选 `-ExecutablePath`、`-RequestDirectory`、`-TimeoutSeconds`（默认 300）。请求目录必须与目标应用配置一致。批准请求的窗口即为连接目标。

持久授权客户端可以直接使用独立 cmdlet：

```powershell
Connect-ProjectFrameCutPersistent -ClientId '<ClientId>' -PrivateKey '.\pjfc-client-private.pem' -Service 'rpc'
```

可选 `-ExecutablePath`、`-RequestDirectory` 和 `-TimeoutSeconds`。该命令通过 `pjfc rpc_request --wait` 获取由客户端公钥加密的新管道信息，再连接到当前授权项目。

也可以使用目标项目窗口生成的管道 ID：

```powershell
Connect-ProjectFrameCut -PipeId $pipeId
# 或使用 rpc_request 返回的信息：
Connect-ProjectFrameCut -PipeName $connection.PipeName
```

`PipeId` 是 64 位十六进制令牌，对应 `projectFrameCut-rpc-<PipeId>`。不要公开令牌。每个运行空间拥有一个当前连接；新连接验证失败会保留原连接。

```powershell
Get-ProjectInfo
$track = Add-ProjectTrack -PassThru
$asset = Add-ProjectAsset -FilePath ./input.mp4 -Name '素材' -PassThru
$clip = Add-ProjectClip -TrackId $track.TrackId -AssetId $asset.AssetId -StartFrame 0 -DurationFrames 120 -PassThru
Set-ProjectClip -ClipId $clip.ClipId -Name '开场' -StartFrame 30 -ChangeReason '调整开场片段'
Get-ProjectClip -TrackId $track.TrackId | Remove-ProjectClip -WhatIf
Get-ProjectEffectProviderType
Get-ProjectTextStyle
Get-ProjectHistory
Undo-ProjectHistory -PassThru
Redo-ProjectHistory
Restore-ProjectHistory -SnapshotId '<SnapshotId>' -PassThru
Save-Project
Disconnect-ProjectFrameCut
```

时间参数均为项目帧；尺寸和位置分别用 `WidthPixels/HeightPixels/XPixels/YPixels`。查询名称按不区分大小写的子串匹配。动态字段用 `-Fields @{ FieldId = Value }`，先用 `Get-ProjectEffectProviderField -TypeName ...` 或 `Get-ProjectTextStyleField -StyleId ...` 查询字段。

`Add-ProjectClip` 必须指定已有 `TrackId`，可选 FilePath、AssetId，或不指定来源创建空片段；默认起点 0，空片段默认长度 300 帧。文件路径相对 PowerShell 当前文件系统位置解析；素材导入复制到项目 assets 目录，移除素材记录不会删除源文件。复制片段默认放到源片段末尾，使用独立的效果实例。

写命令支持 `-WhatIf/-Confirm`、`-PassThru` 和 `-ChangeReason`，默认以 cmdlet 名称记录更改原因且不输出结果。所有项目命令支持 `-TimeoutSeconds`（默认 60）。项目关闭后连接失效，必须重新连接；模块不会自动重试写请求。超时或取消不能回滚已经开始执行的修改，可查询项目确认最终状态。

`-ChangeReason` 可用于记录更详细的更新原因。此原因会在用户的UI上显示，也可以在历史记录里查询到。如果不指定此参数，将使用默认的预设值。

`Get-ProjectHistory` 返回当前快照状态和完整历史节点列表。历史节点包含前后继快照、保存时间、变更原因和操作者。Undo、Redo 和 Restore 只切换当前 GUI 项目的历史状态，不会隐式调用 `Save-Project`；Redo 遇到多个分支时选择保存时间最新的后继。

## 迁移

| 原接口 | 新接口 |
| --- | --- |
| 进程内运行空间、`$page`、`InnerClip` | 自行启动 pwsh、Import-Module、Connect-ProjectFrameCut；查询返回数据快照 |
| `Id`（片段）、`Track` | `ClipId`、`TrackId` |
| `StartX`、时间线 `Width` | `StartFrame`、`DurationFrames`；旧像素值需按当时缩放比例转换 |
| `SourceStart` | `SourceStartFrame` |
| `TargetWidth/TargetHeight/TargetX/TargetY` | `WidthPixels/HeightPixels/XPixels/YPixels` |
| `Get-EffectProviderTypes/Field` | `Get-ProjectEffectProviderType/Field` |
| `Get-TextStyleField` | `Get-ProjectTextStyleField` |
| `Disabled` | `Enabled`（布尔值） |
| `BindedInputId/BindedOutputId` | `InputProviderId/IsFinalOutput` |
| `Force` | 标准 PowerShell `-Confirm:$false`，不绕过 `-WhatIf` |
| `Get-EnvironmentInfo` | `Get-ProjectTextStyle` 与 `Get-ProjectEffectProviderType` |
