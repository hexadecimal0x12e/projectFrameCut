# projectFrameCut MCP Skill 完整包

## 📦 Skill 内容清单

本 Skill 包包含以下文件和功能：

### 核心文件

| 文件 | 大小 | 描述 |
|---|---|---|
| `skill.js` | ~18KB | 主程序 - CLI 命令实现 |
| `manifest.json` | ~5KB | Skill 元数据和配置 |

### 文档文件

| 文件 | 描述 |
|---|---|
| `SKILL_IMPLEMENTATION.md` | 完整的 Skill 实现指南（16KB） |
| `SKILL_QUICKSTART.md` | 快速开始指南（5KB） |
| `SKILL_INTEGRATION_GUIDE.md` | 集成指南（14KB） |
| `README.md` | MCP 服务器使用指南（9KB） |
| `MCP_TESTS.md` | 测试用例和指南（5KB） |
| `MCP_TOOL_MAPPING.md` | 工具映射文档（3KB） |
| `COMPLETION_REPORT.md` | 项目完成报告（5KB） |

---

## 🚀 快速安装

### Windows (PowerShell)

```powershell
# 1. 创建 Skill 目录
$skillDir = "$env:USERPROFILE\.copilot\skills\projectFrameCut-mcp"
mkdir -Force $skillDir

# 2. 复制文件
Copy-Item "projectFrameCut.McpServer\skill.js" $skillDir
Copy-Item "projectFrameCut.McpServer\manifest.json" $skillDir

# 3. 验证
projectFrameCut-mcp help
```

### Linux/macOS

```bash
# 1. 创建 Skill 目录
mkdir -p ~/.copilot/skills/projectFrameCut-mcp

# 2. 复制文件
cp projectFrameCut.McpServer/skill.js ~/.copilot/skills/projectFrameCut-mcp/
cp projectFrameCut.McpServer/manifest.json ~/.copilot/skills/projectFrameCut-mcp/

# 3. 验证
projectFrameCut-mcp help
```

---

## 🎯 主要功能

### 1. 服务器管理

```bash
# 启动 Stdio 模式（本地）
projectFrameCut-mcp start --project ~/my_project

# 启动 HTTP 模式（远程）
projectFrameCut-mcp start --project ~/my_project --transport http --port 32123

# 后台运行
projectFrameCut-mcp start --project ~/my_project --transport http --port 32123 --bg

# 检查状态
projectFrameCut-mcp status

# 停止服务器
projectFrameCut-mcp stop
```

### 2. 项目查询

```bash
# 获取项目信息
projectFrameCut-mcp project-info --project ~/my_project

# 输出示例：
# 📊 Project Information
# ======================
# Name: My Video Project
# Resolution: 1920x1080
# Frame Rate: 60 fps
# Clips: 5
```

### 3. 客户端生成

```bash
# 生成 Python 客户端
projectFrameCut-mcp generate-client --language python --output ./client.py

# 生成 JavaScript 客户端
projectFrameCut-mcp generate-client --language js --output ./client.js
```

### 4. 测试工具

```bash
# 查看测试文档
projectFrameCut-mcp test

# 展示所有 15 个测试用例
```

### 5. 批量编辑

```bash
# 查看批量编辑脚本
projectFrameCut-mcp batch-edit --script ops.json --project ~/my_project
```

---

## 📋 支持的命令

| 命令 | 用途 | 示例 |
|---|---|---|
| `start` | 启动服务器 | `start --project ~/proj --transport http --port 32123 --bg` |
| `stop` | 停止服务器 | `stop` / `stop --all` |
| `status` | 检查状态 | `status` |
| `test` | 显示测试 | `test` |
| `generate-client` | 生成客户端 | `generate-client --language python --output ./client.py` |
| `project-info` | 项目信息 | `project-info --project ~/proj` |
| `batch-edit` | 批量编辑 | `batch-edit --script ops.json --project ~/proj` |
| `help` | 帮助信息 | `help` |

