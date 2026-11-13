# 自定义端口的外部编辑器

在自定义端口的 `DataFormat` 中声明编辑器。配置是插件元数据，不写入项目参数；`CustomEffectPortValue` 仍只保存 `ContractId` 和 `Data`。

```csharp
var contract = new EffectPortTypeContract("example.curve.v1", "Curve");
var field = new EffectArgumentFieldDescriptor
{
    Id = "Curve",
    FieldType = EffectArgumentFieldType.CustomType,
    TypeContract = contract,
    DefaultValue = "{\"points\":[]}",
    DataFormat = new CustomEffectPortEditorConfiguration
    {
        Windows = new() { EntryPoint = "editor/CurveEditor.exe", Arguments = ["--mode=curve"] },
        MacOS = new() { EntryPoint = "editor/CurveEditor" },
        Linux = new() { EntryPoint = "editor/CurveEditor" },
    },
};
```

将描述符用于 Provider 对应的输入端口和参数字段。默认属性面板在有所属 Clip 上下文、当前平台配置和有效入口时显示“打开外部编辑器”；绑定中的输入参数沿用静态编辑器禁用行为。

入口支持绝对路径或相对插件目录的路径；相对路径不能越出插件目录，工作目录为所属插件目录。启动使用 `ProcessStartInfo.ArgumentList`，先传入 `Arguments`，再追加：

```text
--pipe=projectFrameCut-rpc-<one-time-token>
--clipId=<Guid>
--providerId=<Guid>
--fieldId=Curve
--parentPid=<pid>
```

`providerId` 是正在配置的 Provider ID，不是其构建出来的运行时 Effect ID。编辑器自行显示窗口，不使用外部后端插件的 `plugin_worker` 命令和握手。`UseShellExecute` 默认 `false`，`CreateNoWindow` 默认 `true`；后者控制控制台窗口，不影响编辑器自身创建 GUI 窗口。

需要可写的本地项目和可用的进程式 RPC 后端，并遵守全局外部 RPC 开关。每次启动创建属于当前项目的一次性管道；编辑器使用该连接完成读取和提交，不再申请一次授权，也不要把管道名写入日志。后续切换选中 Clip 不会改变管道对应的项目及传入的目标 ID。

## 读取和提交参数

编辑器引用 `projectFrameCut.Render.Contracts`；构造自定义值也可引用 `projectFrameCut.Render.RenderAPIBase`。以下代码在编辑器解析上述启动参数后执行，`newPoints` 为用户确认的新数据：

```csharp
using System.Text.Json;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

var clientId = $"curve-editor-{Environment.ProcessId}";
await using var client = new RenderClient(new NamedPipeRenderClientTransport(pipe, clientId), clientId);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

var read = await client.InvokeGuiProjectAsync(new()
{
    Operation = GuiProjectOperation.GetClipEffectProvider,
    ParametersJson = JsonSerializer.Serialize(new { ClipId = clipId, ProviderId = providerId }),
}, timeout.Token);
read.Error?.ThrowAsException();
using var current = JsonDocument.Parse(read.Json);
var provider = current.RootElement.EnumerateArray().Single();
var field = provider.GetProperty("Fields").EnumerateArray()
    .Single(f => f.GetProperty("FieldId").GetString() == fieldId);
var contractId = field.GetProperty("TypeContract").GetProperty("Id").GetString()!;
// field.GetProperty("Value") 包含当前的 { ContractId, Data }，可用于初始化编辑器。

var value = new CustomEffectPortValue(contractId, JsonSerializer.SerializeToElement(new { points = newPoints }));
var saved = await client.InvokeGuiProjectAsync(new()
{
    Operation = GuiProjectOperation.SetClipEffectProvider,
    ChangeReason = "Edit curve",
    ParametersJson = JsonSerializer.Serialize(new
    {
        ClipId = clipId,
        ProviderId = providerId,
        Fields = new Dictionary<string, object> { [fieldId] = value },
    }),
}, timeout.Token);
saved.Error?.ThrowAsException();
```

提交仅包含需要修改的字段。GUI RPC 负责效果重建、属性与节点面板刷新、预览更新及历史记录；有效图的提交成功后无需再发刷新命令，也无需关闭进程。存在未连接端口等图诊断时，沿用 GUI RPC 保存配置并暂缓渲染的行为。用户取消编辑时不提交；项目关闭、Clip 或 Provider 被删除以及 RPC 错误均应在编辑器中作为提交失败处理。

这里使用 `InvokeGuiProjectAsync` 操作当前 GUI 项目，`AddOrReplaceHeadlessEffect` 等方法用于独立的 headless 项目。
