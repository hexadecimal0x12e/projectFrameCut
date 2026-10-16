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

## 矢量剪辑与组件

`VectorContentClip.cs` 中的可编辑画布类型是 `VectorCanvasClip`。画布剪辑默认使用项目尺寸，可包含多个组件；`VectorComponentClip` 默认按组件内容计算视口和显示边界。

| 命令 | 用途 |
| --- | --- |
| `Get-ProjectVectorComponentType [-TypeName ...] [-Name ...]` | 查询可用组件类型及 `DefaultParameters`，包含插件组件和 `ComponentGroup` |
| `Get-ProjectVectorClip [-ClipId ...] [-TrackId ...] [-Name ...]` | 查询矢量剪辑、画布、视口和组件快照 |
| `Add-ProjectVectorCanvasClip -TrackId ... [-Components ...]` | 创建空画布或带初始组件的画布 |
| `Add-ProjectVectorComponentClip -TrackId ... -TypeName ... [-Parameters ...]` | 创建独立组件剪辑 |
| `Add-ProjectVectorComponentClip -TrackId ... -FilePath ...` | 从 SVG 或组件 JSON 创建组件剪辑 |
| `Set-ProjectVectorClip -ClipId ...` | 设置名称、轨道、时间、显示位置尺寸、旋转、抗锯齿，以及组件剪辑的画布和视口 |
| `Get-ProjectVectorComponent -ClipId ... [-ComponentId ...] [-ParentComponentId ...] [-TypeName ...]` | 递归查询组件，返回所属剪辑、父组和组件 ID |
| `Add-ProjectVectorComponent -ClipId ... -TypeName ... [-Parameters ...] [-ParentComponentId ...] [-Index ...]` | 添加组件或分组；省略父组时添加到根列表 |
| `Set-ProjectVectorComponent -ClipId ... -ComponentId ... [-Name ...] [-Parameters ...] [-Index ...] [-ResetToDefaults]` | 修改组件参数、名称和同级顺序，或恢复默认参数 |
| `Remove-ProjectVectorComponent -ClipId ... -ComponentId ...` | 删除组件；删除分组时同时删除组内组件 |

创建剪辑必须指定已有 `TrackId`，默认从第 0 帧开始、持续 300 帧。创建和设置剪辑都支持 `Name/StartFrame/DurationFrames/SourceStartFrame/WidthPixels/HeightPixels/XPixels/YPixels/RotationDegrees/AntiAliasMode`。`AntiAliasMode` 可选 `Default/None/SSAA2x/SSAA4x/SSAA8x`。

组件剪辑另支持 `CanvasWidthPixels/CanvasHeightPixels/SourceLengthFrames/Viewport`。`Viewport` 为 `@(x, y, width, height)`，宽高必须为正；`SourceLengthFrames` 是源内容长度，独立于时间线上的剪辑长度。`Set-ProjectVectorClip -UpdateBounds` 根据组件重新计算视口，不能同时指定 `Viewport`。修改组件时默认更新组件剪辑的边界，可通过 `-UpdateBounds $false` 保留当前视口；画布尺寸由剪辑自身的宽高控制。

```powershell
Get-ProjectVectorComponentType
(Get-ProjectVectorComponentType -TypeName Rectangle).DefaultParameters

$track = Add-ProjectTrack -PassThru
$canvas = Add-ProjectVectorCanvasClip -TrackId $track.TrackId -Name '矢量画布' `
  -DurationFrames 120 -Components @(
    @{ TypeName = 'Rectangle'; Name = '背景'; Parameters = @{ Width = 0.6; Height = 0.4; FillB = 65535 } }
  ) -PassThru

$group = Add-ProjectVectorComponent -ClipId $canvas.ClipId -TypeName ComponentGroup -Name '图形组' -PassThru
$ellipse = Add-ProjectVectorComponent -ClipId $canvas.ClipId -ParentComponentId $group.ComponentId `
  -TypeName Ellipse -Parameters @{ RadiusX = 0.1; RadiusY = 0.1; FillR = 65535 } -PassThru
Set-ProjectVectorComponent -ClipId $canvas.ClipId -ComponentId $ellipse.ComponentId `
  -Parameters @{ RelativeX = 0.3; FillA = 0.8 }
