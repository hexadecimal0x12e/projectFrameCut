# MCP 服务器使用指南

## 概述

projectFrameCut MCP 服务器允许外部 AI Agent 通过 MCP（Model Context Protocol）协议对项目进行结构化编辑。支持创建/修改/删除 Clip、管理 Effect/EffectBundle 等核心编辑操作。

## 快速开始

### 启动服务器

#### 方式 1：Stdio 模式（推荐用于本地 CLI）
```bash
cd D:\code\projectFrameCut
dotnet run --project projectFrameCut.McpServer\projectFrameCut.McpServer.csproj -- --project "path/to/project" --stdio
```

#### 方式 2：HTTP 模式（用于远程或 WebUI）
```bash
dotnet run --project projectFrameCut.McpServer\projectFrameCut.McpServer.csproj -- --project "path/to/project" --http --port 32123
```

HTTP 模式下还会自动提供客户端链接通道：

- MCP JSON-RPC: `http://127.0.0.1:32123/mcp`
- 客户端 WebSocket: `ws://127.0.0.1:32123/client`

## 可用工具列表

### 查询工具（只读）

#### 1. `get_timeline_info` - 获取时间线基本信息
**参数**：无

**返回**：
```json
{
  "projectName": "My Project",
  "width": 1920,
  "height": 1080,
  "frameRate": 60,
  "totalFrames": 3600,
  "layerCount": 3,
  "clipCount": 5,
  "lastChanged": "2026-05-14T21:43:00Z",
  "savedAt": "2026-05-14T21:43:00Z"
}
```

#### 2. `list_layers` - 列出所有图层/轨道
**参数**：无

**返回**：
```json
{
  "layers": [
    {
      "layerIndex": 0,
      "clipCount": 2,
      "clips": [
        {
          "id": "clip_001",
          "name": "Opening",
          "startFrame": 0,
          "duration": 120
        }
      ]
    }
  ]
}
```

#### 3. `list_available_effects` - 列出可用的特效
**参数**：无

**返回**：
```json
{
  "effects": [
    {
      "typeName": "Opacity",
      "name": "Opacity",
      "fromPlugin": "internal",
      "effectType": "NormalEffect",
      "description": "Adjust clip opacity"
    }
  ],
  "count": 15
}
```

#### 4. `get_effect_info` - 获取特定特效的详细信息
**参数**：
```json
{
  "effectType": "Opacity"
}
```

**返回**：
```json
{
  "typeName": "Opacity",
  "name": "Opacity",
  "fromPlugin": "internal",
  "effectType": "NormalEffect",
  "description": "Adjust clip opacity",
  "isEnabled": true,
  "index": 0,
  "parameters": {
    "alpha": {
      "name": "Alpha",
      "type": "System.Single",
      "defaultValue": 1.0
    }
  }
}
```

#### 5. `list_clips` - 列出所有 Clip
**参数**：无

**返回**：
```json
{
  "clips": [
    {
      "id": "clip_001",
      "name": "Video 1",
      "layerIndex": 0,
      "startFrame": 0,
      "duration": 120,
      "filePath": "/media/video1.mp4"
    }
  ]
}
```

#### 6. `get_clip` - 获取单个 Clip 详情
**参数**：
```json
{
  "clipId": "clip_001"
}
```

**返回**：Clip 对象的完整信息

#### 7. `get_project_metadata` - 获取项目元数据
**参数**：无

**返回**：
```json
{
  "projectName": "My Project",
  "projectPath": "/home/user/projects/my_project",
  "fileSize": 2048576,
  "createdOrModified": "2026-05-14T21:43:00Z",
  "lastSnapshotId": "550e8400-e29b-41d4-a716-446655440000",
  "pluginsUsed": ["plugin_1"],
  "normallyExited": true
}
```

#### 8. `list_connected_clients` - 列出已连接编辑器客户端
**参数**：无

#### 9. `get_client_environment` - 获取客户端环境能力（Effect/Mixture/插件）
**参数**：
```json
{
  "clientId": "pjfc-client-xxxx",
  "timeoutMs": 10000
}
```

#### 10. `render_client_preview` - 请求客户端渲染某一帧预览图
**参数**：
```json
{
  "clientId": "pjfc-client-xxxx",
  "frame": 120,
  "width": 1280,
  "height": 720,
  "timeoutMs": 15000
}
```

