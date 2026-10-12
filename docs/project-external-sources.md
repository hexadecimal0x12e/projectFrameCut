# 项目随附外置媒体源

在添加素材面板的“RPC 外部源”Tab 选择“添加外置源”，选中发布目录。整个目录会复制到 `externalSource/<ImportId>/`，索引保存在 `externalSource/index.json`。相同清单 ID 可替换、跳过或同时保留；替换保持 ImportId，清单哈希会更新。

每次打开编辑或独立导出页面时，Popup 默认不选任何源。选择仅在本次打开期间有效，从编辑进入导出会继承选择。导入的新源立即加载。跳过或加载失败时保留素材、时间线参数和描述快照，显示已有的初始化失败占位画面。

项目设置的“外部源”Tab 管理 Assembly 源和已注册视频或音频源的 RPC 客户端。Assembly 源的加载、卸载会同步本次允许列表与预览后端；卸载保留目录和索引，移除会删除项目副本和索引，时间线引用仍保留。未打开项目的设置页可查看和移除 Assembly 源，加载、卸载需要进入项目。

RPC 客户端的卸载会释放视频和音频实例并暂时停用源，保留注册目录供重新加载；客户端更新目录不会自动解除卸载状态。移除清除两类源的注册，保留 RPC 连接和授权，客户端可重新注册。RPC 管理操作只接受应用自身的管理连接，外部客户端不能管理其他客户端。

## 清单版本 1

源目录根部必须有 `external-source.json`，使用 UTF-8 JSON：

```json
{
  "formatVersion": 1,
  "id": "example.video",
  "name": "Example source",
  "version": "1.0.0",
  "author": "Example author",
  "description": "Optional description",
  "authorUrl": "https://example.com",
  "assembly": "Example.dll",
  "entryPoint": "Example.VideoProvider",
  "files": {
    "Example.dll": "<64 hexadecimal SHA-256 characters>",
    "dependencies/Dependency.dll": "<64 hexadecimal SHA-256 characters>"
  }
}
```

除 `description` 和 `authorUrl` 外均为必填。`id` 是发布者定义的稳定 ID。路径使用 `/`，相对于源目录，禁止绝对路径、`.`、`..` 和目录链接。`files` 必须覆盖除根部清单自身外的所有文件，包括依赖、原生库、配置、资源和子目录中的文件；文件缺失、多余或哈希不符均拒绝加载。空目录也会复制。修改文件后需要重新生成清单并重新导入。

入口必须公开、非抽象、具有公开无参构造函数，并实现 `projectFrameCut.Render.Contracts.IExternalVideoSourceProvider` 或 `IExternalAudioSourceProvider`，也可以同时实现两者。发布普通 DLL 与依赖即可，不需要项目插件的加密包。Assembly 只会在隔离 Worker 内加载；托管与原生依赖由独立加载上下文解析，契约程序集使用 Worker 的版本。

## Provider 契约

- `Sources` 可返回多个描述，`SourceId` 在 Provider 内唯一且跨版本稳定，`DecoderName` 非空。填写尺寸、帧率、帧数、位深及 Alpha/HDR 能力。
- `CreateAsync` 接收 `request.Source`，包含 SourceId、DecoderName、描述快照和持久化元数据，返回非空且唯一的 InstanceId。
- `InitializeAsync` 返回同一 InstanceId 及实际描述；`ReadFrameAsync` 和 `ReleaseAsync` 只接受本 Worker 创建的实例。
- `ExternalVideoSourceDescriptor.AllowCachingResult` 表示同一帧的内容是否始终相同，默认 `true`。摄像头、直播或依赖外部状态的源设为 `false`，预览不复用帧结果，也不进行缓存预热；RPC 客户端源和项目 Assembly 外部源共用此字段。初始化返回的实际描述应保留该值。
- 读取使用 `TargetFrame`；`UseRegion=true` 时，按 SourceX/Y/Width/Height 裁剪并缩放到 TargetWidth/Height。遵守 Index、EnableLock、StrictMode 和 CancellationToken。
- RGB 是逐平面的 byte 数组，8 位每像素 1 字节，16 位每像素 2 字节；16 位按宿主端序编码（当前桌面平台为小端）。Alpha、HDR Brightness 是每像素 4 字节 float 平面；未提供时为空。HDR 同时填写 MaximumBrightness。
- Worker 串行调用 Provider。关闭和管道断开时尝试释放剩余实例；Provider 可实现 IDisposable/IAsyncDisposable 清理自身资源。超时或进程异常会终止 Worker。
- Metadata 和描述会保存在项目中，只应包含可移植的源参数；不要放绝对路径、凭据或运行时 InstanceId。