Set-ProjectVectorComponent -ClipId $canvas.ClipId -ComponentId $group.ComponentId `
  -Parameters @{ Rotation = 0.3 }
Get-ProjectVectorComponent -ClipId $canvas.ClipId

$shape = Add-ProjectVectorComponentClip -TrackId $track.TrackId -TypeName RoundedRectangle `
  -Name '圆角矩形' -Parameters @{ Width = 0.25; Height = 0.15; CornerRadius = 0.02 } -PassThru
Set-ProjectVectorClip -ClipId $shape.ClipId -StartFrame 120 -DurationFrames 90 `
  -XPixels 100 -YPixels 80 -WidthPixels 480 -HeightPixels 270 -RotationDegrees 15 -AntiAliasMode SSAA4x
Get-ProjectVectorClip -ClipId $shape.ClipId

Add-ProjectVectorComponentClip -TrackId $track.TrackId -FilePath './logo.svg' -StartFrame 210
Save-Project
```

`-Components` 接受 Hashtable 数组，每项包含 `TypeName`、可选 `Name/Parameters`；`ComponentGroup` 还可通过 `Children` 数组嵌套定义子组件。组件参数名不区分大小写，未指定的参数保留原值；`ResetToDefaults` 保留组件 ID、名称和分组内的子组件。分组子项通过组件命令编辑，不能直接写入 `Parameters.Children`。`Index` 从 0 开始，调整同级顺序及图层索引。查询结果的 `ParentComponentId` 在根组件上为 null；用 `-ParentComponentId ([guid]::Empty)` 可只查询根组件。

常用形状的坐标和尺寸是相对画布的比例，颜色通道为 0–65535，透明度为 0–1。组件参数 `Rotation` 使用弧度，剪辑的 `RotationDegrees` 使用角度。具体参数以类型查询结果为准。文件导入接受 SVG、单个组件 JSON 或组件数组；保存的完整时间线剪辑不是组件定义。由时间线剪辑合并得到的矢量组允许调整已有组件参数，不支持通过这些命令增删其组件结构。

## Clip 效果绑定图

绑定图命令与草稿效果编辑器共用 provider、字段类型解析、分组和跨 Clip 端点模型。`Get-ProjectClipEffectGraph` 返回节点、输入绑定、各管线的最终输出、分组及诊断；`-AsMermaid` 返回图文本。provider 快照也包含 `InFields`、`OutFields`、`AnchorsBindingState`、`MetaData` 和 `Pipeline`，多输出节点必须选择具体输出字段。

| 命令 | 用途 |
| --- | --- |
| `Get-ProjectClipEffectGraph -ClipId ... [-AsMermaid]` | 查询完整绑定图及诊断 |
| `Get-ProjectClipEffectBinding -ClipId ... [-ProviderId ...] [-FieldId ...]` | 枚举已连接的输入字段；最终输出见图快照的 `Outputs` |
| `Get-ProjectClipEffectBindingSource -ClipId ... [-ProviderId ...] [-FieldId ...] [-Pipeline ...]` | 枚举来源；指定字段时筛选兼容来源 |
| `Set-ProjectClipEffectBinding -ClipId ... [-ProviderId ...] -FieldId ...` | 设置主输入、普通参数或节点内的输入字段 |
| `Remove-ProjectClipEffectBinding -ClipId ... [-ProviderId ...] -FieldId ...` | 断开输入，恢复字段的静态值 |
| `Clear-ProjectClipEffectBinding -ClipId ... [-ProviderId ...] [-OutputFieldId ...] [-Pipeline ...]` | 断开整图、单节点的全部连线，或指定输出端口的全部出线 |
| `Set-ProjectClipEffectOutput -ClipId ... [-ProviderId ...] [-OutputFieldId ...] [-Pipeline ...]` | 选择最终输出；不指定 provider 时清除该管线的输出 |
| `Get-ProjectClipArgument -ClipId ... [-FieldId ...]` | 查询该 Clip 实际支持的参数及绑定 |
| `Set-ProjectClipArgument -ClipId ... [-Fields ...] [-ResetToDefaults]` | 设置 Clip 参数静态覆盖，或恢复使用 Clip 自身的值 |

