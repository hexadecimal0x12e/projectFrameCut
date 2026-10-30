# 会话与交付

## 导入模块

使用 PowerShell 7.6+ Core，并导入完整发布目录中的 `.psd1`。下面从仓库根目录运行；实际发布目录不同时替换 `$modulePath`。

```powershell
$ErrorActionPreference = 'Stop'
$modulePath = Join-Path (Get-Location).Path 'tools/projectFrameCut.PowerShell/artifacts/projectFrameCut.PowerShell/projectFrameCut.PowerShell.psd1'
if (!(Test-Path -LiteralPath $modulePath)) { throw '请提供已发布的 projectFrameCut.PowerShell 模块路径。' }
Import-Module $modulePath
Get-Command -Module projectFrameCut.PowerShell
```

源码目录中的清单不能代替包含 DLL 和依赖的发布目录。需要准备模块时，先检查 `tools/projectFrameCut.PowerShell/projectFrameCut.PowerShell.csproj` 的实际目标框架；说明文档与当前源码可能不同，不照抄过期的框架版本。查询参数使用 `Get-Command <命令> -Syntax`。

## 选择或创建项目

编辑已打开的项目时直接连接。创建新作品时先离线创建目录，再打开 GUI；离线命令自身不提供时间线编辑会话。

```powershell
Get-Project
$project = New-Project -Name '产品短片' -Width 1080 -Height 1920 -FrameRate 30 -PassThru
Start-Project -Project $project.Path
```

`-ProjectRoot` 可指定项目集合目录；省略时使用应用用户数据目录下的 `My Drafts`。Windows 可用 `Get-ProjectInstance` 查询安装实例，再通过 `Start-Project -Instance` 选择。使用已有项目时保留其画幅和帧率，除非任务需要改变。

## 同一运行空间完成操作

下面是普通编辑的连接骨架，编辑命令插入 `try` 中；大量写入时用下一节的批量流程替换编辑与 `Save-Project` 段。通过执行工具操作时，可把整个流程写进一个 `.ps1` 并用 `pwsh -NoProfile -File` 执行；分段调用仅在工具确实保留同一运行空间时可用。

```powershell
try {
    Connect-ProjectFrameCut -Name 'Video creator' -Author 'Agent' -Purpose '制作用户要求的视频'
    $info = Get-ProjectInfo
    $info | Select-Object SessionId, Name, WorkingPath, FrameRate, WidthPixels, HeightPixels
    $fps = [double]$info.FrameRate
    function F([double]$seconds) {
        [uint32][Math]::Round($seconds * $fps, [MidpointRounding]::AwayFromZero)
    }

    Get-ProjectTrack
    Get-ProjectAsset
    Get-ProjectClip
    # 在此执行所选配方，确认 WorkingPath 对应用户要求的项目后再写入。

    Save-Project -ChangeReason '保存本次视频创作' -PassThru
} finally {
    Disconnect-ProjectFrameCut
}
```

默认连接会运行 `pjfc rpc_request --wait` 并等待目标窗口批准。需要指定安装时传 `-ExecutablePath`，自定义请求目录使用 `-RequestDirectory`。遇到授权等待应告知用户需要批准应用中的请求；不能把等待超时当作授权。

