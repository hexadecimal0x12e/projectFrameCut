# projectFrameCut MCP Skill 集成指南

## 📚 目录

1. [概述](#概述)
2. [安装](#安装)
3. [配置](#配置)
4. [在 Copilot CLI 中使用](#在-copilot-cli-中使用)
5. [在自定义 Agent 中使用](#在自定义-agent-中使用)
6. [常见集成场景](#常见集成场景)
7. [高级用法](#高级用法)

---

## 概述

**projectFrameCut MCP Skill** 是为 GitHub Copilot CLI 设计的插件，提供以下功能：

- 🚀 **服务器管理** - 启动、停止、检查 MCP 服务器状态
- 🔌 **传输模式** - 支持 Stdio（本地）和 HTTP（远程）两种模式
- 📝 **客户端生成** - 自动生成 Python/JavaScript 客户端代码
- 🧪 **测试工具** - 集成测试文档和测试用例
- 📊 **项目管理** - 查看项目信息、批量编辑操作

---

## 安装

### 前置要求

- Node.js 18 或更高版本
- .NET SDK 10.0 或更高版本
- GitHub Copilot CLI（已安装）
- projectFrameCut 项目（包含 MCP 服务器源代码）

### 安装步骤

#### 方法 1：自动安装脚本

```powershell
# Windows PowerShell
$skillDir = "$env:USERPROFILE\.copilot\skills\projectFrameCut-mcp"
mkdir -Force $skillDir
Copy-Item "projectFrameCut.McpServer\skill.js" $skillDir
Copy-Item "projectFrameCut.McpServer\manifest.json" $skillDir
Write-Host "✅ Skill installed to $skillDir"
```

#### 方法 2：手动安装

```bash
# 1. 创建 Skill 目录
mkdir -p ~/.copilot/skills/projectFrameCut-mcp

# 2. 复制文件
cp projectFrameCut.McpServer/skill.js ~/.copilot/skills/projectFrameCut-mcp/
cp projectFrameCut.McpServer/manifest.json ~/.copilot/skills/projectFrameCut-mcp/

# 3. 验证
projectFrameCut-mcp help
```

#### 方法 3：符号链接（开发模式）

```bash
# 使用符号链接便于开发时快速更新
ln -s $(pwd)/projectFrameCut.McpServer ~/.copilot/skills/projectFrameCut-mcp
```

### 验证安装

```bash
# 检查 Skill 是否可用
projectFrameCut-mcp --help

# 预期输出：显示所有可用命令
# projectFrameCut MCP Skill - CLI Integration for MCP Server
# ===========================================================
# Commands: start, stop, status, test, generate-client, project-info, batch-edit, help
```

---

## 配置

### 环境变量

在 `~/.bashrc` 或 `~/.bash_profile` 中设置：

```bash
# 项目根目录
export PROJECTFRAMECUT_ROOT="D:\code\projectFrameCut"

# 默认项目
export PJFC_DEFAULT_PROJECT="D:\projects\my_video"

# 默认端口
export PJFC_DEFAULT_PORT=32123

# 默认传输方式
export PJFC_DEFAULT_TRANSPORT="stdio"
```

### 全局配置文件

创建 `~/.copilot/skills/projectFrameCut-mcp/config.json`：

```json
{
  "defaultTransport": "stdio",
  "defaultPort": 32123,
  "defaultTimeout": 5000,
  "autoStartBackground": false,
  "projectRoots": [
    "D:\\projects\\*",
    "C:\\Users\\username\\Videos\\*"
  ],
  "logging": {
    "enabled": true,
    "level": "info",
    "file": "~/.copilot/skills/projectFrameCut-mcp/skill.log"
  }
}
```

---

## 在 Copilot CLI 中使用

### 基本使用

#### 通过 /skill 命令

```bash
# 启动服务器
/skill projectFrameCut-mcp start --project ~/my_project

# 检查状态
/skill projectFrameCut-mcp status

# 生成客户端
/skill projectFrameCut-mcp generate-client --language python --output ~/client.py
```

#### 与 /assist 命令结合

```bash
# 在 AI 助手中使用 Skill
/assist "Use projectFrameCut-mcp to start a server for ~/my_project and list all clips"

# AI 会自动调用相应的 Skill 命令
```

### Copilot CLI 工作流示例

```bash
# 1. 启动 Copilot CLI
copilot

# 2. 在 Copilot CLI 中执行命令
/skill projectFrameCut-mcp start --project ~/video_project --transport http --port 32123 --bg

# 3. 验证服务器
/skill projectFrameCut-mcp status

# 4. 生成客户端
/skill projectFrameCut-mcp generate-client --language python --output ./client.py

# 5. 退出
exit
```

---

## 在自定义 Agent 中使用

### Node.js Agent 集成

```javascript
// my_agent.js
import { spawn } from 'child_process';
import fetch from 'node-fetch';

class ProjectFrameCutAgent {
  constructor(projectPath) {
    this.projectPath = projectPath;
    this.serverProcess = null;
    this.endpoint = 'http://localhost:32123';
  }

  async start() {
    console.log('🚀 Starting MCP server...');
    const cmd = `projectFrameCut-mcp start --project "${this.projectPath}" --transport http --port 32123 --bg`;
    
    this.serverProcess = spawn('cmd', ['/c', cmd], {
      detached: true
    });
    
    // 等待服务器启动
    await this.waitForServer();
  }

  async waitForServer(maxRetries = 10) {
    for (let i = 0; i < maxRetries; i++) {
      try {
        const response = await fetch(this.endpoint + '/status', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            jsonrpc: '2.0',
            method: 'tools/list',
            id: 1
          })
        });
        
        if (response.ok) {
          console.log('✅ Server started successfully');
          return;
        }
      } catch (e) {
        // 继续重试
      }
      
      await new Promise(resolve => setTimeout(resolve, 1000));
    }
    
    throw new Error('Server failed to start');
  }

  async callTool(toolName, args) {
    const response = await fetch(this.endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        jsonrpc: '2.0',
        method: 'tools/call',
        params: {
          name: toolName,
          arguments: args
        },
        id: 1
      })
    });

    const data = await response.json();
    if (data.error) {
      throw new Error(data.error.message);
    }
    return data.result;
  }

  async stop() {
    console.log('🛑 Stopping server...');
    await spawn('cmd', ['/c', 'projectFrameCut-mcp stop'], {
      detached: true
    });
  }
}

// 使用示例
const agent = new ProjectFrameCutAgent('D:\\my_project');
await agent.start();

// 编辑项目
const clips = await agent.callTool('list_clips', {});
console.log('Current clips:', clips);

await agent.callTool('upsert_clip', {
  clip: {
    id: 'new_clip',
    name: 'Generated Clip',
    layerIndex: 0,
    startFrame: 0,
    duration: 120,
    targetWidth: 1920,
    targetHeight: 1080,
    filePath: null,
    fromPlugin: 'internal',
    typeName: 'DefaultClip'
  }
});

await agent.callTool('save_project', {
  changeReason: 'AI Agent generated new clip'
});

await agent.stop();
```

### Python Agent 集成

```python
# my_agent.py
import subprocess
import requests
import json
import time

class ProjectFrameCutAgent:
    def __init__(self, project_path):
        self.project_path = project_path
        self.endpoint = 'http://localhost:32123'
        self.server_process = None
    
    def start(self):
        print('🚀 Starting MCP server...')
        cmd = f'projectFrameCut-mcp start --project "{self.project_path}" --transport http --port 32123 --bg'
        
        self.server_process = subprocess.Popen(
            cmd,
            shell=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL
        )
        
        self.wait_for_server()
    
    def wait_for_server(self, max_retries=10):
        for i in range(max_retries):
            try:
                response = requests.post(self.endpoint, json={
                    'jsonrpc': '2.0',
                    'method': 'tools/list',
                    'id': 1
                })
                
                if response.status_code == 200:
                    print('✅ Server started successfully')
                    return
            except requests.ConnectionError:
                pass
            
            time.sleep(1)
        
        raise RuntimeError('Server failed to start')
    
    def call_tool(self, tool_name, arguments):
        response = requests.post(self.endpoint, json={
            'jsonrpc': '2.0',
            'method': 'tools/call',
            'params': {
                'name': tool_name,
                'arguments': arguments
            },
            'id': 1
        })
        
        data = response.json()
        if 'error' in data:
            raise RuntimeError(data['error']['message'])
        
        return data['result']
    
    def stop(self):
        print('🛑 Stopping server...')
        subprocess.run('projectFrameCut-mcp stop', shell=True)

# 使用示例
if __name__ == '__main__':
    agent = ProjectFrameCutAgent('D:\\my_project')
    agent.start()
    
    try:
        # 获取项目信息
        info = agent.call_tool('get_timeline_info', {})
        print(f'Project: {info["projectName"]}')
        print(f'Resolution: {info["width"]}x{info["height"]}')
        
        # 列出所有 Clips
        clips = agent.call_tool('list_clips', {})
        print(f'Total clips: {len(clips["clips"])}')
        
        # 添加新 Clip
        agent.call_tool('upsert_clip', {
            'clip': {
                'id': 'ai_generated_clip',
                'name': 'AI Generated',
                'layerIndex': 0,
                'startFrame': 0,
                'duration': 120,
                'targetWidth': 1920,
                'targetHeight': 1080,
                'filePath': None,
                'fromPlugin': 'internal',
                'typeName': 'DefaultClip'
            }
        })
        
        # 保存项目
        agent.call_tool('save_project', {
            'changeReason': 'AI Agent editing'
        })
        
    finally:
        agent.stop()
```

---

## 常见集成场景

### 场景 1：CI/CD 流程中的自动化编辑

```yaml
# .github/workflows/auto-edit.yml
name: Auto Edit Project

on:
  schedule:
    - cron: '0 0 * * *'  # 每天午夜运行

jobs:
  edit:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v2
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v2
        with:
          dotnet-version: '10.0.x'
      
      - name: Install Skill
        run: |
          mkdir -Force $env:USERPROFILE\.copilot\skills\projectFrameCut-mcp
          Copy-Item "projectFrameCut.McpServer\skill.js" $env:USERPROFILE\.copilot\skills\projectFrameCut-mcp\
          Copy-Item "projectFrameCut.McpServer\manifest.json" $env:USERPROFILE\.copilot\skills\projectFrameCut-mcp\
      
      - name: Start Server
        run: projectFrameCut-mcp start --project ./test_project --transport http --port 32123 --bg
      
      - name: Run Auto Edit Script
        run: python scripts/auto_edit.py
      
      - name: Stop Server
        run: projectFrameCut-mcp stop
      
      - name: Commit Changes
        run: |
          git config --local user.email "action@github.com"
          git config --local user.name "GitHub Action"
          git add .
          git commit -m "Auto-edited project" || true
          git push
```

### 场景 2：WebUI 中的即时编辑

```typescript
// web-app/server.ts
import express from 'express';
import { spawn } from 'child_process';
import fetch from 'node-fetch';

const app = express();
app.use(express.json());

let serverProcess: any = null;

app.post('/api/start-server', async (req, res) => {
  const { project } = req.body;
  
  if (serverProcess) {
    return res.status(400).json({ error: 'Server already running' });
  }
  
  try {
    const cmd = `projectFrameCut-mcp start --project "${project}" --transport http --port 32123 --bg`;
    serverProcess = spawn('cmd', ['/c', cmd]);
    
    // 等待服务器启动
    await new Promise(resolve => setTimeout(resolve, 3000));
    
    res.json({ status: 'Server started' });
  } catch (error) {
    res.status(500).json({ error: error.message });
  }
});

app.post('/api/call-tool', async (req, res) => {
  const { toolName, arguments: args } = req.body;
  
  try {
    const response = await fetch('http://localhost:32123', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        jsonrpc: '2.0',
        method: 'tools/call',
        params: {
          name: toolName,
          arguments: args
        },
        id: 1
      })
    });
    
    const data = await response.json();
    res.json(data);
  } catch (error) {
    res.status(500).json({ error: error.message });
  }
});

app.post('/api/stop-server', async (req, res) => {
  if (serverProcess) {
    serverProcess.kill();
    serverProcess = null;
  }
  res.json({ status: 'Server stopped' });
});

app.listen(3000, () => {
  console.log('Web API listening on port 3000');
});
```

### 场景 3：批量项目处理

```bash
#!/bin/bash
# batch_process.sh

PROJECTS_DIR="/mnt/projects"
LOG_FILE="batch_process.log"

for project_dir in "$PROJECTS_DIR"/*; do
    if [ -f "$project_dir/project.pjfc" ]; then
        echo "Processing: $project_dir" | tee -a "$LOG_FILE"
        
        # 启动服务器
        projectFrameCut-mcp start --project "$project_dir" --transport http --port 32123 --bg
        sleep 2
        
        # 运行编辑脚本
        python scripts/process_project.py "$project_dir" 2>&1 | tee -a "$LOG_FILE"
        
        # 停止服务器
        projectFrameCut-mcp stop
        sleep 1
    fi
done

echo "Batch processing completed" | tee -a "$LOG_FILE"
```

---

## 高级用法

### 自定义 Skill 扩展

```javascript
// ~/.copilot/skills/projectFrameCut-mcp/extensions/custom-tools.js
// 添加自定义工具

export async function customBatchImport(projectPath, assetsPath) {
  // 实现自定义的批量导入逻辑
  console.log(`Importing assets from ${assetsPath} to ${projectPath}`);
  
  // 1. 启动服务器
  // 2. 遍历资源文件
  // 3. 为每个资源创建 Clip
  // 4. 保存项目
}

export async function customBatchExport(projectPath, outputPath) {
  // 实现自定义的批量导出逻辑
  console.log(`Exporting project from ${projectPath} to ${outputPath}`);
}
```

### 性能优化

```javascript
// 使用连接池减少开销
class MCPClientPool {
  constructor(maxConnections = 5) {
    this.maxConnections = maxConnections;
    this.connections = [];
  }
  
  async getConnection() {
    if (this.connections.length < this.maxConnections) {
      return new MCPClient();
    }
    
    // 等待可用连接
    return await new Promise(resolve => {
      const checkInterval = setInterval(() => {
        if (this.connections.length < this.maxConnections) {
          clearInterval(checkInterval);
          resolve(new MCPClient());
        }
      }, 100);
    });
  }
}
```

---

## 📝 总结

projectFrameCut MCP Skill 提供了：

✅ 快速的 CLI 集成  
✅ 多种传输方式支持  
✅ 自动客户端生成  
✅ 与 Copilot CLI 无缝集成  
✅ 支持各种编程语言  
✅ 灵活的扩展机制  

通过这个 Skill，你可以轻松地在各种应用中集成 projectFrameCut MCP 服务器！
