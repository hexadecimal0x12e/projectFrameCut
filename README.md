# codename 'projectFrameCut'

<image src="projectFrameCut\Resources\AppIcon\projectframecut.svg" width="300" height="300" />

一个强大，易上手且完全自由的视频剪辑软件

> [!WARNING]
> **请注意**，由于主程序的许可的原因，projectFrameCut**只自带了LGPL的FFmpeg库**，这意味着默认情况下，你不能解码一些类型的视频（比如`h264`或者`h265`等）格式
如果你需要，可以考虑安装编解码扩展包。
> 
> **projectFrameCut仍在开发**，目前并不能代替任何的视频剪辑软件（还缺一堆功能）。**请不要用于任何的生产用途**，并且我们不会由于projectFrameCut出现了异常导致你的工作流程被打断**做任何的担保**，这也是许可证规定的一部分（不提供任何担保）
> 
> 本人很忙，接下来的开发过程会很慢。如果你有很好的想法，建议你写个[Issue](https://github.com/hexadecimal0x12e/projectFrameCut/issues/new)。



### 为什么要做这个东西

众所周知，某个剪辑软件的越来越多基础的功能要VIP了（比如生成字幕）~~就差直接先开VIP再用了~~ ，很多人都忍不了做了一些开源的替代品，包括我。



### 路线图

- [x] 交互式剪辑

- [x] 基础特效（移色，裁剪，缩放...）

- [x] 高级特效（过渡，关键帧，对象跟踪...）

- [x] Windows - 硬件加速渲染

- [x] Android - 硬件加速渲染

- [ ] MacOS/iOS - 硬件加速渲染

- [x] 音频处理

- [x] 字幕和文本

- [ ] AI功能（AI生成字幕，配音，甚至素材）

- [ ] AI全自动剪辑

- [ ] ...

### 支持的平台
要使用projectFrameCut，你的设备需要至少有8GB的运行内存和至少5GB的可用存储空间；要渲染视频，你的设备必须拥有大于8GB(带独显)/12GB(不带独显)的内存，4GB显存(独显)和10GB的可用存储空间来存储渲染途中的数据。

推荐使用至少有24GB(桌面)/12GB(移动)内存的设备，同时带独立和集成显卡，并且有50GB的可用空间。

projectFrameCut性能的差异不会随着CPU或者GPU的变化而差异很大，但是你的CPU或者GPU越好，渲染就越快。

对于Windows目标，我们支持**Windows 10 2004或者更新**的系统，并且你还需要安装WinUI3的必要组件（系统会提示你安装它）。要使用硬件加速，你的电脑上还要有一张/多张支持CUDA或者OpenCL的图形处理器（这涵盖了你在市面上能买到的99%的显卡）。

对于安卓目标，目前我们只保证projectFrameCut能够**在Android 12或者更新系统**，搭载至少8GB的运行内存，使用Arm64架构的CPU上运行。要使用硬件加速，你的设备的GPU必须支持OpenGL ES 3.1 或者更新。

对于iOS目标，我们支持iOS 17.0 或者更新。请注意，**projectFrameCut不支持运行内存小于4GB的iOS设备**。
如果你使用iPhone，建议使用iPhone 12/13/14 Pro \(Max\)，或者iPhone 15及更新的各款机型。
如果你使用iPad，建议使用 iPad 11th Generation或者更新、Pad mini 5th Generation或者更新、以及使用Apple M系列芯片的各款iPad Air/Pro。

对于MacCatalyst目标，我们支持MacOS 14.0\(macOS Sonoma\) 或者更新的系统上运行，同时支持Intel或者Apple芯片的Mac。**我们建议使用至少有16GB的统一内存Apple芯片的Mac。**

### 如何编译

项目基于.NET 10和MAUI开发，请先确保你的电脑里安装了Visual Studio或者VS Code，**确认你安装了.NET 10 的SDK和MAUI的组件**，

1. 你需要准备一个适用于Windows的**8.x** FFmpeg库(他们太大了，Git存储库里塞不下)，放在项目文件夹以外的地方。
   按照下列结构放置文件

```
c:\path\to\your\folder\Windows
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

建议使用[Gyan.dev他们家的](https://www.gyan.dev/ffmpeg/builds/)FFmpeg库，请下载文件名带`shared`的版本。

2. 你还需要编译一个适用于Android的**8.x** FFmpeg动态库(他们太大了，Git存储库里塞不下)，放在项目文件夹以外的地方。
   按照下列结构放置文件
```
c:\path\to\your\folder\Android
└─FFmpeg
    └─<CPU架构(比如arm64-v8a)>
            libavcodec.so
            libavfilter.so
            libavformat.so
            libavutil.so
            libc++_shared.so
            libswresample.so
            libswscale.so
```

你需要准备所有的目标架构的.so动态库文件，请记得使用16KB对齐以避免应用程序不能在Android 16或者更新的版本上运行的问题。

3. 修改`projectFrameCut.csproj`里的这几行：

```xml
<ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'windows'">
		...
    <MauiAsset Include="[你的路径(c:\path\to\your\folder)]\**" LogicalName="%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
...
<ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">
        ...
    <AndroidNativeLibrary Include="[你的路径(c:\path\to\your\folder)]\**\*.so" />
</ItemGroup>
```

把Include里的内容替换成你的路径，**请只修改方括号扩起来的部分，以避免莫名其妙的缺动态库的问题。**

4. 重新配置iOS预配（如果你需要）:

修改`projectFrameCut.iDevices.csproj`

```xml

<PropertyGroup Condition="'$(TargetFramework)'=='net10.0-ios'">
    <CodesignKey>你的Codesign Key</CodesignKey>
    <CodesignProvision>你的描述文件的名字</CodesignProvision>

    ...

```

5. 在项目根目录里运行`dotnet workload restore`安装所有的SDK组件。

6. 编译，运行。

因为一些原因，如果你需要生成iOS/MacCatalyst目标，请使用`projectFrameCut.iDevices.csproj`，而不是`projectFrameCut.csproj`


### 关于本地化
目前，除了中文的本地化资源以外，所有的本地化字符串都是由AI生成的。如果你发现了问题，请提交Issue。

### 插件
projectFrameCut支持插件。详见[这里](https://github.com/hexadecimal0x12e/projectFrameCut.PluginTemplate)

### 许可和第三方库致谢
projectFrameCut的主程序使用了Apache License，共享库（projectFrameCut.Shared和projectFrameCut.Render.RenderAPIBase）使用了MIT License。

详见[license.md](license.md)


