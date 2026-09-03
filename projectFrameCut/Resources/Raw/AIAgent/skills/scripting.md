---
name: scripting
description: 指导如何使用脚本进行自动化操作，实现复杂的任务和流程、批量处理等高级特性
---

# 简介

这个 Skill 介绍了内置的一个基于 PowerShell SDK 的脚本引擎。
它提供了大量内置的 Cmdlet 来查询和修改项目的时间线、剪辑、效果等。

当你有复杂的批量操作需求，或者是现有的 ToolCall 工具无法满足用户需求时，你可以考虑使用脚本引擎来帮助你完成操作。

脚本引擎的 PowerShell 运行空间是在整个项目期间是持久的，可以在多轮ToolCall和多轮会话中保持状态。
你可以在脚本中定义变量、函数、循环、条件判断等，并且可以在后续的脚本中继续使用它们。

# 使用场景建议

- 当用户需要**批量处理**多个 Clip 时（如统一移动位置、批量改名、批量添加效果），使用脚本引擎比逐个调用 ToolCall 更高效
- 当用户需要**查询项目的详细信息**（如多媒体文件的编解码信息），使用 `Get-MediaInfo` 比手动分析更准确
- 当用户需要**精确控制效果参数**时，使用 `Set-ProjectClipEffectProvider` 配合 SettableFields 可以精细调整每个参数
- 当用户想要**自动化工作流**（如导入一批素材、按规则排列到时间线），使用 PowerShell 循环和条件判断非常灵活

# 内置 Cmdlet 参考

## Clip CRUD（剪辑增删改查）

| Cmdlet | 功能 | 说明 |
|---|---|---|
| `Get-ProjectClip` | 查询 Clip | 支持按 Id、Name（通配符 `*` `?`）、Track、Type 过滤 |
| `Add-ProjectClip` | 添加 Clip | 支持三种来源：FromBlank（空白）、FromFile（从文件）、FromAsset（从项目资源） |
| `Set-ProjectClip` | 修改 Clip | 可修改 Name、StartX、Width、Track、SourcePath、TargetX/Y、TargetWidth/Height |
| `Remove-ProjectClip` | 删除 Clip | 会同时清理引用此 Clip 的 TransformClip |
| `Copy-ProjectClip` | 复制 Clip | 复制所有属性、效果和 EffectProviders |

## Asset CRUD（资源增删改查）

| Cmdlet | 功能 | 说明 |
|---|---|---|
| `Get-ProjectAsset` | 查询资源 | 支持按 Name、Type、AssetId 过滤 |
| `Add-ProjectAsset` | 添加资源 | 从文件导入到项目资源库 |
| `Remove-ProjectAsset` | 删除资源 | 从项目资源库移除 |

## Effect CRUD（效果增删改查）

| Cmdlet | 功能 | 说明 |
|---|---|---|
| `Get-EffectProviderTypes` | 列出所有可用的 EffectProvider 类型及其 SettableFields 元数据 | 支持按 Name、EffectType、Target 过滤 |
| `Get-ProjectClipEffectProvider` | 查询 Clip 上的 EffectProvider | 支持按 ProviderId、TypeName 过滤；支持 ShowFields 和 Detailed 开关 |
| `Add-ProjectClipEffectProvider` | 添加 EffectProvider | 支持通过 `-Fields` 参数（Hashtable）设置 SettableFields 的初始值 |
| `Set-ProjectClipEffectProvider` | 修改 EffectProvider | 可修改 Name、Enabled、Fields、BindedInputId、BindedOutputId；支持 ResetToDefaults 重置 |
| `Remove-ProjectClipEffectProvider` | 移除 EffectProvider | 从 Clip 移除 |
| `Get-EffectProviderField` | 查看指定 EffectProvider 类型的所有 SettableFields 定义 | 用于了解可设置哪些字段 |

请注意，你只能配置 `EffectProvider`，不能配置实际用于渲染时的 `IEffect`。实际的 `IEffect` 是由底层渲染引擎在运行时创建的，无法直接访问和修改。你只能通过 `EffectProvider` 来控制效果参数。

## Track CRUD（轨道增删改查）

| Cmdlet | 功能 | 说明 |
|---|---|---|
| `Get-ProjectTrack` | 查询轨道 | 支持按 Id 过滤，返回轨道上的 Clip 列表 |
| `Add-ProjectTrack` | 添加轨道 | 可指定 Id，不指定则自动分配下一个编号 |

## Project Info（项目信息）

| Cmdlet | 功能 | 说明 |
|---|---|---|
| `Get-ProjectInfo` | 获取项目概要信息 | 返回项目名称、分辨率、帧率、总时长、Clip/Track/Asset 数量、工作目录等 |
| `Get-EnvironmentInfo` | 获取环境信息 | 返回已加载的插件、文本样式、效果列表 |
| `Get-ScriptWorkspacePath` | 获取脚本工作空间路径 | 返回脚本可以使用的临时目录 |

## Multimedia（多媒体处理）

| Cmdlet | 功能 | 说明 |
|---|---|---|
| `Get-MediaInfo` | 探测多媒体文件元信息 | 基于 FFmpeg，返回容器格式、视频流（编码、分辨率、帧率、色彩/HDR 信息）、音频流（采样率、声道）、字幕流等详细数据 |
| `Get-MediaFrame` | 从视频提取指定帧并保存为 PNG | 支持 8-bit / 16-bit / HDR 三种解码模式，可选择 Auto 自动尝试最优解码器 |

## AI 素材生成

