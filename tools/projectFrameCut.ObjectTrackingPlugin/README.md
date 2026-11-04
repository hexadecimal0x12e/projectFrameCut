# 对象跟踪外置后端插件

Windows x64 独立插件，使用 OpenCvSharp 4.13 的 ORB 特征、Hamming 匹配和 RANSAC 单应性估计。
每帧独立定位，不依赖播放顺序，不下载模型。适合纹理丰富、外观稳定的目标；明显变形、遮挡、重复纹理可能导致丢失或误匹配。

## 端口

| 方向 | ID | 类型 | 含义 |
|---|---|---|---|
| 输入 | `__Input__` | IPicture | 要搜索的图片，支持 byte、ushort、Half |
| 输入/参数 | `Target` | CustomType | 被跟踪的对象，由外部窗口框选 |
| 输入/参数 | `CanvasCoordinates` | Boolean | 默认 true：画布坐标；false：输入图片像素坐标 |
| 输入 | `ImagePlacement` | SizeAndPosition | 可选，图片在画布中的绝对位置、尺寸及旋转 |
| 输出 | `Position` | SizeAndPosition | ClipPositionTuple，目标的轴对齐包围框 |
| 输出 | `Found` | Boolean | 是否定位到目标 |

`ImagePlacement` 不连接时，输入图片按比例覆盖整个项目画布。连接时，其位置和尺寸使用项目逻辑坐标，
旋转单位为度，绕图片中心旋转；需要提供绝对值和正尺寸。若输入已经包含素材变换，勿再次传入相同变换。
输入图片坐标模式忽略 `ImagePlacement`。

位置输出使用包围框左上角、宽和高，`IsDelta=false`、`Rotation=0`、`Opacity=1`。
画布坐标不随预览分辨率改变；边界可以超出画布。未找到时输出零尺寸包围框及 false，不沿用历史位置。
未配置目标也输出未找到；配置损坏、图片类型不支持及无效位置输入会报错。

## 框选目标

1. 导入插件，在“渲染”阶段添加“对象跟踪”，将节点的图片输入连接到需要分析的画面。
2. 将时间线移动到包含目标的参考帧，在“被跟踪的对象”参数旁打开外部编辑器。
3. 窗口通过宿主提供的 GUI RPC 管道调用 `GetInfo` 和 `GetFramePreview`，显示当前时间线整张画布。
4. 拖动鼠标框选目标；“刷新当前帧”重新读取主程序的当前位置并清空尚未保存的选区。
5. 应用或确定保存目标；关闭丢弃未应用的修改。已有目标但未重新框选时，应用保留原目标图。

添加时只连接图片输入，不替换画面输出。需要控制 Clip 位置时，在绑定编辑器中将 `Position` 输出连接到最终位置端口；`Found` 可独立连接到条件端口。

参考图为时间线合成画面，需保证其中目标的外观与节点输入中的目标相符。
缩放、留白和 DPI 不改变选区对应的图片像素。目标裁剪 PNG（JSON 中为 Base64）、参考尺寸、选区及阈值
存入契约 `projectFrameCut.ObjectTrackingPlugin.TrackingPlugin.target.v1`，可随项目迁移，不引用预览缓存路径。

匹配比例默认 0.75，最少内点 8，内点比例 0.5。降低匹配比例或提高内点要求可减少误匹配。
小选区或纯色区域可能没有足够特征，编辑器会提示重新框选。
同一帧中存在多个相似目标时，不保证区分其身份。

编辑器要求可写 GUI 项目及可用的外部 RPC。切换选中 Clip 不改变窗口正在编辑的 Provider；主程序退出时窗口关闭。
RPC 读取、预览或提交失败在窗口底部显示；异常进入现有 Logger，正常匹配及未匹配不逐帧写日志。
原生算法可能产生轻微采样差异，因此插件不声明确定性。

## 发布与打包

在仓库根目录运行（需要 .NET 11 SDK）：

```powershell
dotnet publish tools/projectFrameCut.ObjectTrackingPlugin/projectFrameCut.ObjectTrackingPlugin.csproj `
  -c Release -r win-x64 --self-contained false `
  -o ./artifacts/object-tracking/win-x64
```

目标机器需要 .NET 11 Windows Desktop Runtime。完整保留发布目录，包含 OpenCvSharp 的原生 DLL 和运行时依赖。
同一个 `projectFrameCut.ObjectTrackingPlugin.exe` 提供 `plugin_worker` 和 `edit_target` 入口。

使用你自己的签名证书打包：

```powershell
dotnet run --project tools/projectFrameCut.PluginPackageUtility -- pack `
  --input ./artifacts/object-tracking/win-x64 `
  --output ./artifacts/ObjectTracking.pjfcPlugin `
  --plugin-id projectFrameCut.ObjectTrackingPlugin.TrackingPlugin `
  --version 1.0.0.0 `
  --name "Object tracking" --author projectFrameCut `
  --description "Locate a selected reference object in each frame." `
  --author-url https://github.com/projectFrameCut `
  --backend External `
  --windows-entry projectFrameCut.ObjectTrackingPlugin.exe `
  --external-capabilities Effects `
  --certificate ./publisher-plugin-signing.pfx `
  --chain ./publisher-chain.pem `
  --password-env PJFC_PLUGIN_PFX_PASSWORD
```

包为 v3 外置后端格式，所有依赖由签名清单覆盖。更新后需重新导入包。
此工具不依赖 ExamplePlugin1，不提供视频源、音频源或其他插件能力。

## 手动验收

- 打开编辑器，确认当前时间线帧、缩放后的框选位置及应用后的参数一致；保存重开项目后目标仍可使用。
- 对有纹理目标测试平移、缩放、旋转，以及目标消失、遮挡、纯色和空目标。
- 跳转、倒放和并行导出，确认没有上一帧残留状态。
- 分别连接 Position 与 Found 到位置及条件端口，确认远程传输保留位置结构和布尔类型。
- 使用不同预览尺寸检查画布坐标稳定性；切换到图片坐标，检查其随输入像素尺寸变化。
- 连接已知 ImagePlacement，检查缩放、偏移、中心旋转和超出画布的包围框。
- 在编辑器打开时删除 Provider、关闭项目，确认提交失败有提示且未修改其他对象。
