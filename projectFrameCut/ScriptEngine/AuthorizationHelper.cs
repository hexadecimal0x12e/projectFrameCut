using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Text;
using projectFrameCut.Shared;

namespace projectFrameCut.ScriptEngine
{
    /// <summary>
    /// 授权决策结果。
    /// </summary>
    public enum AuthorizationResult
    {
        /// <summary>允许命令执行。</summary>
        Allow,
        /// <summary>拒绝命令执行。</summary>
        Deny,
        /// <summary>允许并记住此决策（当前会话不再询问）。</summary>
        AllowAndRemember,
        /// <summary>拒绝并记住此决策（当前会话不再询问）。</summary>
        DenyAndRemember,
    }

    /// <summary>
    /// 授权回调委托，宿主通过此委托对命令做出授权决策。
    /// </summary>
    /// <param name="commandInfo">待执行的命令信息。</param>
    /// <param name="commandOrigin">命令来源。</param>
    /// <returns>授权决策结果。</returns>
    public delegate AuthorizationResult CommandAuthorizationCallback(CommandInfo commandInfo, CommandOrigin commandOrigin);

    // ═══════════════════════════════════════════════════════════════
    // CommandFilter 增强类型
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 携带 <see cref="CommandFilter"/> 分析结果的授权上下文，
    /// 为授权决策提供更丰富的命令参数信息。
    /// </summary>
    public class AuthorizationContext
    {
        /// <summary>待执行的命令信息。</summary>
        public CommandInfo CommandInfo { get; init; } = null!;

        /// <summary>命令来源。</summary>
        public CommandOrigin CommandOrigin { get; init; }

        /// <summary>文件操作的目标路径（如果适用）。</summary>
        public string? TargetPath { get; init; }

        /// <summary>目标路径的安全状态。</summary>
        public PathSafety? PathSafetyStatus { get; init; }

        /// <summary>网络请求的目标 URL（如果适用）。</summary>
        public string? TargetUrl { get; init; }

        /// <summary>混淆警告信息。</summary>
        public string? ObfuscationWarning { get; init; }

        /// <summary>脚本威胁级别（如果已分析）。</summary>
        public ThreatLevel? ThreatLevel { get; init; }
    }

    /// <summary>
    /// 增强的授权回调委托，携带 <see cref="AuthorizationContext"/> 丰富上下文。
    /// </summary>
    /// <param name="context">包含命令详细参数和安全分析结果的上下文。</param>
    /// <returns>授权决策结果。</returns>
    public delegate AuthorizationResult EnhancedAuthorizationCallback(AuthorizationContext context);

    /// <summary>
    /// 自定义 PowerShell 授权管理器，实现三层检测：
    /// Always Deny → Always Allow → Prompt User (通过可配置的 Handler)。
    /// </summary>
    internal class PSCommandAuthorizationHelper : AuthorizationManager
    {
        /// <summary>
        /// 审计模式开关，开启时所有命令调用均记入日志。
        /// </summary>
        public static bool AuditMode { get; set; } = true;

        /// <summary>
        /// 可配置的授权处理器。宿主可注入自己的授权逻辑（例如弹出对话框）。
        /// 若为 null，则 PromptUserCommands 中的命令默认被拒绝。
        /// </summary>
        public CommandAuthorizationCallback? AuthorizationHandler { get; set; }

        /// <summary>
        /// 增强的授权处理器，接收包含路径/URL/混淆分析的 <see cref="AuthorizationContext"/>。
        /// 当设置此处理器时，优先于 <see cref="AuthorizationHandler"/> 被调用。
        /// </summary>
        public EnhancedAuthorizationCallback? EnhancedAuthorizationHandler { get; set; }

        /// <summary>
        /// 关联的 CommandFilter 实例，用于路径检查和混淆检测信息。
        /// 由 <see cref="ScriptCore"/> 在初始化时注入。
        /// </summary>
        internal CommandFilter? CommandFilter { get; set; }

        /// <summary>
        /// 会话级别的记忆缓存（命令名 → 是否允许）。
        /// </summary>
        private readonly Dictionary<string, bool> _sessionCache = new(StringComparer.OrdinalIgnoreCase);

