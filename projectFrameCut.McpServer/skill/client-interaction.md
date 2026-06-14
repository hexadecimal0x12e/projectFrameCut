---
name: 客户端交互
description: 与已连接的 projectFrameCut 编辑器客户端通信，查询环境和预览渲染
tools:
  - list_connected_clients
  - get_client_environment
  - render_client_preview
  - apply_client_patch
  - move_client_clip
---

# 客户端交互

与正在运行并连接到 MCP 服务器的 projectFrameCut 编辑器客户端进行交互。

## 前提条件

必须有一个 projectFrameCut 编辑器实例已连接到 MCP 服务器。
启动服务器时使用 `--pullApplication` 参数可自动拉起客户端。

## 可用操作

### 1. 列出已连接客户端

```
工具: list_connected_clients
参数: 无
```

返回当前所有连接的客户端列表及其 ID。

### 2. 查询客户端环境

获取客户端支持的效果、混合模式和插件信息：

```
工具: get_client_environment
参数:
  clientId: string       # 客户端 ID
  timeoutMs: integer     # [可选] 超时时间（毫秒，默认 10000）
```

### 3. 请求预览渲染

从客户端请求渲染一帧预览图像：

```
工具: render_client_preview
参数:
  clientId: string       # 客户端 ID
  frame: integer         # 要渲染的时间线帧索引
  width: integer         # [可选] 输出宽度
  height: integer        # [可选] 输出高度
  timeoutMs: integer     # [可选] 超时时间（毫秒，默认 15000）
```

### 4. 应用客户端补丁

在客户端上实时应用剪辑修改并同步 UI：

```
工具: apply_client_patch
参数:
  clientId: string                     # 客户端 ID
  clipId: string                       # 目标剪辑 ID
  patch: { [key: string]: any }        # 要更新的字段
  timeoutMs: integer                   # [可选] 超时时间
```

### 5. 移动客户端剪辑

在客户端上实时移动剪辑并同步 UI：

```
工具: move_client_clip
参数:
  clientId: string       # 客户端 ID
  clipId: string         # 目标剪辑 ID
  layerIndex: integer    # 目标图层索引
  startFrame: integer    # 目标起始帧
  timeoutMs: integer     # [可选] 超时时间
```

## 典型流程

### 预览当前帧

1. `list_connected_clients` — 获取可用客户端
2. `render_client_preview` — 请求渲染当前帧
3. 处理返回的图像数据

### 实时编辑并验证

1. `list_connected_clients` — 获取客户端
2. `apply_client_patch` — 在客户端上实时调整剪辑
3. `render_client_preview` — 渲染一帧查看效果
4. 根据预览结果继续调整

## 注意事项

- 客户端操作有超时限制，长时间操作可能超时
- 客户端断开连接后需要重新连接才能继续交互
- `render_client_preview` 返回的是图像数据，需要适当处理
