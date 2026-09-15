# External plugin backend example

这个工具是一个独立进程示例。它复用 `projectFrameCut.ExamplePlugin1` 的 provider，
由 `ExternalPluginBackend.RunAsync` 接收主 App 传入的 `plugin_worker`、命名管道、token、
会话目录、插件 ID 和插件根目录参数，然后提供以下远程能力：

- `ExampleInvert` 图片效果
- `ExampleVideoSource` 视频源
- `ExampleAudioSource` 音频源
- `ExampleToneTrack` 音轨
- `ExampleCrossfade` Transform
- `ExampleAddComputer` Computer
- `ExampleFrameStream` VideoWriter

## 构建

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
  --external-capabilities Effects,VideoSources,AudioSources,SoundTracks,Transforms,Computers,VideoWriters `
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
