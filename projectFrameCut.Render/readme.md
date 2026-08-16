# projectFrameCut 核心渲染库

这是 projectFrameCut 的核心渲染库，提供了渲染引擎的基础设施和常用效果实现。

`RPCProtocol` 提供基于 `projectFrameCut.Render.Contracts` 的进程内 Render 服务宿主。
编辑器通过 Protobuf DTO 发起请求，Render 层负责解码、Effect、合成与编码，并通过项目相对 Artifact 返回磁盘文件。
全局预览继续写入项目 `thumbs`，时间线 Clip 缩略图写入 `thumbs/perClip/<clip-id>/timeline`，DynamicPreview 的 Clip 分块预览写入 `thumbs/perClip/<clip-id>/dynamic`，最终布局仍由 UI 完成。
