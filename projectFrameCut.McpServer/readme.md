# projectFrameCut.McpServer
一个的MCP服务器，允许第三方Agent连接到projectFrameCut来控制软件。
MCP Server和Agent直接可以通过HTTP(推荐)或者stdio进行通信，软件和MCPServer之间通过WebSocket通信。

## 快速入门
1. 下载MCP Server的Release版本，解压到任意位置。
2. 运行命令：
```
projectFrameCut.McpServer http --project <项目根目录> --pullApplication
``` 
3. 等待App启动，然后在你的Agent App里连接到MCP Server (默认地址为`http://127.0.0.1:32123`)

## MCP服务器模式
`projectFrameCut.McpServer` 的第一个参数用于指定模式，支持`http`或者`stdio`两种模式。
`http`模式下，MCP Server会启动一个HTTP服务器来接受来自Agent的请求，推荐使用这种模式。
`stdio`模式下，MCP Server会通过标准输入输出与Agent通信，这种模式适合一些特殊的环境，比如没有网络权限的环境。

## 参数说明
`--project <项目根目录>`: 指定项目根目录，你必须指定这个参数。
`--pullApplication`: 自动拉起应用程序。需要你的设备已经安装了projectFrameCut的一个实例，并且已经运行了至少一次来完成必要的初始化。如果没有定义这个参数，你可以使用控制台里输出的URL来手动拉起应用程序。
`--port <端口号>`: 指定服务器的端口号，默认为32123，仅限HTTP模式。

### Skill
是的，这个东西是有skill的。在[skill](./skill)目录下，存在一些预定义的技能，可以让Agent们更好的实现一些复杂的功能。直接和你的Agent说安装它即可安装。


## 支持的工具

### 查询 - 时间线信息
| 工具 | 说明 |
|------|------|
| `get_timeline_info` | 获取时间线元数据：帧率、分辨率、总帧数、图层数 |
| `list_layers` | 列出时间线中的所有图层/轨道及其属性 |
| `list_available_effects` | 列出所有可用的效果类型及其参数和默认值 |
| `get_effect_info` | 获取特定效果类型的详细信息 |
| `get_project_metadata` | 获取项目元数据：名称、文件路径、创建/修改时间、文件大小 |

### 查询 - 连接客户端
| 工具 | 说明 |
|------|------|
| `list_connected_clients` | 列出当前连接的编辑器客户端 |
| `get_client_environment` | 查询已连接客户端的环境能力（效果/混合/插件） |
| `render_client_preview` | 请求从已连接客户端渲染一帧预览图像 |
| `apply_client_patch` | 在已连接客户端上应用剪辑补丁并同步回 UI |
| `move_client_clip` | 在已连接客户端上移动剪辑并同步回 UI |

### 剪辑管理
| 工具 | 说明 |
|------|------|
| `list_clips` | 列出当前项目中的所有剪辑 |
| `get_clip` | 通过 ID 获取单个剪辑 |
| `upsert_clip` | 创建或替换剪辑 |
| `move_clip` | 将剪辑移动到另一个轨道或帧位置 |
| `patch_clip` | 非破坏性更新选定的剪辑字段 |
| `delete_clip` | 通过 ID 删除剪辑 |

### 效果管理
| 工具 | 说明 |
|------|------|
| `add_effect` | 在剪辑上添加或替换一个效果 |
| `remove_effect` | 通过名称或 ID 从剪辑中移除效果 |
| `add_effect_bundle` | 在剪辑上添加或替换一个效果包 |
| `remove_effect_bundle` | 通过包 ID 从剪辑中移除效果包 |

### 项目管理
| 工具 | 说明 |
|------|------|
| `save_project` | 将当前项目状态持久化到磁盘 |

### 更多文档
想了解更多？你可以查看[docs](./docs/)目录下的文档，~~或者直接查看代码~~。
