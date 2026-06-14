# projectFrameCut.StandaloneRender

独立渲染器组件 - 用于从项目文件渲染成果的命令行工具。
**此工具的使用许可和projectFrameCut主程序一致**。

## 系统要求
独立渲染器支持Windows和Linux操作系统。最低系统要求如下：
- .NET 10 运行时
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
运行性能基准测试。TODO

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

- **`-oneByOneRender=<true|false>`**  
  是否逐帧渲染，并且在每一帧的结果产生之后同步写入输出视频，而不是计划写入。默认：`false`
  设置此参数为`true`会覆盖参数 **`-maxParallelThreads`** 为1。

- **`-renderByLayer=<true|false>`**  
  是否为每个图层分别渲染（而不是合并所有图层）。默认：`false`

- **`-prepareInWorker=<true|false>`**  
  是否在工作线程中准备帧（而不是在渲染线程中准备）。默认：`false`

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

- **`-VideoFrameDiskCacheRoot=<path to video frame disk cache root>`**
  指定磁盘缓存的根目录。默认位于projectFrameCut缓存目录下的`VideoFrameDiskCache`文件夹中。
  不定义此参数代表禁用磁盘缓存。

- **`-VideoFrameMemoryCache=<true|false>`**
  是否启用内存缓存。默认：`false`

- **`-ApproximateMixture=<true|false>`**
  是否允许近似混合模式。这会提高性能，但可能会降低某些效果的质量。默认：`false`

- **`-ForcePreferToType=<NotSpecified|cpu|cuda|opencl>`**
  强制所有效果使用指定的实现类型，覆盖正常的自动选择。默认：`NotSpecified`（不强制）

### 模式 'bench' 的参数

- **`-multiAccelerator=<true|false>`**  
  是否使用多个加速器。

- **`-acceleratorType=<auto|cuda|opencl|cpu>`**  
  加速器类型。

- **`-acceleratorDeviceId=<device id>`**  
  加速器设备 ID。

- **`-acceleratorDeviceIds=<device ids|all>`**  
  多个加速器设备 ID。

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

## 项目结构要求

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