---

## 🔧 配置选项

### 启动命令选项

| 选项 | 类型 | 默认值 | 描述 |
|---|---|---|---|
| `--project` | string | 必需 | 项目目录路径 |
| `--transport` | enum | stdio | 传输方式（stdio 或 http） |
| `--port` | number | 32123 | HTTP 端口 |
| `--bg` | boolean | false | 后台运行 |

### 其他命令选项

| 命令 | 选项 | 类型 | 描述 |
|---|---|---|---|
| `stop` | `--all` | boolean | 停止所有 dotnet 进程 |
| `status` | `--endpoint` | string | 服务器端点 URL |
| `test` | `--project` | string | 项目路径 |
| `test` | `--case` | string | 特定测试用例 |
| `generate-client` | `--language` | enum | 编程语言（python 或 js） |
| `generate-client` | `--output` | string | 输出文件路径 |
| `project-info` | `--project` | string | 项目路径 |
| `batch-edit` | `--script` | string | JSON 脚本路径 |
| `batch-edit` | `--project` | string | 项目路径 |

---

## 💡 使用场景示例

### 场景 1：本地 AI 编辑

```bash
# 启动服务器
projectFrameCut-mcp start --project D:\my_project

# 在另一个终端运行 Agent
python my_agent.py
```

### 场景 2：HTTP 模式远程编辑

```bash
# 启动服务器（后台）
projectFrameCut-mcp start --project D:\my_project --transport http --port 32123 --bg

# 验证服务器
projectFrameCut-mcp status

# 使用生成的客户端
python -c "
from client import MCPClient
client = MCPClient('http://localhost:32123')
print(client.list_clips())
"
```

### 场景 3：自动化批量处理

```bash
# 为每个项目启动服务器并处理
for proj in projects/*; do
  projectFrameCut-mcp start --project "$proj" --transport http --port 32123 --bg
  python process.py "$proj"
  projectFrameCut-mcp stop
done
```

---

## 🔌 集成示例

### Node.js 集成

```javascript
import { spawn } from 'child_process';

async function startServer(projectPath) {
  return spawn('projectFrameCut-mcp', [
    'start',
    '--project', projectPath,
    '--transport', 'http',
    '--port', '32123',
    '--bg'
  ]);
}

await startServer('D:\\my_project');
```

### Python 集成

```python
import subprocess
import requests

# 启动服务器
subprocess.run([
    'projectFrameCut-mcp', 'start',
    '--project', 'D:\\my_project',
    '--transport', 'http',
    '--port', '32123',
    '--bg'
])

# 调用工具
response = requests.post('http://localhost:32123', json={
    'jsonrpc': '2.0',
    'method': 'tools/call',
    'params': {
        'name': 'list_clips',
        'arguments': {}
    },
    'id': 1
})
```

### Copilot CLI 集成

```bash
# 使用 /skill 命令
/skill projectFrameCut-mcp start --project ~/my_project --transport http --port 32123 --bg

# 使用 /assist 命令
/assist "Start a projectFrameCut MCP server for ~/my_project"
```

---

## 📚 文档导航

### 快速开始
- **SKILL_QUICKSTART.md** - 5 分钟快速上手

### 详细指南
- **SKILL_IMPLEMENTATION.md** - 完整实现细节
- **SKILL_INTEGRATION_GUIDE.md** - 集成到你的应用
- **README.md** - MCP 服务器完整文档

### 参考资料
- **MCP_TESTS.md** - 15 个测试用例
- **MCP_TOOL_MAPPING.md** - 工具映射和架构
- **COMPLETION_REPORT.md** - 项目完成总结

---

## 🎯 典型工作流

### 工作流 1：开发

```
1️⃣ 启动开发服务器
   projectFrameCut-mcp start --project ~/dev_proj

2️⃣ 开发 Agent 代码
   在编辑器中编写 Agent 代码

3️⃣ 测试与调试
   运行 Agent，观察结果

4️⃣ 验证项目
   检查生成的项目文件
```

