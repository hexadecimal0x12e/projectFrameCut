---
name: 批处理操作
description: 对 projectFrameCut 项目执行批量编辑操作，如批量创建剪辑、批量添加效果
tools:
  - upsert_clip
  - move_clip
  - patch_clip
  - add_effect
  - add_effect_bundle
  - save_project
---

# 批处理操作

在 projectFrameCut 项目中执行一系列编辑操作，适用于自动化工作流和批量处理场景。

## 策略说明

批处理操作没有单一的 MCP 工具，而是通过编排多个工具调用来实现。
核心思路是：先规划、再执行、最后保存。

## 常见批处理场景

### 场景 1：批量创建剪辑序列

从一组媒体文件创建多个剪辑并排列在时间线上：

```
1. upsert_clip — 创建第 1 个剪辑，startFrame: 0, duration: 150
2. upsert_clip — 创建第 2 个剪辑，startFrame: 150, duration: 120
3. upsert_clip — 创建第 3 个剪辑，startFrame: 270, duration: 180
4. save_project — 保存所有更改
```

关键：每个剪辑的 `startFrame` = 前一个的 `startFrame + duration`。

### 场景 2：批量添加相同效果

为多个剪辑添加相同的效果：

```
1. get_effect_info — 查看效果参数结构
2. add_effect — 为剪辑 A 添加效果
3. add_effect — 为剪辑 B 添加效果
4. add_effect — 为剪辑 C 添加效果
5. save_project — 保存更改
```

### 场景 3：批量移动剪辑到新图层

将所有剪辑从图层 0 移动到图层 1：

```
1. list_clips — 获取所有剪辑
2. move_clip — 逐一遍历并将 layerIndex 改为 1
3. save_project — 保存更改
```

### 场景 4：批量更新剪辑属性

统一修改多个剪辑的分辨率或名称：

```
1. list_clips — 获取所有剪辑 ID
2. patch_clip — 逐个更新 name、targetWidth、targetHeight 等字段
3. save_project — 保存更改
```

## 批处理脚本格式

如果客户端支持，可以使用 JSON 脚本文件描述批处理操作：

```json
[
  {
    "operation": "upsert_clip",
    "params": {
      "clip": {
        "id": "batch_clip_1",
        "name": "片头",
        "layerIndex": 0,
        "startFrame": 0,
        "duration": 150,
        "targetWidth": 1920,
        "targetHeight": 1080,
        "fromPlugin": "internal",
        "typeName": "DefaultClip"
      }
    }
  },
  {
    "operation": "upsert_clip",
    "params": {
      "clip": {
        "id": "batch_clip_2",
        "name": "正片",
        "layerIndex": 0,
        "startFrame": 150,
        "duration": 600,
        "targetWidth": 1920,
        "targetHeight": 1080,
        "fromPlugin": "internal",
        "typeName": "DefaultClip"
      }
    }
  },
  {
    "operation": "save_project",
    "params": {
      "changeReason": "批处理：创建片头和正片"
    }
  }
]
```

## 注意事项

- 批处理操作后务必调用 `save_project` 持久化
- 大量操作建议分批次执行，避免单次上下文过长
- `move_clip` 时注意目标位置不要与其他剪辑重叠（除非需要叠加）
