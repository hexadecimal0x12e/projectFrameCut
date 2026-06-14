---
name: 项目概览
description: 查询 projectFrameCut 项目的元数据、时间线信息和图层结构
tools:
  - get_timeline_info
  - get_project_metadata
  - list_layers
  - list_clips
---

# 项目概览

查询当前 projectFrameCut 项目的各项基本信息和元数据。

## 用法

### 1. 获取时间线元数据

获取帧率、分辨率、总帧数、图层数等核心信息：

```
工具: get_timeline_info
参数: 无
```

返回示例：
```json
{
  "projectName": "My Project",
  "width": 1920,
  "height": 1080,
  "frameRate": 60,
  "totalFrames": 7200,
  "layerCount": 4,
  "clipCount": 12
}
```

### 2. 获取项目元数据

获取项目文件路径、大小、使用的插件等信息：

```
工具: get_project_metadata
参数: 无
```

### 3. 列出所有图层

查看时间线中的所有图层/轨道及其包含的剪辑：

```
工具: list_layers
参数: 无
```

### 4. 列出所有剪辑

获取项目中所有剪辑的列表：

```
工具: list_clips
参数: 无
```

## 典型流程

当需要全面了解项目状态时，按以下顺序调用：

1. `get_timeline_info` — 获取核心时间线参数
2. `get_project_metadata` — 获取文件级元数据
3. `list_layers` — 查看图层结构
4. `list_clips` — 查看所有剪辑

## 注意事项

- 所有查询工具均为只读操作，不会修改项目
- `list_clips` 返回所有剪辑的完整详情，数据量可能较大
