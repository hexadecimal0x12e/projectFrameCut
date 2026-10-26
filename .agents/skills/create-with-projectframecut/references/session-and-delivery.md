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

下面是连接骨架，编辑命令插入 `try` 中。通过执行工具操作时，可把整个流程写进一个 `.ps1` 并用 `pwsh -NoProfile -File` 执行；分段调用仅在工具确实保留同一运行空间时可用。

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

已有有效授权时使用现有连接方式，无需重新注册：

```powershell
Connect-ProjectFrameCutPersistent -ClientId $clientId -PrivateKey $privateKeyPath -Service rpc
# 或使用已获得的管道信息：
Connect-ProjectFrameCut -PipeId $pipeId
```

管道令牌、私钥及授权响应不写入交付说明。连接失败可能保留原连接；再次写入前仍需检查项目身份。仅在操作对象存在歧义时澄清，不为用户已明确授权的正常编辑重复请求确认。

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
$preview = Get-ProjectFramePreview -FrameIndex 0
$preview.Path
Get-ProjectClip | Sort-Object StartFrame, TrackId |
    Select-Object ClipId, Name, TrackId, StartFrame, DurationFrames, SourceStartFrame
```

打开返回的 PNG 实际检查构图。画布预览包含布局与合成；`Get-ProjectClipFramePreview -ClipId ... -FrameIndex ...` 用于定位单个素材或效果问题，不包含布局、Clip 旋转、边缘转场及其他 Clip 合成。两者的帧号都是项目时间线帧号，单片段请求必须落在其有效区间。

在剪口前一帧、剪口帧和后一帧检查画面；在转场或动画的起点、中点、终点额外抽查。长片按场景抽样，发现问题后加密检查。音频需检查首尾、剪口和混音，不能从 PNG 推断声音质量。

超时、取消或中途异常后，先用 `Get-ProjectClip/Get-ProjectAsset/Get-ProjectHistory` 判断已完成的写入。修改既有内容前可记录历史快照 ID；仅在需要恢复本次操作且没有夹带用户后续修改时使用 `Restore-ProjectHistory`。恢复、撤销和重做之后仍需显式保存。

## 导出成片

用户要求成片时，先 `Save-Project`，再使用同一安装版本的 `pjfc render` 或 `projectFrameCut.StandaloneRender`。模块没有渲染 cmdlet。参数以实际可执行文件的帮助、仓库 `projectFrameCut/CLIProgram.cs` 的 `WriteRenderHelp` 和 `projectFrameCut.StandaloneRender/readme.md` 为准。

下面示例导出 SDR H.264；`$pjfc` 是已核实的可执行文件路径或命令，`$outputPath` 是本次交付的新输出路径。编码器需在该安装中可用，HDR 或透明视频应按要求另选格式。

```powershell
Save-Project -ChangeReason '保存待导出视频'
$info = Get-ProjectInfo
& $pjfc render "-project=$($info.WorkingPath)" "-output=$outputPath" `
    "-output_options=$($info.WidthPixels),$($info.HeightPixels),$($info.FrameRate),AV_PIX_FMT_YUV420P,libx264" `
    '-target=all'
if ($LASTEXITCODE -ne 0) { throw 'projectFrameCut 渲染失败，请检查本次渲染输出。' }
if (!(Test-Path -LiteralPath $outputPath) -or (Get-Item -LiteralPath $outputPath).Length -eq 0) {
    throw '没有生成有效的输出文件。'
}
```

导出后检查时长、尺寸、帧率和音视频流，并实际播放或抽取关键区间验收。缺失编码器、插件或外部来源时根据具体错误处理，不把现有文件或退出码单独当作成片正确的证明。

交付时给出 `.pjfc` 项目目录、要求导出的视频路径、完成的主要内容、保存状态，以及无法核验的事项。只要求项目时交付项目；需要归档或转移时同时检查外部素材和插件依赖。
