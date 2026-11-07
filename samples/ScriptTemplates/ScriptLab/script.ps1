$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$title = [string]$TemplateParameters['Title']
$subtitle = [string]$TemplateParameters['Subtitle']
$seconds = [double]$TemplateParameters['SceneSeconds']
$particleCount = [int]$TemplateParameters['ParticleCount']
$seed = [int]$TemplateParameters['Seed']
$failAfterScene = [int]$TemplateParameters['FailAfterScene']
$bars = @($TemplateParameters['Bars'])
if ($TemplateParameters['UseBatch'] -isnot [bool]) { throw 'UseBatch 必须注入为 Boolean，不能是字符串。' }
$useBatch = $TemplateParameters['UseBatch']
if ([string]::IsNullOrWhiteSpace($title)) { throw 'Title 不能为空。' }
if ($seconds -lt 2 -or $seconds -gt 12) { throw 'SceneSeconds 必须在 2～12 秒之间。' }
if ($particleCount -lt 0 -or $particleCount -gt 80) { throw 'ParticleCount 必须在 0～80 之间。' }
if ($bars.Count -lt 2 -or $bars.Count -gt 8) { throw 'Bars 必须包含 2～8 项。' }
if ($failAfterScene -lt 0 -or $failAfterScene -gt 4) { throw 'FailAfterScene 必须为 0～4，0 表示不故意报错。' }
foreach ($bar in $bars) {
    if ($bar -isnot [System.Collections.IDictionary] -or !$bar.Contains('Label') -or !$bar.Contains('Value')) {
        throw 'Bars 每项必须包含 Label 和 Value，例如 {"Label":"渲染","Value":86}。'
    }
    if ([double]$bar['Value'] -lt 1 -or [double]$bar['Value'] -gt 100) { throw 'Bars.Value 必须在 1～100 之间。' }
}

$hex = [string]$TemplateParameters['Accent']
if ($hex -notmatch '^#[0-9a-fA-F]{6}$') { throw 'Accent 必须为 #RRGGBB。' }
$accent = @(
    [Convert]::ToInt32($hex.Substring(1, 2), 16) * 257
    [Convert]::ToInt32($hex.Substring(3, 2), 16) * 257
    [Convert]::ToInt32($hex.Substring(5, 2), 16) * 257
)

$info = Get-ProjectInfo
$fps = [double]$info.FrameRate
$sx = [double]$info.WidthPixels / 1920
$sy = [double]$info.HeightPixels / 1080
$sceneFrames = [uint32][Math]::Round($seconds * $fps)
$origin = if ($info.DurationFrames -gt 0) { [uint32]($info.DurationFrames + $fps) } else { [uint32]0 }
$random = [Random]::new($seed)
$created = [System.Collections.Generic.List[guid]]::new()
$animated = [System.Collections.Generic.List[guid]]::new()
$tracks = @{}
$labelSlots = @(0, 0, 0, 0)
$style = @(Get-ProjectTextStyle | Where-Object StyleId -eq 'Basic')
if ($style.Count -eq 0) { throw '当前环境缺少内置 Basic 文字样式。' }
$textFields = @(Get-ProjectTextStyleField -StyleId Basic | ForEach-Object FieldId)
foreach ($field in @('FontSize', 'Color', 'LayoutMode', 'WrappingWidth')) {
    if ($field -notin $textFields) { throw "Basic 文字样式缺少字段 $field。" }
}
$null = Get-ProjectEffectProviderField -TypeName ProgressPlacer
$positionFields = @(Get-ProjectEffectProviderField -TypeName ClipPosition | ForEach-Object FieldId)
foreach ($field in @('TargetX', 'TargetY', 'TargetWidth', 'TargetHeight', 'Rotation', 'Opacity', 'IsDelta')) {
    if ($field -notin $positionFields) { throw "ClipPosition 效果缺少字段 $field。" }
}
foreach ($type in @('Rectangle', 'RoundedRectangle', 'Ellipse', 'ComponentGroup')) {
    $null = Get-ProjectVectorComponentType -TypeName $type
}

