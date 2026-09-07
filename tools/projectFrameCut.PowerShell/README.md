# projectFrameCut.PowerShell

PowerShell 7.6.5+（Core）/ .NET 10 模块。只操作授权窗口中已打开的本地 GUI 项目。

## 打包和导入

```powershell
dotnet publish projectFrameCut.PowerShell.csproj -c Release -o ./artifacts/projectFrameCut.PowerShell
Import-Module ./artifacts/projectFrameCut.PowerShell/projectFrameCut.PowerShell.psd1
Get-Command -Module projectFrameCut.PowerShell
```

分发整个输出目录，保留 Contracts、RpcClient 和 protobuf 依赖。调用方自行安装 PowerShell；不需要安装模块到系统目录，也不会自动修改 Profile。

## 连接

在目标窗口打开项目，然后请求授权：

```powershell
Connect-ProjectFrameCut -Name 'Batch editor' -Author 'Me' -Purpose '批量编辑当前项目'
```

默认执行 `pjfc rpc_request --wait`。可选 `-ExecutablePath`、`-RequestDirectory`、`-TimeoutSeconds`（默认 300）。请求目录必须与目标应用配置一致。批准请求的窗口即为连接目标。

也可以使用目标项目窗口生成的管道 ID：

```powershell
Connect-ProjectFrameCut -PipeId $pipeId
# 或使用 rpc_request 返回的信息：
Connect-ProjectFrameCut -PipeName $connection.PipeName -Token $connection.Token
```

`PipeId` 是 64 位十六进制令牌，对应 `projectFrameCut-rpc-<PipeId>`。不要公开令牌。每个运行空间拥有一个当前连接；新连接验证失败会保留原连接。

```powershell
Get-ProjectInfo
$track = Add-ProjectTrack -PassThru
$asset = Add-ProjectAsset -FilePath ./input.mp4 -Name '素材' -PassThru
$clip = Add-ProjectClip -TrackId $track.TrackId -AssetId $asset.AssetId -StartFrame 0 -DurationFrames 120 -PassThru
Set-ProjectClip -ClipId $clip.ClipId -Name '开场' -StartFrame 30
Get-ProjectClip -TrackId $track.TrackId | Remove-ProjectClip -WhatIf
Get-ProjectEffectProviderType
Get-ProjectTextStyle
Save-Project
Disconnect-ProjectFrameCut
```

时间参数均为项目帧；尺寸和位置分别用 `WidthPixels/HeightPixels/XPixels/YPixels`。查询名称按不区分大小写的子串匹配。动态字段用 `-Fields @{ FieldId = Value }`，先用 `Get-ProjectEffectProviderField -TypeName ...` 或 `Get-ProjectTextStyleField -StyleId ...` 查询字段。

`Add-ProjectClip` 必须指定已有 `TrackId`，可选 FilePath、AssetId，或不指定来源创建空片段；默认起点 0，空片段默认长度 300 帧。文件路径相对 PowerShell 当前文件系统位置解析；素材导入复制到项目 assets 目录，移除素材记录不会删除源文件。复制片段默认放到源片段末尾，使用独立的效果实例。

写命令支持 `-WhatIf/-Confirm` 和 `-PassThru`，默认不输出结果。所有项目命令支持 `-TimeoutSeconds`（默认 60）。项目关闭后连接失效，必须重新连接；模块不会自动重试写请求。超时或取消不能回滚已经开始执行的修改，可查询项目确认最终状态。

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
