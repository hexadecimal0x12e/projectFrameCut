# codename 'projectFrameCut'

<image src="projectFrameCut\Resources\Images\projectframecut.svg" width="300" height="300" />

真正强大、可扩展、自由的视频剪辑软件

> [!WARNING]
> **请注意**，由于主程序的许可的原因，projectFrameCut**只自带了LGPL的FFmpeg库**，这意味着默认情况下，你不能解码一些类型的视频（比如`h264`或者`h265`等）格式。
> 
> 如果你需要，可以考虑安装[编解码扩展包](https://github.com/hexadecimal0x12e/projectFrameCut.CodecExtendPack)。
> 
> ---
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

- [x] 转场

- [x] 模板

- [x] Windows - 硬件加速渲染

- [x] Android - 硬件加速渲染

- [x] MacOS/iOS - 硬件加速渲染

- [x] 音频处理

- [x] 字幕和文本

- [x] AI功能（AI生成字幕，配音，甚至素材）

- [x] AI全自动剪辑

- [x] 脚本/自动化

- [ ] ...

### 支持的平台
要使用projectFrameCut，你的设备需要至少有8GB的运行内存和至少5GB的可用存储空间；要渲染视频，你的设备必须拥有大于8GB(带独显)/12GB(不带独显)的内存，4GB显存(独显)和10GB的可用存储空间来存储渲染途中的数据。

推荐使用至少有24GB(桌面)/12GB(移动)内存的设备，同时带独立和集成显卡，并且有50GB的可用空间。

projectFrameCut性能的差异不会随着CPU或者GPU的变化而差异很大，但是你的CPU或者GPU越好，渲染就越快。

对于Windows目标，我们支持**Windows 10 2004或者更新**的系统，并且你还需要安装WinUI3的必要组件（系统会提示你安装它）。要使用硬件加速，你的电脑上还要有一张/多张支持CUDA或者OpenCL的图形处理器（这涵盖了你在市面上能买到的99%的显卡）。

对于安卓目标，projectFrameCut需要**在Android 11或者更新系统**，搭载至少8GB的运行内存，使用Arm64架构的CPU上运行。要使用硬件加速，你的设备的GPU必须支持OpenGL ES 3.1 和/或 Vulkan 4.50。

对于iOS目标，我们支持iOS 13.0 或者更新，建议使用 iOS 17.0 或者更新。请注意，**projectFrameCut不支持运行内存小于4GB的iOS设备**。
如果你使用iPhone，建议使用iPhone 12/13/14 Pro \(Max\)，或者iPhone 15及更新的各款机型。
如果你使用iPad，建议使用 iPad 11th Generation或者更新、Pad mini 5th Generation或者更新、以及使用Apple M系列芯片的各款iPad Air/Pro。

对于MacCatalyst目标，我们支持MacOS 14.0\(macOS Sonoma\) 或者更新的系统上运行，同时支持Intel或者Apple芯片的Mac。**我们建议使用至少有16GB的统一内存Apple芯片的Mac。**

### AI支持
我们知道，现在什么软件都在搞AI集成。

软件支持基础的AI聊天、Agent、图片与视频生成。所有的服务你都需要自备一个API Key。
软件内有一个基础的AI Agent，叫做'Assistant P'，它可以帮助你完成一些基础的操作（比如剪辑管理，效果管理，甚至是一些简单的编辑任务）。你可以直接和它对话来让它帮你完成一些任务。
我们适配了大部分主流的API服务提供商，包括OpenAI、Azure OpenAI、Anthropic、DeepSeek官方API、以及一些云平台的私有接口（比如阿里云和腾讯云）。
请注意，目前AI模型有以下限制：
* 不支持Anthropic的API模式、DeepSeek官方API在思考模式下的ToolCall，以及一些云平台的私有接口。
* 第三方API要求使用兼容OpenAI API的接口（比如Azure OpenAI），但是我们不保证所有的API都能正常工作。

### Agent 自动化
软件内的AI支持ToolCall，允许让AI代替你完成项目编辑和一些操作。你可以直接询问'Assistant P' “你能用ToolCall干什么”，来了解他们能做什么。

AI Agent也可以使用脚本引擎 (详见下文[自动化](#自动化))，允许 Assistant P 为你自动完成一些批量处理任务。

#### 软件内AI Skill
软件内支持AI Skill，并且内置了几个简单的skill，允许你在软件内使用AI来完成一些任务。
你可以直接询问'Assistant P' “你能用Skill干什么”，来了解他们能做什么。

我们还支持自定义的Claude风格的Skill，你只需要把他们放在`<用户数据>\My Skills`目录下，软件会自动加载他们。

#### MCP Server
想要在第三方Agent里使用软件的功能？没问题，我们提供了一个MCP Server，通过WebSocket连接到软件来实现对软件的控制。

你可以在Release里找到它。MCP服务器和软件内AI ToolCall使用了同一套接口，它也可以实现大部分的功能。

关于MCP Server、Skill和AI ToolCall的更多信息，你可以[去这里看看](./projectFrameCut.McpServer/README.md)

### 插件
你可以使用插件来自定义projectFrameCut。

要开发插件，如果你感兴趣[这里有教程](https://github.com/hexadecimal0x12e/projectFrameCut.PluginTemplate)

### 自动化
项目中内置了一个基于 PowerShell Core SDK 的脚本引擎，它可以将时间线暴露给 PowerShell 脚本执行。
这意味着你可以使用PowerShell脚本来控制项目，甚至可以让软件自动化完成一些任务。

目前，项目中支持“基于脚本的模板”，允许模板作者使用PowerShell脚本来控制模板的行为。
你可以在模板中使用脚本来实现一些复杂的逻辑，比如根据视频内容自动生成字幕，或者根据用户的操作自动调整特效参数。

在开发者模式下，项目UI的工具栏里会出现“脚本引擎”选项，允许你直接在软件内运行PowerShell脚本来测试你的脚本的行为。

### 如何编译

项目基于.NET 10和MAUI开发，请先确保你的电脑里安装了Visual Studio或者VS Code，**确认你安装了.NET 10 的SDK和MAUI的组件**，

0. 配置projectFrameCut.Drawing库
    a. 克隆[projectFrameCut.Drawing](https://github.com/hexadecimal0x12e/projectFrameCut.Drawing)项目到本地，和`projectFrameCut`项目放在同一层级的目录下。
    b. 进入`projectFrameCut.Drawing`目录，运行`pack-all.ps1`编译这个库。
    c. 对主项目(`projectFrameCut.sln`)进行还原。

1. 准备一个适用于Windows的**8.1.x** FFmpeg库(他们太大了，Git存储库里塞不下)，放在项目文件夹以外的地方。
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

2. 你还需要编译一个适用于Android的**8.1.x** FFmpeg动态库(他们太大了，Git存储库里塞不下)，放在项目文件夹以外的地方。
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

### 许可和第三方库致谢
projectFrameCut的主程序、核心渲染库（CRL）与ApplicationAPIBase使用了Apache License，共享库（projectFrameCut.Shared和projectFrameCut.Render.RenderAPIBase）使用了MIT License。
项目的核心Drawing库（projectFrameCut.Drawing）使用了LGPLv3 License。

更多详情，请见[license.md](license.md)