function Add-Entrance($clip, [double]$x, [double]$y, [double]$width, [double]$height, [double]$offset = 45) {
    $provider = Add-ProjectClipEffectProvider -ClipId $clip.ClipId -TypeName ProgressPlacer -Name '入场 / 停留 / 退场' -Stage Render -PassThru
    foreach ($step in @(
        @{ Progress = 0.0; Y = $y + $offset; Opacity = 0.0 }
        @{ Progress = 0.16; Y = $y; Opacity = 1.0 }
        @{ Progress = 0.82; Y = $y; Opacity = 1.0 }
        @{ Progress = 1.0; Y = $y - $offset; Opacity = 1.0 }
    )) {
        Add-ProjectClipEffectStep -ClipId $clip.ClipId -ProviderId $provider.ProviderId -Fields @{
            Progress = $step.Progress; TargetX = [int][Math]::Round($x * $sx)
            TargetY = [int][Math]::Round($step.Y * $sy)
            TargetWidth = [int][Math]::Round($width * $sx); TargetHeight = [int][Math]::Round($height * $sy)
            Opacity = $step.Opacity; Rotation = 0; IsDelta = $false
        }
    }
    Set-ProjectClipEffectStep -ClipId $clip.ClipId -ProviderId $provider.ProviderId -StepIndex 3 -Fields @{ Opacity = 0.0 }
    $animated.Add($clip.ClipId)
}

function Add-Label([string]$text, [int]$scene, [double]$x, [double]$y, [double]$width, [double]$height,
    [double]$size = 48, [string]$color = '#EAF0FF', [bool]$animate = $true, [int]$delay = 0) {
    $slot = $labelSlots[$scene]++
    $clip = Add-ProjectTextClip -StyleId Basic -Text $text -TrackId $tracks["Text$slot"] `
        -StartFrame ($origin + $scene * $sceneFrames + $delay) -DurationFrames ($sceneFrames - $delay) -Fields @{
            FontSize = $size * $sy; Color = $color; LayoutMode = 'FixedWidth'
            WrappingWidth = [int][Math]::Round($width * $sx); HorizontalAlignment = 'Left'; VerticalAlignment = 'Top'
        } -PassThru
    Set-ProjectClip -ClipId $clip.ClipId -Name "S$($scene + 1) / $($text.Split([char]10)[0])"
    Set-ProjectClipGeometry -ClipId $clip.ClipId -XPixels ([int]($x * $sx)) -YPixels ([int]($y * $sy)) `
        -WidthPixels ([int]($width * $sx)) -HeightPixels ([int]($height * $sy))
    $created.Add($clip.ClipId)
    if ($animate) { Add-Entrance $clip $x $y $width $height }
    return $clip
}

