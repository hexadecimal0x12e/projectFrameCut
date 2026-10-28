---
name: create-with-projectframecut
description: 使用 tools/projectFrameCut.PowerShell 操作 projectFrameCut 会话，把素材和创作要求做成可编辑的视频项目或成片。适用于剪口播、按脚本组合多个素材、产品宣传片、字幕包装、矢量动画和动态图文创作；模块开发和单纯查询命令接口优先使用 projectframecut-powershell。
---

# 使用 projectFrameCut 创作视频

通过 `tools/projectFrameCut.PowerShell` 完成实际的素材导入、时间线编辑、文字字幕、转场、效果和动画。用户要求制作视频时，推进到保存项目及所要求的成片交付，不停在脚本、分镜或命令建议上。

## 按任务读取

- 开始操作、创建项目、连接会话、保存或导出：阅读 [会话与交付](references/session-and-delivery.md)。
- 剪口播、字幕重排、多素材合成、产品宣传：阅读 [剪辑配方](references/editing-recipes.md) 中对应部分。
- 矢量图形、Logo 动画、动态图文、关键帧：阅读 [动画创作](references/animation.md)。
- 需要其他命令或排查连接问题时，查阅 [模块说明](../../../tools/projectFrameCut.PowerShell/README.md) 和 [PowerShell 接口 skill](../projectframecut-powershell/SKILL.md)，再以当前命令元数据和源码核实参数。

## 从要求到时间线

1. 从用户要求提取交付规格：主题、受众、平台、横竖比、时长、语言、必须出现的素材与文案，以及交付项目还是视频。沿用已有要求；可推断的细节直接给出合理假设，关键素材或事实缺失时再询问。
2. 读取当前项目和素材，核对实际画面、声音、时长、分辨率及字幕。文件名只能作为线索，不能证明素材内容。需要探测元数据时可使用已安装的 `ffprobe` 等工具，编辑仍通过 projectFrameCut 完成。
3. 将要求变成简短的剪辑表：素材、源入点、源出点、时间线入点、轨道、画面适配、原声、字幕或卖点。简单任务直接使用内存中的表；较复杂任务可随项目保存剪辑表供核对。
4. 先完成主画面和声音，再加字幕、标题、补充画面及动画。同一素材复用已有 `AssetId`，后续操作使用返回的真实 ID。大量关联编辑按场景、字幕或动画阶段使用批量操作：素材导入和外部源注册先完成，再 `Start-ProjectBatch`，最后 `Complete-ProjectBatch -ChangeReason ...` 合成一条已保存的历史。
5. 成功提交当前批量后，在开场、每个剪口和转场附近、字幕密集处、关键动画时刻、结尾抽取画布预览；据实际结果修正遮挡、构图和时序。声音和连续动作还需试听、播放或检查输出视频。
6. 读取修改后的状态，确认批量提交或普通编辑的显式保存成功；提交已保存的同一批更改无需再 `Save-Project`。需要成片时继续渲染和验收。交付实际路径，简述完成内容和仍未核验的项目。

## 操作约定

- 连接只属于当前 PowerShell 运行空间。将连接、编辑、保存、断开放在同一个脚本或确实持久的运行空间中；多个独立 `pwsh -Command` 不共享会话。
- 连接后先检查 `Get-ProjectInfo` 的 `SessionId/Name/WorkingPath`。打开项目、批准 RPC 请求和获得连接是不同步骤；已有连接可用时继续使用。
- Windows 沙盒需要额外管道权限时，在一次性授权连接命令中传入 `Connect-ProjectFrameCut -Name ... -Author ... -Purpose ... -AddSID $sandboxSids`，使用实际沙盒 SID 数组。具体流程及旧版模块的 CLI 用法见[会话与交付](references/session-and-delivery.md)。
- 时间线入点和拆分位置使用项目帧号；源入点和片段长度也使用项目帧率的时间基准。字幕条目使用相对字幕源的毫秒。配方中的直接换算适用于正常速度；变速片段须核对实际时长和源位置映射。
- `DurationFrames` 不能在变速情况下直接当作最终可见时长；不要通过中途修改帧率来迁就素材。
- `Set-ProjectClip` 管理时间和来源，显示几何使用 `Set-ProjectClipGeometry`。字段、样式、转场、组件和效果类型先查询再使用，不猜名称、范围或单位。
- 写命令需要返回对象时加 `-PassThru`；批量中的查询和返回 ID 立即反映暂存数据。GUI 在批量期间显示遮罩，不能预览、单独保存、撤销历史或渲染；完整流程见[批量操作](../projectframecut-powershell/references/batch-operations.md)。
- 按操作意图填写简短的 `-ChangeReason`，批量历史使用提交命令上的原因。普通写操作超时后先读回状态；批量提交超时则保留同一连接，仅重试 `Complete-ProjectBatch`，不能重放编辑循环。错误后默认取消未提交批量，只有成功部分本身符合交付意图时才提交；`Stop-ProjectBatch` 和断开连接都不撤销已保存结果。
- 视频通常带绑定音轨。先查询 `Get-ProjectClipProperties` 的音频状态，再决定静音、调音量或解绑；解绑后的声音需单独维护同步。
- `Join-ProjectClip` 用于分组或矢量合组；普通素材的顺接通过时间线位置实现。主轨、副轨不能混合组合；删除轨道后重新读取编号。
- 当前 PowerShell 模块没有语音识别、自动卡点、生成音乐或直接导出成片的 cmdlet。需要时使用现有且获授权的辅助工具，并明确其结果；不虚构 `Export-ProjectVideo` 等命令。

## 按结果验收

| 任务 | 必须检查的结果 |
| --- | --- |
| 口播 | 语义连贯、无误删词尾、声音同步、字幕随剪辑重排、没有意外空隙 |
| 多素材合成 | 素材和顺序符合要求、构图一致、无意外黑帧、原声和配乐安排正确 |
| 产品宣传 | 品牌与卖点准确、产品主体清晰、文字可读、行动引导完整 |
| 动画 | 入场与退场时刻正确、运动连续、层级正确、最后有效帧没有残留或跳变 |

PNG 预览只能验证对应帧的画面，保存成功只证明项目保存；都不能代替成片的连续画面与声音验收。
