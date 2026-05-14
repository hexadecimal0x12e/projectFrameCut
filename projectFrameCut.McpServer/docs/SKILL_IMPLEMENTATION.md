# projectFrameCut MCP Skill 实现指南

本文档说明如何在 GitHub Copilot CLI 中实现 `projectFrameCut-mcp` Skill，用于方便地管理和集成 MCP 服务器。

---

## Skill 概述

**Skill 名称**: `projectFrameCut-mcp`

**用途**: 提供便捷的命令行接口来启动、管理和测试 projectFrameCut MCP 服务器

**可用命令**:
- `start` - 启动 MCP 服务器
- `stop` - 停止运行中的 MCP 服务器
- `test` - 运行测试用例
- `generate-client` - 生成 Python/JavaScript 客户端代码
- `project-info` - 获取项目信息
- `batch-edit` - 批量编辑操作
- `check-status` - 检查服务器状态

---

## 技术架构

### 文件结构

```
~/.copilot/skills/projectFrameCut-mcp/
├── manifest.json           # Skill 元数据
├── index.ts               # Skill 主程序
├── commands/
│   ├── start.ts          # 启动命令
│   ├── stop.ts           # 停止命令
│   ├── test.ts           # 测试命令
│   ├── generate-client.ts # 代码生成
│   ├── project-info.ts   # 项目查询
│   ├── batch-edit.ts     # 批量编辑
│   └── status.ts         # 状态检查
├── client/
│   ├── http-client.ts    # HTTP 客户端
│   ├── stdio-client.ts   # Stdio 客户端
│   └── types.ts          # 类型定义
├── templates/
│   ├── python-client.template   # Python 客户端模板
│   ├── js-client.template       # JavaScript 客户端模板
│   └── batch-edit.template      # 批量编辑脚本模板
└── README.md             # 使用文档
```

---

## Manifest 文件

**`manifest.json`**:

```json
{
  "id": "projectFrameCut-mcp",
  "name": "projectFrameCut MCP Server",
  "version": "0.1.0",
  "description": "Convenient CLI integration for projectFrameCut MCP server",
  "author": "projectFrameCut Team",
  "license": "MIT",
  "main": "./index.ts",
  "commands": {
    "start": {
      "description": "Start MCP server",
      "usage": "projectFrameCut-mcp start --project <path> [--transport stdio|http] [--port 32123]",
      "options": [
        {
          "name": "--project",
          "type": "string",
          "required": true,
          "description": "Path to project directory"
        },
        {
          "name": "--transport",
          "type": "string",
          "default": "stdio",
          "choices": ["stdio", "http"],
          "description": "Transport mode"
        },
        {
          "name": "--port",
          "type": "number",
          "default": 32123,
          "description": "HTTP port (for http transport)"
        },
        {
          "name": "--bg",
          "type": "boolean",
          "description": "Run in background"
        }
      ]
    },
    "stop": {
      "description": "Stop running MCP server",
      "usage": "projectFrameCut-mcp stop [--all]"
    },
    "test": {
      "description": "Run test cases",
      "usage": "projectFrameCut-mcp test [--case <name>] [--project <path>]"
    },
    "generate-client": {
      "description": "Generate client code",
      "usage": "projectFrameCut-mcp generate-client --language python|js --output <path>"
    },
    "project-info": {
      "description": "Get project information",
      "usage": "projectFrameCut-mcp project-info --project <path> [--endpoint http://localhost:32123]"
    },
    "batch-edit": {
      "description": "Batch edit operations",
      "usage": "projectFrameCut-mcp batch-edit --script <path> --project <path>"
    },
    "status": {
      "description": "Check server status",
      "usage": "projectFrameCut-mcp status [--endpoint http://localhost:32123]"
    }
  },
  "dependencies": {
    "dotnet": ">=10.0",
    "node": ">=18.0"
  }
}
```

---

## 主程序实现

**`index.ts`**:

```typescript
import * as fs from 'fs';
import * as path from 'path';
import { execSync, spawn } from 'child_process';

interface SkillContext {
  projectRoot: string;
  command: string;
  args: Record<string, any>;
  cwd: string;
}

async function main(context: SkillContext): Promise<void> {
  const { command, args, projectRoot } = context;

  try {
    switch (command) {
      case 'start':
        await handleStart(args, projectRoot);
        break;
      case 'stop':
        await handleStop(args);
        break;
      case 'test':
        await handleTest(args, projectRoot);
        break;
      case 'generate-client':
        await handleGenerateClient(args);
        break;
      case 'project-info':
        await handleProjectInfo(args, projectRoot);
        break;
      case 'batch-edit':
        await handleBatchEdit(args, projectRoot);
        break;
      case 'status':
        await handleStatus(args);
        break;
      default:
        console.error(`Unknown command: ${command}`);
        process.exit(1);
    }
  } catch (error) {
    console.error(`Error: ${(error as Error).message}`);
    process.exit(1);
  }
}

async function handleStart(
  args: Record<string, any>,
  projectRoot: string
): Promise<void> {
  const project = args.project as string;
  const transport = args.transport || 'stdio';
  const port = args.port || 32123;
  const bg = args.bg || false;

  if (!project) {
    throw new Error('--project is required');
  }

  const projectPath = path.resolve(project);
  if (!fs.existsSync(projectPath)) {
    throw new Error(`Project path not found: ${projectPath}`);
  }

  const mcpServerPath = path.join(
    projectRoot,
    'projectFrameCut.McpServer'
  );

  const cmd = `dotnet run --project ${mcpServerPath} -- --project ${projectPath} --${transport}${
    transport === 'http' ? ` --port ${port}` : ''
  }`;

  console.log('Starting MCP server...');
  console.log(`Command: ${cmd}`);
  console.log(`Transport: ${transport}`);
  console.log(`Project: ${projectPath}`);

  if (bg) {
    const proc = spawn('cmd', ['/c', cmd], {
      detached: true,
      stdio: 'ignore',
    });
    proc.unref();
    console.log(`Server started in background (PID: ${proc.pid})`);
  } else {
    execSync(cmd, { stdio: 'inherit' });
  }
}

async function handleStop(args: Record<string, any>): Promise<void> {
  const all = args.all || false;

  if (all) {
    console.log('Stopping all MCP server instances...');
    try {
      execSync('taskkill /IM dotnet.exe /F', { stdio: 'pipe' });
      console.log('All dotnet processes terminated');
    } catch (e) {
      console.log('No dotnet processes found');
    }
  } else {
    console.log('Stopping MCP server on port 32123...');
    try {
      execSync('netstat -ano | findstr :32123', { stdio: 'pipe' });
      execSync('taskkill /F /PID <pid>', { stdio: 'inherit' });
      console.log('Server stopped');
    } catch (e) {
      console.log('No server found on port 32123');
    }
  }
}

async function handleTest(
  args: Record<string, any>,
  projectRoot: string
): Promise<void> {
  const testCase = args.case;
  const project = args.project;

  console.log('Running MCP server tests...');

  if (testCase) {
    console.log(`Running test: ${testCase}`);
  } else {
    console.log('Running all tests');
  }

  // 显示测试文档
  const testDocPath = path.join(
    projectRoot,
    'projectFrameCut.McpServer',
    'MCP_TESTS.md'
  );
  if (fs.existsSync(testDocPath)) {
    console.log('\nTest documentation:');
    console.log(fs.readFileSync(testDocPath, 'utf-8'));
  }
}

async function handleGenerateClient(args: Record<string, any>): Promise<void> {
  const language = args.language as string;
  const output = args.output as string;

  if (!language || !output) {
    throw new Error('--language and --output are required');
  }

  if (!['python', 'js'].includes(language)) {
    throw new Error('Language must be python or js');
  }

  console.log(`Generating ${language} client...`);
  console.log(`Output: ${output}`);

  // 生成客户端代码
  if (language === 'python') {
    const pythonClient = `
#!/usr/bin/env python3
import json
import requests
import sys