`Set-ProjectClipEffectBinding` 的来源四选一：`-SourceId`（查询返回的来源标识）、`-SourceProviderId [-SourceOutputFieldId]`、`-ClipInput` 或 `-BuiltIn Frame/Progress`。绑定和解绑命令省略 `ProviderId` 时操作 Clip 参数节点；节点在首次写入时创建。`Get-ProjectClipArgument` 的 `ProviderId` 在节点尚未创建时为 null。

```powershell
$clipId = $clip.ClipId
$effect = Add-ProjectClipEffectProvider -ClipId $clipId -TypeName Crop -NoAutoConnect -PassThru
Get-ProjectClipEffectBindingSource -ClipId $clipId -ProviderId $effect.ProviderId -FieldId __Input__
Set-ProjectClipEffectBinding -ClipId $clipId -ProviderId $effect.ProviderId -FieldId __Input__ -ClipInput
Set-ProjectClipEffectOutput -ClipId $clipId -ProviderId $effect.ProviderId

# 命名输出也可连接到任意输入字段。
Set-ProjectClipEffectBinding -ClipId $clipId -ProviderId $effect.ProviderId -FieldId '<FieldId>' `
  -SourceProviderId '<SourceProviderId>' -SourceOutputFieldId '<OutputFieldId>'
Remove-ProjectClipEffectBinding -ClipId $clipId -ProviderId $effect.ProviderId -FieldId '<FieldId>'

Get-ProjectClipArgument -ClipId $clipId
Set-ProjectClipArgument -ClipId $clipId -Fields @{ TargetX = 100 }
Set-ProjectClipEffectBinding -ClipId $clipId -FieldId TargetX -BuiltIn Frame
Get-ProjectClipEffectGraph -ClipId $clipId
```

`Add/Set-ProjectClipEffectProvider` 新增 `-Pipeline Picture/NativeContent` 和 `-Metadata @{ ... }`，metadata 值为 null 时删除对应键，可配置实现类型、连续效果和节点位置等 provider 元数据。`Add` 的 `-NoAutoConnect` 保留未连线的节点；默认仍自动接入输出链。静态 `-Fields` 写入会解除对应字段绑定。`Set-ProjectClipArgument -ResetToDefaults` 同时清除静态覆盖和参数绑定。

字段和管线不兼容、循环依赖及无效分组会拒绝写入。允许逐步建立尚未接完的图；使用图快照的 `Diagnostics` 检查剩余问题，图完整后重新生成运行时效果。写命令沿用 `-WhatIf/-Confirm`、`-PassThru`、`-ChangeReason` 和项目历史流程。

### 跨 Clip 值端点

```powershell
$output = Add-ProjectClipValueEndpoint -ClipId $sourceClipId -Name '位置' -ValueType Integer -PassThru
Set-ProjectClipEffectBinding -ClipId $sourceClipId -ProviderId $output.EndpointId -FieldId __Input__ -BuiltIn Frame
Get-ProjectClipValueEndpoint -ClipId $clipId -Available -Pipeline Picture
$input = Add-ProjectClipValueEndpoint -ClipId $clipId -SourceClipId $sourceClipId `
  -SourceEndpointId $output.EndpointId -PassThru
Set-ProjectClipEffectBinding -ClipId $clipId -FieldId TargetX -SourceProviderId $input.EndpointId
Set-ProjectClipValueEndpoint -ClipId $sourceClipId -EndpointId $output.EndpointId -Name '横向位置' -Enabled $true
```

`Get-ProjectClipValueEndpoint` 默认查询当前 Clip 的输入和输出端点，支持 `-EndpointId`、`-Pipeline`；`-Available` 列出当前 Clip 可引用的输出，返回 `Enabled` 和 `Status`。`Add` 指定 `SourceClipId/SourceEndpointId` 时创建输入，否则创建输出；`Set` 可修改名称、启用状态、输出类型、来源或管线。输入类型随来源确定。`Remove-ProjectClipValueEndpoint -ClipId ... -EndpointId ...` 删除端点并清理本地图中的引用。