#### 11. `apply_client_patch` - 对客户端当前草稿应用 Clip Patch 并同步 UI
**参数**：
```json
{
  "clientId": "pjfc-client-xxxx",
  "clipId": "clip_001",
  "patch": {
    "name": "Updated by AI",
    "targetWidth": 1280
  }
}
```

#### 12. `move_client_clip` - 移动客户端上的 Clip 并同步 UI
**参数**：
```json
{
  "clientId": "pjfc-client-xxxx",
  "clipId": "clip_001",
  "layerIndex": 1,
  "startFrame": 200
}
```

### Clip 编辑工具

#### 8. `upsert_clip` - 创建或更新 Clip
**参数**：
```json
{
  "clip": {
    "id": "clip_002",
    "name": "New Clip",
    "layerIndex": 1,
    "startFrame": 240,
    "duration": 180,
    "filePath": "/media/video2.mp4",
    "targetWidth": 1920,
    "targetHeight": 1080,
    "targetX": 0,
    "targetY": 0
  }
}
```

#### 9. `move_clip` - 移动 Clip 到不同位置
**参数**：
```json
{
  "clipId": "clip_001",
  "layerIndex": 2,
  "startFrame": 500,
  "subLayerIndex": 2
}
```

#### 10. `patch_clip` - 部分更新 Clip 属性
**参数**：
```json
{
  "clipId": "clip_001",
  "patch": {
    "name": "Updated Name",
    "targetWidth": 1280,
    "targetHeight": 720
  }
}
```

#### 11. `delete_clip` - 删除 Clip
**参数**：
```json
{
  "clipId": "clip_001"
}
```

**返回**：
```json
{
  "deleted": true
}
```

### Effect 编辑工具

#### 12. `add_effect` - 添加或替换 Effect
**参数**：
```json
{
  "clipId": "clip_001",
  "effect": {
    "typeName": "Opacity",
    "fromPlugin": "internal",
    "name": "Fade Out",
    "enabled": true,
    "index": 0,
    "parameters": {
      "alpha": 0.5
    }
  }
}
```

#### 13. `remove_effect` - 移除 Effect
**参数**：
```json
{
  "clipId": "clip_001",
  "effectKey": "Fade Out"
}
```

#### 14. `add_effect_bundle` - 添加或替换 EffectBundle
**参数**：
```json
{
  "clipId": "clip_001",
  "bundle": {
    "id": "550e8400-e29b-41d4-a716-446655440000",
    "name": "Color Grade",
    "bundleTypeName": "ColorGradingBundle",
    "enabled": true,
    "parameters": {}
  }
}
```

#### 15. `remove_effect_bundle` - 移除 EffectBundle
**参数**：
```json
{
  "clipId": "clip_001",
  "bundleId": "550e8400-e29b-41d4-a716-446655440000"
}
```

### 项目管理

#### 16. `save_project` - 保存项目
**参数**：
```json
{
  "changeReason": "Updated via MCP"
}
```

**返回**：
```json
{
  "saved": true,
  "projectRoot": "/home/user/projects/my_project",
  "clipCount": 5
}
```

---

## 使用示例

### Python 客户端示例

```python
import json
import subprocess

def call_mcp_tool(tool_name, arguments):
    """调用 MCP 工具"""
    request = {
        "jsonrpc": "2.0",
        "method": "tools/call",
        "params": {
            "name": tool_name,
            "arguments": arguments
        },
        "id": 1
    }
    
    # 通过 stdio 调用 MCP 服务器
    process = subprocess.Popen(
        ["dotnet", "run", "--project", 
         "projectFrameCut.McpServer.csproj", 
         "--", "--project", "my_project", "--stdio"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True
    )
    
    output, error = process.communicate(json.dumps(request))
    response = json.loads(output)
    
    return response

# 示例：列出所有 Clip
result = call_mcp_tool("list_clips", {})
print(json.dumps(result, indent=2))

# 示例：创建新 Clip
result = call_mcp_tool("upsert_clip", {
    "clip": {
        "id": "new_clip_001",
        "name": "New Clip",
        "layerIndex": 0,
        "startFrame": 0,
        "duration": 120
    }
})
print(json.dumps(result, indent=2))
```

### JavaScript/Node.js 客户端示例

