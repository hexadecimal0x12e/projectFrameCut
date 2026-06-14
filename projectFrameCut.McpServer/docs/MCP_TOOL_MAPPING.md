# MCP 工具映射文档

## AITools → MCP 服务器工具对应关系

本文档说明了 App 内置 AI 助手（AITools）与 MCP 服务器暴露工具之间的对应关系。

### Clip 管理工具

| AITools 工具名 | MCP 工具名 | 共同实现 | 说明 |
|---|---|---|---|
| `get_all_clips` | `list_clips` | TimelineMcpLiveService.ListClips | 列出项目中的所有 Clip |
| `get_selected_clip_info` | `get_clip` | TimelineMcpLiveService.GetClip | 获取单个 Clip 信息 |
| `set_clip_info` | `upsert_clip` | TimelineMcpLiveService.ReplaceClip | 创建或更新 Clip |
| `move_clip` | `move_clip` | TimelineMcpLiveService.MoveClip | 移动 Clip 到不同 Track/Frame |
| `patch_clip` | `patch_clip` | TimelineMcpLiveService.ApplyClipPatch | 更新 Clip 部分属性 |

### Effect 管理工具

| AITools 工具名 | MCP 工具名 | 共同实现 | 说明 |
|---|---|---|---|
| `add_effect_to_clip` | `add_effect` | TimelineMcpLiveService.AddEffect | 添加或替换 Effect |
| `remove_effect_from_clip` | `remove_effect` | TimelineMcpLiveService.RemoveEffect | 移除 Effect |
| `add_effect_bundle_to_clip` | `add_effect_bundle` | TimelineMcpLiveService.AddEffectBundle | 添加或替换 EffectBundle |
| `remove_effect_bundle_from_clip` | `remove_effect_bundle` | TimelineMcpLiveService.RemoveEffectBundle | 移除 EffectBundle |
| `get_effect_info` | `get_effect_info` | EffectHelper.EffectsEnum | 获取 Effect 详细信息 |
| `get_effect_bundle_info` | 无对应 | PluginManager | 获取 EffectBundle 详细信息 |

### 其他工具

| AITools 工具名 | MCP 工具名 | 说明 |
|---|---|---|
| `create_an_AIGC_image` | 无对应 | App 特定功能，生成 AI 图像 |
| `create_an_AIGC_video` | 无对应 | App 特定功能，生成 AI 视频 |
| `run_sub_agent` | 无对应 | App 特定功能，运行子 Agent |

---

## 架构整合说明

### 当前架构

```
┌─────────────────────────┐
│    AIAssistance         │
│   (App 内 AI 助手)      │
│   - AITools.cs          │
│   - AIHelper.cs         │
│   - AssistanceChatView  │
└────────────┬────────────┘
             │
             ↓
┌─────────────────────────┐
│  TimelineMcpLiveService │
│  (编辑应用服务层)      │
│  - 直接操作 DraftPage   │
│  - 保证线程安全         │
│  - 触发UI刷新事件       │
└────────────┬────────────┘
             │
             ↓
┌─────────────────────────┐
│   DraftPage / Clips     │
│  (UI 编辑页面和数据)    │
└─────────────────────────┘
```

### 整合后架构

```
┌─────────────────────────┐         ┌──────────────────┐
│    AIAssistance         │         │ MCP 客户端 App   │
│   (App 内 AI 助手)      │         │  (外部 Agent)    │
└────────────┬────────────┘         └────────┬─────────┘
             │                               │
             └───────────────┬───────────────┘
                             ↓
                  ┌────────────────────┐
                  │  MCP 服务器        │
                  │  (Program.cs)      │
                  │  - HTTP/Stdio      │
                  │  - 工具分发        │
                  └────────┬───────────┘
                           │
                           ↓
                  ┌────────────────────┐
                  │ TimelineProjectEditor│
                  │ (MCP 编辑核心)     │
                  └────────┬───────────┘
                           │
                           ↓
                  ┌────────────────────┐
                  │ TimelineProjectWorks│
                  │ pace (项目工作区)   │
                  └────────┬───────────┘
                           │
                           ↓
                  ┌────────────────────┐
                  │ 项目文件 / Draft    │
                  │ (本地持久化)       │
                  └────────────────────┘
```

### 关键设计决策

1. **双路径支持**
   - App 内：AITools → TimelineMcpLiveService → DraftPage（快速、有UI反馈）
   - 外部：MCP Client → MCP Server → TimelineProjectEditor → TimelineProjectWorkspace（跨进程、可远程）

2. **共同的实现基础**
   - TimelineMcpLiveService 在 App 内使用
   - TimelineProjectEditor 在 MCP 服务器中使用
   - 两者都基于相同的数据模型和编辑逻辑

3. **参数统一**
   - 使用相同的 DTO 对象（ClipDraftDTO、EffectAndMixtureJSONStructure 等）
   - 使用相同的错误处理机制
   - 返回值格式保持一致

---

## 迁移说明

AITools 已经大量基于 TimelineMcpLiveService 实现，因此 mcp-ai-assistant-integration 的工作主要是：

1. ✅ **参数 Schema 统一** - 确保 AITools 和 MCP 服务器使用相同的参数类型
2. ✅ **错误处理统一** - 所有错误返回格式一致
3. ✅ **向后兼容** - 保留 AITools 现有的别名（如 `get_all_clips` vs `list_clips`）
4. ✅ **事件触发** - App 内工具负责 UI 刷新，MCP 工具不负责（因为没有 UI）

---

## 已完成的集成

- ✅ Clip 管理工具（list、get、create、move、update、remove）
- ✅ Effect 管理工具（add、remove）
- ✅ EffectBundle 管理工具（add、remove）
- ✅ 项目保存（save_project）

---

## 后续工作

- 完成单元测试
- 编写使用文档
- 提供外部 Agent 集成示例
