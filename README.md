# codename 'projectFrameCut'

<image src="projectFrameCut\\\\\\\\Resources\\\\\\\\AppIcon\\\\\\\\projectframecut.svg" width="300" height="300" />

一个强大，易上手且完全自由的视频剪辑软件

> [!WARNING]
> **projectFrameCut仍在开发**，目前并不能代替任何的视频剪辑软件（还缺一堆功能）。**请不要用于任何的生产用途**，并且我们不会由于projectFrameCut出现了异常导致你的工作流程被打断**做任何的担保**，这也是GNU GPL规定的一部分（不提供任何担保）
> 
> 本人很忙，接下来的开发过程会很慢。如果你有很好的想法，建议你写个[Issue](https://github.com/hexadecimal0x12e/projectFrameCut/issues/new)。



### 为什么要做这个东西

众所周知，某个剪辑软件的越来越多基础的功能要VIP了（比如生成字幕）~~就差直接先开VIP再用了~~ ，很多人都忍不了做了一些开源的替代品，包括我。



### 路线图

- [x] 交互式剪辑

- [x] 基础特效（移色，裁剪，缩放...）

- [ ] 高级特效（过渡，关键帧，对象跟踪...）

- [x] Windows - 硬件加速渲染

- [ ] Android - 硬件加速渲染

- [ ] MacOS/iOS - 硬件加速渲染

- [ ] 音频处理

- [ ] 字幕和文本

- [ ] AI功能（AI生成字幕，配音，甚至素材）

- [ ] AI全自动剪辑

- [ ] ...

### 支持的平台
要使用projectFrameCut，你的设备需要至少有8GB的运行内存和至少10GB的可用存储空间；要渲染视频，你的设备必须拥有大于16GB的内存或者50GB的可用存储空间来存储渲染途中的数据。

projectFrameCut性能的差异不会随着CPU或者GPU的变化而差异很大，但是你的CPU或者GPU越好，渲染就越快。

对于Windows目标，我们支持**Windows 10 2004或者更新**的系统，并且你还需要安装WinUI3的必要组件（系统会提示你安装它）。要使用硬件加速，你的电脑上还要有一张/多张支持CUDA或者OpenCL的图形处理器（这涵盖了你在市面上能买到的99%的显卡）。

对于安卓目标，目前我们只保证projectFrameCut能够**在Android 12或者更新系统**，搭载至少8GB的运行内存，使用Arm64架构的CPU上运行。要使用硬件加速，你的设备的GPU必须支持OpenGL ES 3.1 或者更新。

对于iOS目标，我们支持iOS 17.0 或者更新。请注意，**projectFrameCut不支持运行内存小于4GB的iOS设备**。
如果你使用iPhone，建议使用iPhone 12/13/14 Pro \(Max\)，或者iPhone 15及更新的各款机型。
如果你使用iPad，建议使用 iPad 11th Generation或者更新、Pad mini 5th Generation或者更新、以及使用Apple M系列芯片的各款iPad Air/Pro。

对于MacCatalyst目标，我们支持MacOS 14.0\(macOS Sonoma\) 或者更新的系统上运行，同时支持Intel或者Apple芯片的Mac。**我们建议使用至少有16GB的统一内存Apple芯片的Mac。**

### 如何编译

项目基于.NET 10和MAUI开发，请先确保你的电脑里安装了Visual Studio或者VS Code，**确认你安装了.NET 9和.NET 10 的SDK和MAUI的组件**，

1. 你需要准备一个适用于Windows的**8.x** FFmpeg库(他们太大了，Git存储库里塞不下)，放在项目文件夹以外的地方。
   按照下列结构放置文件

```
c:\path\to\your\folder
└─FFmpeg
    └─8.x\_internal
            avcodec-62.dll
            avdevice-62.dll
            avfilter-11.dll
            avformat-62.dll
            avutil-60.dll
            ffmpeg.exe
            ffplay.exe
            ffprobe.exe
            swresample-6.dll
            swscale-9.dll
```
建议使用[Gyan.dev他们家的](https://www.gyan.dev/ffmpeg/builds/)FFmpeg库，请下载文件名带`shared`的版本
2. 修改projectFrameCut.csproj里的这一行：

```xml
<MauiAsset Include="你的路径\**" LogicalName="%(RecursiveDir)%(Filename)%(Extension)" />
```

把Include里的内容替换成你的路径，**确保在文件夹路径的末尾添加一个反斜杠和两个星号。**

3.重新配置iOS预配（如果你需要）:

修改`projectFrameCut.iDevices.csproj`

```xml

<PropertyGroup Condition="'$(TargetFramework)'=='net10.0-ios'">
    <CodesignKey>你的Codesign Key</CodesignKey>
    <CodesignProvision>你的描述文件的名字</CodesignProvision>

    ...

```

4.在项目根目录里运行`dotnet workload restore`安装所有的SDK组件。

5.编译，运行。

因为一些原因，如果你需要生成iOS/MacCatalyst目标，请使用`projectFrameCut.iDevices.csproj`，而不是`projectFrameCut.csproj`


### 关于本地化
目前，除了中文的本地化资源以外，所有的本地化字符串都是由AI生成的。如果你发现了问题，请提交Issue。


### 许可和第三方库致谢

项目使用了 **GNU GPL v2** (or later) 进行开源。
项目使用了[FFmpeg](https://ffmpeg.org)和[SixLabors.ImageSharp](https://sixlabors.com/products/imagesharp/)及其系列库作为基本的帧提取和处理，使用了FFmpeg\_droidFix.AutoGen来调用FFmpeg。

对于Windows目标，还使用了[ILGPU](https://github.com/m4rs-mt/ILGPU/)做硬件加速。

在Android目标上，我们使用了[Fishnet](https://github.com/Kyant0/Fishnet)做崩溃日志处理。

