---
name: subtitle-generation
description: 指导如何生成视频字幕的样式、格式和时间轴对齐
---

## Description

本 Skill 指导你如何为视频生成和编辑字幕，包括字幕样式、时间轴对齐、格式选择等。

## Instructions

### 1. 字幕基础参数

字幕 clip 通过 `add_text_clip` 工具创建。创建时需指定：
- `styleId`：字幕样式 ID
- `text`：字幕文本内容
- `startPosition`：在时间轴上的起始帧位置
- `track`：所在轨道编号

### 2. 字幕样式设置

使用 `get_propertypanel_visual_tree` 和 `set_propertypanel_properties` 工具可以调整字幕的以下样式属性：

- **字体**：使用 `FontFamily` 属性设置字体名称
- **字号**：使用 `FontSize` 属性设置字号（建议范围：24-72）
- **颜色**：使用 `FontColor` 属性设置字体颜色（十六进制格式，如 `#FFFFFFFF`）
- **对齐**：使用 `TextAlignment` 属性设置对齐方式（Left/Center/Right）
- **描边**：使用 `StrokeColor` 和 `StrokeWidth` 设置文字描边
- **阴影**：使用 `ShadowColor`、`ShadowOffset`、`ShadowBlurRadius` 设置阴影效果
- **背景**：使用 `BackgroundColor` 设置字幕背景

### 3. 推荐的字幕样式

#### 标准字幕
- 字体：微软雅黑 / Noto Sans SC
- 字号：36-48
- 颜色：`#FFFFFFFF`（白色）
- 描边：`#FF000000`，宽度 2
- 对齐：Center

#### 标题字幕
- 字体：思源黑体 / Source Han Sans
- 字号：56-72
- 颜色：`#FFFFD700`（金色）
- 阴影：开启，偏移 (2,2)，模糊半径 4
- 对齐：Center

#### 底部滚动字幕
- 字体：微软雅黑
- 字号：28-32
- 颜色：`#FFFFFFFF`
- 背景：`#80000000`（半透明黑）
- 对齐：Center

### 4. 时间轴对齐建议

- 确保字幕的 `StartFrame` 与对应音频/视频片段的起始对齐
- 字幕长度（`lengthInFrame`）应足够让观众自然阅读完毕
- 一般阅读速度参考：每 4-5 帧阅读 1 个中文字符
