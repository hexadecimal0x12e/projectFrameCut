# projectFrameCut.StandaloneRender

独立渲染器组件 - 用于从项目文件渲染成果的命令行工具。
**此工具的使用许可和projectFrameCut主程序一致**。

## 系统要求
独立渲染器支持Windows和Linux操作系统。最低系统要求如下：
- .NET 11 运行时
- **8.1.x**的FFmpeg 库
- CUDA 或 OpenCL 支持的 GPU，用于硬件加速

独立渲染器**不支持MacOS**，因为没有很好的方法在命令行程序里调用Metal API。你可以考虑使用主程序的自动渲染功能。

## 使用方法

```bash
projectFrameCut.StandaloneRender <mode> [<args>]
```

## 可用模式

### render
从给定的项目文件渲染视频/音频/全部内容。

### list_accels
列出可用的硬件加速器设备。
读取的加速器信息将会以JSON格式输出到标准错误流。
输出结果类似于这样子：
```json
[
  {
    "index": 0,
    "name": "CPUAccelerator",
    "Type": "CPU"
  },
  {
    "index": 1,
    "name": "Intel(R) UHD Graphics",
    "Type": "OpenCL"
  },
  {
    "index": 2,
    "name": "NVIDIA GeForce RTX 4090",
    "Type": "Cuda"
  }
]
```

### bench
运行渲染管线的性能基准测试，**无需外部项目文件**。

#### 渲染调度检测

```powershell
projectFrameCut.StandaloneRender bench schedule -reportDirectory=D:\bench-results
# 指定编码器、候选数量和随机种子
projectFrameCut.StandaloneRender bench schedule -encoder=h264_nvenc -scheduleCandidates=120 -scheduleSeed=42
```

`bench schedule` 使用完整渲染管线，逐轮生成并实际编码 1920×1080、30fps、300 帧的内置项目。默认优先选择硬件 H.264，不可用时回退软件编码；指定 `-encoder` 时严格使用该编码器。预热后固定实际编码器，编码条件在整个搜索期间保持一致。`-pixelFormat` 默认为 `AV_PIX_FMT_YUV420P`。

默认最多 256 轮：完整预热一次、120 个候选各测两次，再对前五名各追加三轮确认。先完成最多 40 个直接合成候选（包含默认基线及固定种子抽样），再按原顺序仅切换延迟合成开关，使用完全相同的其他参数做配对比较。候选预算较小时，初始直接合成阶段最多使用一半预算，至少测一个候选。

如果连续 3 组延迟合成的平均 FPS 都低于对应的直接合成，就跳过剩余延迟合成候选及其确认轮次，继续探索直接合成领先组合的单参数邻域。否则后续邻域探索仍先测直接合成，再测相同参数的延迟合成，沿用相同的提前跳过规则。`scheduleCandidates` 包含两种模式的候选总数；候选空间耗尽时提前结束，候选顺序同时受实测排名影响。报告中的 `DeferredCompositionSkipped` 记录是否提前跳过延迟合成。

搜索线程数、软并发、预渲染窗口、线程亲和性、内存预算，以及直接编码/延迟合成。每轮使用独立项目和缓存，关闭预览复用、断点恢复、效果自动重试及详细诊断。VideoBuilder 的写入队列缓存不参与搜索，因为完整渲染管线直接调用视频写入器。

评分为有效重复轮次的总输出帧数除以总耗时，包含写入器初始化、渲染准备、渲染、编码 flush 和封装关闭。结果是本机、当前编码条件和已测组合中的最佳值。内存峰值按 100ms 采样整个进程的工作集。

每次检测在报告目录下建立独立的 `schedule-*` 子目录，逐轮更新 `report.json` 和 `runs.csv`。JSON 包含运行环境、逐轮结果、排名、`BestConfirmed`、相对基线提升及 `RecommendedArguments`。将推荐参数附加到正常 `render` 命令，并提供项目与输出路径即可复用；不会自动改变默认配置。生成的视频和延迟缓存逐轮清理，仅保留报告。Ctrl+C 会停止检测、清理当前轮并保存已有结果；失败过的组合不参与推荐。

