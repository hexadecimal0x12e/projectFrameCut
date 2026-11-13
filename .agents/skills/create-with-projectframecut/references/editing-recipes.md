# 剪辑配方

以下片段在 [会话与交付](session-and-delivery.md) 的已连接运行空间中执行，沿用 `$info/$fps/F`。文件名、保留区间和时长是示例，执行前根据实际素材替换；完成后按会话流程预览、保存和交付。

## 剪口播

先根据原声、转写或用户标注选择保留区间，删除失误、重复和过长停顿，保留完整词句及自然呼吸。没有转写时使用可用且获授权的辅助工具，或明确需要人工听校的位置；模块自身不识别语音。

对于新建剪辑，按源入出点重建连续时间线比先铺满再反复删除更直接。下面假设正常速度、源时长已经核对：

```powershell
$track = Add-ProjectTrack -PassThru
$asset = Add-ProjectAsset -FilePath './talk.mp4' -Name '口播原片' -PassThru
$keep = @(
    @{ In = 1.2; Out = 5.6 }
    @{ In = 7.0; Out = 12.3 }
    @{ In = 14.0; Out = 19.0 }
)
$cursor = [uint32]0
$segments = foreach ($cut in $keep) {
    $start = F $cut.In
    $end = F $cut.Out
    if ($end -le $start) { throw '保留区间必须包含至少一帧。' }
    $clip = Add-ProjectClip -TrackId $track.TrackId -AssetId $asset.AssetId `
        -StartFrame $cursor -SourceStartFrame $start -DurationFrames ($end - $start) `
        -ChangeReason '保留口播有效段落' -PassThru
    [pscustomobject]@{
        ClipId = $clip.ClipId
        SourceInFrame = $start
        SourceOutFrame = $end
        TargetInFrame = $cursor
    }
    $cursor += $end - $start
}
$segments | ForEach-Object {
    Get-ProjectClipProperties -ClipId $_.ClipId |
        Select-Object ClipId, SourceDurationFrames, AudioClipId, AudioEnabled, AudioVolume
}
```

保留 `$segments` 作为字幕和补充画面的时间映射。检查每段的源范围未越界、视频原声没有丢失或重复；不要额外再铺一遍同一份声音。

编辑已有且带效果的片段时优先 `Split-ProjectClip -FrameIndex ... -PassThru`，使用其返回的左右 `ClipId` 继续操作，以保留已有内容。拆分参数是绝对时间线帧号。删除废段后显式移动后续片段收紧空隙，不能假定 `Remove-ProjectClip` 会波纹删除。已解绑音轨需要同步剪切和移动；变速段不要套用上面的线性换算。

跳切处优先保证语义和声音自然，可用相关补充画面遮盖明显的动作跳变；只有需要时才添加转场。

### 随剪辑重排字幕

剪辑完成后再导入针对最终时间线的字幕，或按 `$segments` 重映射原始字幕。对每个保留区间，将其帧入出点换成毫秒，与原字幕区间求交；交集 `[a,b)` 映射为：

```text
新起点 = 目标段起点毫秒 + a - 源段入点毫秒
新终点 = 目标段起点毫秒 + b - 源段入点毫秒
```

删除空交集，跨剪口字幕按语义拆开并修正文案，消除四舍五入造成的重叠。字幕条目的时间相对字幕源；下面字幕 Clip 从时间线 0 开始且没有源偏移，所以条目时间就是最终视频时间。

```powershell
$subTrack = Add-ProjectSubTrack -PassThru
$subtitle = Add-ProjectSubtitleClip -TrackId $subTrack.TrackId -StartFrame 0 `
    -DurationFrames $cursor -Cues @(
        @{ StartMilliseconds = 0; EndMilliseconds = 1500; Markdown = '今天介绍一个实用方法。' }
        @{ StartMilliseconds = 1500; EndMilliseconds = 3600; Markdown = '先把准备工作做好。' }
    ) -ChangeReason '添加剪辑后字幕' -PassThru
(Get-ProjectSubtitleClipArgument -ClipId $subtitle.ClipId).Fields
Set-ProjectSubtitleClipArgument -ClipId $subtitle.ClipId -Fields @{
    'SubtitleStyle.FontSize' = 48
    'SubtitleStyle.Alignment' = 'Center'
    'SubtitleStyle.BottomMargin' = 100
} -ChangeReason '设置口播字幕排版'
```

条目起点必须非负、结束晚于开始且互不重叠。已有最终字幕文件时使用 `Add-ProjectSubtitleClip -FilePath './final.srt'`；支持 SRT/VTT/ASS/SSA。`-FitLength` 根据字幕内容适配片段长度，不能代替剪口映射。检查字体实际支持中文、换行和安全区；字幕样式不是单帧可读就足够，还需给观众阅读时间。

## 组合多个素材

先将脚本整理成剪辑表，每行至少有素材、源入出点、目标入点、用途和原声处理。主画面按顺序排列，叠加画面单独放轨；不要把“合成视频”误解为调用一次 `Join-ProjectClip`。

```powershell
$mainTrack = Add-ProjectTrack -PassThru
$a = Add-ProjectAsset -FilePath './scene-a.mp4' -PassThru
$b = Add-ProjectAsset -FilePath './scene-b.mp4' -PassThru
$first = Add-ProjectClip -TrackId $mainTrack.TrackId -AssetId $a.AssetId `
    -StartFrame 0 -SourceStartFrame (F 2) -DurationFrames (F 4) -PassThru
$second = Add-ProjectClip -TrackId $mainTrack.TrackId -AssetId $b.AssetId `
    -StartFrame (F 4) -SourceStartFrame (F 1) -DurationFrames (F 5) -PassThru