项目时间线保存 `#ProjectExternalVideoSource:<编码引用>`，其中 ClientId 字段表示 ImportId。引用包含 SourceId、DecoderName、Metadata 和描述快照。现有 `#RemoteRpcVideoSource:` 客户端源保持原有行为。

音频 Provider 实现 `IExternalAudioSourceProvider`：

- `AudioSources` 返回音频目录，填写 SourceId、DecoderName、Name、Duration（秒）、ChannelCount 和 SamplePerSecond。
- `CreateAudioAsync`、`InitializeAudioAsync` 和 `ReleaseAudioAsync` 管理独立音频实例，初始化返回同一 InstanceId 及实际声道数、采样率和时长。
- `ReadSamplesAsync` 接收 State、StartIndex 和 Count；StartIndex、Count 均按每声道采样数计算。返回 `ExternalAudioSamples`，PCM 使用逐声道排列的小端 float32，字节数必须等于 ChannelCount × SampleCount × 4，SampleCount 不超过请求 Count。到达末尾可返回空数据。
- 音频载荷沿用 Inline、SharedMemory、LocalFile 三种传输方式，读取完成后释放。Worker 串行调用 Provider，并在关闭、卸载或管道断开时释放音频实例。

音频时间线引用为 `#ProjectExternalAudioSource:<编码引用>`，RPC 音频引用为 `#RemoteRpcAudioSource:<编码引用>`。音频描述与视频描述分别保存，不需要迁移旧项目。

跳过或无法初始化的外部音轨保留引用并记录 `ExternalSourceUnavailable`，预览和导出跳过该音轨，其余内容继续处理。重新加载源后刷新项目会重新初始化音轨；读取期间发生的异常仍会上报。

## 隔离与 CLI

GUI 和应用 CLI 在 Windows 使用 AppContainer，其他桌面平台使用进程隔离；StandaloneRender 在所有桌面平台使用进程隔离。Windows 会将源复制到独立运行目录并设置现有 Worker 所需 ACL，沿用包内激活机制。

应用 CLI `render`、`headless` 及 StandaloneRender 接受：

```text
--allowExternalSources=all
--allowExternalSources=<ImportId>,<ImportId>
```

默认不加载。允许列表在运行时绑定 ImportId 与当前索引清单哈希。GUI 给自身渲染子进程传递确认时的 ID 和哈希，刷新预览不重复询问，也不会自动允许清单变更。RPC 新增 `ListProjectExternalSources` 和 `SetProjectExternalSources`，请求使用 ProjectRoot，设置请求包含 AllowedSources（ImportId、ManifestSha256）；OpenProject 和 OpenHeadlessProject 也接受 AllowedExternalSources。

管理连接还提供 `ListExternalVideoSourceClients` 和 `ManageExternalVideoSourceClient`；列表的 AudioSources 包含音频目录，后者包含 ClientId 和 Action（Load、Unload、Remove），同时管理该客户端的视频和音频源。

## 示例及验收

`tools/projectFrameCut.ExternalSourceExample` 提供四个视频源（8 位、16 位、Alpha 和 HDR）及一个 48 kHz 双声道音频源。发布后运行 `Create-Manifest.ps1 -Directory <发布目录>`，将目录导入项目。示例帧随帧号变化，并支持区域读取；音频左右声道分别生成 440 Hz 和 660 Hz 正弦波。

```powershell
dotnet publish tools/projectFrameCut.ExternalSourceExample -o artifacts/external-source-example
./tools/projectFrameCut.ExternalSourceExample/Create-Manifest.ps1 -Directory artifacts/external-source-example
```

现有测试项目新增清单/文件集合、导入回滚、索引搬移、引用与允许列表往返、跨后端实例隔离、管道退出清理及三种帧载荷模式的检查。实际 GUI/进程验收需另外执行：

1. 导入示例，检查四个源及作者、版本、状态，分别添加到时间线并检查搜索、排序、刷新。
2. 重新打开，选择部分、全选或全部跳过；检查占位与正常帧、编辑到导出的选择继承、直接导出页面的 Popup。
3. 替换或同时保留同 ID 包；复制、导出并搬移项目，检查索引和时间线引用仍可用。
4. 删除或修改源文件、破坏依赖、终止 Worker，检查具体错误日志与占位；重新导入并允许后检查恢复。
5. GUI、应用 CLI 与 StandaloneRender 分别读取和导出 8/16 位、Alpha、HDR 及裁剪帧，确认关闭项目后进程和临时载荷释放。
6. 在项目设置中加载、卸载和移除两类源，检查素材列表与预览立即更新，重新加载恢复读帧；RPC 移除后授权仍有效且可重新注册。
