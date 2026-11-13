# ScriptLab · 四幕动态图文实验室

无需外部素材。默认生成 18 秒、1920×1080 / 30 fps 的四幕动态图文；应用到已有项目时继承其尺寸和帧率，在现有时长后留一秒再追加，不删除现有内容。

1. 片头：标题、副标题、带随机种子的分组矢量粒子。
2. 图表：JSON 驱动的柱条、错峰生长动画、标签和数值。
3. 参数卡片：三张卡片，展示布尔、数字和 JSON 参数；参数说明通过内置 Progress 绑定到 ClipPosition 效果的 Opacity，逐渐显现。
4. 片尾：完成提示、圆角矩形和横线装饰。

默认生成 51 个片段、25 条轨道、29 组动画、121 个关键帧，以及 18 个粒子组件。文字、柱条和卡片分别放到独立轨道，便于检查。

## 导入与测试

将同目录的 `ScriptLab.pjfcTemplate` 导入模板库，选择创建项目；也可以从已打开项目的添加片段面板应用。建议先用新项目检查默认效果。

- 默认参数：验证完整执行、Batch 提交、时间线及保存结果。
- `UseBatch=false`：验证逐条 RPC 应用与最后保存。
- `UseBatch=false`、`FailAfterScene=2`：故意在第二幕后报错，检查已完成的修改仍保留在编辑器中；失败路径不主动保存。
- `UseBatch=true`、`FailAfterScene=2`：检查未提交的 Batch 被取消，已有项目内容保留。
- 增加 `ParticleCount`，或修改 `Bars` 的项目数、数值、主题色和随机种子：验证循环、参数类型与可编辑组件。
- 标题包含单引号、双引号、反引号或换行：验证参数按数据传入。
- 执行中取消或关闭项目：检查隔离进程、连接及未提交 Batch 的清理。

`Bars` 示例：

```json
[{"Label":"第一项","Value":35},{"Label":"第二项","Value":95},{"Label":"第三项","Value":68}]
```

模板直接使用宿主注入的 `$TemplateParameters` 和 RPC 连接，不自行导入模块或重新连接。脚本会先检查参数和内置样式、效果、矢量类型，再开始修改项目。实际画面和隔离执行结果由应用内测试确认。

## 修改并重新打包

编辑 `script.ps1` 或 `template.json` 后，在 PowerShell 中运行：

```powershell
./Build.ps1
```

打包脚本只使用 .NET ZIP 和 SHA-256，不需要启动应用或加载 projectFrameCut 模块；会更新 `template.json` 的脚本哈希，并生成 `ScriptLab.pjfcTemplate`。