        // ====================================================================
        // 高危命令 —— 直接拦截，不问用户
        // ====================================================================
        private static readonly HashSet<string> AlwaysDenyCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            // ---- 代码执行 / 注入 ----
            "Invoke-Expression", "iex",
            "Invoke-Command", "icm",
            "Add-Type",

            // ---- 进程操作 ----
            "Start-Process", "saps", "start",
            "Stop-Process", "spps", "kill",
            "Debug-Process",
            "Wait-Process",

            // ---- 系统状态变更 ----
            "Restart-Computer", "Stop-Computer", "Shutdown",
            "Add-Computer", "Remove-Computer", "Rename-Computer",
            "Suspend-Computer",
            "Checkpoint-Computer", "Restore-Computer",

            // ---- 服务操作 ----
            "New-Service", "Set-Service", "Remove-Service",
            "Restart-Service", "Stop-Service", "Start-Service",
            "Resume-Service", "Suspend-Service",

            // ---- 计划任务 ----
            "Register-ScheduledJob", "Set-ScheduledJob", "Unregister-ScheduledJob",
            "Enable-JobTrigger", "Disable-JobTrigger",
            "Add-JobTrigger", "Remove-JobTrigger",
            "New-JobTrigger",

            // ---- 远程管理 ----
            "Enable-PSRemoting", "Disable-PSRemoting",

            // ---- 安全策略 ----
            "Set-ExecutionPolicy",
            "Set-MpPreference", "Add-MpPreference",

            // ---- 文件删除 / 移动 / 重命名 / 复制 ----
            "Remove-Item", "ri", "del", "rm", "rd", "erase",
            "Move-Item", "mi", "mv", "move",
            "Rename-Item", "rni", "ren",
            "Clear-Item", "cli",
            "Remove-ItemProperty", "rp",
            "Copy-Item", "ci", "cp", "copy",

            // ---- 注册表映射 ----
            "New-PSDrive", "Remove-PSDrive",

            // ---- Windows 功能 ----
            "Disable-WindowsOptionalFeature", "Enable-WindowsOptionalFeature",

            // ---- 会话 / 远程连接 ----
            "New-PSSession", "Remove-PSSession",
            "Enter-PSSession", "Exit-PSSession",
        };

        // ====================================================================
        // 安全命令 —— 默认放行
        // ====================================================================
        private static readonly HashSet<string> AlwaysAllowCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            // ---- 项目自有 cmdlet（允许所有 project-* 前缀） ----
            // 以下在检查时按前缀匹配，不在 HashSet 中逐条列出

            // ---- 输出 ----
            "Write-Output", "echo",
            "Write-Host",
            "Write-Progress",
            "Write-Verbose",
            "Write-Debug",
            "Write-Warning",

            // ---- 格式化 ----
            "Format-Table", "ft",
            "Format-List", "fl",
            "Format-Wide", "fw",
            "Format-Custom", "fc",

            // ---- 管道 / 过滤 / 选择 ----
            "Select-Object", "select",
            "Where-Object", "where",
            "ForEach-Object", "foreach",
            "Sort-Object", "sort",
            "Group-Object",
            "Measure-Object",
            "Compare-Object",
            "Tee-Object",

            // ---- 输出定向 ----
            "Out-String",
            "Out-Default",
            "Out-Null",
            "Out-Host",

            // ---- 变量操作 ----
            "Get-Variable", "gv",
            "Set-Variable", "sv", "set",
            "New-Variable", "nv",
            "Remove-Variable", "rv",

            // ---- 路径操作 ----
            "Get-Location", "gl", "pwd",
            "Push-Location", "pushd",
            "Pop-Location", "popd",
            "Split-Path",
            "Join-Path",
            "Convert-Path",
            "Resolve-Path",

            // ---- 信息查询 ----
            "Get-Command", "gcm",
            "Get-Help", "help", "man",
            "Get-Member", "gm",
            "Get-Date",
            "Get-Random",
            "Get-UICulture", "Get-Culture",
            "Get-Host",
            "Get-Unique",

            // ---- 调试 / 工具 ----
            "Measure-Command",
            "Set-PSDebug",
            "Get-PSCallStack",