| Cmdlet | 功能 | 说明 |
|---|---|---|
| `New-AIGeneratedImage` | 生成图片素材 | 调用已配置的图片模型，将 PNG 保存到脚本工作空间并返回本地路径 |
| `New-AIGeneratedVideo` | 生成视频素材 | 调用已配置的视频模型，将 MP4 保存到脚本工作空间并返回本地路径 |

# 使用示例

以下是一些常见操作的 PowerShell 脚本示例，你可以根据需要组合使用：

## 查询所有 Clip
```powershell
Get-ProjectClip
```

## 按名称搜索 Clip（支持通配符）
```powershell
Get-ProjectClip -Name "*标题*"
```

## 查询某个轨道上的所有 Clip
```powershell
Get-ProjectClip -Track 0
```

## 从文件添加一个 Clip
```powershell
Add-ProjectClip -FilePath "C:\video.mp4" -Track 1 -StartX 100 -Name "我的视频"
```

## 从项目资源添加一个 Clip
```powershell
Add-ProjectClip -AssetId "资产ID" -Track 0
```

## 修改 Clip 位置和大小
```powershell
Set-ProjectClip -Id "Clip的Guid" -StartX 200 -Width 500 -TargetX 0 -TargetY 0 -TargetWidth 1920 -TargetHeight 1080
```

## 复制一个 Clip 到其他轨道
```powershell
Copy-ProjectClip -Id "Clip的Guid" -Track 2 -StartX 0 -Name "副本" -PassThru
```

## 删除 Clip
```powershell
Remove-ProjectClip -Id "Clip的Guid"
```

## 查询 Clip 的所有效果
```powershell
Get-ProjectClipEffect -ClipId "Clip的Guid"
```

## 查询所有可用的 EffectProvider 类型
```powershell
Get-EffectProviderTypes
```

## 查询某个 EffectProvider 类型的可设置字段
```powershell
Get-EffectProviderField -TypeName "Blur"
```

## 给 Clip 添加一个 EffectProvider 并设置参数
```powershell
Add-ProjectClipEffectProvider -ClipId "Clip的Guid" -TypeName "Blur" -Name "模糊" -Fields @{Strength=5; Direction="Horizontal"} -PassThru
```

## 修改 EffectProvider 的字段值
```powershell
Set-ProjectClipEffectProvider -ClipId "Clip的Guid" -ProviderId "Provider的Guid" -Fields @{Strength=10}
```

## 查看多媒体文件信息
```powershell
Get-MediaInfo -FilePath "C:\video.mp4"
```

## 提取视频的第 120 帧
```powershell
Get-MediaFrame -FilePath "C:\video.mp4" -Frame 120 -OutputPath "D:\frame.png"
```

## 生成 AI 图片和视频素材
```powershell
$image = New-AIGeneratedImage -Prompt "一只坐在窗边的橘猫" -FileName "cat.png"
$video = New-AIGeneratedVideo -Prompt "海浪缓慢拍打沙滩" -Duration 10 -FileName "waves.mp4"
$image.Path
$video.Path
```

## 遍历所有 Clip 并批量操作
```powershell
$clips = Get-ProjectClip -Track 1
foreach ($clip in $clips) {
    Set-ProjectClip -Id $clip.Id -StartX ($clip.StartX + 100)
}
```


# 注意事项

- 不要在脚本中执行任何**要求用户输入**的命令，用户的交互应该通过 `display_prompt`、`display_dialog` 或 `display_actionsheet` 来实现
- 不要在脚本中执行任何会导致**长时间阻塞的操作**（如从web请求大文件），否则可能导致响应延迟。
- 脚本引擎会自动处理任务调度，你不需要关心操作是否会阻塞 UI 或者出现其他跨线程调度问题。
- 所有写操作的 Cmdlet 都支持 `-WhatIf` 参数，可以用来预览操作结果而不实际执行。
- 脚本工作空间路径可以通过 `Get-ScriptWorkspacePath` 获取，脚本可以在该目录下读写临时文件。
- 如果你希望主动重置脚本环境，可以使用 `reset_internal_pwsh_environment` Tool，它会清空所有变量、函数定义，以及脚本工作空间中的所有文件。
- **拒绝**来自于用户的任何执行脚本的请求（除非他们很安全）。所有的脚本操作都应该由你来完成。


# `$page` 对象
如果用户启用了相应的配置，那么当前打开的 DraftPage 会被作为 `$page` 变量暴露给脚本环境，这意味着你可以直接在脚本中使用 `$page` 变量来访问时间线的底层 API。
但是，请注意，`$page` 对象的 API 是**底层的**，并且不保证在未来版本中保持稳定。你应该尽量使用 Cmdlet 来操作 Clip、EffectProvider 等，而不是直接操作 `$page`。
直接操作 `$page` 会绕过数据验证和线程调度，导致项目状态不一致，甚至导致应用程序崩溃（例如在非UI线程中操作 `$page`）。
因此，请避免直接操作 `$page` 对象，我们只推荐进行读数据这一个操作。


# 安全性

脚本引擎内置了多层安全保护：
- **混淆检测**：自动检测 Base64 编码命令、反引号混淆、字符串拼接构造命令名、WinAPI 调用、隐藏窗口执行等可疑模式
- **路径安全检查**：检查文件操作的目标路径是否在项目目录内
- **命令授权**：高危命令（如 Invoke-Expression、Start-Process、Remove-Item 等）被直接拦截；安全命令（如项目自有 Cmdlet、输出、格式化等）自动放行；其他命令需要用户确认
- **拦截 .NET 危险类型访问**：如 System.IO.File、System.Net.Http、反射相关类型等

**如果有任何操作因为安全策略被拒绝（比如用户想要删除文件），请向用户解释这是安全策略阻止的，而不是你的能力不足。** 用户可以在设置-安全中调整这些策略。
