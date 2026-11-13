# 渲染性能诊断

2026-10-17。范围包括导出、RPC 预览、动态预览、视频解码器池及 Windows/Headless ILGPU 路径。以下结论来自当前工作区的调用链和资源生命周期；具体项目中各项占比，需要由实际渲染日志确认。

## 为什么解码活跃，GPU 计算却很少

当前图像管线以 CPU 内存中的 `IPicture` 为中心。硬件解码、GPU 特效和硬件编码之间存在多次读回、格式转换和重新上传。

```mermaid
flowchart LR
    D[硬件解码] --> R[读回 CPU / sws 转换]
    R --> P[CPU IPicture]
    P --> C[CPU 转换 float 通道]
    C --> U[分配显存并上传]
    U --> G[GPU 特效核函数]
    G --> B[下载通道并重建 IPicture]
    B --> C
    B --> M[布局旋转 / 图层合成]
    M --> E[CPU 打包及颜色转换]
    E --> W[编码器]
```

GPU 执行核函数的时间可能只占一个特效调用的一小部分。CPU 转换、内存复制、SHA256、同步等待和磁盘缓存，都可以让 CPU 与内存很忙，同时让计算引擎间歇工作。`GetAsArray1D()` 会分配主机数组并读回数据，见 [ILGPU 内存文档](https://ilgpu.net/docs/02-beginner/02-memorybuffers-and-arrayviews/)。

## 已确认的问题及处理

| 问题 | 证据 | 本次处理 |
| --- | --- | --- |
| 工作进程漏用特效偏好 | GUI 的 `MainSettingsPage.SyncSettingToModules` 和 `MauiProgram` 应用强制实现、每效果默认值及光栅器；原 `CLIProgram.InitializeRenderRuntime` 仅初始化设置和插件 | RPC/CLI 加载 `EffectImplement.json` 和全局偏好，并应用缩放、矢量光栅器、抗锯齿等相关设置 |
| 解码加速与特效实现选择独立 | `BlurEffectProvider.SupportedImplementTypes` 等默认把 `IPicture` 放在前面；加载硬件插件本身不会把这些节点改成 GPU 实现 | 记录实际效果类型及实现；可以从日志直接判断 `Blur=IPicture` 或 `Blur=HwAcceleration` |
| 渲染提前量只随线程数增长 | `Renderer` 原默认线程数为逻辑处理器数的 1.75 倍，预渲染帧数随线程数增长，没有字节预算 | 默认线程上限改为逻辑处理器数，默认提前量最多 8 帧，并按图像尺寸和预算约束工作线程及排队帧数 |
| 开始前为整段视频建立任务表 | `PrepareRender` 原遍历全部帧，为每个活动片段分配请求、GUID，并逐帧排序 | 只预先排序片段，进入预渲染窗口时生成请求 |
| 同帧不同片段被缓存锁串行化 | `FrameHashContext.Evaluate` 原在帧级锁内执行完整 `compute()` | 只同步结果发布及同一依赖的等待；不同依赖可并行，保留循环检测、取消及释放等待 |
| 不同帧在节点缓存复制时互相等待 | `FrameHashCache` 原在全局锁内复制整幅图片及释放淘汰项 | 图像复制和释放移到全局锁外，按缓存项保护读与释放；增加命中、未命中及超限跳过计数 |
| 解码器实例膨胀 | `VideoClip.VideoDecoderPool` 原可保留 128 个实例，满额后还新建临时解码器 | 每个源池最多使用 `min(CPU 数, 4)` 个实例；满额等待复用，支持取消与释放唤醒，优先选择可向前解码的实例 |
| 分块并发突破线程预算 | 分块数可大于总渲染线程数，此时每块仍分到至少一个工作线程 | 并发分块数受总线程预算约束 |
| 预览耗时归因错误 | 动态哈希可能调用渲染回调，原统计把这部分算进 `lookup`，随后 `render` 接近零 | 分开记录初始化、哈希、真实渲染、查找及落盘耗时 |

主要改动位于：

- [Render.cs](../projectFrameCut.Render/Rendering/Render.cs)、[RenderMemoryBudget.cs](../projectFrameCut.Render/Rendering/RenderMemoryBudget.cs)
- [FrameHashContext.cs](../projectFrameCut.Render/Rendering/FrameHashContext.cs)、[FrameHashCache.cs](../projectFrameCut.Render/Rendering/FrameHashCache.cs)
- [VideoClip.cs](../projectFrameCut.Render/ClipsAndTracks/VideoClip.cs)、[ChunkRenderCoordinator.cs](../projectFrameCut.Render/Rendering/ChunkRenderCoordinator.cs)
- [CLIProgram.cs](../projectFrameCut/CLIProgram.cs)、[RenderBackendService.cs](../projectFrameCut.Render/RPCProtocol/RenderBackendService.cs)、[RenderBackendService.DynamicPreview.cs](../projectFrameCut.Render/RPCProtocol/RenderBackendService.DynamicPreview.cs)

内存调度预算默认取 .NET 可用内存的 1/8，限制在 128 MiB～1 GiB；每个片段按每像素 64 字节估算图片、依赖快照和临时通道。`Renderer.MaxBufferedBytes` 可以覆盖预算。单帧超预算时仍允许处理，避免队列停住。这是调度估算，原生解码帧、GPU 显存、SSAA 中间图和多个并发 Renderer 的总占用仍需另外观察。

## 仍然存在的主要开销

1. **特效间的 GPU 数据往返。** `HwAccelEffectHelper.ExtractFloatChannels` 在 CPU 上生成四个 float 数组；各 Windows 特效逐次 `Allocate1D`、执行核函数、`GetAsArray1D`，最后由 `BuildPicture` 在 CPU 上转回整数通道。`VideoDecoderHW.cs` 通过 `av_hwframe_transfer_data` 读回硬解帧，`VideoWriterHWAccel.Append` 又逐像素打包并调用 CPU 颜色转换。持续驻留 GPU 的帧格式和显存池，是进一步提效的首要方向。

2. **CPU 与 GPU 混合的合成。** `ClassicOverlayMixture` 在 CPU 上扫描覆盖区，收集半透明像素，再分别处理 R/G/B；硬件实现只加速其中的混合计算。不透明像素走 CPU 复制路径。三个颜色通道还会分别上传相同的 alpha，并分别读回结果。可把 RGBA 混合合成一个核函数，复用 alpha 和显存缓冲。

3. **合成与写出同步执行。** `Renderer` 的单个合成线程直接调用 `Writer.Append`，写出期间它不能继续合成下一帧；`VideoClipRotation.RotatePixels` 也在 CPU 上执行布局旋转。后续可通过容量为 2～3 帧的队列分开合成与写出，继续保持顺序、取消和图像所有权约束。

4. **缓存的收益可能覆盖不了成本。** `FrameHashHelper` 会反射、序列化配置，并对图片通道做 SHA256。节点键包含帧号和进度，普通运动视频跨帧复用有限。节点缓存目前只有 64 MiB，图片按每像素 16 字节估算；4K 图片因此会被拒收，但前面的哈希成本仍可能已经发生。新增缓存计数可以确认具体项目的命中情况。

5. **GPU 调度仍有串行点。** 特效使用 `LoadAutoGroupedStreamKernel`，走默认 stream；OpenCL 或强制同步路径还共用 `ILGPUExecutionHelper.locker`。多设备也会竞争这把锁。后续应按设备建立提交队列、缓冲池及独立 stream，再调整同步粒度。[ILGPU 核函数文档](https://ilgpu.net/docs/03-advanced/02-kernels/)说明了默认 stream 与显式 stream 的区别。

6. **模糊核的并行粒度偏粗。** `BlurEffect_HwAccel` 的横向核每行一个工作项、纵向核每列一个工作项，每个工作项内部循环整行或整列。1080p 的工作项数量分别只有 1080 和 1920。需要结合模糊半径及 GPU 采样，选择分块并行或前缀和等实现。

7. **额外的 CPU 分配。** 多处特效和解码每次都创建 `StackTrace(true)`，即使没有输出过程栈报告。矢量 ILGPU 光栅器读回后还使用 CPU `Parallel.For` 转换完整图像。前者可改为诊断时采集，后者应随 GPU 帧格式一起处理。

8. **分块合并再次编解码。** `ChunkRenderCoordinator.MergeAsync` 对已完成的块逐帧解码，再交给 writer 编码。输出兼容时可改为包级拼接并校正时间戳；编码参数、额外数据或颜色信息不兼容时再走重编码路径。

仅一张 3840×2160 的四通道 float 图就约 126.6 MiB。模糊实现同时分配 12 个这样的单通道 GPU 缓冲，合计约 379.7 MiB 显存；CPU 侧还持有原图、输入/输出 float 通道、重建图片和依赖快照。并发提高时，这些占用会叠加。

## 用日志定位下一处瓶颈

启动及动态预览日志现在显示特效的实际实现；加速器初始化日志显示设备名称和 `Cuda`、`OpenCL` 或 `CPU` 类型。先确认这些值与项目选择一致。

导出结束及状态日志新增：

| 字段 | 含义与排查方向 |
| --- | --- |
| `Source` | 每次片段源读取的平均墙钟时间，包含解码、预览缓存读取及源尺寸转换 |
| `effects` | 每次片段处理的平均墙钟时间，包含效果图、节点哈希、上传、同步和下载 |
| `wait` | 合成线程等待当前帧片段就绪的时间；高值说明上游供给不足 |
| `compose` | 布局、旋转、图层合成及相关复制的耗时 |
| `write` | 编码写入和写出预览通知的耗时 |
| `node cache` | 会话/渲染器累计命中、未命中及因超限或类型不支持而跳过的写入次数 |

`Source/effects` 的单位是 ms/clip，后面三项是 ms/frame；并发和依赖嵌套会导致耗时重叠，不能直接相加作为整段渲染时间。预览的慢请求日志另列 `gate/init/hash/render/lookup/write/track`，维持原有的慢请求阈值和日志节流。

对同一段素材分别观察普通视频、视频加 GPU 特效、多层半透明合成和文字/矢量场景，比较 FPS、进程峰值内存、这些阶段耗时及缓存命中率。冷缓存和热缓存应分别比较。Windows 中同时观察 Compute/CUDA、Copy、Video Decode、Video Encode 引擎；各引擎统计含义可见 [Microsoft 的任务管理器 GPU 说明](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/)。