function Add-Panel([string]$name, [int]$scene, [double]$x, [double]$y, [double]$width, [double]$height,
    [int[]]$rgb, [int]$track, [bool]$animate = $false) {
    $clip = Add-ProjectSolidColorClip -TrackId $track -Name $name `
        -StartFrame ($origin + $scene * $sceneFrames) -DurationFrames $sceneFrames `
        -WidthPixels ([int]($width * $sx)) -HeightPixels ([int]($height * $sy)) `
        -Red $rgb[0] -Green $rgb[1] -Blue $rgb[2] -Alpha 1 -PassThru
    Set-ProjectClipGeometry -ClipId $clip.ClipId -XPixels ([int]($x * $sx)) -YPixels ([int]($y * $sy))
    $created.Add($clip.ClipId)
    if ($animate) { Add-Entrance $clip $x $y $width $height 24 }
    return $clip
}

function Assert-Scene([int]$scene) {
    Write-Host "[ScriptLab] 第 $scene / 4 幕配置完成：$($created.Count) 个片段，$($animated.Count) 组动画。"
    if ($failAfterScene -eq $scene) { throw "测试异常：第 $scene 段已配置完成，按 FailAfterScene 参数中止。" }
}

$batchStarted = $false
Write-Host "[ScriptLab] 开始配置项目，Batch=$useBatch，总时长 $($sceneFrames * 4) 帧。"
if ($useBatch) {
    $null = Start-ProjectBatch
    $batchStarted = $true
}
try {
    # 轨道编号越小越靠前；按前景到背景创建，不覆盖或删除已有轨道。
    $names = @(
        for ($i = 0; $i -lt [Math]::Max(10, 4 + 2 * $bars.Count); $i++) { "Text$i" }
        'Accent'
        for ($i = 0; $i -lt $bars.Count; $i++) { "Bar$i" }
        'Card0'; 'Card1'; 'Card2'; 'Decorations'; 'Background'
    )
    foreach ($name in $names) {
        $tracks[$name] = (Add-ProjectTrack -PassThru).TrackId
    }
    for ($scene = 0; $scene -lt 4; $scene++) {
        $null = Add-Panel "S$($scene + 1) / 深色背景" $scene 0 0 1920 1080 @(2300, 3600, 7200) $tracks.Background
        $null = Add-Panel "S$($scene + 1) / 品牌色条" $scene 112 130 10 72 $accent $tracks.Accent
        $null = Add-Label ('SCRIPT LAB   /   {0:00}' -f ($scene + 1)) $scene 150 133 1400 70 32 $hex $false
        $null = Add-Label ('{0:00} / 04   ·   {1} fps   ·   {2} × {3}' -f ($scene + 1), $fps, $info.WidthPixels, $info.HeightPixels) `
            $scene 120 978 1660 55 25 '#8F9EBF' $false
    }

    $opening = Add-Label $title 0 120 285 1660 220 108
    $null = Add-Label $subtitle 0 128 545 1400 160 42 '#A9B9DA'
    $null = Add-Label 'SANDBOX → POWERSHELL → RPC → PROJECT' 0 128 812 1650 70 30 $hex
    $canvas = Add-ProjectVectorCanvasClip -TrackId $tracks.Decorations -Name '片头 / 随机粒子与分组' `
        -WidthPixels $info.WidthPixels -HeightPixels $info.HeightPixels -StartFrame $origin -DurationFrames $sceneFrames -AntiAliasMode SSAA2x -PassThru
    $created.Add($canvas.ClipId)
    $group = Add-ProjectVectorComponent -ClipId $canvas.ClipId -TypeName ComponentGroup -Name '可编辑粒子组' -PassThru
    for ($i = 0; $i -lt $particleCount; $i++) {
        Add-ProjectVectorComponent -ClipId $canvas.ClipId -ParentComponentId $group.ComponentId -TypeName Ellipse -Name "粒子 $($i + 1)" -Parameters @{
            RelativeX = 0.05 + $random.NextDouble() * 0.90; RelativeY = 0.10 + $random.NextDouble() * 0.80
            RadiusX = 0.002 + $random.NextDouble() * 0.006; RadiusY = 0.004 + $random.NextDouble() * 0.010
            FillR = $accent[0]; FillG = $accent[1]; FillB = $accent[2]; FillA = 0.22; StrokeA = 0.0
        }
    }
    Set-ProjectVectorComponent -ClipId $canvas.ClipId -ComponentId $group.ComponentId -Parameters @{ Rotation = 0.08 }
    Add-Entrance $canvas 0 0 1920 1080 18
    Assert-Scene 1

    $null = Add-Label '把 JSON 参数变成动态图表' 1 120 245 1660 130 72
    $null = Add-Label '每根柱条独立入场，标签和值来自 Bars 参数' 1 128 385 1660 80 32 '#A9B9DA'
    $barWidth = [Math]::Floor(1420 / $bars.Count) - 50
    for ($i = 0; $i -lt $bars.Count; $i++) {
        $x = 190 + $i * (1420 / $bars.Count)
        $height = 360 * [double]$bars[$i]['Value'] / 100
        $clip = Add-Panel "柱条 / $($bars[$i]['Label'])" 1 $x (855 - $height) $barWidth $height $accent $tracks["Bar$i"]
        $effect = Add-ProjectClipEffectProvider -ClipId $clip.ClipId -TypeName ProgressPlacer -Name '柱条生长' -Stage Render -PassThru
        foreach ($step in @(
            @{ P = 0.0; H = 2; A = 0.0 }
            @{ P = 0.12 + $i * 0.035; H = 2; A = 1.0 }
            @{ P = 0.40 + $i * 0.035; H = $height; A = 1.0 }
            @{ P = 0.87; H = $height; A = 1.0 }
            @{ P = 1.0; H = 2; A = 0.0 }
        )) {
            Add-ProjectClipEffectStep -ClipId $clip.ClipId -ProviderId $effect.ProviderId -Fields @{
                Progress = $step.P; TargetX = [int]($x * $sx); TargetY = [int]((855 - $step.H) * $sy)
                TargetWidth = [int]($barWidth * $sx); TargetHeight = [Math]::Max(1, [int]($step.H * $sy))
                IsDelta = $false; Opacity = $step.A; Rotation = 0
            }
        }
        $animated.Add($clip.ClipId)
        $null = Add-Label ([string]$bars[$i]['Label']) 1 ($x - 5) 882 ($barWidth + 40) 65 30 '#A9B9DA' $false
        $null = Add-Label ([string]$bars[$i]['Value']) 1 $x (790 - $height) ($barWidth + 30) 70 38 '#EAF0FF' $true ([int]($i * $fps * 0.10))
    }
    Assert-Scene 2

    $null = Add-Label '参数驱动 · 多轨道 · 可编辑' 2 120 245 1660 125 72
    $cardTitles = @('隔离进程', 'RPC 配置', '结构化参数')
    $cardValues = @('PowerShell Core', $(if ($useBatch) { 'Batch 提交' } else { '逐条应用' }), "$($bars.Count) 组数据")
    for ($i = 0; $i -lt 3; $i++) {
        $x = 128 + $i * 560
        $null = Add-Panel "参数卡片 $($i + 1)" 2 $x 450 510 330 @(4900, 6500, 11500) $tracks["Card$i"] $true
        $null = Add-Label $cardTitles[$i] 2 ($x + 36) 490 445 75 32 '#A9B9DA'
        $null = Add-Label $cardValues[$i] 2 ($x + 36) 610 445 100 46 $hex
    }
    $probe = Add-Label "字符串 / Number=$seconds / Integer=$particleCount / Boolean=$useBatch / JSON=$($bars.Count)" `
        2 128 842 1660 80 27 '#A9B9DA' $false
    $opacityProvider = Add-ProjectClipEffectProvider -ClipId $probe.ClipId -TypeName ClipPosition -Name '参数说明渐显' -Stage Render -Fields @{
        TargetX = [int](128 * $sx); TargetY = [int](842 * $sy)
        TargetWidth = [int](1660 * $sx); TargetHeight = [int](80 * $sy)
        Rotation = 0; Opacity = 1; IsDelta = $false
    } -PassThru
    Set-ProjectClipEffectBinding -ClipId $probe.ClipId -ProviderId $opacityProvider.ProviderId -FieldId Opacity -BuiltIn Progress
    $null = Get-ProjectClipEffectGraph -ClipId $probe.ClipId
    Assert-Scene 3

    $null = Add-Label '配置完成，开始检查时间线' 3 120 305 1660 180 88
    $summary = Add-Label '统计生成中…' `
        3 128 535 1580 180 38 '#A9B9DA'
    $outro = Add-ProjectVectorCanvasClip -TrackId $tracks.Decorations -Name '片尾 / 矢量装饰' `
        -WidthPixels $info.WidthPixels -HeightPixels $info.HeightPixels -StartFrame ($origin + 3 * $sceneFrames) -DurationFrames $sceneFrames `
        -Components @(
            @{ TypeName = 'RoundedRectangle'; Name = '装饰面板'; Parameters = @{
                RelativeX = 0.80; RelativeY = 0.54; Width = 0.24; Height = 0.52; CornerRadius = 0.025
                Rotation = 0.12; FillR = $accent[0]; FillG = $accent[1]; FillB = $accent[2]; FillA = 0.10; StrokeA = 0.0
            } }
            @{ TypeName = 'Rectangle'; Name = '装饰横线'; Parameters = @{
                RelativeX = 0.50; RelativeY = 0.78; Width = 0.86; Height = 0.004
                FillR = $accent[0]; FillG = $accent[1]; FillB = $accent[2]; FillA = 0.65; StrokeA = 0.0
            } }
        ) -PassThru
    $created.Add($outro.ClipId)
    Add-Entrance $outro 0 0 1920 1080 24
    Set-ProjectTextClipStyle -ClipId $summary.ClipId -Fields @{
        Text = "本次创建 $($created.Count) 个片段、$($animated.Count) 组关键帧动画。`n试试修改主题色、JSON 数据、随机种子和 Batch 开关。"
    }
    Set-ProjectClip -ClipId $summary.ClipId -Name 'S4 / 生成统计'
    $null = Get-ProjectClip -ClipId $opening.ClipId
    $null = Get-ProjectVectorComponent -ClipId $canvas.ClipId
    $null = Get-ProjectClipEffectBinding -ClipId $probe.ClipId -ProviderId $opacityProvider.ProviderId -FieldId Opacity
    Assert-Scene 4
} catch {
    $failure = $_
    if ($batchStarted) {
        try { $null = Stop-ProjectBatch } catch { Write-Warning '取消 Batch 失败；连接结束后未提交的数据会被清理。' }
    }
    throw $failure
}

# 提交超时不重放编辑循环，也不在异常处理中撤销可能已经提交的批次。
if ($useBatch) {
    Write-Host '[ScriptLab] 正在提交 Batch 并刷新项目…'
    $null = Complete-ProjectBatch -ChangeReason '脚本模板：四幕动态图文实验室' -TimeoutSeconds 600
} else {
    Write-Host '[ScriptLab] 正在保存项目…'
    Save-Project -ChangeReason '脚本模板：四幕动态图文实验室' -TimeoutSeconds 300
}
$result = Get-ProjectInfo
Write-Output "ScriptLab 完成：新增 $($created.Count) 个片段，$($animated.Count) 组动画，起点 $origin，总时长 $($sceneFrames * 4) 帧。当前项目共有 $($result.ClipCount) 个片段。"