### 工作流 2：生产部署

```
1️⃣ 生成生产客户端
   projectFrameCut-mcp generate-client --language python --output ./prod_client.py

2️⃣ 启动生产服务器（HTTP 模式）
   projectFrameCut-mcp start --project ~/prod_proj --transport http --port 32123 --bg

3️⃣ 部署应用
   使用生产客户端连接服务器

4️⃣ 监控和维护
   定期检查服务器状态：projectFrameCut-mcp status
```

### 工作流 3：批量处理

```
1️⃣ 准备项目列表
   projects/proj1, projects/proj2, ...

2️⃣ 创建处理脚本
   编写 Python/JavaScript 脚本

3️⃣ 批量执行
   为每个项目启动服务器、处理、保存

4️⃣ 验证结果
   检查所有项目的修改
```

---

## ⚙️ 系统要求

- **操作系统**: Windows 10+, Linux, macOS
- **运行环境**: Node.js 18+, .NET SDK 10.0+
- **内存**: 最小 512MB（推荐 2GB+）
- **磁盘**: 最小 500MB 用于 .NET SDK 和 MCP 服务器
- **网络**: 如使用 HTTP 模式需要网络连接

---

## 🐛 故障排查

### 服务器启动失败

```bash
# 检查 .NET SDK
dotnet --version

# 检查 MCP 服务器代码
dotnet build projectFrameCut.McpServer

# 查看项目文件
dir /s project.pjfc timeline.json
```

### 连接被拒绝

```bash
# 检查端口占用
netstat -ano | findstr :32123

# 检查防火墙
# Windows Defender Firewall -> 允许应用通过防火墙

# 尝试不同端口
projectFrameCut-mcp start --project ~/proj --transport http --port 32124
```

### 权限错误

```bash
# 以管理员身份运行
# PowerShell: 右键 -> "以管理员身份运行"

# 或使用 sudo（Linux/macOS）
sudo projectFrameCut-mcp start --project ~/proj
```

---

## 📞 获取帮助

1. **查看帮助信息**
   ```bash
   projectFrameCut-mcp help
   projectFrameCut-mcp <command> --help
   ```

2. **查看文档**
   - 快速开始: SKILL_QUICKSTART.md
   - 详细指南: SKILL_IMPLEMENTATION.md
   - 集成示例: SKILL_INTEGRATION_GUIDE.md

3. **检查日志**
   ```bash
   cat ~/.copilot/skills/projectFrameCut-mcp/skill.log
   ```

4. **测试连接**
   ```bash
   projectFrameCut-mcp status
   ```

---

## 📊 项目统计

| 指标 | 值 |
|---|---|
| Skill 代码行数 | ~550 |
| 支持的命令 | 8 |
| 支持的语言 | 2 (Python, JavaScript) |
| 包含文档 | 8 篇 |
| 总文档字数 | ~70KB |
| 测试用例 | 15 个 |

---

## ✨ 功能优势

✅ **易于集成** - 一键启动服务器  
✅ **双传输支持** - Stdio + HTTP  
✅ **自动代码生成** - Python/JavaScript 客户端  
✅ **完整文档** - 8 篇详细文档  
✅ **多种场景** - 开发、生产、批量处理  
✅ **零配置** - 开箱即用  
✅ **Copilot CLI 集成** - 原生支持  

---

## 🚀 下一步

1. ✅ 复制 Skill 文件到 `~/.copilot/skills/projectFrameCut-mcp/`
2. ✅ 运行 `projectFrameCut-mcp help` 验证
3. ✅ 阅读 **SKILL_QUICKSTART.md** 了解基本用法
4. ✅ 启动第一个服务器
5. ✅ 生成客户端代码
6. ✅ 集成到你的应用

祝你使用愉快！🎉