Windows 沙盒中的 Agent 若需要额外管道 ACL，使用运行环境配置或实际进程令牌中的沙盒 SID，执行 `Connect-ProjectFrameCut -Name Agent -Author Agent -Purpose '制作用户要求的视频' -AddSID $sandboxSids`。`-AddSID` 接受字符串数组，逐项转发为 `rpc_request --addSID=<SID>`，并继续完成审批等待和连接。若导入的旧版模块尚无该参数，按 [Windows 沙盒连接](../../projectframecut-powershell/references/gui-projects.md#windows-sandbox-connection) 中的直接 CLI 流程请求并连接。

`--addSID` 仅适用于 Windows 一次性请求，只为本次管道追加读写权限，仍需应用审批和令牌校验。不要猜测 SID 或改授予 Everyone；已创建的管道不会补加权限，需要重新请求。它不解决请求目录的文件访问权限，也不能用于持久客户端请求。

已有有效授权时使用现有连接方式，无需重新注册：

```powershell
Connect-ProjectFrameCutPersistent -ClientId $clientId -PrivateKey $privateKeyPath -Service rpc
# 或使用已获得的管道信息：
Connect-ProjectFrameCut -PipeId $pipeId
```

管道令牌、私钥及授权响应不写入交付说明。连接失败可能保留原连接；再次写入前仍需检查项目身份。仅在操作对象存在歧义时澄清，不为用户已明确授权的正常编辑重复请求确认。

## 批量编辑与提交

批量操作的命令骨架和限制见[PowerShell 批量操作](../../projectframecut-powershell/references/batch-operations.md)。为字幕、场景搭建、动态图形或关键帧循环优先使用批量，避免每条编辑刷新预览并增加历史。

1. 先核对项目、素材内容和参数；在批量外完成 `Add-ProjectAsset`、外部源注册与加载。当前素材 cmdlet 未暴露批量 RPC 所需的 `CopyToProject`，批量中使用已有 `AssetId`。
2. 在当前连接执行 `Start-ProjectBatch`，再运行配方中的相关编辑。查询和 `-PassThru` 读取最新暂存状态，返回的片段、组件和 Provider ID 可继续使用。GUI 显示遮罩，其他 RPC 连接不能编辑。
3. 用一次 `Complete-ProjectBatch -ChangeReason '本阶段的创作意图' -TimeoutSeconds 300` 提交并等待完成。它会统一保存、生成一条历史并刷新 UI/子系统；无需再为同一批更改执行 `Save-Project`。
4. 成功后读回结果，再抽帧、试听或打开渲染页。发现问题时开启下一批修正；不要在未提交批量里运行预览、`Save-Project`、历史恢复或打开渲染页。

编辑错误默认用 `Stop-ProjectBatch` 取消暂存；需要保留成功部分时，先核对它们确实符合创作意图再提交。提交超时仅在同一连接重试 `Complete-ProjectBatch`，不重放配方；按[批量失败处理](../../projectframecut-powershell/references/troubleshooting.md#batch-failures) 限制重试并核实最终状态。若要保留未提交工作，不要先退出运行空间或让 `finally` 断开连接。已保存的批量不会因取消或断开而回滚。

## 时间、素材与图层

- 先统一到项目帧率，再计算长度：`DurationFrames = 出点帧 - 入点帧`，采用左闭右开区间。所有关联画面、音频和字幕共用同一份时间映射。
- `Get-ProjectClipProperties` 的 `SourceDurationFrames` 可检查有限来源长度；无限图片或图形需要显式指定展示时长。变速素材先查看其效果和实际时间线，再剪切。
- 使用 `Add-ProjectAsset -FilePath ... -PassThru` 后保留 `AssetId`；再次使用前可通过 `Get-ProjectAsset` 检查。当前 cmdlet 未暴露 `CopyToProject`，导入方式沿用应用行为，不能承诺一定复制；需要可搬移项目时核对返回的 `Path` 和依赖文件。
- `Add-ProjectClip` 在 `AssetId` 与 `FilePath` 中选择一个。直接引用外部路径时保持源文件可访问；不要覆盖或移动原素材。
- 主画面可放一条主轨，叠加字幕、标题、补充画面和声音分别使用合适的轨道。用 `Add-ProjectTrack/Add-ProjectSubTrack -PassThru` 获取编号；具体遮挡顺序通过画布预览确认。
- `Set-ProjectClipGeometry` 的像素位置和尺寸、裁切矩形需结合源比例计算。等比适配后允许留边，铺满时计算居中裁切；避免通过任意宽高拉伸人物和产品。

## 预览与读回

```powershell
$info = Get-ProjectInfo
$frames = Export-ProjectFrameSequence -StartFrame 0 -FrameCount 30 -WidthPixels 960 -HeightPixels 540 -TimeoutSeconds 300
$frames | Select-Object FrameIndex, Path
Get-ProjectClip | Sort-Object StartFrame, TrackId |
    Select-Object ClipId, Name, TrackId, StartFrame, DurationFrames, SourceStartFrame
```

单帧和批量预览使用 PowerShell 等组件提供的预览接口。示例假定项目至少有 30 帧，实际请求须满足 `StartFrame + FrameCount` 不超过项目总帧数；帧号从 0 开始，范围为左闭右开。短项目可一次导出全部帧，长项目按场景分段导出，先低分辨率检查，再对问题区间提高尺寸。`Export-ProjectFrameSequence` 由渲染后端输出连续 PNG，仅视频，无音频；支持 PowerShell 进度条，返回每帧的 `Path/FrameIndex/WidthPixels/HeightPixels/FrameRate`。必须打开返回的图片实际检查，命令成功不等于画面正确。

只需单帧时使用 `Get-ProjectFramePreview -FrameIndex ...`。画布预览包含布局与合成；`Get-ProjectClipFramePreview -ClipId ... -FrameIndex ...` 用于定位单个素材或效果问题，不包含布局、Clip 旋转、边缘转场及其他 Clip 合成。两者的帧号都是项目时间线帧号，单片段请求必须落在其有效区间。

在剪口前一帧、剪口帧和后一帧检查画面；在转场或动画的起点、中点、终点额外抽查。长片按场景抽样，发现问题后加密检查。音频需检查首尾、剪口和混音，不能从 PNG 推断声音质量。

普通编辑超时、取消或中途异常后，先用 `Get-ProjectClip/Get-ProjectAsset/Get-ProjectHistory` 判断已完成的写入。批量的编辑异常和提交超时按上面的批量流程处理；暂存读回不能证明已保存。修改既有内容前可记录历史快照 ID；仅在需要恢复本次操作且没有夹带用户后续修改时使用 `Restore-ProjectHistory`。恢复、撤销和重做之后仍需显式保存。

## 导出成片

用户要求成片时，先成功完成当前 `Complete-ProjectBatch`；普通编辑则先 `Save-Project`。完成预览后，使用同一安装版本的 `pjfc gui --render {<target>|--continue} [options]`。渲染参数只覆盖本次页面默认值，不修改用户偏好；省略 `--output` 时由用户手动启动，指定 `--output=<file>` 时自动开始全项目渲染，成功后退出 App。`pjfc render` 是内部用途命令，Agent 不使用它进行预览或成片渲染。

下面示例打开已保存项目的渲染页，首行 `Save-Project` 适用于普通未保存编辑，已成功提交批量时省略该行；`$pjfc` 是已核实的可执行文件路径或命令。

```powershell
Save-Project -ChangeReason '保存待导出视频'
$info = Get-ProjectInfo
& $pjfc gui --render $info.WorkingPath
```

可添加 `--width/--height/--fps/--encoder/--bitDepth/--bitRate` 等参数预填页面；参数均使用 `--name=value`，`--bitRate` 的单位为 Mbps，`--fps` 为正整数。自动交付时指定本次实际输出路径：

```powershell
& $pjfc gui --render $info.WorkingPath `
    "--width=$($info.WidthPixels)" "--height=$($info.HeightPixels)" `
    "--fps=$($info.FrameRate)" --encoder=libx264 --bitDepth=8 --bitRate=20 "--output=$outputPath"
```

示例适用于 SDR 项目；HDR 项目使用 `--bitDepth=10` 和支持 HDR 的编码器。`$outputPath` 是已确定的输出文件路径；相对路径按启动工作目录解析，父目录自动创建。默认拒绝覆盖已有文件，确实需要替换时加 `--overwrite=true`。失败或取消不会自动退出，也不会执行用户偏好的结束动作；先检查错误再修正，不把已有文件当作本次渲染成功的证明。

优先传入明确的项目路径；只有确认最近打开的项目就是目标时才使用 `pjfc gui --render --continue`。需要时沿用该项目的 `--userData` 等 GUI 选项，完整参数见[渲染参数](../../projectframecut-powershell/references/gui-projects.md#video-delivery)，以 `pjfc help gui` 或 `projectFrameCut/CLIProgram.cs` 的 `WriteGuiHelp` 为准。仅预填时不代替用户点击开始，也不把 GUI 启动成功当作成片已生成。

手动或自动渲染完成后，核对实际输出路径，检查时长、尺寸、帧率和音视频流，并实际播放或抽取关键区间验收。缺失编码器、插件或外部来源时根据具体错误处理，不把现有文件或退出码单独当作成片正确的证明。

交付时给出 `.pjfc` 项目目录、完成的主要内容、`Save-Project` 或 `Complete-ProjectBatch` 的保存结果，以及无法核验的事项。手动模式下尚未开始渲染时说明需用户启动；自动模式说明实际完成或失败状态，只交付已核实的视频路径。只要求项目时交付项目；需要归档或转移时同时检查外部素材和插件依赖。