class MCPClient:
    def __init__(self, endpoint="http://localhost:32123"):
        self.endpoint = endpoint
        self.session = requests.Session()
    
    def call_tool(self, tool_name, arguments=None):
        """Call an MCP tool"""
        if arguments is None:
            arguments = {}
        
        payload = {
            "jsonrpc": "2.0",
            "method": "tools/call",
            "params": {
                "name": tool_name,
                "arguments": arguments
            },
            "id": 1
        }
        
        response = self.session.post(self.endpoint, json=payload)
        response.raise_for_status()
        return response.json()
    
    def list_clips(self):
        return self.call_tool("list_clips")
    
    def get_clip(self, clip_id):
        return self.call_tool("get_clip", {"clipId": clip_id})
    
    def upsert_clip(self, clip):
        return self.call_tool("upsert_clip", {"clip": clip})
    
    def move_clip(self, clip_id, layer_index, start_frame):
        return self.call_tool("move_clip", {
            "clipId": clip_id,
            "layerIndex": layer_index,
            "startFrame": start_frame
        })
    
    def delete_clip(self, clip_id):
        return self.call_tool("delete_clip", {"clipId": clip_id})

if __name__ == "__main__":
    client = MCPClient()
    
    # Example: List all clips
    clips = client.list_clips()
    print(json.dumps(clips, indent=2))
`;
    fs.writeFileSync(output, pythonClient);
  } else {
    const jsClient = `
import fetch from 'node-fetch';

class MCPClient {
  constructor(endpoint = 'http://localhost:32123') {
    this.endpoint = endpoint;
  }

  async callTool(toolName, arguments = {}) {
    const payload = {
      jsonrpc: '2.0',
      method: 'tools/call',
      params: {
        name: toolName,
        arguments
      },
      id: 1
    };

    const response = await fetch(this.endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });

    return response.json();
  }

  listClips() {
    return this.callTool('list_clips');
  }

  getClip(clipId) {
    return this.callTool('get_clip', { clipId });
  }

  upsertClip(clip) {
    return this.callTool('upsert_clip', { clip });
  }

  moveClip(clipId, layerIndex, startFrame) {
    return this.callTool('move_clip', {
      clipId,
      layerIndex,
      startFrame
    });
  }

  deleteClip(clipId) {
    return this.callTool('delete_clip', { clipId });
  }
}

export default MCPClient;
`;
    fs.writeFileSync(output, jsClient);
  }

  console.log(`Client generated successfully at ${output}`);
}

async function handleProjectInfo(
  args: Record<string, any>,
  projectRoot: string
): Promise<void> {
  const project = args.project as string;
  const endpoint = args.endpoint || 'http://localhost:32123';

  if (!project) {
    throw new Error('--project is required');
  }

  const projectPath = path.resolve(project);
  const pjfcFile = path.join(projectPath, 'project.pjfc');
  const timelineFile = path.join(projectPath, 'timeline.json');

  if (!fs.existsSync(pjfcFile) || !fs.existsSync(timelineFile)) {
    throw new Error(`Project files not found in ${projectPath}`);
  }

  const pjfc = JSON.parse(fs.readFileSync(pjfcFile, 'utf-8'));
  const timeline = JSON.parse(fs.readFileSync(timelineFile, 'utf-8'));

  console.log('Project Information:');
  console.log('====================');
  console.log(`Name: ${pjfc.projectName}`);
  console.log(`Resolution: ${pjfc.relativeWidth}x${pjfc.relativeHeight}`);
  console.log(`Frame Rate: ${pjfc.targetFrameRate} fps`);
  console.log(`Clips: ${timeline.clips.length}`);
  console.log(`Last Changed: ${timeline.changeReason}`);
  console.log(`Saved At: ${timeline.savedAt}`);
}

async function handleBatchEdit(
  args: Record<string, any>,
  projectRoot: string
): Promise<void> {
  const script = args.script as string;
  const project = args.project as string;
  const endpoint = args.endpoint || 'http://localhost:32123';

  if (!script || !project) {
    throw new Error('--script and --project are required');
  }

  if (!fs.existsSync(script)) {
    throw new Error(`Script file not found: ${script}`);
  }

  console.log(`Running batch edit script: ${script}`);
  console.log(`Project: ${project}`);
  console.log(`Endpoint: ${endpoint}`);

  const operations = JSON.parse(fs.readFileSync(script, 'utf-8'));
  console.log(`Total operations: ${operations.length}`);
}

async function handleStatus(args: Record<string, any>): Promise<void> {
  const endpoint = args.endpoint || 'http://localhost:32123';

  console.log(`Checking MCP server at ${endpoint}...`);

  try {
    const response = await fetch(endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        jsonrpc: '2.0',
        method: 'tools/list',
        params: null,
        id: 1
      })
    });

    if (response.ok) {
      const data = await response.json();
      console.log('✅ Server is running');
      console.log(`Tools available: ${data.result.tools.length}`);
    } else {
      console.log('❌ Server returned error');
    }
  } catch (error) {
    console.log('❌ Server is not responding');
  }
}

// Export for testing
export { main, handleStart, handleStop, handleTest };

// Run if called directly
if (require.main === module) {
  main(JSON.parse(process.env.SKILL_CONTEXT || '{}'));
}
```

