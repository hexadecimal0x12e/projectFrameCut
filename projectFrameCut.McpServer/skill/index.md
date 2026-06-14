---
name: projectFrameCut MCP 技能集
description: projectFrameCut 视频编辑器的 MCP 服务器预设技能集合，提供剪辑管理、效果编辑、项目查询等能力
---

# projectFrameCut MCP 技能集

本目录包含预定义的技能文件，为 AI Agent 提供在 projectFrameCut 中执行编辑操作的指导。

## 技能列表

| 技能 | 文件 | 说明 |
|------|------|------|
| 项目概览 | [project-overview.md](project-overview.md) | 查询项目元数据、时间线信息 |
| 剪辑管理 | [clip-management.md](clip-management.md) | 剪辑的增删改查和移动 |
| 效果管理 | [effect-management.md](effect-management.md) | 效果和效果包的增删查 |
| 客户端交互 | [client-interaction.md](client-interaction.md) | 与编辑器客户端通信和预览 |
| 批处理操作 | [batch-operations.md](batch-operations.md) | 批量编辑和自动化工作流 |

## 使用方式

直接告诉 AI Agent 加载对应的技能文件即可。例如：

> "请读取 projectFrameCut MCP 的剪辑管理技能，然后帮我创建一个新的视频剪辑。"

Agent 会读取对应的 Markdown 技能文件，了解可用的工具和调用序列，然后执行操作。

## 连接信息

- MCP 服务器默认地址：`http://127.0.0.1:32123`
- 传输模式支持：HTTP（推荐） / stdio
- 启动命令：`projectFrameCut.McpServer http --project <项目根目录>`