正常渲染新增 `-renderAheadFrames=<帧数>`、`-enableMemoryBudget=<true|false>` 和 `-maxBufferedBytes=<字节数>`；窗口和预算的 `0` 使用渲染器原默认值。

bench 模式会使用 `BenchmarkSourceGenerator` 自动生成一个包含多种渲染场景的复杂测试项目，覆盖以下内容：

- **层 0 — 纯色背景段**：5 段不同颜色的纯色 clip（红/绿/蓝/橙/紫），段间有 10 帧重叠以测试叠化合成
- **层 0 - 子层 — 画中画 (PiP)**：小尺寸半透明叠加，测试非全屏 clip 合成
- **层 1 — 单效果演示**：Blur、Rotation、Sharpen、Vignette 各单独一段
- **层 2 — 效果链**：多效果叠加在同一 clip 上（如 FadeOpacity+ColorAdjustment、Blur+Sharpen+FadeOpacity、Flip+Rotation+Vignette），测试效果管线的链式处理性能
- **层 3 — 位置动画**：Jitter 效果（抖动），测试 `IClipPositionProvider` 路径
- **层 4 — 文字叠加**：多段文字，含 FadeOpacity、ColorAdjustment、TextFadeIn、ZoomIn 等效果
- **层 5 — HUD/装饰**：信息栏、分割线、帧计数器等持续显示元素

**默认参数**：1920×1080 @ 60fps，默认约 300 帧（~5 秒），输出到空设备（不产生实际文件）。

**输出结果**：渲染完成后会输出详细的性能统计，包括：
- 总渲染帧数、总耗时、整体 FPS
- 平均帧渲染时间、平均帧准备时间、平均每帧总时间
- **逐步骤耗时分解**：按照渲染管线的每个处理步骤（如 Blur、Rotation、ColorAdjustment、Jitter、TextRendering 等）分别统计平均耗时和调用次数

### about
显示程序信息和版本详情。

## 参数说明

### 全局参数
- **`-h`**, **`--help`**  
  显示帮助信息。

- **`--nolog`**  
  禁用日志输出到控制台。

- **`--logDiagnostic`**  
  启用诊断日志记录。与projectFrameCut主程序的设置-高级-记录诊断性日志相同。

- **`--externalAssemblyPath`**
  指定外部程序集路径，主要用于插件加载。
  使用`;`分隔多个路径。

- **`--resolveArgsFromEnvironmentVars`**
  尝试从环境变量解析参数值。参数名称应与环境变量名称匹配，前缀为 `projectFrameCut_`。
  例如，参数 `-project` 可通过环境变量 `projectFrameCut_project` 设置。

  如果一个参数在命令行和环境变量中都有定义，以环境变量里的参数优先。
  请注意，运行模式参数（如 `render`）不能通过环境变量设置，必须在命令行中指定。
  
- **`--trace`**
  启用IPicture对象跟踪，并且输出更详细的调试信息。

- **`--noSigInt`**
  禁止将 `SIGINT` 信号（通常由 Ctrl+C 产生）注册为中断渲染的信号。这对于某些环境（如Docker容器）可能有用。
  如果没有定义此参数，程序会默认注册 `SIGINT` 信号处理程序，这意味着你可以通过按 Ctrl+C 来优雅地停止渲染过程。

- **`--keyboardInterrupt`**
  在渲染过程中，监听键盘输入。
  当启用此选项时，你可以通过按5次Esc/Q键来取消渲染，或者按S来显示当前进度。

### 模式 'render' 的参数

#### 必需参数

- **`-project=<project dir>`**  
  项目目录路径，包含 `project.json`、`timeline.json` 和 `assets.json` 文件。

- **`-output=<output file/folder>`**  
  输出文件或文件夹路径。
  可以在文件名中包含`{CurrentTime}`，程序会将其替换为当前时间，格式为 `yyyyMMdd_HHmmss`。