```javascript
const { spawn } = require('child_process');

function callMcpTool(toolName, arguments_) {
    return new Promise((resolve, reject) => {
        const request = {
            jsonrpc: "2.0",
            method: "tools/call",
            params: {
                name: toolName,
                arguments: arguments_
            },
            id: 1
        };
        
        const proc = spawn('dotnet', [
            'run', '--project', 
            'projectFrameCut.McpServer.csproj',
            '--', '--project', 'my_project', '--stdio'
        ]);
        
        let output = '';
        
        proc.stdout.on('data', (data) => {
            output += data.toString();
        });
        
        proc.on('close', (code) => {
            try {
                const response = JSON.parse(output);
                resolve(response);
            } catch (e) {
                reject(e);
            }
        });
        
        proc.stdin.write(JSON.stringify(request));
        proc.stdin.end();
    });
}

// 示例：获取时间线信息
(async () => {
    const timelineInfo = await callMcpTool('get_timeline_info', {});
    console.log(JSON.stringify(timelineInfo, null, 2));
})();
```

---

## 最佳实践

### 1. 错误处理
```python
try:
    result = call_mcp_tool("move_clip", {
        "clipId": "clip_001",
        "layerIndex": 1,
        "startFrame": 500
    })
    
    if "error" in result.get("result", {}):
        print(f"Error: {result['result']['error']}")
    else:
        print(f"Success: {result['result']}")
except Exception as e:
    print(f"Failed to call MCP: {e}")
```

### 2. 事务性操作
执行多个相关操作时，建议在成功后再调用 `save_project`：

```python
# 创建多个 Clip
clips = [
    {"id": "c1", "name": "Clip 1", ...},
    {"id": "c2", "name": "Clip 2", ...},
    {"id": "c3", "name": "Clip 3", ...}
]

for clip in clips:
    call_mcp_tool("upsert_clip", {"clip": clip})

# 全部创建完成后保存
call_mcp_tool("save_project", {
    "changeReason": "Batch import 3 clips"
})
```

### 3. 查询优化
先查询信息，再执行编辑操作：

```python
# 1. 查询现有 Clip
clips_response = call_mcp_tool("list_clips", {})
existing_clip_ids = {c["id"] for c in clips_response["result"]["clips"]}

# 2. 根据查询结果决定编辑操作
if "target_clip" not in existing_clip_ids:
    call_mcp_tool("upsert_clip", {...})
else:
    call_mcp_tool("patch_clip", {...})
```

---

## 常见问题

### Q: 如何远程连接到 MCP 服务器？
A: 使用 HTTP 模式启动服务器，然后通过 HTTP POST 请求调用工具：
```bash
curl -X POST http://localhost:32123/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","method":"tools/list","id":1}'
```

### Q: Clip 的 layerIndex 是什么？
A: layerIndex 表示 Clip 所在的轨道（Track）编号。0 为最底层，数值越大越靠上。

### Q: 如何获取可用的 Effect 列表？
A: 调用 `list_available_effects` 工具，它会返回系统中所有已安装的 Effect 类型。

### Q: 修改后需要手动保存吗？
A: 是的，每次编辑后调用 `save_project` 工具来持久化修改。

---

## 性能考虑

- **Clip 数量**：支持数千个 Clip，但建议保持在 1000 个以下以保持良好性能
- **Effect 层数**：单个 Clip 上的 Effect 数建议不超过 20 个
- **并发操作**：当前不支持多客户端并发编辑，建议单客户端使用

---

## 项目文件结构

MCP 服务器与以下文件交互：

```
project_root/
├── project.pjfc          # 项目配置文件（JSON）
├── timeline.json         # 时间线编辑状态
├── assets.json          # 资源列表
└── saveSlots/           # 保存槽位（用于版本管理）
    └── slot_<GUID>/
        ├── timeline.json
        └── assets.json
```

---

## 后续功能规划

- [ ] 支持多客户端并发编辑（冲突解决）
- [ ] 添加 Asset 管理工具（上传、删除资源）
- [ ] 添加 Layer 管理工具（创建、删除、重新排序）
- [ ] 添加撤销/重做功能
- [ ] 添加项目导出/渲染工具
- [ ] 添加权限控制和会话管理

---

## 支持

遇到问题？提交 Issue 或查看项目文档。
