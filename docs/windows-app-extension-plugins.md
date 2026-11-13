# Windows MSIX 应用扩展插件

Windows 主程序声明的应用扩展名称是 `projectFrameCut.ExternalPlugin`。普通版和商店版使用同一个名称。
主程序在启动、重新加载插件和进入扩展设置页时发现扩展；发现不会启动后端。
新扩展默认禁用，用户在设置的“Windows 应用扩展”区域启用后才会作为插件提供。

## 扩展包声明

其他 MSIX 包可在自己的 Application 的 Extensions 中声明：

```xml
<!-- Package 上声明 xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3" -->
<uap3:Extension Category="windows.appExtension">
  <uap3:AppExtension Name="projectFrameCut.ExternalPlugin"
                     Id="MyBackend"
                     DisplayName="My backend plugin"
                     Description="An external backend for projectFrameCut"
                     PublicFolder="Public" />
</uap3:Extension>
```

在 `Public/metadata.json` 放置现有 v3 外置后端元数据，例如：

```json
{
  "PackageFormatVersion": 3,
  "BackendKind": "External",
  "PluginID": "Example.MyBackend",
  "PluginAPIVersion": 8,
  "PluginAPIMinorVersion": 0,
  "IsAppLevelPlugin": false,
  "Name": "My backend plugin",
  "Author": "Example",
  "Description": "An external backend for projectFrameCut",
  "Version": "1.0.0.0",
  "AuthorUrl": "https://example.com",
  "ExternalBackend": {
    "Windows": {
      "EntryPoint": "MyBackend.exe",
      "UseShellExecute": false,
      "CreateNoWindow": true
    },
    "Capabilities": "Effects"
  }
}
```

`PluginAPIVersion` 必须匹配宿主当前的 `IPluginBase.CurrentPluginAPIVersion`。
后端返回的插件身份、版本、名称、作者、描述、网址和能力目录必须与元数据一致，校验规则与手动安装的外置后端相同。
这里只支持 `External` 后端和 `IsAppLevelPlugin=false`，必须提供 Windows 启动配置。

## 激活与信任

主程序信任 Windows 部署的已签名 MSIX 包，并检查包是否可用；无需另外提供 `manifest.json`、插件签名证书链或加密程序集。
未签名的开发布局包（`PackageSignatureKind.None`）会显示为加载失败。
这不影响手动导入 `.pjfcPlugin` 时的签名校验。

启动方式与现有外置后端一致：使用 `Process.Start` 和元数据中的启动选项，追加 `plugin_worker`、`--pipe`、
`--token`、`--sessionRoot`、`--parentPid`、`--pluginId`、`--pluginRoot`、`--instancePackageName`、`--ffmpegRoot` 参数，
由后端连接命名管道并完成认证和能力协商。可使用 `ExternalPluginBackend.RunAsync` 实现后端协议。

`EntryPoint` 可以使用绝对路径；`UseShellExecute=false` 时，相对入口以公共目录为根解析，不能通过 `..` 越界。
上面的例子要求 `MyBackend.exe` 及其依赖位于 `Public` 内。也可以配置现有 Shell 启动方式和应用执行别名；
主程序不会根据扩展声明自动选择 AUMID 或启动其所属 Application。目标程序需要支持上述后端参数和协议。
Windows AppContainer 中的后端访问权限不在本次接入范围内，现有传输面向可访问宿主管道和会话目录的桌面后端。

公共目录只读。配置保存在主程序数据目录的 `Plugins/<PluginID>/option.json`，会话数据保存在主程序缓存中。
外置端口编辑器的相对入口也使用同一个公共目录。

## 状态与包更新

- 启用偏好按 `PackageFamilyName + AppExtension.Id` 保存，包版本变化不会重置。
- `PluginID` 与手动插件冲突时保留手动插件；多个 MSIX 扩展声明相同 ID 时阻止这些扩展加载，并显示冲突。
- 禁用会卸载插件。扩展设置页不卸载 MSIX 包，请通过 Windows 管理安装和卸载。
- 刷新时发现包被卸载、不可用或元数据无效，会卸载对应的运行实例；启用偏好保留，重装后可在下次启动恢复加载。
- 正在运行的扩展包更新后，刷新会卸载旧实例并提示重启；新版本在下次启动加载。
- 不监听实时包事件，打开设置页或重启主程序可以刷新列表。非 Windows 平台不发现 MSIX 扩展。

应用扩展声明和公共目录 API 参见 [Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/develop/launch/app-extensions)。
