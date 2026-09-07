# 外部 RPC 授权请求

打开本地草稿后，命名管道后端每秒扫描 `CLIProgram.AppDataPath/RpcRequest/*.json`，由草稿页弹框授权。多实例使用请求文件的独占写句柄竞争，同一文件只由一个后端处理。没有可用草稿页时不领取新请求，也不自动启动应用。进程内后端、远程项目及不提供此能力的传输不处理请求。

## CLI

等待授权并将解密后的 `RequestId`、`PipeName`、`Token` JSON 输出到标准输出：

```powershell
pjfc rpc_request --name="My App" --author="My Company" --purpose="生成预览" --wait
```

私钥仅在 CLI 内存中存在，退出时释放；请求文件在退出时尝试删除。授权中的文件被锁定，超时或取消时可能保留文件，但文件中没有私钥或明文连接凭据。

由调用方保管私钥，CLI 只提交公钥并立即返回请求 ID 和文件路径：

```powershell
pjfc rpc_request --name="My App" --author="My Company" --purpose="生成预览" --publicKey="C:\MyApp\public.pem"
```

`--timeout=300` 指定请求有效期（5–3600 秒）。`--requestDir=...` 可指定目标安装实例的请求目录。默认目录是应用数据目录，不是可自定义的素材/草稿数据目录；打包 Windows 应用使用 LocalState，非打包版本使用其应用数据目录。

退出码：0 成功或已提交，1 失败，2 参数错误，3 拒绝，4 过期，130 用户取消。等待模式的标准输出包含明文凭据，应由调用程序读取，不要写入公开日志。

## 文件协议 v1

先写临时文件，再重命名为 `<RequestId>.json`，不要让后端读取尚未写完的 JSON。字段名区分大小写，总大小不超过 32 KiB。

```json
{
  "Version": 1,
  "RequestId": "8ba83b86-8ff5-4202-a10f-3315c99e0dce",
  "AppName": "My App",
  "Author": "My Company",
  "Purpose": "生成预览",
  "PublicKey": "BASE64_DER_SUBJECT_PUBLIC_KEY_INFO",
  "ExpiresAt": "2026-09-06T12:05:00Z",
  "Status": "pending",
  "EncryptedConnection": ""
}
```

`ExpiresAt` 使用实际提交时间加有效期，最多一小时。名称、作者、用途非空且各不超过 2048 字符。`PublicKey` 是 3072–8192 位 RSA 公钥的 DER SubjectPublicKeyInfo 的 Base64 编码；CLI 的 `--publicKey` 接受 PEM 文件。

授权后，后端改写原文件：`Status` 变为 `approved`、`denied`、`expired` 或 `failed`。仅 `approved` 包含 `EncryptedConnection`，它是 RSA-OAEP-SHA256 加密后字节的 Base64。解密得到 UTF-8 JSON：

```json
{"RequestId":"原请求ID","PipeName":"projectFrameCut-rpc-...","Token":"..."}
```

使用对应私钥解密，校验 RequestId，再以 PipeName 和 Token 完成现有 Render RPC 握手。管道在后端关闭时失效；请求过期时间不是已授权管道的失效时间。不会执行任何 callback。

授权弹框期间文件保持打开，禁止其他写入及替换。读取方使用 `FileAccess.Read, FileShare.ReadWrite`，遇到暂时的 IO/JSON 错误重试；结果在同一文件句柄上更新，因此读取方需要容忍短暂的不完整内容。处理完的状态不会重复弹框；重新申请应提交新的请求 ID 和文件。后端异常退出后尚为 pending 的请求可被重新领取，必须重新授权。

## 保护边界

请求文件和日志不包含明文管道名、token 或私钥，泄漏文件不会直接泄漏 RPC 凭据。公钥指纹在弹框中展示；名称、作者和用途由请求方填写，不代表已验证身份。此协议不认证主应用的响应，也不防御能修改文件或读取进程内存的同用户恶意程序；调用方仍应保护私钥和程序输出。

`GetExternalRpcRequest` 和 `ResolveExternalRpcRequest` 只在内部管理管道提供，额外管道不能领取或批准请求。原菜单手动创建管道功能保持可用。
