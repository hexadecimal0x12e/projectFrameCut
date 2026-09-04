using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Text;
using System.Text.RegularExpressions;
using projectFrameCut.Shared;
using LocalizedResources;

namespace projectFrameCut.ScriptEngine
{
    // ═══════════════════════════════════════════════════════════════
    // 枚举类型
    // ═══════════════════════════════════════════════════════════════

    /// <summary>脚本级威胁级别。</summary>
    public enum ThreatLevel
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4,
    }

    /// <summary>脚本分析中检测到的特定标记。</summary>
    [Flags]
    public enum ScriptFlag
    {
        None = 0,
        Base64EncodedCommand = 1,
        ExcessiveBacktickUsage = 2,
        StringConcatenation = 4,
        EncodedCommandParameter = 8,
        SuspiciousInvocation = 16,
        SuspiciousVariableConstruction = 32,
        WinApiInvocation = 64,
        AssemblyLoading = 128,
        HiddenWindowExecution = 256,
        DotNetReflection = 512,
        SensitiveTypeAccess = 1024,
        DynamicTypeAccess = 2048,
    }

    /// <summary>路径安全状态。</summary>
    public enum PathSafety
    {
        /// <summary>路径在项目目录内。</summary>
        Safe,
        /// <summary>路径在项目目录外。</summary>
        OutsideProject,
        /// <summary>检测到路径遍历攻击。</summary>
        PathTraversal,
        /// <summary>路径无效或无法解析。</summary>
        Invalid,
        /// <summary>路径来自变量或表达式，无法静态解析。</summary>
        Unresolved,
        /// <summary>路径指向应用程序数据目录，禁止访问。</summary>
        NotAllowToAccess
    }

    // ═══════════════════════════════════════════════════════════════
    // 结果类型
    // ═══════════════════════════════════════════════════════════════

    /// <summary>描述一个混淆模式。</summary>
    public class ObfuscationDetail
    {
        /// <summary>检测到的模式名称。</summary>
        public string Pattern { get; init; } = "";

        /// <summary>对人类可读的描述。</summary>
        public string Description { get; init; } = "";

        /// <summary>严重程度（1-5）。</summary>
        public int Severity { get; init; }

        /// <summary>在脚本中出现的行号（从 1 开始）。</summary>
        public int LineNumber { get; init; }
    }

    /// <summary>脚本级分析的结果。</summary>
    public class ScriptAnalysisResult
    {
        /// <summary>总体威胁级别。</summary>
        public ThreatLevel ThreatLevel { get; set; } = ThreatLevel.None;

        /// <summary>检测到的标记列表。</summary>
        public List<ScriptFlag> Flags { get; } = new();

        /// <summary>检测到的混淆详情。</summary>
        public List<ObfuscationDetail> Obfuscations { get; } = new();

        /// <summary>是否可疑（ThreatLevel >= Medium）。</summary>
        public bool IsSuspicious => ThreatLevel >= ThreatLevel.Medium;

        /// <summary>人类可读的摘要。</summary>
        public string Summary { get; set; } = "";
    }

    /// <summary>从脚本中提取的单个命令的参数快照。</summary>
    public class CommandParameterInfo
    {
        /// <summary>命令名称（如 Set-Content、Invoke-WebRequest）。</summary>
        public string CommandName { get; init; } = "";

        /// <summary>文件操作的目标路径（-Path / -LiteralPath / -Destination）。</summary>
        public string? TargetPath { get; set; }

        /// <summary>路径是否在项目目录内。</summary>
        public bool IsPathWithinProject { get; set; }

        /// <summary>路径安全状态的详细枚举。</summary>
        public PathSafety PathSafetyStatus { get; set; } = PathSafety.Unresolved;

        /// <summary>网络请求的目标 URL（-Uri）。</summary>
        public string? TargetUrl { get; set; }

        /// <summary>命令的原始调用文本。</summary>
        public string RawInvocationText { get; init; } = "";

        /// <summary>命令在脚本中的行号。</summary>
        public int LineNumber { get; init; }
    }

    // ═══════════════════════════════════════════════════════════════
    // 核心 CommandFilter 类
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// PowerShell 命令筛选器，与 <see cref="PSCommandAuthorizationHelper"/> 配合，
    /// 提供脚本级预分析和命令参数级检查：
    /// <list type="bullet">
    ///   <item>检测混淆模式（Base64 编码命令、字符串拼接、反引号混淆等）</item>
    ///   <item>提取文件操作的目标路径，检查是否在项目目录内</item>
    ///   <item>提取 Web 请求的目标 URL</item>
    ///   <item>拦截高度可疑的脚本（ThreatLevel >= Critical）</item>
    /// </list>
    /// </summary>
    internal class CommandFilter
    {
        /// <summary>项目的根工作目录，用于路径安全检查。</summary>
        public string? WorkingPath { get; set; }

        // ─── 别名解析说明 ────────────────────────────────────────
        // 不使用静态别名映射表。
        // 别名→标准命令名的解析由 PSCommandAuthorizationHelper 在运行时
        // 通过 PowerShell SDK 的 AliasInfo 完成，可正确处理所有别名。
        // ───────────────────────────────────────────────────────────

        // ─── 混淆检测阈值 ────────────────────────────────────────

        /// <summary>反引号混淆检测阈值（占比超过此值即标记）。</summary>
        public double BacktickThreshold { get; set; } = 0.05;

        /// <summary>Base64 最小长度才触发检测。</summary>
        public int MinBase64Length { get; set; } = 40;

        // ─── .NET 反射访问检测 ──────────────────────────────────

        /// <summary>
        /// 安全的 .NET 类型（完全放行，不需要询问）。
        /// 仅包含纯计算、日期、路径处理等无害类型。
        /// </summary>
        private static readonly HashSet<string> SafeDotNetTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            // 基础类型
            "System.DateTime", "System.DateTimeOffset", "System.TimeSpan",
            "System.DateOnly", "System.TimeOnly",
            "System.Math", "System.Random",
            "System.Guid", "System.Version", "System.Uri",
            "System.UriBuilder",

            // 数值
            "System.Int32", "System.Int64", "System.Int16", "System.SByte",
            "System.UInt32", "System.UInt64", "System.UInt16", "System.Byte",
            "System.Double", "System.Single", "System.Decimal",
            "System.Numerics.BigInteger",
            "System.Numerics.Vector2", "System.Numerics.Vector3", "System.Numerics.Vector4",
            "System.Numerics.Matrix4x4", "System.Numerics.Quaternion",
            "System.Numerics.Plane",

            // 文本
            "System.String", "System.Char", "System.Text.StringBuilder",
            "System.Text.Encoding", "System.Text.ASCIIEncoding",
            "System.Text.UTF8Encoding", "System.Text.UnicodeEncoding",
            "System.Text.RegularExpressions.Regex",
            "System.Text.RegularExpressions.Match",
            "System.Text.RegularExpressions.Group",
            "System.Text.RegularExpressions.Capture",

            // 集合
            "System.Array", "System.Buffer",
            "System.Tuple", "System.ValueTuple",
            "System.Range", "System.Index",
            "System.Collections.Generic.List`1",
            "System.Collections.Generic.Dictionary`2",

            // 路径（只读操作安全）
            "System.IO.Path",

            // 转换
            "System.Convert", "System.BitConverter",
            "System.Enum", "System.Type",
            "System.FormattableString",

            // 数学扩展
            "System.Numerics.Complex",

            // 格式化
            "System.IFormatProvider", "System.Globalization.CultureInfo",
            "System.Globalization.DateTimeFormatInfo",
            "System.Globalization.NumberFormatInfo",
        };

        /// <summary>
        /// 高危/敏感的 .NET 类型（一旦检测即威胁级别 >= High）。
        /// 这些类型可被用于访问用户数据、系统设置、执行代码或网络请求。
        /// </summary>
        private static readonly HashSet<string> DangerousDotNetTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            // ── 存储 / 设置 ──
            "projectFrameCut.User", //not for the oss version; for the real production app
            "projectFrameCut.APIClient", //not for the oss version; for the real production app
            "projectFrameCut.Setting.SettingManager.SettingsManager",
            "Microsoft.Maui.Storage.SecureStorage",
            "Microsoft.Maui.Storage.Preferences",
            "Microsoft.Maui.Storage.FileSystem",
            "Microsoft.Maui.Storage.FilePicker",
            "Microsoft.Maui.Storage.FolderPicker",
            "Microsoft.Maui.Storage.MediaPicker",
            "Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard",

            // ── 权限 / 设备信息 ──
            "Microsoft.Maui.ApplicationModel.Permissions",
            "Microsoft.Maui.ApplicationModel.Platform",
            "Microsoft.Maui.ApplicationModel.Map",
            "Microsoft.Maui.ApplicationModel.Browser",
            "Microsoft.Maui.ApplicationModel.Communication.Email",
            "Microsoft.Maui.ApplicationModel.Communication.PhoneDialer",
            "Microsoft.Maui.ApplicationModel.Communication.Sms",


            // ── 反射 / 代码执行 ──
            "System.Reflection.Assembly",
            "System.Reflection.AssemblyName",
            "System.Reflection.Emit.AssemblyBuilder",
            "System.Reflection.Emit.TypeBuilder",
            "System.Reflection.Emit.MethodBuilder",
            "System.Reflection.Emit.DynamicMethod",
            "System.Reflection.Emit.ILGenerator",
            "System.Reflection.FieldInfo",
            "System.Reflection.MethodInfo",
            "System.Reflection.ConstructorInfo",
            "System.Reflection.PropertyInfo",
            "System.Reflection.MemberInfo",
            "System.Reflection.Module",
            "System.Activator",
            "System.Runtime.Serialization.FormatterServices",

            // ── 互操作 / 内存 ──
            "System.Runtime.InteropServices.Marshal",
            "System.Runtime.InteropServices.GCHandle",
            "System.Runtime.InteropServices.SafeHandle",
            "Microsoft.Win32.SafeHandles",

            // ── 注册表 ──
            "Microsoft.Win32.Registry",
            "Microsoft.Win32.RegistryKey",

            // ── 进程 / 服务 ──
            "System.Diagnostics.Process",
            "System.Diagnostics.ProcessModule",
            "System.Diagnostics.ProcessThread",
            "System.Diagnostics.ProcessStartInfo",
            "System.Diagnostics.PerformanceCounter",
            "System.Diagnostics.EventLog",
            "System.IO.FileSystemWatcher",
            "System.IO.FileSystemEventArgs",
            "System.Management.Automation.PowerShell",
            "System.Management.Automation.Runspaces.Runspace",

            // ── 网络 ──
            "System.Net.WebClient",
            "System.Net.Http.HttpClient",
            "System.Net.Http.HttpMessageHandler",
            "System.Net.Http.HttpRequestMessage",
            "System.Net.Sockets.TcpClient",
            "System.Net.Sockets.UdpClient",
            "System.Net.Sockets.Socket",
            "System.Net.Mail.SmtpClient",
            "System.Net.Dns",

            // ── 文件系统 ──
            "System.IO.File",
            "System.IO.FileInfo",
            "System.IO.Directory",
            "System.IO.DirectoryInfo",
            "System.IO.DriveInfo",
            "System.IO.StreamReader",
            "System.IO.StreamWriter",
            "System.IO.FileStream",
            "System.IO.MemoryStream",
            "System.IO.BufferedStream",
            "System.IO.Compression.ZipFile",
            "System.IO.Compression.GZipStream",
            "System.IO.FileSystemAclExtensions",

            // ── 加密 / 安全 ──
            "System.Security.Cryptography.Aes",
            "System.Security.Cryptography.RSA",
            "System.Security.Cryptography.ECDsa",
            "System.Security.Cryptography.ProtectedData",
            "System.Security.Cryptography.CryptoStream",
            "System.Security.Cryptography.X509Certificates.X509Certificate2",
            "System.Security.Cryptography.X509Certificates.X509Store",

            // ── MAUI 页面 / 导航 ──
            "Microsoft.Maui.Controls.Application",
            "Microsoft.Maui.Controls.Page",
            "Microsoft.Maui.Controls.NavigationPage",
            "Microsoft.Maui.Controls.Shell",

            // ── WinUI / 平台特定 ──
            "Windows.ApplicationModel.DataTransfer.Clipboard",
            "Windows.Storage.ApplicationData",
            "Windows.Storage.StorageFile",
            "Windows.Storage.StorageFolder",
            "Windows.Storage.KnownFolders",
            "Windows.UI.ViewManagement.ApplicationView",
            "Microsoft.UI.Windowing.AppWindow",
            "Microsoft.UI.Xaml.Window",
            "Microsoft.UI.Xaml.Application",
        };

        // ──────────────────────────────────────────────────────────
        //  1. 脚本级综合分析
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 对整段脚本进行安全性分析，遍历所有检测器后汇总威胁级别。
        /// </summary>
        public ScriptAnalysisResult AnalyzeScript(string script)
        {
            var result = new ScriptAnalysisResult();
            if (string.IsNullOrWhiteSpace(script))
                return result;

            var flags = new List<ScriptFlag>();
            var obfuscations = new List<ObfuscationDetail>();
            var lines = script.Split('\n');
            int highestSeverity = 0;

            // 尝试解析 AST
            ScriptBlockAst? ast = null;
            try
            {
                ast = Parser.ParseInput(script, out _, out var parseErrors);
                // 解析错误过多说明脚本本身可疑
                if (parseErrors != null && parseErrors.Length > 5)
                {
                    obfuscations.Add(new ObfuscationDetail
                    {
                        Pattern = "ExcessiveParseErrors",
                        Description = Localized.ScriptEngine_Filter_Obfs_ExcessiveParseErrors(parseErrors.Length),
                        Severity = 2,
                        LineNumber = 1,
                    });
                    highestSeverity = Math.Max(highestSeverity, 2);
                }
            }
            catch
            {
                // 解析失败本身可能是混淆信号
                obfuscations.Add(new ObfuscationDetail
                {
                    Pattern = "ParseFailure",
                    Description = Localized.ScriptEngine_Filter_Obfs_ParseFailure,
                    Severity = 3,
                    LineNumber = 1,
                });
                highestSeverity = Math.Max(highestSeverity, 3);
            }

            // ── 2. 各种检测 ──

            // 2a. EncodedCommand 参数检测
            if (DetectEncodedCommandParameter(script, out var encCmdDetail))
            {
                flags.Add(ScriptFlag.EncodedCommandParameter);
                obfuscations.Add(encCmdDetail);
                highestSeverity = Math.Max(highestSeverity, encCmdDetail.Severity);
            }

            // 2b. Base64 使用检测
            if (DetectBase64Usage(script, out var base64Details))
            {
                flags.Add(ScriptFlag.Base64EncodedCommand);
                obfuscations.AddRange(base64Details);
                highestSeverity = Math.Max(highestSeverity, base64Details.Max(d => d.Severity));
            }

            // 2c. 反引号混淆检测
            if (DetectExcessiveBacktickUsage(script, out var backtickDetail, out _))
            {
                flags.Add(ScriptFlag.ExcessiveBacktickUsage);
                obfuscations.Add(backtickDetail);
                highestSeverity = Math.Max(highestSeverity, backtickDetail.Severity);
            }

            // 2d. 字符串拼接检测（命令名位置）
            if (ast != null && DetectStringConcatenation(ast, out var concatDetails))
            {
                flags.Add(ScriptFlag.StringConcatenation);
                obfuscations.AddRange(concatDetails);
                highestSeverity = Math.Max(highestSeverity, concatDetails.Max(d => d.Severity));
            }

            // 2e. 可疑的 Invoke-Expression 调用
            if (ast != null && DetectSuspiciousInvocation(ast, out var invokeDetails))
            {
                flags.Add(ScriptFlag.SuspiciousInvocation);
                obfuscations.AddRange(invokeDetails);
                highestSeverity = Math.Max(highestSeverity, invokeDetails.Max(d => d.Severity));
            }

            // 2f. WinAPI 调用检测
            if (DetectWinApiPatterns(script, out var winApiDetails))
            {
                flags.Add(ScriptFlag.WinApiInvocation);
                obfuscations.AddRange(winApiDetails);
                highestSeverity = Math.Max(highestSeverity, winApiDetails.Max(d => d.Severity));
            }

            // 2g. Assembly 动态加载检测
            if (DetectAssemblyLoading(ast, script, out var assemblyDetails))
            {
                flags.Add(ScriptFlag.AssemblyLoading);
                obfuscations.AddRange(assemblyDetails);
                highestSeverity = Math.Max(highestSeverity, assemblyDetails.Max(d => d.Severity));
            }

            // 2h. 隐藏窗口执行检测
            if (DetectHiddenWindow(script, out var hiddenWindowDetail))
            {
                flags.Add(ScriptFlag.HiddenWindowExecution);
                obfuscations.Add(hiddenWindowDetail);
                highestSeverity = Math.Max(highestSeverity, hiddenWindowDetail.Severity);
            }

            // 2i. .NET 反射访问检测（需 AST）
            if (ast != null && DetectDotNetReflection(ast, out var reflectionDetails))
            {
                obfuscations.AddRange(reflectionDetails);

                // 检查是否包含危险类型访问
                if (reflectionDetails.Any(d => d.Pattern == "DangerousDotNetAccess"))
                    flags.Add(ScriptFlag.SensitiveTypeAccess);
                if (reflectionDetails.Any(d => d.Pattern == "DynamicTypeNameVariable"
                    || d.Pattern == "LateBindingReflection"))
                    flags.Add(ScriptFlag.DynamicTypeAccess);
                if (reflectionDetails.Any(d => d.Pattern == "UnknownDotNetAccess"
                    || d.Pattern == "DotNetTypeReference"))
                    flags.Add(ScriptFlag.DotNetReflection);

                highestSeverity = Math.Max(highestSeverity, reflectionDetails.Max(d => d.Severity));
            }

            // ── 3. 汇总 ──

            result.Flags.AddRange(flags.Distinct());

            // 按行号排序
            obfuscations = obfuscations.OrderBy(o => o.LineNumber).ToList();
            result.Obfuscations.AddRange(obfuscations);

            // 确定总威胁级别
            result.ThreatLevel = highestSeverity switch
            {
                >= 5 => ThreatLevel.Critical,
                4 => ThreatLevel.High,
                3 => ThreatLevel.Medium,
                2 => ThreatLevel.Low,
                _ => ThreatLevel.None,
            };

            // 生成摘要
            result.Summary = BuildSummary(result);

            return result;
        }

        /// <summary>
        /// 基于结果构建人类可读的摘要。
        /// </summary>
        private static string BuildSummary(ScriptAnalysisResult result)
        {
            if (result.ThreatLevel == ThreatLevel.None)
                return Localized.ScriptEngine_Filter_SummaryNone;

            var parts = new List<string>();
            if (result.Flags.Contains(ScriptFlag.EncodedCommandParameter))
                parts.Add(Localized.ScriptEngine_Filter_FlagEncodedCmd);
            if (result.Flags.Contains(ScriptFlag.Base64EncodedCommand))
                parts.Add(Localized.ScriptEngine_Filter_FlagBase64);
            if (result.Flags.Contains(ScriptFlag.ExcessiveBacktickUsage))
                parts.Add(Localized.ScriptEngine_Filter_FlagBacktick);
            if (result.Flags.Contains(ScriptFlag.StringConcatenation))
                parts.Add(Localized.ScriptEngine_Filter_FlagStringConcat);
            if (result.Flags.Contains(ScriptFlag.SuspiciousInvocation))
                parts.Add(Localized.ScriptEngine_Filter_FlagSuspiciousInvoke);
            if (result.Flags.Contains(ScriptFlag.WinApiInvocation))
                parts.Add(Localized.ScriptEngine_Filter_FlagWinApi);
            if (result.Flags.Contains(ScriptFlag.AssemblyLoading))
                parts.Add(Localized.ScriptEngine_Filter_FlagAssemblyLoad);
            if (result.Flags.Contains(ScriptFlag.HiddenWindowExecution))
                parts.Add(Localized.ScriptEngine_Filter_FlagHiddenWindow);
            if (result.Flags.Contains(ScriptFlag.DotNetReflection))
                parts.Add(Localized.ScriptEngine_Filter_FlagDotNetReflect);
            if (result.Flags.Contains(ScriptFlag.SensitiveTypeAccess))
                parts.Add(Localized.ScriptEngine_Filter_FlagSensitiveType);
            if (result.Flags.Contains(ScriptFlag.DynamicTypeAccess))
                parts.Add(Localized.ScriptEngine_Filter_FlagDynamicType);

            var prefix = result.ThreatLevel switch
            {
                ThreatLevel.Low => Localized.ScriptEngine_Filter_ThreatPrefix_Low,
                ThreatLevel.Medium => Localized.ScriptEngine_Filter_ThreatPrefix_Medium,
                ThreatLevel.High => Localized.ScriptEngine_Filter_ThreatPrefix_High,
                ThreatLevel.Critical => Localized.ScriptEngine_Filter_ThreatPrefix_Critical,
                _ => Localized.ScriptEngine_Filter_ThreatPrefix_None,
            };

            return Localized.ScriptEngine_Filter_SummaryFormat(prefix, parts.Count, string.Join("；", parts));
        }

        // ──────────────────────────────────────────────────────────
        //  2. 命令参数提取（AST）
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 使用 PowerShell AST 解析器提取脚本中每个命令的文件路径/URL 参数。
        /// </summary>
        public List<CommandParameterInfo> AnalyzeCommands(string script)
        {
            var results = new List<CommandParameterInfo>();
            if (string.IsNullOrWhiteSpace(script))
                return results;

            ScriptBlockAst? ast;
            try
            {
                ast = Parser.ParseInput(script, out _, out _);
            }
            catch
            {
                return results;
            }

            // 查找所有命令节点
            var commandAsts = ast.FindAll(n => n is CommandAst, true);
            foreach (CommandAst cmd in commandAsts)
            {
                var info = ExtractCommandParameterInfo(cmd);
                if (info != null)
                    results.Add(info);
            }

            // 阻断：检测到任何命令尝试访问应用数据目录 → 直接抛出异常，不进入授权流程
            foreach (var blocked in results)
            {
                if (blocked.PathSafetyStatus == PathSafety.NotAllowToAccess)
                {
                    Logger.Log($"[CommandFilter] 命令 '{blocked.CommandName}' 尝试访问应用数据目录，已阻断: {blocked.TargetPath}");
                    throw new NotAllowedCommandException(
                        NotAllowedCommandException.DeniedReason.DisallowedByInternalRules,
                        $"命令 '{blocked.CommandName}' 尝试访问应用程序数据目录（{blocked.TargetPath}），已被安全策略禁止。");
                }
            }

            return results;
        }

        /// <summary>
        /// 从单个 CommandAst 节点提取参数信息。
        /// 处理两种参数风格：
        ///   命名参数：Set-Content -Path "foo.txt" -Value "bar"
        ///   位置参数：Set-Content "foo.txt" "bar"   /   ls c:\windows
        /// </summary>
        private CommandParameterInfo? ExtractCommandParameterInfo(CommandAst cmd)
        {
            var cmdName = cmd.GetCommandName();
            if (string.IsNullOrEmpty(cmdName))
                return null;

            var extent = cmd.Extent;
            int lineNumber = extent?.StartLineNumber ?? 0;

            var info = new CommandParameterInfo
            {
                CommandName = cmdName,
                RawInvocationText = extent?.Text ?? cmdName,
                LineNumber = lineNumber,
            };

            bool isFileCmd = IsFileManipulationCommand(cmdName);
            bool isWebCmd = IsWebRequestCommand(cmdName);
            bool isCopyMoveCmd = IsCopyMoveCommand(cmdName);

            if (!isFileCmd && !isWebCmd && !isCopyMoveCmd)
                return null;

            // 遍历 CommandElements 提取参数
            // CommandElements[0] 是命令名，从 index=1 开始才是参数
            for (int i = 1; i < cmd.CommandElements.Count; i++)
            {
                var element = cmd.CommandElements[i];

                // ── 处理命名参数（-Path "value" 形式） ──
                if (element is CommandParameterAst param)
                {
                    var paramName = param.ParameterName ?? "";

                    // 文件路径参数
                    if ((isFileCmd || isCopyMoveCmd) && IsPathParameter(paramName))
                    {
                        var path = TryExtractStringValue(param.Argument);
                        if (path != null)
                        {
                            info.TargetPath = path;
                            info.PathSafetyStatus = CheckPathSafety(path);
                            info.IsPathWithinProject = info.PathSafetyStatus == PathSafety.Safe;
                        }
                    }

                    // 复制/移动命令的 -Destination 参数
                    if (isCopyMoveCmd
                        && string.Equals(paramName, "Destination", StringComparison.OrdinalIgnoreCase))
                    {
                        var dest = TryExtractStringValue(param.Argument);
                        if (dest != null)
                        {
                            info.TargetPath = dest;
                            info.PathSafetyStatus = CheckPathSafety(dest);
                            info.IsPathWithinProject = info.PathSafetyStatus == PathSafety.Safe;
                        }
                    }

                    // URL 参数
                    if (isWebCmd
                        && string.Equals(paramName, "Uri", StringComparison.OrdinalIgnoreCase))
                    {
                        info.TargetUrl = TryExtractStringValue(param.Argument);
                    }

                    // WebRequest 的 -OutFile 参数（相当于文件写入）
                    if (isWebCmd
                        && string.Equals(paramName, "OutFile", StringComparison.OrdinalIgnoreCase))
                    {
                        var outFile = TryExtractStringValue(param.Argument);
                        if (outFile != null)
                        {
                            info.TargetPath = outFile;
                            info.PathSafetyStatus = CheckPathSafety(outFile);
                            info.IsPathWithinProject = info.PathSafetyStatus == PathSafety.Safe;
                        }
                    }

                    continue;
                }

                // ── 处理位置参数（裸的字符串字面量） ──
                if (element is StringConstantExpressionAst positionalArg)
                {
                    var val = positionalArg.Value;

                    // Web 命令：第一个位置参数通常是 URL
                    if (isWebCmd && info.TargetUrl == null
                        && (val.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            || val.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    {
                        info.TargetUrl = val;
                        continue;
                    }

                    // 文件命令：第一个看起来像路径的位置参数就是目标路径
                    if ((isFileCmd || isCopyMoveCmd) && info.TargetPath == null
                        && LooksLikePath(val))
                    {
                        info.TargetPath = val;
                        info.PathSafetyStatus = CheckPathSafety(val);
                        info.IsPathWithinProject = info.PathSafetyStatus == PathSafety.Safe;
                        continue;
                    }
                }
            }

            // 如果文件命令没有提取到路径，尝试用第一个位置参数作为路径（兜底）
            if ((isFileCmd || isCopyMoveCmd) && info.TargetPath == null)
            {
                foreach (var element in cmd.CommandElements)
                {
                    if (element is StringConstantExpressionAst fallback)
                    {
                        var val = fallback.Value;
                        if (!string.IsNullOrEmpty(val) && !val.StartsWith("-"))
                        {
                            info.TargetPath = val;
                            info.PathSafetyStatus = CheckPathSafety(val);
                            info.IsPathWithinProject = info.PathSafetyStatus == PathSafety.Safe;
                            break;
                        }
                    }
                }
            }

            return info;
        }

        /// <summary>判断字符串是否看起来像文件系统路径。</summary>
        private static bool LooksLikePath(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            // 包含路径分隔符
            if (value.Contains('\\') || value.Contains('/'))
                return true;

            // 以驱动器号开头（如 C:）
            if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
                return true;

            // 以 .\ 或 ..\ 开头（相对路径）
            if (value.StartsWith(".\\", StringComparison.Ordinal) || value.StartsWith("..\\", StringComparison.Ordinal))
                return true;

            // 以 $ 开头（变量引用，如 $WorkingPath）
            if (value.StartsWith("$", StringComparison.Ordinal))
                return true;

            // 包含文件扩展名特征
            if (value.Contains('.') && value.Length > 5 && !value.Contains(' '))
                return true;

            return false;
        }

        // ──────────────────────────────────────────────────────────
        //  3. 路径安全检查
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 检查目标路径是否在项目工作目录内。
        /// </summary>
        public PathSafety CheckPathSafety(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
                return PathSafety.Invalid;

            if (string.IsNullOrWhiteSpace(WorkingPath) || string.IsNullOrWhiteSpace(MauiProgram.DataPath) || string.IsNullOrWhiteSpace(MauiProgram.BasicDataPath))
                return PathSafety.Unresolved;

            try
            {
                var fullPath = Path.GetFullPath(targetPath);
                var basePath = Path.GetFullPath(WorkingPath);
                var workspacePath = Path.GetFullPath(Path.Combine(MauiProgram.CachePath, "ScriptWorkspace"));
                var userDataPath = Path.GetFullPath(MauiProgram.DataPath);
                var appDataPath = Path.GetFullPath(MauiProgram.BasicDataPath);

                // 确保 basePath 以目录分隔符结尾，防止前缀误匹配
                if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    basePath += Path.DirectorySeparatorChar;

                if (fullPath.StartsWith(appDataPath, StringComparison.OrdinalIgnoreCase))
                    return PathSafety.NotAllowToAccess;

                // 检查路径遍历：规范化路径是否在 basePath 内
                if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) || fullPath.StartsWith(userDataPath, StringComparison.OrdinalIgnoreCase) || fullPath.StartsWith(workspacePath, StringComparison.OrdinalIgnoreCase))
                    return PathSafety.Safe;

                return PathSafety.OutsideProject;
            }
            catch (ArgumentException)
            {
                return PathSafety.Invalid;
            }
            catch (IOException)
            {
                return PathSafety.Invalid;
            }
        }

        /// <summary>
        /// 静态辅助方法：判断候选路径是否在指定基目录内。
        /// </summary>
        public static PathSafety IsPathWithinDirectory(string candidatePath, string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(baseDirectory))
                return PathSafety.Invalid;

            try
            {
                var full = Path.GetFullPath(candidatePath);
                var baseDir = Path.GetFullPath(baseDirectory);
                if (!baseDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    baseDir += Path.DirectorySeparatorChar;

                return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
                    ? PathSafety.Safe
                    : PathSafety.OutsideProject;
            }
            catch
            {
                return PathSafety.Invalid;
            }
        }

        // ──────────────────────────────────────────────────────────
        //  4. 各混淆检测器的实现
        // ──────────────────────────────────────────────────────────

        /// <summary>检测脚本中的 -EncodedCommand / -e 参数。</summary>
        public bool DetectEncodedCommandParameter(string script, out ObfuscationDetail detail)
        {
            var regex = new Regex(
                @"-(?:EncodedCommand|enc|e)\s+([A-Za-z0-9+/=]{40,})",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var match = regex.Match(script);
            if (match.Success)
            {
                var line = GetLineNumber(script, match.Index);
                detail = new ObfuscationDetail
                {
                    Pattern = "EncodedCommandParameter",
                    Description = Localized.ScriptEngine_Filter_Obfs_EncodedCmdParam(line),
                    Severity = 4,
                    LineNumber = line,
                };
                return true;
            }

            detail = null!;
            return false;
        }

        /// <summary>检测 Base64 解码命令模式。</summary>
        public bool DetectBase64Usage(string script, out List<ObfuscationDetail> details)
        {
            details = new List<ObfuscationDetail>();

            // 使用脚本文本正则查找所有 base64 解码调用模式
            var base64DecodeRegex = new Regex(
                @"\[Convert\][:\s]*::\s*FromBase64String",
                RegexOptions.IgnoreCase);

            var matches = base64DecodeRegex.Matches(script);
            foreach (Match match in matches)
            {
                var line = GetLineNumber(script, match.Index);
                details.Add(new ObfuscationDetail
                {
                    Pattern = "Base64Decode",
                    Description = Localized.ScriptEngine_Filter_Obfs_Base64Decode(line),
                    Severity = 4,
                    LineNumber = line,
                });
            }

            // 额外匹配长 base64 字符串拼接（脚本中内联的编码内容）
            var longBase64 = new Regex(@"""[A-Za-z0-9+/=]{60,}""", RegexOptions.CultureInvariant);
            var b64matches = longBase64.Matches(script);
            foreach (Match b64match in b64matches)
            {
                var line = GetLineNumber(script, b64match.Index);
                if (!details.Any(d => d.LineNumber == line && d.Pattern == "Base64Decode"))
                {
                    details.Add(new ObfuscationDetail
                    {
                        Pattern = "InlineBase64",
                        Description = Localized.ScriptEngine_Filter_Obfs_InlineBase64(line),
                        Severity = 3,
                        LineNumber = line,
                    });
                }
            }

            return details.Count > 0;
        }

        /// <summary>检测反引号混淆（如 `I`n`v`o`k`e`-`E`x`p`r`e`s`s`i`o`n）。</summary>
        public bool DetectExcessiveBacktickUsage(string script, out ObfuscationDetail detail, out double ratio)
        {
            if (string.IsNullOrWhiteSpace(script))
            {
                detail = null!;
                ratio = 0;
                return false;
            }

            int backtickCount = 0;
            int totalChars = 0;

            foreach (char c in script)
            {
                if (c == '`')
                    backtickCount++;
                if (!char.IsWhiteSpace(c))
                    totalChars++;
            }

            ratio = totalChars > 0 ? (double)backtickCount / totalChars : 0;

            if (ratio > BacktickThreshold && backtickCount >= 3)
            {
                var line = FindFirstBacktickLine(script);
                detail = new ObfuscationDetail
                {
                    Pattern = "ExcessiveBacktick",
                    Description = Localized.ScriptEngine_Filter_Obfs_ExcessiveBacktick($"{ratio:P1}"),
                    Severity = ratio > 0.15 ? 4 : 3,
                    LineNumber = line,
                };
                return true;
            }

            detail = null!;
            return false;
        }

        /// <summary>检测字符串拼接构造命令名（如 "Inv"+"oke-Expr"+"ession"）。</summary>
        public bool DetectStringConcatenation(ScriptBlockAst ast, out List<ObfuscationDetail> details)
        {
            details = new List<ObfuscationDetail>();

            var commands = ast.FindAll(n => n is CommandAst, true);
            foreach (CommandAst cmd in commands)
            {
                if (cmd.CommandElements.Count < 2)
                    continue;

                var firstElement = cmd.CommandElements[0];
                if (firstElement is BinaryExpressionAst binary
                    && binary.Operator == TokenKind.Plus)
                {
                    var line = binary.Extent?.StartLineNumber ?? 0;
                    details.Add(new ObfuscationDetail
                    {
                        Pattern = "CommandNameConcatenation",
                        Description = Localized.ScriptEngine_Filter_Obfs_CmdNameConcat(line),
                        Severity = 3,
                        LineNumber = line,
                    });
                }
            }

            return details.Count > 0;
        }

        /// <summary>检测危险的动态调用（iex + 变量/表达式）。</summary>
        public bool DetectSuspiciousInvocation(ScriptBlockAst ast, out List<ObfuscationDetail> details)
        {
            details = new List<ObfuscationDetail>();

            var commands = ast.FindAll(n => n is CommandAst, true);
            foreach (CommandAst cmd in commands)
            {
                var name = cmd.GetCommandName();
                if (!string.Equals(name, "Invoke-Expression", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "iex", StringComparison.OrdinalIgnoreCase))
                    continue;

                // iex 后跟着变量或复杂表达式（而不是字符串字面量）
                if (cmd.CommandElements.Count > 1)
                {
                    var arg = cmd.CommandElements[1];
                    if (arg is VariableExpressionAst
                        || arg is BinaryExpressionAst
                        || arg is InvokeMemberExpressionAst
                        || arg is ConvertExpressionAst)
                    {
                        var line = cmd.Extent?.StartLineNumber ?? 0;
                        details.Add(new ObfuscationDetail
                        {
                            Pattern = "SuspiciousIex",
                            Description = Localized.ScriptEngine_Filter_Obfs_SuspiciousIex(line),
                            Severity = 5,
                            LineNumber = line,
                        });
                    }
                }
            }

            return details.Count > 0;
        }

        /// <summary>检测 WinAPI 调用（VirtualAlloc/CreateThread/Marshal 等）。</summary>
        public bool DetectWinApiPatterns(string script, out List<ObfuscationDetail> details)
        {
            details = new List<ObfuscationDetail>();

            var patterns = new (string pattern, string name)[]
            {
                (@"VirtualAlloc", "VirtualAlloc"),
                (@"CreateThread", "CreateThread"),
                (@"WriteProcessMemory", "WriteProcessMemory"),
                (@"VirtualProtect", "VirtualProtect"),
                (@"\[System\.Runtime\.InteropServices\.Marshal\]", "Marshal"),
                (@"\[DllImport\]", "DllImport"),
                (@"LoadLibrary", "LoadLibrary"),
                (@"GetProcAddress", "GetProcAddress"),
            };

            foreach (var (pattern, name) in patterns)
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                var match = regex.Match(script);
                if (match.Success)
                {
                    var line = GetLineNumber(script, match.Index);
                    details.Add(new ObfuscationDetail
                    {
                        Pattern = $"WinAPI_{name}",
                        Description = Localized.ScriptEngine_Filter_Obfs_WinApi(name, line),
                        Severity = 5,
                        LineNumber = line,
                    });
                }
            }

            return details.Count > 0;
        }

        /// <summary>检测动态程序集加载。</summary>
        public bool DetectAssemblyLoading(ScriptBlockAst? ast, string script, out List<ObfuscationDetail> details)
        {
            details = new List<ObfuscationDetail>();

            // 匹配 Assembly::Load([byte[]] 或 Assembly::LoadFrom
            var asmLoadRegex = new Regex(
                @"Assembly[:\s]*\.\s*Load(?:From|File)?\s*\(\s*\[?byte\d?\[\]\]?",
                RegexOptions.IgnoreCase);

            var match = asmLoadRegex.Match(script);
            if (match.Success)
            {
                var line = GetLineNumber(script, match.Index);
                details.Add(new ObfuscationDetail
                {
                    Pattern = "AssemblyLoadByteArray",
                    Description = Localized.ScriptEngine_Filter_Obfs_AssemblyLoadByteArray(line),
                    Severity = 5,
                    LineNumber = line,
                });
            }

            // 检查 Add-Type -TypeDefinition（用于内联 C# 代码）
            if (ast != null)
            {
                var commands = ast.FindAll(n => n is CommandAst, true);
                foreach (CommandAst cmd in commands)
                {
                    var name = cmd.GetCommandName();
                    if (!string.Equals(name, "Add-Type", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var element in cmd.CommandElements)
                    {
                        if (element is CommandParameterAst param)
                        {
                            var pn = param.ParameterName ?? "";
                            if (string.Equals(pn, "TypeDefinition", StringComparison.OrdinalIgnoreCase))
                            {
                                var line = cmd.Extent?.StartLineNumber ?? 0;
                                details.Add(new ObfuscationDetail
                                {
                                    Pattern = "AddTypeInlineCode",
                                    Description = Localized.ScriptEngine_Filter_Obfs_AddTypeInlineCode(line),
                                    Severity = 4,
                                    LineNumber = line,
                                });
                            }
                        }
                    }
                }
            }

            return details.Count > 0;
        }

        /// <summary>检测隐藏窗口执行。</summary>
        public bool DetectHiddenWindow(string script, out ObfuscationDetail detail)
        {
            var regex = new Regex(@"-WindowStyle\s+Hidden", RegexOptions.IgnoreCase);
            var match = regex.Match(script);
            if (match.Success)
            {
                var line = GetLineNumber(script, match.Index);
                detail = new ObfuscationDetail
                {
                    Pattern = "HiddenWindow",
                    Description = Localized.ScriptEngine_Filter_Obfs_HiddenWindow(line),
                    Severity = 3,
                    LineNumber = line,
                };
                return true;
            }

            detail = null!;
            return false;
        }

        /// <summary>检测 .NET 反射访问和危险类型调用。</summary>
        public bool DetectDotNetReflection(ScriptBlockAst ast, out List<ObfuscationDetail> details)
        {
            details = new List<ObfuscationDetail>();

            // 查找所有 InvokeMemberExpressionAst 节点（静态成员访问）
            var memberInvocations = ast.FindAll(n => n is InvokeMemberExpressionAst, true);
            foreach (InvokeMemberExpressionAst invoke in memberInvocations)
            {
                var exprText = invoke.Expression?.ToString() ?? "";
                var line = invoke.Extent?.StartLineNumber ?? 0;

                // 检查是否为 [TypeName]::Member 模式（TypeExpression 的字符串以 [ 开头）
                if (!exprText.StartsWith("[", StringComparison.Ordinal))
                    continue;

                // 提取类型名（去掉 [ 和 ]）
                var typeName = exprText.Trim('[', ']');
                if (string.IsNullOrEmpty(typeName))
                    continue;

                var memberName = invoke.Member?.ToString() ?? "";

                // 安全类型：完全放行
                if (IsSafeDotNetType(typeName))
                    continue;

                // 危险类型检查
                if (IsDangerousDotNetType(typeName))
                {
                    details.Add(new ObfuscationDetail
                    {
                        Pattern = "DangerousDotNetAccess",
                        Description = Localized.ScriptEngine_Filter_Obfs_DangerousDotNet(typeName, memberName, line),
                        Severity = 5,
                        LineNumber = line,
                    });
                }
                else
                {
                    // 非白名单、非高危的未知类型 —— 询问用户
                    details.Add(new ObfuscationDetail
                    {
                        Pattern = "UnknownDotNetAccess",
                        Description = Localized.ScriptEngine_Filter_Obfs_UnknownDotNet(typeName, memberName, line),
                        Severity = 3,
                        LineNumber = line,
                    });
                }
            }

            // 检测动态类型名构造
            if (ast != null)
                DetectDynamicTypeAccess(ast, details);

            return details.Count > 0;
        }

        /// <summary>检测动态构造类型名的访问模式（通过变量或表达式构建类型名）。</summary>
        private void DetectDynamicTypeAccess(ScriptBlockAst ast, List<ObfuscationDetail> details)
        {
            // 模式 1: [Type]::GetType("动态名称")
            // 模式 2: 通过变量引用类型
            // 模式 3: via GetType() on an object

            var commands = ast.FindAll(n => n is CommandAst, true);
            foreach (CommandAst cmd in commands)
            {
                var cmdName = cmd.GetCommandName();

                // GetType() 或类似反射方法
                if (string.Equals(cmdName, "GetType", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cmdName, "GetTypeFromHandle", StringComparison.OrdinalIgnoreCase))
                {
                    if (cmd.CommandElements.Count > 1)
                    {
                        var arg = cmd.CommandElements[1];
                        if (arg is VariableExpressionAst)
                        {
                            var line = cmd.Extent?.StartLineNumber ?? 0;
                            details.Add(new ObfuscationDetail
                            {
                                Pattern = "DynamicTypeNameVariable",
                                Description = Localized.ScriptEngine_Filter_Obfs_DynamicTypeName(line),
                                Severity = 5,
                                LineNumber = line,
                            });
                        }
                    }
                }

                // 通过 Add-Type -TypeDefinition 动态定义类型
                if (string.Equals(cmdName, "Add-Type", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var element in cmd.CommandElements)
                    {
                        if (element is CommandParameterAst param
                            && string.Equals(param.ParameterName, "TypeDefinition", StringComparison.OrdinalIgnoreCase))
                        {
                            // 已由 DetectAssemblyLoading 处理，此处不再重复
                        }
                    }
                }
            }

            // 查找通过 GetMethod/GetProperty/GetField/InvokeMember 进行的后期绑定调用
            var memberAccesses = ast.FindAll(n =>
                n is InvokeMemberExpressionAst invoke
                && (invoke.Member?.ToString()?.Equals("GetMethod", StringComparison.OrdinalIgnoreCase) == true
                    || invoke.Member?.ToString()?.Equals("GetProperty", StringComparison.OrdinalIgnoreCase) == true
                    || invoke.Member?.ToString()?.Equals("GetField", StringComparison.OrdinalIgnoreCase) == true
                    || invoke.Member?.ToString()?.Equals("InvokeMember", StringComparison.OrdinalIgnoreCase) == true), true);

            foreach (InvokeMemberExpressionAst access in memberAccesses)
            {
                var line = access.Extent?.StartLineNumber ?? 0;
                details.Add(new ObfuscationDetail
                {
                    Pattern = "LateBindingReflection",
                    Description = Localized.ScriptEngine_Filter_Obfs_LateBindingReflection(access.Member.ToString(), line),
                    Severity = 5,
                    LineNumber = line,
                });
            }
        }

        /// <summary>判断类型名是否在安全白名单中。</summary>
        private static bool IsSafeDotNetType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return false;

            // 精确匹配
            if (SafeDotNetTypes.Contains(typeName))
                return true;

            // 泛型类型匹配（去掉 `1、`2 后缀）
            var backtickIdx = typeName.IndexOf('`');
            if (backtickIdx > 0)
            {
                var genericBase = typeName[..backtickIdx];
                if (SafeDotNetTypes.Contains(genericBase))
                    return true;
                if (SafeDotNetTypes.Contains(genericBase + "`1"))
                    return true;
            }

            return false;
        }

        /// <summary>判断类型名是否在高危名单中。</summary>
        private static bool IsDangerousDotNetType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return false;

            if (DangerousDotNetTypes.Contains(typeName))
                return true;

            // 泛型匹配
            var backtickIdx = typeName.IndexOf('`');
            if (backtickIdx > 0)
            {
                var genericBase = typeName[..backtickIdx];
                if (DangerousDotNetTypes.Contains(genericBase))
                    return true;
                if (DangerousDotNetTypes.Contains(genericBase + "`1"))
                    return true;
            }

            return false;
        }

        /// <summary>判断命令是否为文件操作命令（含别名）。</summary>
        private static bool IsFileManipulationCommand(string name)
        {
            return string.Equals(name, "Set-Content", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "sc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Add-Content", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "ac", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Out-File", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "New-Item", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "ni", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Remove-Item", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "ri", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "del", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "rm", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "rd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "erase", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Set-ItemProperty", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "sp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Set-Location", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "sl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "cd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "chdir", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Get-ChildItem", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "gci", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "ls", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "dir", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Get-Item", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "gi", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Get-Content", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "gc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "cat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "type", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>判断命令是否为 Web 请求命令（含别名）。</summary>
        private static bool IsWebRequestCommand(string name)
        {
            return string.Equals(name, "Invoke-WebRequest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "iwr", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "curl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "wget", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Invoke-RestMethod", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "irm", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>判断命令是否为复制/移动命令（含别名）。</summary>
        private static bool IsCopyMoveCommand(string name)
        {
            return string.Equals(name, "Copy-Item", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "ci", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "cp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "copy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Move-Item", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "mi", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "mv", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "move", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Rename-Item", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "rni", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "ren", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>判断参数名是否为路径相关。</summary>
        private static bool IsPathParameter(string paramName)
        {
            return string.Equals(paramName, "Path", StringComparison.OrdinalIgnoreCase)
                || string.Equals(paramName, "LiteralPath", StringComparison.OrdinalIgnoreCase)
                || string.Equals(paramName, "PSPath", StringComparison.OrdinalIgnoreCase)
                || string.Equals(paramName, "FilePath", StringComparison.OrdinalIgnoreCase)
                || string.Equals(paramName, "SourcePath", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>从参数定义中提取字符串值。</summary>
        private static string? TryExtractStringValue(CommandElementAst? argument)
        {
            if (argument == null)
                return null;

            // 字符串字面量
            if (argument is StringConstantExpressionAst strConst)
                return strConst.Value;

            // 变量引用（提取变量名）
            if (argument is VariableExpressionAst varExpr)
                return $"$({varExpr.VariablePath.UserPath})";

            // 展开表达式（复杂表达式，无法静态解析）
            if (argument is ConvertExpressionAst || argument is InvokeMemberExpressionAst)
                return $"<Expression>";

            return argument.Extent?.Text;
        }

        /// <summary>
        /// 检测脚本中是否直接引用了 <c>$page</c> 对象变量
        /// （如 <c>$page</c>、<c>$page.Property</c>、<c>$page.Method()</c>），
        /// 用于 <c>Security_Script_AllowAccessPageObject</c> 安全策略的访问控制。
        /// 使用 AST 解析进行精确匹配，避免正则误报。
        /// </summary>
        public bool HasPageVariableAccess(string script)
        {
            if (string.IsNullOrWhiteSpace(script))
                return false;

            try
            {
                var ast = Parser.ParseInput(script, out _, out _);
                if (ast == null)
                    return false;

                // 查找所有 VariableExpressionAst 节点（对应于 $xxx 变量引用）
                var variableRefs = ast.FindAll(n => n is VariableExpressionAst, true);
                foreach (VariableExpressionAst varExpr in variableRefs)
                {
                    var name = varExpr.VariablePath.UserPath;
                    // 去掉作用域限定符（如 $script:page → page）
                    var colonIdx = name.LastIndexOf(':');
                    if (colonIdx >= 0)
                        name = name[(colonIdx + 1)..];

                    if (string.Equals(name, "page", StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                return false;
            }
            catch
            {
                // AST 解析失败时回退到简单的正则检测
                return Regex.IsMatch(script, @"\$page\b", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(script, @"\$\w+:page\b", RegexOptions.IgnoreCase);
            }
        }

        /// <summary>从脚本中定位第 N 个字符所在的行号（1-based）。</summary>
        private static int GetLineNumber(string script, int index)
        {
            if (index < 0 || index >= script.Length)
                return 1;
            return script[..index].Count(c => c == '\n') + 1;
        }

        /// <summary>找到脚本中第一个反引号所在的行号。</summary>
        private static int FindFirstBacktickLine(string script)
        {
            for (int i = 0; i < script.Length; i++)
            {
                if (script[i] == '`')
                    return GetLineNumber(script, i);
            }
            return 1;
        }
    }
}