```

示例得到 9 秒的正常速度顺接画面。若用户需要原声则保留；只作补充画面时，对有音轨的片段使用 `Set-ProjectClipProperties -AudioEnabled $false`，防止与旁白叠加。配乐和旁白作为各自的音频 Clip 加入，按同一剪辑表裁切、对齐。

```powershell
$musicTrack = Add-ProjectSubTrack -PassThru
$music = Add-ProjectClip -TrackId $musicTrack.TrackId -FilePath './music.wav' `
    -StartFrame 0 -DurationFrames (F 9) -PassThru
Set-ProjectClipProperties -ClipId $music.ClipId -AudioVolume 0.15 -ChangeReason '降低配乐音量'
```

配乐必须覆盖所需源长度；短于片长时选择合适区间或明确拼接方案。`AudioVolume` 是 0～1 的线性音量，不是 dB，也不是自动响度归一化。以实际试听调整，必要时先用可用工具分析峰值与响度。

画幅不一致时，读取源尺寸，按主体选择等比适配或裁切。使用 `Set-ProjectClipGeometry` 设置尺寸和位置；启用裁切时同时给 `-CropEnabled $true`，需要最终显示尺寸时同次传入 `WidthPixels/HeightPixels`。转场按当前可用类型选择：

```powershell
Get-ProjectTransformType
Get-ProjectClipTransform -ClipId $first.ClipId
```

选定类型后调用 `Set-ProjectClipTransform -Side Right -TypeName $transitionType -InputMode TwoInput -DurationFrames ...`，再读回 `OwnerClipId/ResolvedDurationFrames` 并用 `Get-ProjectClipTransformPreview` 检查。双输入转场匹配同层相邻片段，实际长度可能被可用时长限制；不能通过随意叠放素材假定转场已成立。

检查开头、每个接点和最后有效帧；画面总长还可能被多余音频、字幕或辅轨尾部延长。依据 `Get-ProjectInfo.DurationFrames` 和轨道读回结果修正。

## 产品宣传视频

将提供的产品信息转成“吸引注意 → 展示问题或使用场景 → 产品与卖点证明 → 品牌和行动引导”。时长按用户要求分配；未指定时可先采用短片节奏，避免把固定模板强加给所有产品。

| 段落 | 画面与信息 | 实现方式 |
| --- | --- | --- |
| 开场 | 产品主体或使用结果，加一句核心利益 | 产品视频/图片 + 短标题 |
| 卖点 | 每段一个卖点，配对应细节或演示 | 源片段裁切 + 补充画面 + 标注 |
| 收束 | 品牌、产品名、用户指定的行动引导 | Logo/矢量图形 + 文字 + 简短入退场 |

只使用已提供或已核实的卖点、价格、参数和资质；缺少产品实拍时可先制作明确的图文版本。缺失的图片、配音或音乐根据可用且获授权的工具补齐，再导入项目，不假定应用能自动生成。

先铺产品镜头与声音，统一画幅和色调，再建立标题及品牌视觉。文字样式先查询，`StyleId` 从返回值中选择，不固定取第一个：

```powershell
Get-ProjectTextStyle
Get-ProjectTextStyleField -StyleId $styleId
$titleTrack = Add-ProjectSubTrack -PassThru
$title = Add-ProjectTextClip -TrackId $titleTrack.TrackId -StyleId $styleId `
    -Text '用户确认的核心卖点' -StartFrame 0 -DurationFrames (F 3) -PassThru
(Get-ProjectTextClipArgument -ClipId $title.ClipId).Fields
Get-ProjectClipGeometry -ClipId $title.ClipId
```

通过查询到的字段设置字号、字体、颜色和排版，通过 `Set-ProjectClipGeometry` 控制位置和占屏范围。标题少而清楚，背景复杂时增加底板或遮罩；Logo 优先使用用户提供的矢量或透明图片。动态图文和产品图入场按 [动画创作](animation.md) 实现。

调色、模糊、阴影等效果先用 `Get-ProjectEffectProviderType` 和 `Get-ProjectEffectProviderField` 查询，再用 `Add-ProjectClipEffectProvider` 添加。复用包装时可复制 Clip、导入效果组或使用模板，随后分别校正文案、时间和源范围。

最终检查产品没有被字幕遮挡、文案与镜头互相支持、品牌颜色和 Logo 比例正确、结尾信息有足够停留时间。导出后确认开场封面、声音和行动引导实际出现在成片中。