跨 Clip 来源必须在图层顺序上先于目标 Clip，且使用相同管线；同 Clip 仅允许从原生内容管线向图像管线传递非内容值。端点类型支持 `Numeric/Integer/UnsignedInteger/Long/UnsignedLong/Boolean/String/Color/Size/Position/SizeAndPosition/IPicture/VectorPicture`，内容类型必须匹配管线。源输出改名、禁用、改类型或删除后的状态可通过目标 Clip 的图诊断查询。

### 效果分组与模板

```powershell
$group = Add-ProjectClipEffectGroup -ClipId $clipId -Name '调色' -ProviderIds $providerIds -PassThru
Get-ProjectClipEffectGroup -ClipId $clipId
Set-ProjectClipEffectGroup -ClipId $clipId -GroupId $group.GroupId -Collapsed $false -Ports @(
  @{ ProviderId = $effect.ProviderId; FieldId = '__Input__'; Name = '画面'; IsInput = $true }
)
Export-ProjectClipEffectGroup -ClipId $clipId -GroupId $group.GroupId | Set-Content ./effect-group.json
Import-ProjectClipEffectGroup -ClipId $otherClipId -TemplateJson (Get-Content ./effect-group.json -Raw)
$saved = Save-ProjectClipEffectGroup -ClipId $clipId -GroupId $group.GroupId -PassThru
Get-ProjectEffectGroupTemplate
Import-ProjectClipEffectGroup -ClipId $otherClipId -Hash $saved.Hash
Remove-ProjectClipEffectGroup -ClipId $clipId -GroupId $group.GroupId
Save-Project
```

`Add-ProjectClipEffectGroup` 的 `ProviderIds/ChildGroupIds` 可组合相同父组下的节点和子组。`Ports` 按数组顺序配置公开端口，每项包含 `ProviderId/FieldId/Name/IsInput`；外部连线需要的端口自动补齐。`Add/Set` 支持 `Collapsed/X/Y`，移动已有组会同步移动成员。Clip 参数、跨 Clip 端点和转场不能打包。

`Remove-ProjectClipEffectGroup` 默认解组，加 `-RemoveProviders` 删除组及所有嵌套成员。导出返回可直接保存的 JSON；导入重新生成全部节点和分组 ID，保留内部连线，外部项目连线不导入，可用 `-X/-Y` 放置。`Save-ProjectClipEffectGroup [-ReplaceHash ...]` 保存到应用的效果组库；`Get-ProjectEffectGroupTemplate [-Hash ...]` 返回名称、哈希、JSON 和加载错误。保存到库和导出均不替代 `Save-Project`。

## PNG 帧预览

连接项目后，获取完整画布或单 Clip 在布局、旋转和合成之前的画面：

```powershell
$canvas = Get-ProjectFramePreview -FrameIndex 30
$canvas.Path
$small = Get-ProjectFramePreview -FrameIndex 30 -WidthPixels 960 -HeightPixels 540
$clipFrame = Get-ProjectClipFramePreview -ClipId $clip.ClipId -FrameIndex 30
$clip | Get-ProjectClipFramePreview -FrameIndex 30
```

`FrameIndex` 从 0 开始，始终是项目时间线帧号；单 Clip 请求必须处于该 Clip 的有效时间区间，音频 Clip 不支持图像预览。画布默认使用项目分辨率。单 Clip 保留源图像和图像效果（含原生矢量效果），不应用布局、Clip 旋转、边缘转场或与其他 Clip 的合成。

服务器复用有效预览缓存或生成新预览，再转换为 PNG，保存在项目 `thumbs` 下。返回对象包含 `Path`、`ProjectRelativePath`、`FrameIndex`、`ClipId`、`WidthPixels`、`HeightPixels` 和 `CacheHit`；`CacheHit` 表示复用了 PNG 或源预览缓存。缓存 PNG 沿用预览缓存的 48 小时清理规则；禁止缓存的 Clip 使用会话临时目录，项目会话结束时清理。预览不改变项目内容，无需 `Save-Project`。

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
