# projectFrameCut MCP Skill 快速开始

## 🚀 安装

### 1. 复制 Skill 文件
将 Skill 文件复制到 Copilot CLI Skill 目录：

```bash
# 从项目目录复制
cp projectFrameCut.McpServer/skill.js ~/.copilot/skills/projectFrameCut-mcp/
cp projectFrameCut.McpServer/manifest.json ~/.copilot/skills/projectFrameCut-mcp/
```

### 2. 验证安装
```bash
projectFrameCut-mcp help
```

预期输出：显示所有可用命令和选项

---

## 📋 快速命令参考

### 启动服务器

```bash
# 本地 Stdio 模式（推荐用于本地 Agent）
projectFrameCut-mcp start --project D:\projects\my_video

# HTTP 模式（推荐用于远程/WebUI）
projectFrameCut-mcp start --project D:\projects\my_video --transport http --port 32123

# 后台运行
projectFrameCut-mcp start --project D:\projects\my_video --transport http --port 32123 --bg
```

### 检查服务器状态

```bash
projectFrameCut-mcp status

# 输出示例：
# ✅ Server is running and responding
# 
# 📋 Available Tools:
#   • list_clips
#   • get_clip
#   • upsert_clip
#   • move_clip
#   • patch_clip
#   • delete_clip
#   • add_effect
#   • remove_effect
#   • add_effect_bundle
#   • remove_effect_bundle
#   • save_project
#   • get_timeline_info
#   • list_layers
#   • list_available_effects
#   • get_effect_info
#   • get_project_metadata
```

### 获取项目信息

```bash
projectFrameCut-mcp project-info --project D:\projects\my_video

# 输出示例：
# 📊 Project Information
# ======================
# Name: My Video Project
# Resolution: 1920x1080
# Frame Rate: 60 fps
# Clips: 5
# Last Changed: Initial
# Saved At: 2026-05-14T21:00:00Z
# Project Path: D:\projects\my_video
```

### 生成客户端代码

```bash
# Python 客户端
projectFrameCut-mcp generate-client --language python --output ./mcp_client.py

# JavaScript 客户端
projectFrameCut-mcp generate-client --language js --output ./mcp_client.js

# 验证生成的文件
cat mcp_client.py    # 查看 Python 客户端
cat mcp_client.js    # 查看 JavaScript 客户端
```

### 查看测试文档

```bash
projectFrameCut-mcp test

# 显示所有测试用例和测试指南
```

### 停止服务器

```bash
# 停止特定实例
projectFrameCut-mcp stop

# 停止所有 dotnet 进程
projectFrameCut-mcp stop --all
```

---

## 🔧 常见工作流

### 工作流 1：本地 AI Agent 编辑

```bash
# 1. 启动服务器（Stdio 模式）
projectFrameCut-mcp start --project D:\my_project

# 2. 在另一个终端运行 Agent
python my_agent.py --transport stdio

# 3. Agent 发送命令给 MCP 服务器
# 例如：获取所有 Clips，添加新 Clip，保存项目
```

### 工作流 2：HTTP 模式下的远程编辑

```bash
# 1. 启动 HTTP 服务器（后台）
projectFrameCut-mcp start --project D:\my_project --transport http --port 32123 --bg

# 2. 验证服务器运行
projectFrameCut-mcp status

# 3. 生成 Python 客户端
projectFrameCut-mcp generate-client --language python --output ./client.py

# 4. 使用客户端编辑项目
python -c "
from client import MCPClient
client = MCPClient('http://localhost:32123')
clips = client.list_clips()
print(clips)
"
```

### 工作流 3：自动化批量编辑

```bash
# 1. 准备批量编辑脚本（JSON）
cat > batch_ops.json << 'EOF'
[
  {
    "operation": "upsert_clip",
    "params": {
      "clip": {
        "id": "auto_clip_1",
        "name": "Auto Clip",
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
  }
]
EOF

# 2. 查看批量编辑脚本
projectFrameCut-mcp batch-edit --script batch_ops.json --project D:\my_project

# 3. 启动服务器并执行操作
projectFrameCut-mcp start --project D:\my_project --transport http --port 32123 --bg
```

---

## 💡 使用技巧

### 技巧 1：快速切换项目

```bash
# 定义环境变量
set PJFC_PROJECT=D:\projects\my_video

# 使用环境变量
projectFrameCut-mcp start --project %PJFC_PROJECT%
```

### 技巧 2：健康检查脚本

```bash
# 创建健康检查脚本
@echo off
for /l %%x in (1, 1, 5) do (
  echo Attempt %%x...
  projectFrameCut-mcp status
  if %errorlevel% == 0 (
    echo ✅ Server is healthy
    exit /b 0
  )
  timeout /t 2 /nobreak
)
echo ❌ Server is not responding
exit /b 1
```

### 技巧 3：批量启动多个服务器

```bash
# 为每个项目启动一个服务器（不同端口）
for /d %%p in (D:\projects\*) do (
  echo Starting server for %%p
  projectFrameCut-mcp start --project %%p --transport http --port 32124 --bg
  timeout /t 2
)
```

---

## 🔍 故障排查

### 问题 1：找不到项目目录

```
❌ Project path not found: D:\projects\my_video

解决方案：
- 确保路径存在：dir D:\projects\my_video
- 检查是否有空格，必要时用引号：--project "D:\my projects\video"
```

### 问题 2：服务器启动失败

```
❌ Server exited with error

解决方案：
- 检查 .NET SDK：dotnet --version
- 检查 MCP 服务器代码是否有编译错误
- 查看项目文件是否有效：type project.pjfc
```

### 问题 3：连接被拒绝

```
❌ Server is not responding

解决方案：
- 检查端口是否被占用：netstat -ano | findstr :32123
- 检查防火墙设置
- 确保服务器已启动：projectFrameCut-mcp start --project <path>
```

### 问题 4：权限错误

```
拒绝访问错误

解决方案：
- 以管理员身份运行命令提示符
- 检查项目文件夹权限
- 检查日志文件权限
```

---

## 📚 相关文档

- **SKILL_IMPLEMENTATION.md** - 完整的 Skill 实现指南
- **README.md** - MCP 服务器使用指南
- **MCP_TESTS.md** - 测试用例和测试指南
- **MCP_TOOL_MAPPING.md** - 工具映射和架构文档
- **COMPLETION_REPORT.md** - 项目完成总结

---

## 🎯 下一步

1. ✅ 安装 Skill 文件到 `~/.copilot/skills/projectFrameCut-mcp/`
2. ✅ 运行 `projectFrameCut-mcp help` 验证安装
3. ✅ 使用 `projectFrameCut-mcp start` 启动第一个服务器
4. ✅ 生成客户端代码并测试连接
5. ✅ 阅读详细文档进行高级配置

---

## 📞 支持

如有问题，请参考：
- Skill 文档：`SKILL_IMPLEMENTATION.md`
- 服务器文档：`README.md`
- 测试指南：`MCP_TESTS.md`

或联系项目维护者。
