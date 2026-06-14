---
name: 剪辑管理
description: 对 projectFrameCut 项目中的剪辑进行增删改查操作
tools:
  - list_clips
  - get_clip
  - upsert_clip
  - move_clip
  - patch_clip
  - delete_clip
---

# 剪辑管理

在 projectFrameCut 项目中创建、读取、更新、删除剪辑。

## 可用操作

### 1. 列出所有剪辑

```
工具: list_clips
参数: 无
```

### 2. 获取单个剪辑

```
工具: get_clip
参数:
  clipId: string  # 剪辑的唯一标识符
```

### 3. 创建或替换剪辑

```
工具: upsert_clip
参数:
  clip: {
    id: string,           # 剪辑 ID（相同 ID 会替换已有剪辑）
    name: string,         # 剪辑名称
    layerIndex: uint,     # 所在图层索引
    startFrame: uint,     # 起始帧
    duration: uint,       # 持续帧数
    targetWidth: uint,    # 目标宽度
    targetHeight: uint,   # 目标高度
    filePath: string|null,# 媒体文件路径
    typeName: string,     # 剪辑类型名称
    fromPlugin: string    # 来源插件
  }
```

### 4. 移动剪辑

```
工具: move_clip
参数:
  clipId: string       # 要移动的剪辑 ID
  layerIndex: uint     # 目标图层索引
  startFrame: uint     # 目标起始帧
  subLayerIndex: uint  # [可选] 子图层索引
```

### 5. 更新剪辑字段

非破坏性更新，只修改指定的字段：

```
工具: patch_clip
参数:
  clipId: string                    # 目标剪辑 ID
  patch: { [key: string]: any }     # 要更新的字段
```

常见 patch 示例：
```json
{
  "name": "新名称",
  "targetWidth": 1920,
  "startFrame": 100
}
```

### 6. 删除剪辑

```
工具: delete_clip
参数:
  clipId: string  # 要删除的剪辑 ID
```

## 典型流程

### 创建新剪辑

1. `list_clips` — 查看现有剪辑，确认 ID 不冲突
2. `upsert_clip` — 创建新剪辑
3. `save_project` — 保存更改

### 调整剪辑位置

1. `get_clip` — 获取要移动的剪辑信息
2. `move_clip` — 移动到目标图层和帧位置
3. `save_project` — 保存更改

### 批量删除

1. `list_clips` — 获取所有剪辑列表
2. 根据需要多次调用 `delete_clip`
3. `save_project` — 保存更改
