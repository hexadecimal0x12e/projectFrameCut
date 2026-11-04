# External plugin backend example

这个工具是一个独立进程示例。它复用 `projectFrameCut.ExamplePlugin1` 的 provider，并增加带外配置 UI 示例，
由 `ExternalPluginBackend.RunAsync` 接收主 App 传入的 `plugin_worker`、命名管道、token、
会话目录、插件 ID 和插件根目录参数，然后提供以下远程能力：

- `ExampleInvert` 图片效果
- `ExampleExternalInvert` 图片效果，使用自定义端口和 Windows Forms 配置窗口
- `ExampleVideoSource` 视频源
- `ExampleAudioSource` 音频源
- `ExampleToneTrack` 音轨
- `ExampleCrossfade` Transform
- `ExampleFrameStream` VideoWriter

## 构建

Windows 上默认目标为 `net11.0-windows`，同一个 exe 同时提供后端和 WinForms 编辑器。
下面的非独立发布需要安装 .NET 11 Windows Desktop Runtime；其他平台仍使用 `net11.0` 后端，不提供 WinForms 编辑器。

```powershell
dotnet publish tools/projectFrameCut.ExternalPluginBackendExample/projectFrameCut.ExternalPluginBackendExample.csproj `
  -c Release -r win-x64 --self-contained false `
  -o .\artifacts\external-plugin-example\win-x64
```

发布目录应作为 v3 包的 staging 目录。入口文件是：

```text
projectFrameCut.ExternalPluginBackendExample.exe
```

请保留整个 `dotnet publish` 输出目录，不要只复制 exe。示例后端运行时还需要
`SomePublisher.MyPlugin.dll` 及其依赖；它们是外置进程的依赖文件，不是主 App 要解密加载的
`<PluginId>.dll.enc`。V3 打包工具会将这些依赖作为普通签名文件放进包内。

使用签名证书打包时，入口、所有依赖 DLL 和运行时配置都会被 manifest 覆盖：

```powershell
dotnet run --project tools/projectFrameCut.PluginPackageUtility -- pack `
  --input .\artifacts\external-plugin-example\win-x64 `
  --output .\artifacts\Example.ExternalBackend.pjfcPlugin `
  --plugin-id SomePublisher.MyPlugin `
  --version 5.6.7.8 `
  --name "A usable example plugin" `
  --author SomePublisher `
  --description "A small plugin with one working example of every render provider." `
  --author-url https://example.com `
  --publishing-url https://example.com `
  --backend External `
  --windows-entry projectFrameCut.ExternalPluginBackendExample.exe `
  --external-capabilities Effects,VideoSources,AudioSources,SoundTracks,VideoWriters `
  --certificate .\publisher-plugin-signing.pfx `
  --chain .\publisher-chain.pem `
  --password-env PJFC_PLUGIN_PFX_PASSWORD
```

导入生成的包后，主 App 会在插件缓存目录下拉起这个 exe，并由它主动连接主 App 创建的命名管道。
此示例没有声明 `Clips` 或 `VectorComponents`，因此不会伪造未实现的能力目录。

重新打包后需要在主 App 中重新导入包；旧的已安装目录不会自动替换。排查时可解压包确认：

- `metadata.json` 的 `BackendKind` 为 `External`，`PackageFormatVersion` 为 `3`；
- 包内同时存在入口 exe 和 `SomePublisher.MyPlugin.dll`；
- `manifest.json` 覆盖这些文件。

## Windows 带外配置 UI

1. 按上面的命令发布、打包并重新导入插件，打开一个可写的本地项目，并允许外部 RPC。
2. 给图片或视频 Clip 添加“外部配置反色”（`ExampleExternalInvert`）效果。
3. 在默认属性面板或效果节点的“反色配置”参数旁点击“打开外部编辑器”。
4. 在 WinForms 窗口中修改反色强度（0–100%）和 RGB 通道开关，点击“应用”查看主程序中的变化，或点击“确定”提交并关闭。

窗口打开后通过 `GetClipEffectProvider` 读取当前配置，点击“重新读取”可以取回最新值。
修改控件不会立即提交；“关闭”保留已经应用的配置，丢弃尚未提交的修改。主程序退出时窗口自动关闭。
切换主程序选中的 Clip 后，编辑器仍操作打开时传入的 Clip 和 Provider。

`ExternalInvertEffectProvider` 在 `Settings` 端口的 `DataFormat` 中声明 Windows 启动入口：

```csharp
new CustomEffectPortEditorConfiguration
{
    Windows = new()
    {
        EntryPoint = "projectFrameCut.ExternalPluginBackendExample.exe",
        Arguments = ["edit_invert"],
    },
}
```

宿主追加 `--pipe`、`--clipId`、`--providerId`、`--fieldId`、`--parentPid` 参数。
`Program.Main` 使用 STA 线程启动 WinForms 消息循环；`plugin_worker` 模式仍运行原来的外部后端协议。

参数通过 `CustomEffectPortValue` 保存，契约 ID 为 `SomePublisher.MyPlugin.invert-settings.v1`，数据例如：

```json
{
  "ContractId": "SomePublisher.MyPlugin.invert-settings.v1",
  "Data": { "Amount": 0.75, "Red": true, "Green": false, "Blue": true }
}
```

“应用”通过 `SetClipEffectProvider` 仅更新 `Fields.Settings`，由 GUI RPC 完成效果重建、历史记录和预览刷新。
渲染实现复用 `ExampleInvertEffect` 的 FP16、8/16 位反色处理，并保留未勾选的通道、原始透明度及 HDR 亮度数据。
完整端口协议见[自定义端口的外部编辑器](../../docs/custom-effect-port-editor.md)。