            // ---- Add-Member（非 ScriptMethod 的普通操作，不在此拦截） ----
            // 注：仅当添加 ScriptMethod 时需要额外关注，这里不做硬性限制
        };

        // ====================================================================
        // 可能危害命令 —— 需要询问用户
        // ====================================================================
        private static readonly HashSet<string> PromptUserCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            // ---- 文件写入 / 创建 ----
            "Set-Content", "sc",
            "Add-Content", "ac",
            "Out-File",
            "New-Item", "ni",
            "Set-ItemProperty", "sp",

            // ---- 文件读取（路径不可控） ----
            "Get-Content", "gc", "cat", "type",
            "Get-ChildItem", "gci", "ls", "dir",
            "Get-Item", "gi",
            "Get-ItemProperty", "gp",

            // ---- 网络访问 ----
            "Invoke-WebRequest", "iwr", "curl", "wget",
            "Invoke-RestMethod", "irm",

            // ---- 对象创建 ----
            "New-Object", "no",

            // ---- 反射 / 动态代码 ----
            "Add-Member", "am",

            // ---- 凭据 / 安全字符串 ----
            "Get-Credential",
            "Read-Host",
            "ConvertTo-SecureString",
            "ConvertFrom-SecureString",
            "Protect-CmsMessage",
            "Unprotect-CmsMessage",

            // ---- 通信 ----
            "Send-MailMessage",

            // ---- 数据导出 ----
            "Export-Csv", "epcsv",
            "Import-Csv", "ipcsv",
            "Export-Clixml",
            "Import-Clixml",
            "ConvertTo-Html",
            "Out-GridView",

            // ---- WMI / CIM ----
            "Get-WmiObject", "gwmi",
            "Get-CimInstance",
            "Get-CimClass",
            "Invoke-CimMethod",
            "Invoke-WmiMethod",

            // ---- 事件日志 ----
            "Clear-EventLog",
            "Write-EventLog",
            "Limit-EventLog",

            // ---- 进程 / 服务信息（侦察） ----
            "Get-Process", "gps", "ps",
            "Get-Service", "gsv",

            // ---- 环境变量写入 ----
            // Set-Item 在 env: 驱动器上需要询问，但无法在此区分

            // ---- 位置变更（对注册表驱动器的 cd 是危险的） ----
            "Set-Location", "sl", "cd", "chdir",

            // ---- 会话状态管理 ----
            "Get-PSSession",
            "Receive-Job",

            // ---- 帮助更新（网络访问） ----
            "Update-Help",
            "Save-Help",
        };

        public PSCommandAuthorizationHelper(string shellId) : base(shellId) { }

        /// <summary>
        /// 判断命令是否属于项目自定义 cmdlet（以其前缀识别）。
        /// </summary>
        private static bool IsProjectCmdlet(string commandName)
        {
            return commandName.StartsWith("Get-Project", StringComparison.OrdinalIgnoreCase)
                || commandName.StartsWith("Add-Project", StringComparison.OrdinalIgnoreCase)
                || commandName.StartsWith("Set-Project", StringComparison.OrdinalIgnoreCase)
                || commandName.StartsWith("Remove-Project", StringComparison.OrdinalIgnoreCase)
                || commandName.StartsWith("Copy-Project", StringComparison.OrdinalIgnoreCase)
                || commandName.Equals("Get-EffectBundleTypes", StringComparison.OrdinalIgnoreCase)
                || commandName.Equals("Get-EffectBundleField", StringComparison.OrdinalIgnoreCase)
                || commandName.Equals("Get-EnvironmentInfo", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 清除会话记忆缓存（例如当用户切换项目时可调用）。
        /// </summary>
        public void ClearSessionCache()
        {
            _sessionCache.Clear();
        }

        protected override bool ShouldRun(CommandInfo commandInfo, CommandOrigin commandOrigin, PSHost host, out Exception reason)
        {
            reason = null;

            var cmdName = commandInfo.Name;

            // ── 运行时别名解析：将别名（ls/dir/del 等）解析为被指向的真实命令名 ──
            // 使用 PowerShell SDK 的 AliasInfo，可正确处理"内建别名"和"用户自定义别名"
            // 无论用户怎么 Set-Alias，都不会逃逸安全检查
            if (commandInfo.CommandType == CommandTypes.Alias && commandInfo is AliasInfo aliasInfo)
            {
                var resolved = aliasInfo?.Name ?? aliasInfo?.Definition;
                if (!string.IsNullOrEmpty(resolved))
                {
                    Logger.Log($"[CommandFilter] 别名 '{cmdName}' → '{resolved}'");
                    cmdName = resolved; // 后续所有检查都用解析后的真实命令名
                }
            }

            // ---- 1. 审计日志 ----
            if (AuditMode)
            {
                Logger.Log($"[CommandAuthorization] {cmdName} (origin: {commandOrigin}, host: {host.InstanceId})");

            }

            // ---- 2. 项目自有 cmdlet 始终放行 ----
            if (IsProjectCmdlet(cmdName))
            {
                return true;
            }

            // ---- 3. 检查会话记忆缓存 ----
            if (_sessionCache.TryGetValue(cmdName, out var allowed))
            {
                if (!allowed)
                {
                    reason = new NotAllowedCommandException(
                        NotAllowedCommandException.DeniedReason.DisallowedByRuleOfUser,
                        $"命令 '{cmdName}' 之前已被用户拒绝。如需重新授权，请重新打开项目或重启应用。");
                    return false;
                }
                return true;
            }

            // ---- 4. Always Deny —— 直接拦截 ----
            if (AlwaysDenyCommands.Contains(cmdName))
            {
                reason = new NotAllowedCommandException(
                    NotAllowedCommandException.DeniedReason.DisallowedByInternalRules,
                    $"命令 '{cmdName}' 被安全策略禁止执行。");
                return false;
            }

            // ---- 5. Always Allow —— 直接放行 ----
            if (AlwaysAllowCommands.Contains(cmdName))
            {
                return true;
            }

            // ---- 6. Prompt 命令 或 未分类命令 —— 询问用户 ----
            bool needsPrompt = PromptUserCommands.Contains(cmdName);

            // 6a. 尝试构建 CommandFilter 分析上下文
            AuthorizationContext? authContext = null;
            try
            {
                var pendingParams = ScriptCore.PendingCommandParameters?.Value;

                // 在 PendingCommandParameters 中查找匹配当前命令的参数信息
                // 分三步：1) 精确匹配 AST 命令名  2) 匹配原始别名  3) 功能匹配（文件命令找路径，Web 命令找 URL）
                var match = pendingParams?.FirstOrDefault(p =>
                    string.Equals(p.CommandName, cmdName, StringComparison.OrdinalIgnoreCase))
                    ?? pendingParams?.FirstOrDefault(p =>
                        !string.Equals(cmdName, commandInfo.Name, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.CommandName, commandInfo.Name, StringComparison.OrdinalIgnoreCase))
                    ?? pendingParams?.FirstOrDefault(p =>
                        (p.TargetPath != null || p.TargetUrl != null)
                        && PromptUserCommands.Contains(cmdName));
                if (match != null)
                {
                    // 如果还没有完成路径安全检查，在这里做
                    if (match.PathSafetyStatus == PathSafety.Unresolved && CommandFilter != null)
                    {
                        if (!string.IsNullOrEmpty(match.TargetPath) && !string.IsNullOrEmpty(CommandFilter.WorkingPath))
                        {
                            match.PathSafetyStatus = CommandFilter.CheckPathSafety(match.TargetPath);
                            match.IsPathWithinProject = match.PathSafetyStatus == PathSafety.Safe;
                        }
                    }

                    authContext = new AuthorizationContext
                    {
                        CommandInfo = commandInfo,
                        CommandOrigin = commandOrigin,
                        TargetPath = match.TargetPath,
                        PathSafetyStatus = match.PathSafetyStatus,
                        TargetUrl = match.TargetUrl,
                    };
                }
            }
            catch
            {
                // 无法获取 CommandFilter 分析结果时，回退到普通 handler
                authContext = null;
            }

            // 6b. 路径安全检查：文件操作且目标路径在项目内 → 直接放行
            if (authContext?.TargetPath != null && authContext.PathSafetyStatus == PathSafety.Safe)
            {
                // 路径在项目目录内 → 安全放行，无需打扰用户
                Logger.Log($"[CommandFilter] 文件操作 '{cmdName}' 目标在项目内，已放行: {authContext.TargetPath}");
                return true;
            }

            // 6c. 路径安全检查：目标路径在项目外或路径遍历 → 使用增强的警告对话框
            bool hasPathRisk = authContext?.TargetPath != null
                && (authContext.PathSafetyStatus == PathSafety.OutsideProject
                    || authContext.PathSafetyStatus == PathSafety.PathTraversal);

            bool hasEnhancedContext = authContext != null
                && (hasPathRisk
                    || authContext.TargetUrl != null                               // Web 请求需要显示 URL
                    || authContext.PathSafetyStatus == PathSafety.Unresolved       // 未知路径需用户确认
                    || authContext.ThreatLevel >= ThreatLevel.Medium);             // 混淆警告需展示

            // 优先使用增强处理器（携带路径/URL/混淆信息的对话框）
            if (EnhancedAuthorizationHandler != null && hasEnhancedContext)
            {
                var enhancedResult = EnhancedAuthorizationHandler(authContext);

                switch (enhancedResult)
                {
                    case AuthorizationResult.Allow:
                        return true;

                    case AuthorizationResult.Deny:
                        reason = new NotAllowedCommandException(
                            NotAllowedCommandException.DeniedReason.UserRejected,
                            $"用户拒绝了命令 '{cmdName}'。");
                        return false;

                    case AuthorizationResult.AllowAndRemember:
                        _sessionCache[cmdName] = true;
                        return true;

                    case AuthorizationResult.DenyAndRemember:
                        _sessionCache[cmdName] = false;
                        reason = new NotAllowedCommandException(
                            NotAllowedCommandException.DeniedReason.UserRejected,
                            $"用户拒绝了命令 '{cmdName}'，并选择了记住此决策。");
                        return false;
                }
            }

            // 6c. 回退到原始授权处理器
            if (AuthorizationHandler != null)
            {
                var result = AuthorizationHandler(commandInfo, commandOrigin);

                switch (result)
                {
                    case AuthorizationResult.Allow:
                        return true;

                    case AuthorizationResult.Deny:
                        reason = new NotAllowedCommandException(
                            NotAllowedCommandException.DeniedReason.UserRejected,
                            $"用户拒绝了命令 '{cmdName}'。");
                        return false;

                    case AuthorizationResult.AllowAndRemember:
                        _sessionCache[cmdName] = true;
                        return true;

                    case AuthorizationResult.DenyAndRemember:
                        _sessionCache[cmdName] = false;
                        reason = new NotAllowedCommandException(
                            NotAllowedCommandException.DeniedReason.UserRejected,
                            $"用户拒绝了命令 '{cmdName}'，并选择了记住此决策。");
                        return false;
                }
            }
            else if (needsPrompt)
            {
                // Prompt 命令但无 handler 配置 —— 拒绝
                reason = new NotAllowedCommandException(
                    NotAllowedCommandException.DeniedReason.UserNotRespond,
                    $"命令 '{cmdName}' 需要用户授权，但没有配置授权处理器。");
                return false;
            }

            // ---- 7. 未分类命令且无 handler —— 安全模式：拒绝 ----
            reason = new NotAllowedCommandException(
                NotAllowedCommandException.DeniedReason.DisallowedByInternalRules,
                $"命令 '{cmdName}' 未分类且未配置授权处理器，已被安全策略禁止。");
            return false;
        }
    }

    /// <summary>
    /// 命令被拒绝时抛出的异常，包含拒绝原因枚举。
    /// </summary>
    internal class NotAllowedCommandException(NotAllowedCommandException.DeniedReason reason, string message) : Exception(message)
    {
        public DeniedReason Why { get; init; } = reason;

        public enum DeniedReason
        {
            /// <summary>用户拒绝了命令。</summary>
            UserRejected,
            /// <summary>用户没有回应授权请求（或超时）。</summary>
            UserNotRespond,
            /// <summary>用户的规则禁止了此命令。</summary>
            DisallowedByRuleOfUser,
            /// <summary>管理员的规则禁止了此命令。</summary>
            DisallowedByRuleOfAdministrator,
            /// <summary>内部安全规则禁止了此命令。</summary>
            DisallowedByInternalRules,
        }
    }
}
