# 关于许可证
projectFrameCut的各个部分使用的是分离的许可证：
- `projectFrameCut.Shared` 和 `projectFrameCut.Render.RenderAPIBase` 使用了 MIT License，这是因为他们是开发插件所需的一部分。
- 其他部分，包括主程序，使用的是 Apache License 2.0。（详见[license.txt](license.txt)）
**请注意**，由于主程序的许可的原因，projectFrameCut**只自带了LGPL的FFmpeg库**，这意味着默认情况下，你不能解码一些类型的视频（比如`h264`或者`h265`等）格式
如果你需要，可以考虑安装编解码扩展包。

# 使用许可
此版本的projectFrameCut只为非商业用途设计。如果你是公司，并且对在商业环境里使用projectFrameCut感兴趣，欢迎联系我。

无论你在哪里使用projectFrameCut，我们都**不向你提供任何保证**。

# 重新开发与分发
如果你要修改并且重新分发projectFrameCut，除了许可证已经定义的条款，**你还必须**：
- 必须至少在关于界面，启动页面以及发布页声明**你的项目是从projectFrameCut修改的**
- 按照Apache License 2.0的要求，列出你的修改，并且附上许可证
- 你不得在商业场景上销售，或者使用修改过的projectFrameCut

你可以给你的修改版本使用另一个名字。

# 关于projectFrameCut里的第三方库
项目使用了[FFmpeg](https://ffmpeg.org)和[SixLabors.ImageSharp](https://sixlabors.com/products/imagesharp/)及其系列库作为基本的帧提取和处理，使用了FFmpeg\_droidFix.AutoGen来调用FFmpeg。

项目使用了[CommunityToolkit](https://github.com/CommunityToolkit/Maui)实现了大量的UI层功能。

对于Windows目标，还使用了[ILGPU](https://github.com/m4rs-mt/ILGPU/)做硬件加速。

在Android目标上，我们使用了[Fishnet](https://github.com/Kyant0/Fishnet)做崩溃日志处理。