- **`-output_options=<width>,<height>,<fps>,<pixel format>,<encoder>`**  
  输出选项，包含 5 个值，用逗号分隔：
  - `width`: 视频宽度（像素）
  - `height`: 视频高度（像素）
  - `fps`: 帧率
  - `pixel format`: FFmpeg 像素格式的**完整名称**（如 `AV_PIX_FMT_YUV420P`，[这里](https://ffmpeg.org/doxygen/8.0/pixfmt_8h.html#a9a8e335cf3be472042bc9f0cf80cd4c5)或者FFmpeg源码的`pixfmt.h`里有详细的列表）
  - `encoder`: 编码器名称（如 `libx264`、`h264_nvenc`）

#### 可选参数

- **`-target=<video|audio|all>`**  
  渲染目标。默认：`all`
  - `video`: 仅渲染视频
  - `audio`: 仅渲染音频
  - `all`: 渲染视频和音频并合成

- **`-assetDbFile=<path to database.json file>`**  
  全局素材数据库 JSON 文件路径。默认位于projectFrameCut用户数据目录下的`My Assets/.database/database.json`

- **`-pluginRoot=<path to plugin root>`**  
  外部插件根目录路径。
  程序将尝试加载目录下每一个`.dll`文件，并且尝试寻找程序集中符合projectFrameCut插件标准的**标准插件**的加载器类(`PluginLoader`)，如果找到则尝试初始化。

- **`-Use16bpp=<true|false>`**  
  是否使用 16 位每像素。默认：`true`

- **`-maxParallelThreads=<number>`**  
  最大并行渲染线程数。默认：`8`

- **`-enableThreadAffinity=<true|false>`**  
  是否启用线程亲和性，以减少线程切换带来的性能损失。默认：`true`

- **`-renderWorkerAffinity=<cpu indexes>`**  
  渲染工作线程的手动 CPU 亲和性设置。支持逗号分隔的 CPU 索引或范围，例如 `0-3,5,7`。  
  启用此参数后会自动设置 `enableThreadAffinity=true`。

- **`--incrementalRendering=<true|false>`**
  启用增量渲染。默认：`false`。渲染前计算全部帧的哈希，复用已有的确定性片段帧和完整画布帧，重新计算不确定性、缺失或损坏的帧，再按时间线顺序编码视频。与分块渲染互斥，原 `-deferredComposition` 参数仍然有效。
  每次生成的 VFD 缓存、帧哈希和 SQLite 索引保留在 `<项目目录>/incrementalRendering/`，完成或中断后均保留。`-chunkResume=true` 允许继续相同项目和渲染参数下未完成的任务。

- **`-vfdCompression=<None|Deflate|GZip|Brotli|Zstandard|Lzma2>`**
  帧缓存使用的 VFD 压缩格式，默认 `Zstandard`。名称不区分大小写。

- **`-vfdCompressionLevel=<level>`**
  `Zstandard` 接受整数 `-131072` 至 `22`，默认 `1`；`Lzma2` 接受整数 `1` 至 `10`，默认 `6`。
  `Deflate`、`GZip`、`Brotli` 接受 `Fastest`、`Optimal`、`SmallestSize` 或 `NoCompression`，默认 `Fastest`。
  `None` 不接受级别参数。例如：`--incrementalRendering=true -vfdCompression=Zstandard -vfdCompressionLevel=3`。
  修改压缩配置不影响已有有效缓存的复用；新生成的帧使用当前配置。

- **`-reuseDynamicPreviewCache=<true|false>`**
  复用项目`thumbs/perClip/<片段ID>/dynamic/`下的有效动态预览缓存。默认：`false`。

- **`-chunkRender=<true|false>`**
  启用分块渲染。默认：`false`。分块缓存保存在`<项目目录>/thumbs/render-chunks/<任务指纹>/`。
  分块使用高码率、速度优先的中间编码；全部完成后由内置视频解码器逐帧读取，并写入目标配置的VideoWriter。

- **`-chunkFrames=<number>`** / **`-chunkSeconds=<number>`**
  按帧数或秒数设置分块大小，两者只能指定一个。启用分块但均未指定时默认每块60秒。

- **`-chunkParallelism=<number>`**
  同时渲染的分块数量。默认：`1`（串行）。

- **`-chunkResume=<true|false>`**
  校验并复用已经完成的分块，同时控制延迟合成的断点续渲。默认：`true`，不要求开启分块渲染。
  项目、素材和画面配置未变化时，恢复已保存的片段帧与合成帧，只补齐缺失部分；视频编码从头写入。

- **`-chunkKeepFiles=<true|false>`**
  成功生成最终输出后保留manifest和分块文件。默认：`false`；失败或取消时始终保留。

- **`-multiAccelerator=<true|false>`**  
  是否使用多个加速器设备。默认：`false`

- **`-acceleratorType=<auto|cuda|opencl|cpu>`**  
  加速器类型。默认：`auto`

- **`-acceleratorDeviceId=<device id>`**  
  指定单个加速器设备 ID（与 `acceleratorType` 配合使用）。

- **`-acceleratorDeviceIds=<device ids|all>`**  
  多加速器模式下的设备 ID 列表，用逗号分隔，或使用 `all` 选择所有非 CPU 设备。

- **`-GCOptions=<0|1|2>`**  
  垃圾回收选项：
  - `0`: 默认行为
  - `1`: 每次写入后执行 GC
  - `2`: 启用大对象堆压缩

- **`-outputIntermediatePath=<intermediate output path>`**  
  中间输出文件路径（用于 `target=all` 模式）。

- **`-FFmpegLibraryPath=<path to FFmpeg libraries>`**  
  FFmpeg 库路径。默认与可执行文件所在的目录一致。

- **`-diagReportPath=<path to .csv file or output directory>`**  
  诊断报告输出路径（CSV 格式）。

- **`-LogState=<true|false>`**
  是否在渲染过程中输出详细的状态日志。默认：`false`

- **`-stopAfter=<second>`**
  在一段时间后停止渲染，单位为秒。不定义此参数代表不限制。

- **`-preferHwAccelDecoder=<true|false>`**
  是否优先使用硬件加速解码器。默认：`false`

- **`-PictureResizer=<cpu|hwaccel>`**
  选择要使用的硬件加速图片缩放器。默认：`hwaccel`

- **`-VideoFrameMemoryCache=<true|false>`**
  是否启用内存缓存。默认：`false`

- **`-ApproximateMixture=<true|false>`**
  是否允许近似混合模式。这会提高性能，但可能会降低某些效果的质量。默认：`false`

- **`-ForcePreferToType=<NotSpecified|cpu|cuda|opencl>`**
  强制所有效果使用指定的实现类型，覆盖正常的自动选择。默认：`NotSpecified`（不强制）

### 模式 'bench' 的参数

bench 模式不需要 `-project`、`-output` 等渲染参数，它会使用 `BenchmarkSourceGenerator` 自动生成测试项目结构进行渲染性能测试。

- **`-multiAccelerator=<true|false>`**  
  是否使用多个加速器。

- **`-acceleratorType=<auto|cuda|opencl|cpu>`**  
  加速器类型。

- **`-acceleratorDeviceId=<device id>`**  
  加速器设备 ID。

- **`-acceleratorDeviceIds=<device ids|all>`**  
  多个加速器设备 ID。

- **`-writeToNull=<true|false>`**  
  是否写入空设备（BlackholeVideoWriter）。默认：`true`  
  为 `true` 时渲染结果直接丢弃，消除 I/O 对性能测试的影响；为 `false` 时不创建视频写入器，仅测试渲染管线的帧准备和渲染阶段。

- **`-maxParallelThreads=<number>`**  
  最大并行渲染线程数。默认：`8`

- **`-enableThreadAffinity=<true|false>`**  
  是否启用线程亲和性。默认：`true`

- **`-renderWorkerAffinity=<cpu indexes>`**  
  渲染工作线程的手动 CPU 亲和性设置。支持逗号分隔的 CPU 索引或范围，例如 `0-3,5,7`。

- **`-GCOptions=<0|1|2>`**  
  垃圾回收选项：
  - `0`: 默认行为
  - `1`: 每次写入后执行 GC
  - `2`: 启用大对象堆压缩

- **`-preferHwAccelDecoder=<true|false>`**  
  是否优先使用硬件加速解码器。默认：`false`

- **`-PictureResizer=<cpu|hwaccel>`**  
  选择要使用的图片缩放器。默认：`hwaccel`

- **`-ApproximateMixture=<true|false>`**  
  是否允许近似混合模式。默认：`false`

- **`-ForcePreferToType=<NotSpecified|cpu|cuda|opencl>`**  
  强制所有效果使用指定的实现类型。默认：`NotSpecified`（不强制）

## 返回结果
返回0表示成功。
返回1表示项目损坏、不能识别或者配置错误。
返回2表示找不到加速器。
返回255表示渲染被取消。
返回负数通常表示出现了没有被处理的异常，可能是你的参数问题，或者是程序的bug。如果是程序的bug，请将错误信息反馈给我们。
当渲染正常完成时，环境变量`projectFrameCut_RenderFinished`将会被赋值为1，并且环境变量`projectFrameCut_LastOutput`将被设置为输出文件的路径。

## 使用示例

### 示例 1: 基本视频渲染

```bash
projectFrameCut.StandaloneRender render \
  -project=D:\MyProject \
  -output=D:\Output\video.mp4 \
  -output_options=1920,1080,30,AV_PIX_FMT_YUV420P,libx264
```

### 示例 2: 使用 NVIDIA 硬件加速

```bash
projectFrameCut.StandaloneRender render \
  -project=D:\MyProject \
  -output=D:\Output\video.mp4 \
  -output_options=1920,1080,60,AV_PIX_FMT_YUV420P,h264_nvenc \
  -acceleratorType=cuda \
  -maxParallelThreads=16
```

### 示例 3: 仅渲染音频

```bash
projectFrameCut.StandaloneRender render \
  -project=D:\MyProject \
  -output=D:\Output\audio.wav \
  -output_options=1920,1080,30,AV_PIX_FMT_YUV420P,libx264 \
  -target=audio
```

### 示例 4: 列出可用加速器

```bash
projectFrameCut.StandaloneRender list_accels
```

### 示例 5: 多加速器渲染

```bash
projectFrameCut.StandaloneRender render \
  -project=D:\MyProject \
  -output=D:\Output\video.mp4 \
  -output_options=3840,2160,60,AV_PIX_FMT_YUV420P,h264_nvenc \
  -multiAccelerator=true \
  -acceleratorDeviceIds=all
```

### 示例 6: 使用外部插件和资产数据库

```bash
projectFrameCut.StandaloneRender render \
  -project=D:\MyProject \
  -output=D:\Output\video.mp4 \
  -output_options=1920,1080,30,AV_PIX_FMT_YUV420P,libx264 \
  -pluginRoot=D:\Plugins \
  -assetDbFile=D:\Assets\database.json
```

### 示例 7: 运行性能基准测试

```bash
projectFrameCut.StandaloneRender bench
```

### 示例 8: 使用 CUDA 加速进行基准测试

```bash
projectFrameCut.StandaloneRender bench \
  -acceleratorType=cuda \
  -acceleratorDeviceId=2
```

### 示例 9: 渲染管线基准测试

```bash
projectFrameCut.StandaloneRender bench \
  -acceleratorType=cuda \
  -writeToNull=true
```

### 示例 10: 多加速器基准测试

```bash
projectFrameCut.StandaloneRender bench \
  -multiAccelerator=true \
  -acceleratorDeviceIds=all
```

## 项目结构要求（仅 render 模式）

项目目录必须包含以下文件：

- **`project.pjfc`或者`project.json`**: 项目配置文件
- **`timeline.json`**: 时间线和片段信息
- **`assets.json`**: 资产列表

## 获取帮助

运行以下命令显示帮助信息：

```bash
projectFrameCut.StandaloneRender --help
```

或

```bash
projectFrameCut.StandaloneRender -h
```

## 版本信息

运行以下命令查看详细的版本和组件信息：

```bash
projectFrameCut.StandaloneRender about
```

