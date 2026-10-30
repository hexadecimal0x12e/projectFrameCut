# 动画与动态图文创作

先确定场景时长、对象层级、入场、停留和退场时刻，再建立可编辑对象。需要照片或复杂插画时使用现有图片/视频素材；几何图形、信息卡片、Logo 和流程演示优先使用矢量组件与文本 Clip。

以下代码在已连接会话中执行，沿用 [会话与交付](session-and-delivery.md) 的 `$info/$fps/F`。

## 选择动画对象

| 需求 | 对象与操作 |
| --- | --- |
| 多个形状共用一张画布 | `Add-ProjectVectorCanvasClip`，再增删改组件 |
| 单个图形独立运动 | `Add-ProjectVectorComponentClip`，添加 Clip 位置关键帧 |
| 可编辑的组合图形 | `ComponentGroup` 和 `ParentComponentId`，静态参数控制组内布局 |
| SVG Logo | `Add-ProjectVectorComponentClip -FilePath './logo.svg'` |
| 标题、数字、说明 | `Add-ProjectTextClip`，查询文字参数和效果字段后动画化 |
| 多镜头动画短片 | 每个场景按剪辑表安排 Clip，再设置转场和声音 |

`Set-ProjectVectorComponent -Parameters` 修改的是静态内容，不会自动生成随时间变化的动画。整段位移、缩放和旋转可使用 `ProgressPlacer`；原生矢量内容变化需要支持 `NativeContent` 管线的效果或当前可用的参数绑定。

## 创建矢量内容

```powershell
Get-ProjectVectorComponentType
(Get-ProjectVectorComponentType -TypeName RoundedRectangle).DefaultParameters
$track = Add-ProjectSubTrack -PassThru
$duration = F 3
$shape = Add-ProjectVectorComponentClip -TrackId $track.TrackId `
    -TypeName RoundedRectangle -Name '卖点卡片' -StartFrame 0 -DurationFrames $duration `
    -Parameters @{ Width = 0.5; Height = 0.2; CornerRadius = 0.02; FillR = 5000; FillG = 20000; FillB = 65535 } `
    -PassThru
Get-ProjectVectorComponent -ClipId $shape.ClipId
Get-ProjectClipGeometry -ClipId $shape.ClipId
```

组件字段以 `DefaultParameters` 为准。常用形状的尺寸和位置按画布比例，RGB 为 0～65535、Alpha 为 0～1，组件 `Rotation` 使用弧度；Clip 几何的 `RotationDegrees` 及 `ProgressPlacer` 的旋转使用角度。

画布可通过 `-Components` 接受 `TypeName/Name/Parameters` 的 Hashtable 数组；分组使用 `ComponentGroup`，嵌套定义放在 `Children`，后续通过组件命令编辑，不能写入 `Parameters.Children`。

组件剪辑会按内容计算视口。移动组件或修改尺寸可能改变边界；需要固定构图时用 `Set-ProjectVectorClip -Viewport @(x,y,width,height)`，编辑组件时可传 `-UpdateBounds $false`。自动重算使用 `-UpdateBounds`，不能同时指定 `Viewport`。显示位置和大小仍通过 `Set-ProjectClipGeometry` 设置。

## 添加位置关键帧

先查询 provider，再创建并查询 step 的 `DefaultFields`。关键帧字段与普通 provider 字段不同，不把整串 JSON 塞进猜测的参数。

```powershell
Get-ProjectEffectProviderType -Name ProgressPlacer
Get-ProjectEffectProviderField -TypeName ProgressPlacer
$motion = Add-ProjectClipEffectProvider -ClipId $shape.ClipId -TypeName ProgressPlacer `
    -ChangeReason '添加卡片入场和退场动画' -PassThru
$steps = Get-ProjectClipEffectStep -ClipId $shape.ClipId -ProviderId $motion.ProviderId
$steps.DefaultFields
```

新建 provider 的 step 列表应先读回；修改既有动画时编辑现有 step，避免重复添加。下面用四个 step 实现从左侧进入、停留、向右退出，尺寸取刚创建的图形：

```powershell
$geometry = Get-ProjectClipGeometry -ClipId $shape.ClipId
$fields = @{
    Progress = 0
    TargetX = -$geometry.WidthPixels
    TargetY = [int](($info.HeightPixels - $geometry.HeightPixels) / 2)
    TargetWidth = $geometry.WidthPixels
    TargetHeight = $geometry.HeightPixels
    Rotation = 0
    IsDelta = $false
}
Add-ProjectClipEffectStep -ClipId $shape.ClipId -ProviderId $motion.ProviderId -Fields $fields
$fields.Progress = 0.2
$fields.TargetX = [int](($info.WidthPixels - $geometry.WidthPixels) / 2)
Add-ProjectClipEffectStep -ClipId $shape.ClipId -ProviderId $motion.ProviderId -Fields $fields
$fields.Progress = 0.8
Add-ProjectClipEffectStep -ClipId $shape.ClipId -ProviderId $motion.ProviderId -Fields $fields
$fields.Progress = ($duration - 1) / [double]$duration
$fields.TargetX = $info.WidthPixels
Add-ProjectClipEffectStep -ClipId $shape.ClipId -ProviderId $motion.ProviderId -Fields $fields
Get-ProjectClipEffectStep -ClipId $shape.ClipId -ProviderId $motion.ProviderId
```

`Progress` 为 0～1；上例适用于足够长的正常速度 Clip。当前位置动画进度按已过帧数除以有效时长计算，最后有效帧小于 1，所以把退场终点放到最后有效帧。缩放与旋转也应同时明确宽高、位置及 `IsDelta`，避免 step 默认的 1920×1080 尺寸覆盖实际对象。

修改关键帧进度会重新排序，后续 `StepIndex` 从最新返回的 `Steps` 中读取；不能继续使用旧索引。需要缓动时先检查当前 provider 是否提供相关字段，或用合理的中间 step 塑造速度，不虚构 `Easing` 参数。

## 绑定图与内容动画

需要文字参数驱动、程序化变化或原生矢量效果时，先查实际字段和兼容来源：

```powershell
Get-ProjectClipArgument -ClipId $shape.ClipId
Get-ProjectClipEffectGraph -ClipId $shape.ClipId
Get-ProjectClipEffectBindingSource -ClipId $shape.ClipId -FieldId TargetX
```

按效果支持的管线选择 `Picture/NativeContent`。需要手动建图时添加 provider 使用 `-NoAutoConnect`，随后连接输入并设置最终输出；多输出节点明确指定输出字段。每次完成一段连线后检查图的 `Diagnostics` 和实际画布。

静态 `Set-ProjectClipGeometry` 会清除对应 Clip 参数覆盖和绑定；静态字段写入也会解除对应字段绑定。先建立基础排版，再添加动画绑定，后续调整时检查是否影响已有动画。

## 动画验收

优先使用 `Export-ProjectFrameSequence` 导出运动区间的连续画布帧，覆盖起点、入场结束、停留中点、退场开始、最后有效帧和相邻剪口。单 Clip PNG 不包含位置和旋转，不能用于验收整段运动。

动态图文同时检查标题和底板是否同步、停留是否足够阅读、透明区域和图层是否正确。连续帧用于检查运动和视觉时序，音效与音画同步需单独试听或播放；用户要求成片时，按[成片交付流程](session-and-delivery.md#导出成片)通过 GUI 完成渲染，再播放最终视频验收。