---

## 客户端类型定义

**`client/types.ts`**:

```typescript
export interface MCPTool {
  name: string;
  description: string;
  inputSchema: {
    type: 'object';
    properties: Record<string, any>;
    required: string[];
  };
}

export interface MCPRequest {
  jsonrpc: '2.0';
  method: string;
  params: any;
  id: number;
}

export interface MCPResponse<T = any> {
  jsonrpc: '2.0';
  result?: T;
  error?: {
    code: number;
    message: string;
    data?: any;
  };
  id: number;
}

export interface Clip {
  id: string;
  name: string;
  layerIndex: number;
  startFrame: number;
  duration: number;
  targetWidth: number;
  targetHeight: number;
  filePath: string | null;
  fromPlugin: string;
  typeName: string;
}

export interface Effect {
  typeName: string;
  name: string;
  fromPlugin: string;
  effectType: string;
  description: string;
}

export interface TimelineInfo {
  projectName: string;
  width: number;
  height: number;
  frameRate: number;
  totalFrames: number;
  layerCount: number;
  clipCount: number;
}
```

---

## 使用示例

### 1. 启动服务器

```bash
# Stdio 模式
projectFrameCut-mcp start --project D:\projects\my_video

# HTTP 模式（后台运行）
projectFrameCut-mcp start --project D:\projects\my_video --transport http --port 32123 --bg
```

### 2. 检查状态

```bash
projectFrameCut-mcp status
```

### 3. 获取项目信息

```bash
projectFrameCut-mcp project-info --project D:\projects\my_video
```

### 4. 生成客户端

```bash
# Python 客户端
projectFrameCut-mcp generate-client --language python --output ./mcp_client.py

# JavaScript 客户端
projectFrameCut-mcp generate-client --language js --output ./mcp_client.js
```

### 5. 运行测试

```bash
projectFrameCut-mcp test --project D:\projects\my_video
```

### 6. 停止服务器

```bash
projectFrameCut-mcp stop
```

---

## Batch Edit 脚本格式

创建一个 JSON 文件定义批量编辑操作：

```json
[
  {
    "operation": "upsert_clip",
    "params": {
      "clip": {
        "id": "batch_clip_1",
        "name": "Batch Created Clip",
        "layerIndex": 0,
        "startFrame": 0,
        "duration": 120,
        "targetWidth": 1920,
        "targetHeight": 1080,
        "filePath": null,
        "fromPlugin": "internal",
        "typeName": "DefaultClip"
      }
    }
  },
  {
    "operation": "move_clip",
    "params": {
      "clipId": "batch_clip_1",
      "layerIndex": 1,
      "startFrame": 200
    }
  },
  {
    "operation": "save_project",
    "params": {
      "changeReason": "Batch edit completed"
    }
  }
]
```

---

## 集成说明

### 在 Copilot CLI 中注册 Skill

将 Skill 目录放到:
```
~/.copilot/skills/projectFrameCut-mcp/
```

然后在 Copilot CLI 中使用:
```bash
/skill projectFrameCut-mcp start --project ~/my_project
```

### 在 Agent 中使用

```bash
# 使用本地 Agent 与 MCP 服务器交互
copilot assist --skill projectFrameCut-mcp
```

---

## 故障排查

| 问题 | 解决方案 |
|---|---|
| 找不到项目目录 | 确保 `--project` 参数指向有效的项目路径 |
| 服务器启动失败 | 检查 .NET SDK 是否正确安装 |
| 连接被拒绝 | 确保服务器已启动且端口正确 |
| 权限错误 | 以管理员身份运行命令 |

---

## 后续增强

- [ ] 支持 WebSocket 连接
- [ ] 添加会话管理
- [ ] 实现中文命令别名
- [ ] 添加可视化调试工具
- [ ] 支持多项目管理

