---
name: 效果管理
description: 在 projectFrameCut 的剪辑上添加、移除和查询效果及效果包
tools:
  - list_available_effects
  - get_effect_info
  - add_effect
  - remove_effect
  - add_effect_bundle
  - remove_effect_bundle
---

# 效果管理

管理 projectFrameCut 项目中的剪辑效果（Effect）和效果包（EffectBundle）。

## 查询效果

### 1. 列出所有可用效果

获取所有已注册的效果类型：

```
工具: list_available_effects
参数: 无
```

返回每种效果的名称、类型、描述和来源。

### 2. 获取效果详情

查看特定效果的参数和默认值：

```
工具: get_effect_info
参数:
  effectType: string  # 效果类型名称，例如 "Opacity", "Scale", "Position"
```

返回效果的参数列表、类型、默认值等信息。

## 效果操作

### 3. 添加或替换效果

```
工具: add_effect
参数:
  clipId: string    # 目标剪辑 ID
  effect: {
    typeName: string,     # 效果类型名称
    fromPlugin: string,   # 来源插件
    ...                   # 其他效果参数
  }
```

### 4. 移除效果

```
工具: remove_effect
参数:
  clipId: string     # 目标剪辑 ID
  effectKey: string  # 效果名称或 ID
```

## 效果包操作

### 5. 添加或替换效果包

效果包是一组效果的组合：

```
工具: add_effect_bundle
参数:
  clipId: string    # 目标剪辑 ID
  bundle: {
    bundleTypeName: string,  # 效果包类型名称
    id: string,              # 效果包 ID (GUID)
    ...                      # 其他效果包参数
  }
```

### 6. 移除效果包

```
工具: remove_effect_bundle
参数:
  clipId: string   # 目标剪辑 ID
  bundleId: string # 效果包 ID (GUID)
```

## 典型流程

### 为剪辑添加透明度效果

1. `list_available_effects` — 确认 "Opacity" 效果可用
2. `get_effect_info` — 查看 Opacity 的参数结构
3. `add_effect` — 在目标剪辑上添加透明度效果，设置参数
4. `save_project` — 保存更改

### 移除剪辑上的所有效果

1. `get_clip` — 查看剪辑上的现有效果列表
2. 对每个效果调用 `remove_effect`
3. 对每个效果包调用 `remove_effect_bundle`
4. `save_project` — 保存更改
